using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PdbFinder.Core;

namespace PdbFinder.Vsix
{
    internal sealed class IndexProgressForm : Form
    {
        private readonly Label _phase;
        private readonly Label _reason;
        private readonly Label _counts;
        private readonly Label _elapsed;
        private readonly Label _hint;
        private readonly TextBox _path;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Action _cancelAction;
        private readonly object _gate = new object();
        private IndexProgress _pending;
        private string _pendingMatch;
        private bool _finished;
        private bool _cancelRequested;
        private bool _matching;

        public IndexProgressForm(string reason, Action cancelAction, bool matching = false)
        {
            _cancelAction = cancelAction;
            _matching = matching;
            Text = matching ? "PDB Finder — 匹配符号" : "PDB Finder — 构建索引";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            // Also remain visible above VS's modal Options window.
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(650, 345);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _reason = new Label { Text = reason, AutoSize = true, MaximumSize = new Size(610, 0), Margin = new Padding(0, 0, 0, 12) };
            _phase = new Label { Text = matching ? "等待开始匹配…" : "准备构建索引…", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            _bar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Maximum = 1000 };
            _counts = new Label { Text = matching ? "准备读取 dump 构建标识…" : "正在等待扫描开始…", AutoSize = true };
            _path = new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 65, TabStop = false };
            _elapsed = new Label { Text = "已用时 0 秒", AutoSize = true };
            _hint = new Label { Text = matching ? "先检查本地符号，再查询本地索引，最后扫描共享目录。可随时取消。" : "收集完目录后显示扫描百分比。可随时取消。", AutoSize = true, MaximumSize = new Size(610, 0) };
            _cancel = new Button { Text = "取消", AutoSize = true, Anchor = AnchorStyles.Right };
            layout.Controls.Add(_reason, 0, 0);
            layout.Controls.Add(_phase, 0, 1);
            layout.Controls.Add(_bar, 0, 2);
            layout.Controls.Add(_counts, 0, 3);
            layout.Controls.Add(_path, 0, 4);
            layout.Controls.Add(_elapsed, 0, 5);
            layout.Controls.Add(_hint, 0, 6);
            layout.Controls.Add(_cancel, 0, 7);
            for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
            Controls.Add(layout);
            CancelButton = _cancel;
            _cancel.Click += (_, __) => { if (_finished) Close(); else RequestCancellation(); };
            FormClosing += (_, e) =>
            {
                if (_finished || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall) return;
                e.Cancel = true;
                RequestCancellation();
            };
            // Workers only replace a snapshot; one UI timer renders the latest state without flooding VS.
            _timer = new System.Windows.Forms.Timer { Interval = 150 };
            _timer.Tick += (_, __) => RenderPending();
            _timer.Start();
        }

        public void Report(IndexProgress progress)
        {
            lock (_gate) _pending = progress;
        }

        public void ReportMatch(string message)
        {
            lock (_gate) _pendingMatch = message;
        }

        public void BeginMatching(string expectedPdbName)
        {
            RenderPending();
            _matching = true;
            Text = "PDB Finder — 匹配符号";
            _phase.Text = "查询本地索引";
            _bar.Style = ProgressBarStyle.Marquee;
            _counts.Text = "正在按构建标识查找精确匹配…";
            _path.Text = expectedPdbName;
            _hint.Text = "匹配完成后继续准备符号；取消会停止本次匹配。";
        }

        public void BeginReadingDump(string dumpPath)
        {
            _phase.Text = "读取 dump 构建标识";
            _counts.Text = "正在读取主程序的 PDB GUID + Age…";
            _path.Text = dumpPath;
        }

        public void BeginLocalLookup(string expectedPdbName)
        {
            _phase.Text = "检查本地符号";
            _counts.Text = "正在校验本地已有 PDB 的 GUID + Age…";
            _path.Text = expectedPdbName;
            _hint.Text = "本地命中即可加载；未命中时继续查询索引。";
        }

        public void BeginWaitingForScan()
        {
            RenderPending();
            _phase.Text = "等待索引查询";
            _counts.Text = "本地符号未命中，正在等待上一项扫描任务结束…";
            _hint.Text = "上一项网络请求结束后继续；可随时取消。";
        }

        public void BeginIndexing(string reason)
        {
            RenderPending();
            _matching = false;
            Text = "PDB Finder — 构建索引";
            _reason.Text = reason;
            _phase.Text = "准备构建索引…";
            _bar.Style = ProgressBarStyle.Marquee;
            _counts.Text = "正在等待扫描开始…";
            _hint.Text = "收集完目录后显示扫描百分比。可随时取消。";
        }

