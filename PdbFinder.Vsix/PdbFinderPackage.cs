using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using EnvDTE90;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using PdbFinder.Core;
using Task = System.Threading.Tasks.Task;

[assembly: Microsoft.VisualStudio.Shell.ProvideCodeBase(AssemblyName = "PdbFinder.Vsix")]
[assembly: Microsoft.VisualStudio.Shell.ProvideCodeBase(AssemblyName = "PdbFinder.Core")]

namespace PdbFinder.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("PDB Finder", "Matches UE dump symbols from a network archive", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(FinderOptions), "PDB Finder", "General", 0, 0, true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuid)]
    public sealed class PdbFinderPackage : AsyncPackage
    {
        private const string PackageGuid = "23c647b3-872b-4c3c-b693-ed5051b5a16d";
        internal static PdbFinderPackage Instance { get; private set; }
        private static readonly Guid CommandSet = new Guid("cd617d39-32c4-44e7-94a8-f25ba4434264");
        private const int FindCommand = 0x0100;
        private const int SettingsCommand = 0x0101;
        private const int TestConnectionCommand = 0x0102;
        private const int RebuildIndexCommand = 0x0103;
        private DTE2 _dte;
        private DocumentEvents _documentEvents;
        private WindowEvents _windowEvents;
        private DebuggerEvents _debuggerEvents;
        private OutputWindowPane _output;
        private volatile bool _disposed;
        private CancellationTokenSource _scanCancellation;
        private readonly SemaphoreSlim _scanGate = new SemaphoreSlim(1, 1);
        private MatchSession _matchSession;
        private string _stagedDirectory { get => _matchSession?.StagedDirectory; set => _matchSession.StagedDirectory = value; }
        private bool _stagingHintShown;
        private bool _isStaging { get => _matchSession?.IsStaging == true; set => _matchSession.IsStaging = value; }
        private bool _stageCancelledByUser { get => _matchSession?.CancelledByUser == true; set => _matchSession.CancelledByUser = value; }
        private string _readyHintShownFor;
        private string _lastRequestedDump => _matchSession?.DumpPath;
        private string _lastAddedDirectory;
        private PdbCandidate _activeCandidate { get => _matchSession?.Candidate; set => _matchSession.Candidate = value; }
        private DumpModule _activeModule { get => _matchSession?.Module; set => _matchSession.Module = value; }
        private string _activeModuleName => Path.GetFileName(_activeModule?.Name);

        private bool OwnsSession(MatchSession session) => ReferenceEquals(_matchSession, session);

        private string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDB Finder");

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            _dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            commands?.AddCommand(new MenuCommand((_, __) => StartForActiveDocument(), new CommandID(CommandSet, FindCommand)));
            commands?.AddCommand(new MenuCommand((_, __) => ShowOptionPage(typeof(FinderOptions)), new CommandID(CommandSet, SettingsCommand)));
            commands?.AddCommand(new MenuCommand((_, __) => TestConnection(), new CommandID(CommandSet, TestConnectionCommand)));
            commands?.AddCommand(new MenuCommand((_, __) => RebuildIndex(), new CommandID(CommandSet, RebuildIndexCommand)));
            if (_dte == null) return;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            _documentEvents = _dte.Events.DocumentEvents;
            _documentEvents.DocumentOpened += OnDocumentOpened;
            _windowEvents = _dte.Events.WindowEvents;
            _windowEvents.WindowActivated += OnWindowActivated;
            _debuggerEvents = _dte.Events.DebuggerEvents;
            _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
            try
            {
                var window = (OutputWindow)_dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput).Object;
                _output = window.OutputWindowPanes.Cast<OutputWindowPane>().FirstOrDefault(p => p.Name == "PDB Finder")
                    ?? window.OutputWindowPanes.Add("PDB Finder");
            }
            catch (COMException) { }
            DebugLog("initialized; active=" + SafeName(_dte.ActiveDocument));
            ProcessDocument(_dte.ActiveDocument, false);
        }

        private void OnDocumentOpened(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DebugLog("DocumentOpened: " + SafeName(document));
            ProcessDocument(document, false);
        }

        private void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DebugLog("WindowActivated: " + SafeName(gotFocus?.Document));
            ProcessDocument(gotFocus?.Document, false);
        }

        private static string SafeName(Document document)
        {
            try { return document?.FullName ?? "(null)"; }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TryLoadSymbols();
        }

        private void StartForActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var document = _dte?.ActiveDocument;
            if (document == null || !IsDump(document.FullName))
            {
                MessageBox.Show("请先在 Visual Studio 中打开 .dmp 文件。", "PDB Finder");
                return;
            }
            ProcessDocument(document, true);
        }

        private void ProcessDocument(Document document, bool manual)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed) return;
            string path;
            try { path = document?.FullName; }
            catch (COMException) { return; }
            if (!IsDump(path)) { DebugLog("ProcessDocument skip (not dump): " + (path ?? "(null)")); return; }
            var options = (FinderOptions)GetDialogPage(typeof(FinderOptions));
            if (!manual && !options.AutoMatch) { DebugLog("ProcessDocument skip (automatch off): " + path); return; }
            if (!manual && string.Equals(path, _lastRequestedDump, StringComparison.OrdinalIgnoreCase)
                && _matchSession != null)
            {
                DebugLog("ProcessDocument skip (dup): " + path);
                if (_matchSession.IsMatching || _isStaging || _matchSession.Token.IsCancellationRequested) return;
                if (_activeCandidate != null && _activeModule != null)
                {
                    var storeRoot = GetSymbolCacheDir() ?? Path.Combine(DataDirectory, "pdb-modules");
                    var isLocalSameName = LocalSymbolCache.IsLocalPath(_activeCandidate.PdbPath)
                        && string.Equals(Path.GetFileName(_activeCandidate.PdbPath), _activeModule.ExpectedPdbName, StringComparison.OrdinalIgnoreCase);
                    var ready = isLocalSameName
                        ? File.Exists(_activeCandidate.PdbPath)
                        : LocalSymbolCache.IsStaged(storeRoot, _activeModule);
                    if (isLocalSameName && ready)
                    {
                        try { ready = _activeModule.Pdb.Equals(PdbReader.Read(_activeCandidate.PdbPath)); }
                        catch (Exception) { ready = false; }
                    }
                    if (ready)
                    {
                        if (!string.Equals(_readyHintShownFor, path, StringComparison.OrdinalIgnoreCase))
                        {
                            _readyHintShownFor = path;
                            Log("该 dump 的符号已就绪，可以直接调试。");
                            ShowInfoBar($"PDB Finder：该 dump 的符号已就绪（{_stagedDirectory}），可以直接调试。");
                        }
                    }
                    else
                    {
                        Log("本地符号文件已不存在或失效，重新匹配…");
                    }
                    if (ready) return;
                }
                else return;
            }
            var roots = options.GetShareRoots();
            _matchSession?.Retire();
            CloseStagingForm();
            var session = new MatchSession(path);
            _matchSession = session;
            DebugLog("ProcessDocument matching: " + path);
            _scanCancellation?.Cancel();
            _scanCancellation = null;
            SetProgress(false, null, 0, 0, session);
            _ = JoinableTaskFactory.RunAsync(() => session.RunAsync(() => MatchAsync(roots, manual, session)));
        }

        private async Task MatchAsync(string[] shareRoots, bool manual, MatchSession session)
        {
            var token = session.Token;
            var dumpPath = session.DumpPath;
            IndexProgressForm progressForm = null;
            var entered = false;
            var keepProgressForm = false;
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                progressForm = ShowIndexProgress("正在匹配 " + Path.GetFileName(dumpPath) + "，先检查本地符号，再查询索引和共享目录。", session.Cancel, true);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Log("开始匹配：" + dumpPath);
                SetStatus("正在匹配 " + Path.GetFileName(dumpPath) + "…", session);
                progressForm.BeginReadingDump(dumpPath);
                var module = await Task.Run(() => MinidumpReader.MainExecutable(dumpPath), token);
                if (module?.Pdb == null) throw new InvalidDataException("dump 中没有主程序的 PDB GUID/Age，无法安全自动匹配。");
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                progressForm.BeginLocalLookup(module.ExpectedPdbName);
                var localStores = new[] { GetSymbolCacheDir(), Path.Combine(DataDirectory, "pdb-modules") };
                var local = await Task.Run(() => LocalSymbolCache.FindLocal(module, localStores, Path.GetDirectoryName(dumpPath), token, progressForm.Report), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (local != null)
                {
                    Log($"本地符号直接命中，跳过索引和共享目录查找（耗时 {watch.Elapsed.TotalSeconds:F1} 秒）：{local.PdbPath}");
                    progressForm.Finish("本地符号已就绪。", true);
                    progressForm.Close();
                    progressForm = null;
                    ApplyMatch(module, local, session);
                    return;
                }
                if (shareRoots.Length == 0)
                {
                    var message = "本地没有匹配符号，尚未配置共享根目录，请在 工具→选项→PDB Finder 中添加。";
                    Log(message);
                    SetStatus("PDB Finder: 本地未命中，未配置共享根目录", session);
                    progressForm.Finish(message, false);
                    progressForm.Close();
                    progressForm = null;
                    if (manual) ShowOptionPage(typeof(FinderOptions));
                    else ShowInfoBar("PDB Finder：" + message);
                    return;
                }
                Log("本地符号未命中，继续查询本地索引。");
                progressForm.BeginWaitingForScan();
                // Only archive/index work needs this gate. A local hit need not wait for an old network request.
                await _scanGate.WaitAsync(token);
                entered = true;
                var cache = Path.Combine(DataDirectory, "pdb-index.tsv");
                var roots = shareRoots;
                var finder = await Task.Run(() => new CandidateFinder(cache), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                progressForm.BeginMatching(module.ExpectedPdbName);
                // Find validates index hits within the current roots before enumerating the archive.
                var result = await FindWithProgressAsync(finder, roots, module, session, progressForm);
                token.ThrowIfCancellationRequested();
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!OwnsSession(session)) return;
                if (!string.IsNullOrEmpty(result.Error)) Log(result.Error);
                Log(result.FromIndex
                    ? $"本地索引直接命中，跳过共享目录扫描（耗时 {watch.Elapsed.TotalSeconds:F1} 秒）。"
                    : $"匹配结束：检查 {result.DirectoriesVisited} 个目录、{result.PdbsInspected} 个 PDB，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。");
                // Close before the manual picker or symbol preparation opens its own UI.
                if (progressForm != null)
                {
                    progressForm.Finish(result.Candidate == null ? "仍未找到精确匹配。" : "已找到精确匹配，继续准备符号。", true);
                    progressForm.Close();
                    progressForm = null;
                }
                var candidate = result.Candidate;
                if (candidate == null)
                {
                    Log($"未找到匹配：{module.ExpectedPdbName} ({module.Pdb})。检查 {result.DirectoriesVisited} 个目录、{result.PdbsInspected} 个 PDB。{result.Error}");
                    SetStatus("PDB Finder: 未找到精确匹配", session);
                    if (MessageBox.Show("没有在共享目录中找到精确匹配的 PDB。是否手动选择一个 PDB？", "PDB Finder", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        candidate = SelectPdb(module);
                }
                if (candidate == null) return;
                token.ThrowIfCancellationRequested();
                ApplyMatch(module, candidate, session);
            }
            catch (OperationCanceledException)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (OwnsSession(session))
                {
                    Log("已取消索引构建/匹配，不再重试。");
                    SetStatus("PDB Finder: 已取消索引构建/匹配", session);
                    progressForm?.Finish("本次索引构建/匹配已停止；已保存的索引仍可使用。", false);
                    keepProgressForm = progressForm != null;
                }
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!OwnsSession(session)) return;
                Log("匹配失败：" + ex);
                SetStatus("PDB Finder: 匹配失败", session);
                progressForm?.Finish("构建索引/匹配失败：" + ex.Message, false);
                keepProgressForm = progressForm != null;
                if (manual && progressForm == null) MessageBox.Show(ex.Message, "PDB Finder");
            }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!keepProgressForm) progressForm?.Dispose();
                if (entered) _scanGate.Release();
                session.IsMatching = false;
            }
        }

        private IndexProgressForm ShowIndexProgress(string reason, CancellationTokenSource cancellation, bool matching = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ShowIndexProgress(reason, cancellation.Cancel, matching);
        }

        private IndexProgressForm ShowIndexProgress(string reason, Action cancel, bool matching = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var form = new IndexProgressForm(reason, cancel, matching);
            form.Show();
            return form;
        }

        private Task<FindResult> FindWithProgressAsync(CandidateFinder finder, string[] roots, DumpModule module, MatchSession session, IndexProgressForm form)
            => Task.Run(() => finder.Find(roots, module, session.Token, p =>
            {
                SetStatus("PDB Finder: " + p, session);
                form.ReportMatch(p);
            }, form.Report), session.Token);

        private PdbCandidate SelectPdb(DumpModule module)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (var picker = new OpenFileDialog { Title = "选择与 dump 精确匹配的 PDB", Filter = "PDB files (*.pdb)|*.pdb", CheckFileExists = true })
            {
                if (picker.ShowDialog() != DialogResult.OK) return null;
                PdbIdentity actual;
                try { actual = PdbReader.Read(picker.FileName); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "PDB Finder"); return null; }
                if (!module.Pdb.Equals(actual))
                {
                    MessageBox.Show($"PDB 不匹配。\n需要：{module.Pdb}\n所选：{actual}", "PDB Finder");
                    return null;
                }
                var exe = Path.Combine(Path.GetDirectoryName(picker.FileName), Path.GetFileName(module.Name));
                var verification = File.Exists(exe) ? CandidateFinder.VerifyPe(exe, module) : PeVerification.None;
                return new PdbCandidate(picker.FileName, verification == PeVerification.None ? null : exe, verification);
            }
        }

        private void ApplyMatch(DumpModule module, PdbCandidate candidate, MatchSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            session.Token.ThrowIfCancellationRequested();
            if (!OwnsSession(session)) return;
            var store = SymbolMapping.CreatePointerStore(Path.Combine(DataDirectory, "symbol-store"), module, candidate);
            var pointerPath = "srv*" + store;
            string current;
            try { current = (_dte.Debugger as Debugger5)?.SymbolPath ?? string.Empty; }
            catch (COMException) { current = string.Empty; }
            var segments = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            // SymbolPathState is a string with one '1'/'0' digit per path entry.
            var state = new string('1', segments.Count);
            try
            {
                var read = new ShellSettingsManager(this).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                if (read.CollectionExists("Debugger") && read.PropertyExists("Debugger", "SymbolPathState"))
                {
                    var persisted = read.GetString("Debugger", "SymbolPathState") ?? string.Empty;
                    if (persisted.Length == segments.Count) state = persisted;
                }
            }
            catch (Exception) { /* keep every entry enabled */ }
            if (!string.IsNullOrEmpty(_lastAddedDirectory))
            {
                var index = segments.FindIndex(s => string.Equals(s, _lastAddedDirectory, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) { segments.RemoveAt(index); state = state.Remove(index, 1); }
                _lastAddedDirectory = null;
            }
            if (candidate.HasVerifiedExe && !segments.Any(s => string.Equals(s, candidate.BuildDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                segments.Add(candidate.BuildDirectory);
                state += "1";
                _lastAddedDirectory = candidate.BuildDirectory;
            }
            if (!segments.Any(s => string.Equals(s, pointerPath, StringComparison.OrdinalIgnoreCase)))
            {
                segments.Add(pointerPath);
                state += "1";
            }
            var updated = string.Join(";", segments);
            try
            {
                var settings = new ShellSettingsManager(this).GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!settings.CollectionExists("Debugger")) settings.CreateCollection("Debugger");
                settings.SetString("Debugger", "SymbolPath", updated);
                settings.SetString("Debugger", "SymbolPathState", state);
            }
            catch (Exception ex)
            {
                Log("设置符号路径失败：" + ex.Message);
                try { _dte.ExecuteCommand("Debug.SymbolPath", updated); }
                catch (COMException fallback) { Log("回退命令也失败：" + fallback.Message); }
            }
            _activeCandidate = candidate;
            _activeModule = module;
            _stagedDirectory = null;
            _stagingHintShown = false;
            _readyHintShownFor = null;
            var exeDescription = candidate.ExePath == null ? "目录中无匹配 EXE"
                : candidate.Verification == PeVerification.Exact ? "已精确验证 " + candidate.ExePath
                : "重打包验证（时间戳与 PDB ID 相同、映像大小不同）" + candidate.ExePath;
            Log($"已匹配：{candidate.PdbPath}；EXE：{exeDescription}；dump 预期名：{module.ExpectedPdbName}");
            SetStatus("PDB Finder: 已找到匹配构建 " + Path.GetFileName(candidate.BuildDirectory), session);
            if (candidate.Verification == PeVerification.Repackaged)
                _ = JoinableTaskFactory.RunAsync(() => session.RunAsync(() => OfferExePatchAsync(module, candidate, session)));
            StartStaging(module, candidate, session);
        }

        private async Task OfferExePatchAsync(DumpModule module, PdbCandidate candidate, MatchSession session)
        {
            var token = session.Token;
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var storeRoot = GetSymbolCacheDir() ?? Path.Combine(DataDirectory, "pdb-modules");
                var fullMemory = await Task.Run(() => MinidumpReader.HasFullMemory(session.DumpPath), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!OwnsSession(session)) return;
                if (fullMemory)
                {
                    Log("完整内存 dump 无需二进制文件，跳过 EXE 修补提示。");
                    return;
                }
                var alreadyStaged = await Task.Run(() => LocalSymbolCache.IsExeStaged(storeRoot, module), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!OwnsSession(session)) return;
                if (alreadyStaged)
                {
                    Log("本地已有修补好的 EXE 副本：" + LocalSymbolCache.GetStagedExePath(storeRoot, module));
                    return;
                }
                var pe = await Task.Run(() => PeReader.Read(candidate.ExePath), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!OwnsSession(session)) return;
                if (pe.TimeDateStamp != module.TimeDateStamp || pe.Pdb == null || !pe.Pdb.Equals(module.Pdb))
                {
                    Log("EXE 身份已变化或缺少 PDB 标识，跳过修补。");
                    return;
                }
                var message =
                    $"构建 {Path.GetFileName(candidate.BuildDirectory)} 的 EXE 时间戳及 PDB GUID/Age 与 dump 一致，但 PE 字段不同：\n" +
                    $"映像大小：EXE {pe.SizeOfImage}，dump {module.SizeOfImage}。\n" +
                    $"校验和：EXE 0x{pe.CheckSum:X8}，dump 0x{module.CheckSum:X8}。\n\n" +
                    "是否在本地符号缓存中创建修补副本？\n" +
                    "（仅修改本地副本的映像大小与校验和，共享文件保持原样）";
                var answer = MessageBox.Show(message, "PDB Finder", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                token.ThrowIfCancellationRequested();
                if (!OwnsSession(session)) return;
                if (answer != DialogResult.Yes) { Log("用户选择不创建修补 EXE 副本。"); return; }
                SetStatus("PDB Finder: 正在复制并修补 EXE…", session);
                await Task.Run(() => LocalSymbolCache.StagePatchedExeAsync(storeRoot, module, candidate, token,
                    (copied, total) =>
                    {
                        if (token.IsCancellationRequested) return;
                        SetStatus($"PDB Finder: 正在复制 EXE {copied / 1048576}/{total / 1048576} MB", session);
                        SetProgress(true, "PDB Finder: 复制 EXE", (uint)(copied / 65536), (uint)(total / 65536), session);
                    }), token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (!OwnsSession(session)) return;
                SetProgress(false, null, 0, 0, session);
                Log("已创建修补 EXE 副本：" + LocalSymbolCache.GetStagedExePath(storeRoot, module));
                SetStatus("PDB Finder: 修补 EXE 副本已就绪", session);
                ShowInfoBar($"PDB Finder：已在符号缓存中创建修补 EXE 副本（{LocalSymbolCache.GetStagedExePath(storeRoot, module)}），可用于解析 {Path.GetFileName(module.Name)} 二进制。", session);
                TryLoadSymbols();
            }
            catch (OperationCanceledException)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (OwnsSession(session)) SetProgress(false, null, 0, 0, session);
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!OwnsSession(session)) return;
                SetProgress(false, null, 0, 0, session);
                Log("创建修补 EXE 副本失败：" + ex.Message);
                SetStatus("PDB Finder: 修补 EXE 失败", session);
            }
        }

        private void StartStaging(DumpModule module, PdbCandidate candidate, MatchSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            session.Token.ThrowIfCancellationRequested();
            if (!OwnsSession(session)) return;
            CloseStagingForm();
            var storeRoot = GetSymbolCacheDir() ?? Path.Combine(DataDirectory, "pdb-modules");
            var isLocal = LocalSymbolCache.IsLocalPath(candidate.PdbPath);
            if (isLocal && string.Equals(Path.GetFileName(candidate.PdbPath), module.ExpectedPdbName, StringComparison.OrdinalIgnoreCase))
            {
                _stagedDirectory = candidate.BuildDirectory;
                NotifySymbolsReady(candidate.PdbPath, "本地目录");
                TryLoadSymbols();
                return;
            }
            if (LocalSymbolCache.IsStaged(storeRoot, module))
            {
                _stagedDirectory = LocalSymbolCache.GetStagedDirectory(storeRoot, module);
                NotifySymbolsReady(LocalSymbolCache.GetStagedPdbPath(storeRoot, module), "本地缓存");
                TryLoadSymbols();
                return;
            }
            Log("正在准备本地符号副本 → " + storeRoot);
            SetStatus("PDB Finder: 正在复制符号文件…", session);
            ShowInfoBarCore($"PDB Finder：已匹配构建 {Path.GetFileName(candidate.BuildDirectory)}，PDB 来源 {candidate.PdbPath}；正在准备本地符号文件（可以开始调试，就绪后会自动加载符号）…");
            var token = session.Token;
            _stageCancelledByUser = false;
            var copyForm = new CopyProgressForm(candidate.PdbPath, LocalSymbolCache.GetStagedPdbPath(storeRoot, module), () =>
            {
                if (OwnsSession(session)) session.CancelledByUser = true;
                session.Cancel();
            });
            _stagingForm = copyForm;
            copyForm.Show();
            _isStaging = true;
            _ = JoinableTaskFactory.RunAsync(() => session.RunAsync(async () =>
            {
                try
                {
                    var dir = await Task.Run(() => LocalSymbolCache.StageAsync(storeRoot, module, candidate, token,
                        (copied, total) =>
                        {
                            if (token.IsCancellationRequested) return;
                            SetStatus($"PDB Finder: 正在复制符号文件 {copied / 1048576}/{total / 1048576} MB", session);
                            SetProgress(true, "PDB Finder: 复制符号文件", (uint)(copied / 65536), (uint)(total / 65536), session);
                            copyForm.Report(copied, total);
                        }), token);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (!OwnsSession(session)) return;
                    SetProgress(false, null, 0, 0, session);
                    _stagedDirectory = dir;
                    Log("本地符号副本就绪：" + LocalSymbolCache.GetStagedPdbPath(storeRoot, module));
                    SetStatus("PDB Finder: 本地符号副本就绪", session);
                    copyForm.Finish("复制和校验完成，调试处于中断状态时会尝试自动加载符号。", true);
                    TryLoadSymbols();
                }
                catch (OperationCanceledException)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (OwnsSession(session))
                    {
                        SetProgress(false, null, 0, 0, session);
                        Log("已取消符号复制；可通过 Match Current Dump 手动重新开始。");
                        SetStatus("PDB Finder: 已取消符号复制", session);
                        ShowInfoBar("PDB Finder：符号复制已取消，可通过 Match Current Dump 手动重试。", session);
                        copyForm.Finish("本次复制已停止。需要重试时，请手动匹配当前 dump。", false, true);
                    }
                }
                catch (Exception ex)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!OwnsSession(session)) return;
                    SetProgress(false, null, 0, 0, session);
                    Log("准备本地符号副本失败：" + ex.Message);
                    SetStatus("PDB Finder: 符号副本准备失败", session);
                    ShowInfoBar("PDB Finder：符号复制失败。请查看复制窗口或输出日志后手动重试。", session);
                    copyForm.Finish(ex.Message, false);
                }
                finally
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    session.IsStaging = false;
                }
            }));
        }

        private string GetSymbolCacheDir()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var settings = new ShellSettingsManager(this).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                if (settings.CollectionExists("Debugger") && settings.PropertyExists("Debugger", "SymbolCacheDir"))
                {
                    var dir = settings.GetString("Debugger", "SymbolCacheDir");
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(dir);
                        if (LocalSymbolCache.IsLocalPath(expanded)) return expanded;
                        Log("VS 符号缓存目录不在本机，改用扩展本地缓存：" + expanded);
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private void NotifySymbolsReady(string pdbPath, string source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log($"符号已就绪（{source}）：{pdbPath}");
            SetStatus("PDB Finder: 符号已就绪，可以直接调试", _matchSession);
            ShowInfoBar($"PDB Finder：本地已有该构建的符号文件（{pdbPath}），可以直接调试。");
        }

        private void TryLoadSymbols()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeCandidate == null || _dte?.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return;
            if (_stageCancelledByUser || _matchSession.Token.IsCancellationRequested)
            {
                Log("符号复制已取消，请通过 Match Current Dump 手动重试。");
                return;
            }
            if (_stagedDirectory == null)
            {
                Log("本地符号副本尚未就绪，复制完成后会自动重试加载。");
                if (!_stagingHintShown)
                {
                    _stagingHintShown = true;
                    ShowInfoBar("PDB Finder：本地符号文件还在准备中，就绪后会自动加载；也可以在输出窗口的 PDB Finder 窗格查看进度。");
                }
                return;
            }
            try
            {
                var process = _dte.Debugger.CurrentProcess as Process3;
                if (process == null) return;
                foreach (EnvDTE90.Module module in process.Modules)
                {
                    if (!string.Equals(module.Name, _activeModuleName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(module.SymbolFile)) module.LoadSymbols(_stagedDirectory);
                    if (!string.IsNullOrEmpty(module.SymbolFile))
                    {
                        Log("VS 已加载符号：" + module.SymbolFile);
                        SetStatus("PDB Finder: " + _activeModuleName + " 符号已加载", _matchSession);
                        ShowInfoBar($"PDB Finder：{_activeModuleName} 的符号已加载（{module.SymbolFile}）。详情见输出窗口的 PDB Finder 窗格。");
                    }
                    else Log("VS 尚未加载主模块符号，请查看 Debug > Windows > Modules 的符号状态。");
                    break;
                }
            }
            catch (COMException ex) { Log("VS 加载符号失败：" + ex.Message); }
        }

        internal void TestConnection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var roots = ((FinderOptions)GetDialogPage(typeof(FinderOptions))).GetShareRoots();
            if (roots.Length == 0) { MessageBox.Show("尚未配置共享根目录。", "PDB Finder"); return; }
            SetStatus("PDB Finder: 正在测试连接…");
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                var lines = await Task.Run(() =>
                {
                    var list = new List<string>();
                    foreach (var root in roots)
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            if (!Directory.Exists(root)) { list.Add("不可访问：" + root); continue; }
                            var channels = Directory.GetDirectories(root).Length;
                            sw.Stop();
                            list.Add($"正常：{root}（{channels} 个频道目录，{sw.ElapsedMilliseconds} 毫秒）");
                        }
                        catch (Exception ex) { list.Add("失败：" + root + "：" + ex.Message); }
                    }
                    return string.Join(Environment.NewLine, list);
                });
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                Log("测试连接：" + lines);
                SetStatus("PDB Finder: 连接测试完成");
                MessageBox.Show(lines, "PDB Finder");
            });
        }

        internal void RebuildIndex()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var roots = ((FinderOptions)GetDialogPage(typeof(FinderOptions))).GetShareRoots();
            if (roots.Length == 0) { MessageBox.Show("尚未配置共享根目录。", "PDB Finder"); return; }
            if (_matchSession?.IsMatching == true) _matchSession.Cancel();
            _scanCancellation?.Cancel();
            _scanCancellation = new CancellationTokenSource();
            var cancellation = _scanCancellation;
            var token = cancellation.Token;
            var cache = Path.Combine(DataDirectory, "pdb-index.tsv");
            var form = ShowIndexProgress("正在按配置的共享根目录重新构建 PDB 索引。", cancellation);
            SetStatus("PDB Finder: 正在重建索引…", scan: cancellation);
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                var entered = false;
                try
                {
                    await _scanGate.WaitAsync(token);
                    entered = true;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await Task.Run(() => new CandidateFinder(cache).RebuildIndex(roots, token,
                        p => SetStatus("PDB Finder: " + p, scan: cancellation), form.Report), token);
                    sw.Stop();
                    await JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (!ReferenceEquals(_scanCancellation, cancellation)) { form.Dispose(); return; }
                    var success = result.DirectoriesVisited > 0 && !result.IndexSaveFailed;
                    var message = success
                        ? $"索引已重建：{result.PdbsInspected} 个 PDB、{result.DirectoriesVisited} 个目录，跳过 {result.SkippedDirectories} 个目录，耗时 {sw.Elapsed.TotalSeconds:F1} 秒。{result.Error}"
                        : "重建索引失败：" + result.Error;
                    Log(message);
                    SetStatus("PDB Finder: " + message, scan: cancellation);
                    form.Finish(message, success);
                }
                catch (OperationCanceledException)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ReferenceEquals(_scanCancellation, cancellation))
                    {
                        Log("已取消索引构建。");
                        SetStatus("PDB Finder: 已取消索引构建", scan: cancellation);
                        form.Finish("索引构建已停止；已保存的索引仍可使用。", false);
                    }
                    else form.Dispose();
                }
                catch (Exception ex)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!ReferenceEquals(_scanCancellation, cancellation)) { form.Dispose(); return; }
                    Log("重建索引失败：" + ex.Message);
                    SetStatus("PDB Finder: 重建索引失败", scan: cancellation);
                    form.Finish("重建索引失败：" + ex.Message, false);
                }
                finally
                {
                    if (entered) _scanGate.Release();
                    if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null;
                    cancellation.Dispose();
                }
            });
        }

        private uint _progressCookie;
        private IVsInfoBarUIElement _infoBar;

        private void SetProgress(bool active, string label, uint completed, uint total, MatchSession session = null)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session != null && !OwnsSession(session)) return;
                try
                {
                    var statusbar = (IVsStatusbar)GetGlobalService(typeof(SVsStatusbar));
                    var cookie = _progressCookie;
                    statusbar.Progress(ref cookie, active ? 1 : 0, label, completed, total);
                    _progressCookie = cookie;
                }
                catch (COMException) { }
            });
        }

        private void ShowInfoBar(string message, MatchSession session = null)
        {
            session = session ?? _matchSession;
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session != null && !OwnsSession(session)) return;
                ShowInfoBarCore(message);
            });
        }

        // Returns true when the bar was attached to the dump document.
        private bool ShowInfoBarCore(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var factory = (IVsInfoBarUIFactory)GetGlobalService(typeof(SVsInfoBarUIFactory));
                if (factory == null) return false;
                var model = new InfoBarModel(message, default(Microsoft.VisualStudio.Imaging.Interop.ImageMoniker), true);
                var element = factory.CreateInfoBar(model);
                if (element == null) return false;
                var host = FindInfoBarHost(_lastRequestedDump);
                if (host == null) return false;
                try { _infoBar?.Close(); } catch (COMException) { }
                _infoBar = element;
                host.AddInfoBar(element);
                return true;
            }
            catch (COMException) { return false; }
        }

        private IVsInfoBarHost FindInfoBarHost(string documentPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var uiShell = (IVsUIShell)GetGlobalService(typeof(SVsUIShell));
            if (uiShell == null || uiShell.GetDocumentWindowEnum(out var enumFrames) != 0 || enumFrames == null) return null;
            var fileName = string.IsNullOrEmpty(documentPath) ? null : Path.GetFileName(documentPath);
            var frames = new IVsWindowFrame[1];
            while (enumFrames.Next(1, frames, out var fetched) == 0 && fetched == 1)
            {
                var frame = frames[0];
                if (frame == null) continue;
                if (frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var hostObj) != 0 || !(hostObj is IVsInfoBarHost host)) continue;
                if (string.IsNullOrEmpty(documentPath)) return host;
                // Text documents: match by DocData path.
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData) == 0
                    && docData is IPersistFileFormat persist
                    && persist.GetCurFile(out var mk, out _) == 0
                    && string.Equals(mk, documentPath, StringComparison.OrdinalIgnoreCase))
                    return host;
                // Non-text editors (e.g. the minidump summary page): match by frame caption.
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var captionObj) == 0
                    && captionObj is string caption
                    && string.Equals(caption, fileName, StringComparison.OrdinalIgnoreCase))
                    return host;
            }
            // Never attach to a different document.
            return null;
        }

        private CopyProgressForm _stagingForm;

        private void CloseStagingForm()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Task replacement cancels through its token; disposing avoids invoking the user cancel action again.
            try { _stagingForm?.Dispose(); } catch (Exception) { }
            _stagingForm = null;
        }

        private void SetStatus(string message, MatchSession session = null, CancellationTokenSource scan = null)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session != null && !OwnsSession(session)) return;
                if (scan != null && !ReferenceEquals(_scanCancellation, scan)) return;
                if (_dte != null) _dte.StatusBar.Text = message;
            });
        }

        private void Log(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _output?.OutputString(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
        }

        private static bool IsDump(string path) => !string.IsNullOrEmpty(path) && path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _matchSession?.Retire();
                _matchSession = null;
                _scanCancellation?.Cancel();
                _scanCancellation = null;
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_documentEvents != null) _documentEvents.DocumentOpened -= OnDocumentOpened;
                    if (_windowEvents != null) _windowEvents.WindowActivated -= OnWindowActivated;
                    if (_debuggerEvents != null) _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
                    CloseStagingForm();
                    try { _infoBar?.Close(); } catch (COMException) { }
                });
                if (ReferenceEquals(Instance, this)) Instance = null;
            }
            base.Dispose(disposing);
        }

        internal static void DebugLog(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDB Finder", "package-debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

}
