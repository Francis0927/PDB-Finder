using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PdbFinder.Core
{
    public enum PeVerification
    {
        None,
        // Name, timestamp, image size, checksum and PDB identity all match.
        Exact,
        // Name, timestamp and PDB identity match, but image size/checksum differ.
        // Seen on builds that were re-packaged (re-signed/protected) after linking.
        Repackaged
    }

    public sealed class PdbCandidate
    {
        public PdbCandidate(string pdbPath, string exePath, PeVerification verification = PeVerification.None)
        {
            PdbPath = pdbPath;
            ExePath = exePath;
            Verification = exePath == null ? PeVerification.None : verification;
        }
        public string PdbPath { get; }
        public string ExePath { get; }
        public PeVerification Verification { get; }
        public string BuildDirectory => Path.GetDirectoryName(PdbPath);
        public bool HasVerifiedExe => ExePath != null;
    }

    public sealed class FindResult
    {
        public PdbCandidate Candidate { get; internal set; }
        public int DirectoriesVisited { get; internal set; }
        public int PdbsInspected { get; internal set; }
        public int SkippedDirectories { get; internal set; }
        public string Error { get; internal set; }
        public bool FromIndex { get; internal set; }
        public bool IndexSaveFailed { get; internal set; }
    }

    public sealed class CandidateFinder
    {
        private const int RecentDirectoryLimit = 30;

        private sealed class CachedPdb
        {
            public long Length;
            public long LastWriteTicks;
            public PdbIdentity Identity;
        }

        private readonly string _cachePath;
        private readonly Dictionary<string, CachedPdb> _cache = new Dictionary<string, CachedPdb>(StringComparer.OrdinalIgnoreCase);
        private string[] _cachedRoots;
        private string[] _lastRoots;
        private bool _dirty;
        private string _cacheWarning;

        public CandidateFinder(string cachePath)
        {
            _cachePath = cachePath;
            LoadCache();
        }

        public FindResult Find(string root, DumpModule module, CancellationToken cancellationToken, Action<string> progress = null,
            Action<IndexProgress> indexProgress = null)
            => Find(new[] { root }, module, cancellationToken, progress, indexProgress);

        public FindResult Find(IReadOnlyList<string> roots, DumpModule module, CancellationToken cancellationToken, Action<string> progress = null,
            Action<IndexProgress> indexProgress = null)
        {
            if (module == null || module.Pdb == null) throw new ArgumentException("The dump module has no RSDS PDB identity.", nameof(module));
            if (roots == null || roots.Count == 0) return new FindResult { Error = "未配置共享根目录。" };
            _lastRoots = roots.ToArray();
            var result = new FindResult { Error = _cacheWarning };
            PdbCandidate pdbOnly = null;
            PdbCandidate withExe = null;
            var gate = new object();
            var exeName = Path.GetFileName(module.Name);
            var pending = new Stack<string>();
            var deferred = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var indexed = FindIndexed(roots, module, result, cancellationToken, indexProgress);
                if (indexed != null)
                {
                    result.Candidate = indexed;
                    result.FromIndex = true;
                    return result;
                }
                progress?.Invoke($"本地索引未命中，优先匹配每层最近 {RecentDirectoryLimit} 个目录…");
                foreach (var root in roots.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    indexProgress?.Invoke(new IndexProgress(IndexPhase.CollectingDirectories, 0, pending.Count, result.PdbsInspected, result.SkippedDirectories, root));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root)) { errors.Add(root); continue; }
                    var fullRoot = Path.GetFullPath(root);
                    pending.Push(fullRoot.Length > Path.GetPathRoot(fullRoot).Length
                        ? fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullRoot);
                }
                if (errors.Count != 0) AddError(result, "部分共享目录不可访问：" + string.Join("；", errors));
                if (pending.Count == 0)
                {
                    AddError(result, errors.Count != 0 ? "共享目录不可访问：" + string.Join("；", errors) : "共享根目录中没有可扫描的构建目录。");
                    return result;
                }
                var inspected = result.PdbsInspected;
                var visited = 0;
                var skipped = result.SkippedDirectories;
                var progressGate = new object();
                var history = false;
                var total = 0;
                Action<string> report = path =>
                {
                    lock (progressGate)
                        indexProgress?.Invoke(new IndexProgress(history ? IndexPhase.MatchingPdbs : IndexPhase.MatchingRecentPdbs,
                            Volatile.Read(ref visited), total, Volatile.Read(ref inspected), Volatile.Read(ref skipped), path));
                };
                while (pending.Count != 0 || deferred.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pending.Count == 0)
                    {
                        if (!history) progress?.Invoke("近期目录未命中，逐层扩大到历史目录…");
                        history = true;
                    }
                    // Keep older siblings deferred until recent branches have been exhausted.
                    var next = pending.Count != 0 ? pending : deferred;
                    var directories = new List<string>();
                    while (directories.Count < RecentDirectoryLimit && next.Count != 0)
                    {
                        var dir = next.Pop();
                        if (seen.Add(dir)) directories.Add(dir);
                    }
                    total = visited + directories.Count;
                    skipped = result.SkippedDirectories;
                    report(null);
                    Parallel.ForEach(directories, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 }, (dir, state) =>
                    {
                        if (state.ShouldExitCurrentIteration) return;
                        try
                        {
                            report(dir);
                            cancellationToken.ThrowIfCancellationRequested();
                            string[] files;
                            try { files = Directory.GetFiles(dir); }
                            catch (IOException) { Interlocked.Increment(ref skipped); return; }
                            catch (UnauthorizedAccessException) { Interlocked.Increment(ref skipped); return; }

                            var pdbs = files.Where(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (pdbs.Length == 0) return;
                            var exe = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), exeName, StringComparison.OrdinalIgnoreCase));
                            foreach (var pdb in pdbs)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                report(pdb);
                                Interlocked.Increment(ref inspected);
                                PdbIdentity identity;
                                // Refresh identities even when replacements kept the old file size/time.
                                try { identity = ReadCached(pdb, true); }
                                catch (IOException) { continue; }
                                catch (UnauthorizedAccessException) { continue; }
                                catch (InvalidDataException) { continue; }
                                if (!module.Pdb.Equals(identity)) continue;
                                var verification = exe == null ? PeVerification.None : VerifyPe(exe, module);
                                var candidate = new PdbCandidate(pdb, verification == PeVerification.None ? null : exe, verification);
                                if (verification != PeVerification.None)
                                {
                                    lock (gate) { if (withExe == null || (withExe.Verification != PeVerification.Exact && verification == PeVerification.Exact)) withExe = candidate; }
                                    state.Stop();
                                    return;
                                }
                                lock (gate) { if (pdbOnly == null) pdbOnly = candidate; }
                            }
                        }
                        finally
                        {
                            var n = Interlocked.Increment(ref visited);
                            report(dir);
                            if (n % 25 == 0 || n == total)
                                progress?.Invoke($"已检查 {n} 个目录、{Volatile.Read(ref inspected)} 个 PDB");
                        }
                    });
                    result.DirectoriesVisited = visited;
                    result.PdbsInspected = inspected;
                    result.SkippedDirectories = skipped;
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Candidate = withExe ?? pdbOnly;
                    // GUID+Age suffices; do not walk history just to find a duplicate with an EXE.
                    if (result.Candidate != null) return result;

                    // Expand only after matching this batch, so a recent hit never walks old subtrees.
                    foreach (var dir in directories.AsEnumerable().Reverse())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        indexProgress?.Invoke(new IndexProgress(IndexPhase.CollectingDirectories, visited, seen.Count,
                            inspected, result.SkippedDirectories, dir));
                        cancellationToken.ThrowIfCancellationRequested();
                        var children = GetSearchChildren(dir, result, cancellationToken);
                        for (var i = children.Length - 1; i >= 0; i--)
                        {
                            if (i < RecentDirectoryLimit) pending.Push(children[i]);
                            else deferred.Push(children[i]);
                        }
                    }
                }
                return result;
            }
            finally { if (!cancellationToken.IsCancellationRequested) TrySaveCache(result, cancellationToken); }
        }

        private static string[] GetSearchChildren(string directory, FindResult result, CancellationToken cancellationToken)
        {
            try
            {
                var children = new List<DirectoryInfo>();
                // DirectoryInfo reuses enumeration metadata rather than querying each child over SMB.
                foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { result.SkippedDirectories++; continue; }
                    children.Add(child);
                }
                // shortcut: directory times estimate recency; use a build manifest if backups rewrite/preserve them.
                return children.OrderByDescending(child => string.Equals(child.Name, "execute", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(child => child.LastWriteTimeUtc)
                    .ThenByDescending(child => child.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => child.FullName).ToArray();
            }
            catch (IOException) { result.SkippedDirectories++; }
            catch (UnauthorizedAccessException) { result.SkippedDirectories++; }
            return new string[0];
        }

        private PdbCandidate FindIndexed(IReadOnlyList<string> roots, DumpModule module, FindResult result,
            CancellationToken cancellationToken, Action<IndexProgress> progress)
        {
            progress?.Invoke(new IndexProgress(IndexPhase.CheckingIndex, 0, 0, 0, 0, null));
            // Scope old indexes to the current roots, including when the user narrows a root.
            var prefixes = roots.Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar).ToArray();
            string[] paths;
            lock (_cache)
                paths = _cache.Where(pair => module.Pdb.Equals(pair.Value.Identity)
                    && prefixes.Any(prefix => Path.GetFullPath(pair.Key).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(pair => pair.Key).OrderByDescending(path => Path.GetFileName(Path.GetDirectoryName(path)), StringComparer.OrdinalIgnoreCase).ToArray();
            PdbCandidate pdbOnly = null;
            PdbCandidate withExe = null;
            for (var i = 0; i < paths.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[i];
                progress?.Invoke(new IndexProgress(IndexPhase.CheckingIndex, i, paths.Length, result.PdbsInspected, 0, path));
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    result.PdbsInspected++;
                    // Read only the matching candidates' identity: removed/replaced files must never be accepted from stale metadata.
                    if (!module.Pdb.Equals(ReadCached(path, true))) continue;
                    var exe = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(module.Name));
                    var verification = VerifyPe(exe, module);
                    var candidate = new PdbCandidate(path, verification == PeVerification.None ? null : exe, verification);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (verification == PeVerification.Exact) return candidate;
                    if (verification == PeVerification.Repackaged) withExe = withExe ?? candidate;
                    else pdbOnly = pdbOnly ?? candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return withExe ?? pdbOnly;
        }

        public static PeVerification VerifyPe(string path, DumpModule module)
        {
            PeIdentity pe;
            try { pe = PeReader.Read(path); }
            catch (IOException) { return PeVerification.None; }
            catch (UnauthorizedAccessException) { return PeVerification.None; }
            catch (InvalidDataException) { return PeVerification.None; }
            if (pe.TimeDateStamp != module.TimeDateStamp) return PeVerification.None;
            if (module.Pdb == null || pe.Pdb == null || !pe.Pdb.Equals(module.Pdb)) return PeVerification.None;
            if (pe.SizeOfImage == module.SizeOfImage && (module.CheckSum == 0 || pe.CheckSum == module.CheckSum))
                return PeVerification.Exact;
            return PeVerification.Repackaged;
        }

        // True when the on-disk index is missing or was built with different roots.
        public bool NeedsRebuild(IReadOnlyList<string> roots)
        {
            if (!File.Exists(_cachePath) || _cachedRoots == null || _cachedRoots.Length == 0) return true;
            var current = new HashSet<string>(roots ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return !current.SetEquals(_cachedRoots);
        }

        private static void CollectDirectories(string root, List<string> directories, FindResult result, CancellationToken cancellationToken,
            Action<IndexProgress> indexProgress = null)
        {
            // Archive layouts can be mixed, and a build can have nested symbol folders.
            // Collect all directories; Find prioritizes execute/build paths afterwards.
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(Path.GetFullPath(root));
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                if (!seen.Add(dir)) continue;
                directories.Add(dir);
                indexProgress?.Invoke(new IndexProgress(IndexPhase.CollectingDirectories, 0, directories.Count, 0, result.SkippedDirectories, dir));
                try
                {
                    foreach (var child in Directory.GetDirectories(dir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Do not follow junction cycles or links outside the configured archive.
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { result.SkippedDirectories++; continue; }
                            pending.Push(child);
                        }
                        catch (IOException) { result.SkippedDirectories++; }
                        catch (UnauthorizedAccessException) { result.SkippedDirectories++; }
                    }
                }
                catch (IOException) { result.SkippedDirectories++; }
                catch (UnauthorizedAccessException) { result.SkippedDirectories++; }
            }
        }

        public FindResult RebuildIndex(string root, CancellationToken cancellationToken, Action<string> progress = null,
            Action<IndexProgress> indexProgress = null)
            => RebuildIndex(new[] { root }, cancellationToken, progress, indexProgress);

        public FindResult RebuildIndex(IReadOnlyList<string> roots, CancellationToken cancellationToken, Action<string> progress = null,
            Action<IndexProgress> indexProgress = null)
        {
            if (roots == null || roots.Count == 0) return new FindResult { Error = "未配置共享根目录。" };
            _lastRoots = roots.ToArray();
            var result = new FindResult { Error = _cacheWarning };
            var directories = new List<string>();
            var errors = new List<string>();
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                indexProgress?.Invoke(new IndexProgress(IndexPhase.CollectingDirectories, 0, directories.Count, 0, result.SkippedDirectories, root));
                if (!Directory.Exists(root)) { errors.Add(root); continue; }
                CollectDirectories(root, directories, result, cancellationToken, indexProgress);
            }
            if (errors.Count != 0) AddError(result, "部分共享目录不可访问：" + string.Join("；", errors));
            directories = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase).ToList();
            if (directories.Count == 0)
            {
                AddError(result, errors.Count != 0 ? "共享目录不可访问：" + string.Join("；", errors) : "共享根目录中没有可扫描的构建目录。");
                return result;
            }
            var inspected = 0;
            var visited = 0;
            var skipped = result.SkippedDirectories;
            var progressGate = new object();
            Action<string> report = path =>
            {
                // Serialize snapshots so concurrent workers cannot move displayed counts backwards.
                lock (progressGate)
                    indexProgress?.Invoke(new IndexProgress(IndexPhase.ScanningPdbs, Volatile.Read(ref visited),
                        directories.Count, Volatile.Read(ref inspected), Volatile.Read(ref skipped), path));
            };
            report(null);
            Parallel.ForEach(directories, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 }, dir =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    report(dir);
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.pdb"); }
                    catch (IOException) { Interlocked.Increment(ref skipped); return; }
                    catch (UnauthorizedAccessException) { Interlocked.Increment(ref skipped); return; }
                    foreach (var pdb in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        report(pdb);
                        try { ReadCached(pdb, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (InvalidDataException) { }
                        Interlocked.Increment(ref inspected);
                    }
                }
                finally
                {
                    var n = Interlocked.Increment(ref visited);
                    report(dir);
                    if (n % 25 == 0 || n == directories.Count)
                        progress?.Invoke($"已刷新 {n}/{directories.Count} 个目录、{Volatile.Read(ref inspected)} 个 PDB");
                }
            });
            result.DirectoriesVisited = visited;
            result.PdbsInspected = inspected;
            result.SkippedDirectories = skipped;
            cancellationToken.ThrowIfCancellationRequested();
            indexProgress?.Invoke(new IndexProgress(IndexPhase.SavingIndex, visited, directories.Count, inspected, skipped, _cachePath));
            // Only publish after a complete scan. Cancellation keeps the previous disk index intact.
            _dirty = true; // Persist root metadata even when the archive has no readable PDBs.
            TrySaveCache(result, cancellationToken);
            return result;
        }

        private PdbIdentity ReadCached(string path, bool forceRefresh = false)
        {
            var info = new FileInfo(path);
            if (!forceRefresh)
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(path, out var entry) && entry.Length == info.Length && entry.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                        return entry.Identity;
                }
            }
            var identity = PdbReader.Read(path);
            lock (_cache)
            {
                _cache[path] = new CachedPdb { Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Identity = identity };
                _dirty = true;
            }
            return identity;
        }

        private void LoadCache()
        {
            if (!File.Exists(_cachePath)) return;
            try
            {
                foreach (var line in File.ReadLines(_cachePath))
                {
                    try
                    {
                        if (line.StartsWith("#roots=", StringComparison.Ordinal))
                        {
                            _cachedRoots = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(7))).Split('|');
                            continue;
                        }
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length != 5) { _cacheWarning = "索引包含无效记录，已跳过。"; continue; }
                        var path = Path.GetFullPath(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])));
                        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0
                            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks < 0
                            || !Guid.TryParse(parts[3], out var guid)
                            || !uint.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
                        { _cacheWarning = "索引包含无效记录，已跳过。"; continue; }
                        _cache[path] = new CachedPdb { Length = length, LastWriteTicks = ticks, Identity = new PdbIdentity(guid, age) };
                    }
                    catch (ArgumentException) { _cacheWarning = "索引包含无效记录，已跳过。"; }
                    catch (FormatException) { _cacheWarning = "索引包含无效记录，已跳过。"; }
                    catch (NotSupportedException) { _cacheWarning = "索引包含无效记录，已跳过。"; }
                }
            }
            catch (IOException ex) { IgnoreUnreadableCache(ex); }
            catch (UnauthorizedAccessException ex) { IgnoreUnreadableCache(ex); }
            catch (System.Security.SecurityException ex) { IgnoreUnreadableCache(ex); }
        }

        private void IgnoreUnreadableCache(Exception exception)
        {
            _cache.Clear();
            _cachedRoots = null;
            _cacheWarning = "索引读取失败，已降级为共享目录扫描：" + exception.Message;
        }

        private static void AddError(FindResult result, string message)
            => result.Error = string.IsNullOrEmpty(result.Error) ? message : result.Error + "；" + message;

        private void TrySaveCache(FindResult result, CancellationToken token)
        {
            try { SaveCache(token); }
            catch (IOException ex) { ReportCacheSaveFailure(result, ex); }
            catch (UnauthorizedAccessException ex) { ReportCacheSaveFailure(result, ex); }
            catch (System.Security.SecurityException ex) { ReportCacheSaveFailure(result, ex); }
        }

        private static void ReportCacheSaveFailure(FindResult result, Exception exception)
        {
            result.IndexSaveFailed = true;
            AddError(result, "索引保存失败，匹配结果仍可使用：" + exception.Message);
        }

        private void SaveCache(CancellationToken cancellationToken = default)
        {
            if (!_dirty) return;
            var cacheDirectory = Path.GetDirectoryName(Path.GetFullPath(_cachePath));
            Directory.CreateDirectory(cacheDirectory);
            var temp = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var lines = new List<string>();
            if (_lastRoots != null)
                lines.Add("#roots=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("|", _lastRoots))));
            foreach (var pair in _cache)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "\t" +
                    pair.Value.Length.ToString(CultureInfo.InvariantCulture) + "\t" +
                    pair.Value.LastWriteTicks.ToString(CultureInfo.InvariantCulture) + "\t" +
                    pair.Value.Identity.Guid.ToString("D") + "\t" +
                    pair.Value.Identity.Age.ToString(CultureInfo.InvariantCulture));
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllLines(temp, lines);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_cachePath)) File.Replace(temp, _cachePath, null);
                else File.Move(temp, _cachePath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _cachedRoots = _lastRoots;
            _dirty = false;
        }
    }
}
