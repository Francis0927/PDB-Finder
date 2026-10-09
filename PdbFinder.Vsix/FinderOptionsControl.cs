using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PdbFinder.Vsix
{
    // List-style editor for the shared root directories, mirroring the
    // Debugging > Symbols page: one root per row with add/browse/remove buttons.
    public sealed class FinderOptionsControl : UserControl
    {
        private readonly FinderOptions _page;
        private readonly ListBox _list;
        private readonly TextBox _pathText;
        private readonly CheckBox _autoMatch;

        public FinderOptionsControl(FinderOptions page)
        {
            _page = page;
            Dock = DockStyle.Fill;
            var title = new Label { Text = "共享根目录（每个一行）：", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 2) };

            _list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false, SelectionMode = SelectionMode.One };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(4) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _pathText = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
            var addButton = new Button { Text = "添加(A)", AutoSize = true, Margin = new Padding(2) };
            var browseButton = new Button { Text = "浏览…", AutoSize = true, Margin = new Padding(2) };
            var removeButton = new Button { Text = "移除(R)", AutoSize = true, Margin = new Padding(2) };
            bottom.Controls.Add(_pathText, 0, 0);
            bottom.Controls.Add(addButton, 1, 0);
            bottom.Controls.Add(browseButton, 2, 0);
            bottom.Controls.Add(removeButton, 3, 0);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 2, 4, 4), WrapContents = false };
            _autoMatch = new CheckBox { Text = "打开 .dmp 时自动匹配", AutoSize = true, Margin = new Padding(4, 6, 16, 2) };
            var testButton = new Button { Text = "测试连接(T)", AutoSize = true, Margin = new Padding(2) };
            var rebuildButton = new Button { Text = "重建索引(B)", AutoSize = true, Margin = new Padding(2) };
            actions.Controls.Add(_autoMatch);
            actions.Controls.Add(testButton);
            actions.Controls.Add(rebuildButton);

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(actions);
            Controls.Add(title);

            addButton.Click += (_, __) => AddPath(_pathText.Text);
            _pathText.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddPath(_pathText.Text); e.SuppressKeyPress = true; } };
            browseButton.Click += (_, __) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择共享根目录", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddPath(dialog.SelectedPath);
            };
            removeButton.Click += (_, __) =>
            {
                var index = _list.SelectedIndex;
                if (index < 0) return;
                _list.Items.RemoveAt(index);
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
            };
            testButton.Click += (_, __) => { ApplyToPage(); _page.TestConnectionNow(); };
            rebuildButton.Click += (_, __) => { ApplyToPage(); _page.RebuildIndexNow(); };
        }

        public void LoadFromPage()
        {
            _list.Items.Clear();
            foreach (var root in _page.GetShareRoots()) _list.Items.Add(root);
            _autoMatch.Checked = _page.AutoMatch;
        }

        public void ApplyToPage()
        {
            _page.SetShareRoots(_list.Items.Cast<string>().ToArray());
            _page.AutoMatch = _autoMatch.Checked;
        }

        private void AddPath(string path)
        {
            path = (path ?? string.Empty).Trim().Trim('"');
            if (path.Length == 0) return;
            foreach (var item in _list.Items.Cast<string>())
                if (string.Equals(item, path, StringComparison.OrdinalIgnoreCase)) { _list.SelectedItem = item; return; }
            _list.Items.Add(path);
            _list.SelectedIndex = _list.Items.Count - 1;
            _pathText.Clear();
        }
    }
}
