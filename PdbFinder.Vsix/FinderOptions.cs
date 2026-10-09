using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace PdbFinder.Vsix
{
    public sealed class FinderOptions : DialogPage
    {
        private FinderOptionsControl _control;

        public string ShareRoots { get; set; } = string.Empty;
        public bool AutoMatch { get; set; } = true;

        [Browsable(false)]
        protected override System.Windows.Forms.IWin32Window Window => _control ?? (_control = new FinderOptionsControl(this));

        protected override void OnActivate(CancelEventArgs e)
        {
            base.OnActivate(e);
            _control?.LoadFromPage();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            _control?.ApplyToPage();
            base.OnApply(e);
        }

        public string[] GetShareRoots() => (ShareRoots ?? string.Empty)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length != 0)
            .ToArray();

        internal void SetShareRoots(string[] roots) => ShareRoots = string.Join(";", roots ?? new string[0]);

        internal void TestConnectionNow() => PdbFinderPackage.Instance?.TestConnection();
        internal void RebuildIndexNow() => PdbFinderPackage.Instance?.RebuildIndex();
    }
}
