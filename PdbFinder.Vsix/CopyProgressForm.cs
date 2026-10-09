using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PdbFinder.Vsix
{
    internal sealed class CopyProgressForm : Form
    {
        private readonly Label _phase;
        private readonly Label _counts;
        private readonly Label _elapsed;
        private readonly Label _hint;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _closeTimer;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Action _cancelAction;
        private readonly object _gate = new object();
        private long _pendingCopied;
        private long _pendingTotal;
        private bool _hasPending;
        private long _copied;
        private long _total;
        private bool _finished;
        private bool _cancelRequested;

        public CopyProgressForm(string sourcePath, string targetPath, Action cancelAction)
        {
            _cancelAction = cancelAction;
            Text = "PDB Finder — 复制符号";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(650, 385);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var reason = new Label { Text = "已找到匹配的 PDB，正在准备本地符号副本。", AutoSize = true, MaximumSize = new Size(610, 0), Margin = new Padding(0, 0, 0, 12) };
            _phase = new Label { Text = "准备复制符号文件…", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            _bar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Maximum = 1000 };
            _counts = new Label { Text = "正在读取文件大小…", AutoSize = true };
            var path = new TextBox
            {
                Text = "来源：" + Environment.NewLine + sourcePath + Environment.NewLine + Environment.NewLine + "目标：" + Environment.NewLine + targetPath,
                ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, TabStop = false
            };
            _elapsed = new Label { Text = "已用时 0 秒", AutoSize = true };
            _hint = new Label { Text = "可以开始调试；复制和校验完成后会尝试自动加载符号。", AutoSize = true, MaximumSize = new Size(610, 0) };
            _cancel = new Button { Text = "取消", AutoSize = true, Anchor = AnchorStyles.Right };
            layout.Controls.Add(reason, 0, 0);
            layout.Controls.Add(_phase, 0, 1);
            layout.Controls.Add(_bar, 0, 2);
            layout.Controls.Add(_counts, 0, 3);
            layout.Controls.Add(path, 0, 4);
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
            // Keep worker callbacks lightweight, as in the index progress window.
            _timer = new System.Windows.Forms.Timer { Interval = 150 };
            _timer.Tick += (_, __) => RenderPending();
            _timer.Start();
            _closeTimer = new System.Windows.Forms.Timer { Interval = 1800 };
            _closeTimer.Tick += (_, __) => { _closeTimer.Stop(); Close(); Dispose(); };
        }

        public void Report(long copied, long total)
        {
            lock (_gate)
            {
                _pendingCopied = Math.Max(0, copied);
                _pendingTotal = Math.Max(0, total);
                _hasPending = true;
            }
        }

        public void Finish(string message, bool success, bool cancelled = false)
        {
            if (IsDisposed) return;
            RenderPending();
            _finished = true;
            _timer.Stop();
            _watch.Stop();
            _bar.Style = ProgressBarStyle.Continuous;
            if (success)
            {
                _bar.Value = _bar.Maximum;
                _phase.Text = "本地符号副本已就绪";
                _counts.Text = _total > 0 ? $"已复制 {_total / 1048576.0:F1}/{_total / 1048576.0:F1} MB（100.0%）" : "符号文件已准备好";
                _hint.ForeColor = SystemColors.ControlText;
                _closeTimer.Start();
            }
            else
            {
                _phase.Text = cancelled ? "已取消复制" : "复制失败";
                if (_total <= 0) _counts.Text = "未复制数据";
                _hint.ForeColor = cancelled ? SystemColors.ControlText : Color.DarkRed;
            }
            _hint.Text = message;
            _cancel.Text = "关闭";
            _cancel.Enabled = true;
        }

        private void RequestCancellation()
        {
            if (_cancelRequested) return;
            RenderPending();
            _cancelRequested = true;
            _phase.Text = "正在取消复制…";
            _cancel.Text = "正在取消…";
            _cancel.Enabled = false;
            _hint.Text = "正在等待当前文件/网络请求结束；本次复制不会自动重试。";
            _cancelAction();
        }

        private void RenderPending()
        {
            _elapsed.Text = $"已用时 {_watch.Elapsed.TotalSeconds:F0} 秒";
            if (_cancelRequested || _finished) return;
            lock (_gate)
            {
                if (_hasPending)
                {
                    _copied = _pendingCopied;
                    _total = _pendingTotal;
                    _hasPending = false;
                }
            }
            if (_total <= 0) return;
            var percent = Math.Min(100, _copied * 100.0 / _total);
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)(percent * 10);
            _counts.Text = $"已复制 {_copied / 1048576.0:F1}/{_total / 1048576.0:F1} MB（{percent:F1}%）";
            _phase.Text = _copied >= _total ? "正在校验并保存符号文件…" : $"正在复制符号文件（{percent:F1}%）";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _closeTimer?.Dispose();
                _phase?.Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