        public void Finish(string message, bool success)
        {
            if (IsDisposed) return;
            RenderPending();
            _finished = true;
            _timer.Stop();
            _watch.Stop();
            _bar.Style = ProgressBarStyle.Continuous;
            if (success) _bar.Value = _bar.Maximum;
            _phase.Text = success ? (_matching ? "匹配完成" : "索引构建完成")
                : _cancelRequested ? "已取消" : (_matching ? "匹配未完成" : "索引构建未完成");
            _hint.Text = message;
            _hint.ForeColor = success || _cancelRequested ? SystemColors.ControlText : Color.DarkRed;
            _cancel.Text = "关闭";
            _cancel.Enabled = true;
        }

        private void RequestCancellation()
        {
            if (_cancelRequested) return;
            _cancelRequested = true;
            _phase.Text = "正在取消…";
            _cancel.Text = "正在取消…";
            _cancel.Enabled = false;
            _hint.Text = "正在等待当前文件/网络请求结束，随后停止；不会继续重试匹配。";
            _cancelAction();
        }

        private void RenderPending()
        {
            IndexProgress pending;
            string matching;
            lock (_gate)
            {
                pending = _pending;
                matching = _pendingMatch;
                _pending = null;
                _pendingMatch = null;
            }
            _elapsed.Text = $"已用时 {_watch.Elapsed.TotalSeconds:F0} 秒";
            if (_cancelRequested) return;
            if (pending != null)
            {
                _path.Text = pending.CurrentPath ?? string.Empty;
                _counts.Text = $"已完成 {pending.CompletedDirectories}/{pending.TotalDirectories} 个目录，已检查 {pending.PdbsInspected} 个 PDB，跳过 {pending.SkippedDirectories} 个目录";
                switch (pending.Phase)
                {
                    case IndexPhase.CheckingLocalSymbols:
                        _phase.Text = "检查本地符号";
                        _bar.Style = ProgressBarStyle.Marquee;
                        _counts.Text = $"已检查 {pending.CompletedDirectories}/{pending.TotalDirectories} 个本地位置";
                        _hint.Text = "校验本地 PDB 的 GUID + Age；有效命中后跳过索引和共享目录查找。";
                        break;
                    case IndexPhase.CheckingIndex:
                        _phase.Text = "查询本地索引";
                        _bar.Style = ProgressBarStyle.Marquee;
                        _counts.Text = $"已校验 {pending.CompletedDirectories}/{pending.TotalDirectories} 个索引候选";
                        _hint.Text = "只校验构建标识匹配的文件；有效命中后跳过共享目录扫描。";
                        break;
                    case IndexPhase.CollectingDirectories:
                        _phase.Text = _matching ? "按需展开构建目录" : "阶段 1：收集构建目录";
                        _bar.Style = ProgressBarStyle.Marquee;
                        _counts.Text = _matching ? $"已检查 {pending.CompletedDirectories} 个目录、{pending.PdbsInspected} 个 PDB，跳过 {pending.SkippedDirectories} 个目录"
                            : $"已发现 {pending.TotalDirectories} 个构建目录，跳过 {pending.SkippedDirectories} 个目录（总数统计中）";
                        _hint.Text = _matching ? "边遍历边匹配，较旧分支暂缓；无需预先遍历整个归档。" : "正在收集共享目录，总数确定后显示扫描百分比。";
                        break;
                    case IndexPhase.MatchingRecentPdbs:
                    case IndexPhase.MatchingPdbs:
                        _phase.Text = pending.Phase == IndexPhase.MatchingRecentPdbs ? "优先匹配最近目录（每层 30 个）" : "扩大范围：匹配历史目录";
                        _bar.Style = ProgressBarStyle.Marquee;
                        _counts.Text = $"已检查 {pending.CompletedDirectories} 个目录、{pending.PdbsInspected} 个 PDB，跳过 {pending.SkippedDirectories} 个目录";
                        _hint.Text = "命中后停止；未命中逐层扩大范围，直到覆盖配置的全部目录。";
                        break;
                    case IndexPhase.ScanningPdbs:
                        var percent = pending.TotalDirectories == 0 ? 0 : pending.CompletedDirectories * 100.0 / pending.TotalDirectories;
                        _phase.Text = $"阶段 2：扫描 PDB（{percent:F0}%）";
                        _bar.Style = ProgressBarStyle.Continuous;
                        _bar.Value = (int)Math.Min(_bar.Maximum, percent * 10);
                        _hint.Text = "扫描百分比按已完成目录计算；当前路径显示最近处理的目录或 PDB。";
                        break;
                    case IndexPhase.SavingIndex:
                        _phase.Text = "阶段 3：保存索引";
                        _bar.Style = ProgressBarStyle.Marquee;
                        _hint.Text = "扫描完成，正在写入本地索引…";
                        break;
                }
            }
            if (matching != null) _counts.Text = matching;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _phase?.Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
