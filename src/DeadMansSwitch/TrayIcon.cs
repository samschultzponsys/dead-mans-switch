using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace DeadMansSwitch
{
    /// <summary>
    /// The notification-area icon (WPF has none, so this wraps WinForms'
    /// NotifyIcon). Left-click opens the window; right-click shows the menu.
    /// </summary>
    sealed class TrayIcon : IDisposable
    {
        readonly NotifyIcon icon;
        readonly ToolStripMenuItem pauseItem;

        public TrayIcon(Action open, Action imHere, Action togglePause, Action quit)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Open", null, (s, e) => open()) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add("I'm here (reset timer)", null, (s, e) => imHere());
            pauseItem = new ToolStripMenuItem("Pause", null, (s, e) => togglePause());
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Stop timer and quit", null, (s, e) => quit());

            icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Dead Man's Switch",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) open(); };
        }

        static Icon LoadIcon()
        {
            var buffer = new MemoryStream();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeadMansSwitch.icon.ico"))
                stream.CopyTo(buffer);
            buffer.Position = 0;
            return new Icon(buffer, SystemInformation.SmallIconSize);
        }

        /// <summary>Hover text; Windows allows at most 63 characters.</summary>
        public void SetText(string text)
        {
            if (text.Length > 63) text = text.Substring(0, 63);
            if (icon.Text != text) icon.Text = text;
        }

        public void SetPause(bool paused, bool enabled)
        {
            pauseItem.Text = paused ? "Resume" : "Pause";
            pauseItem.Enabled = enabled;
        }

        public void ShowHint(string title, string text) => icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

        public void Dispose()
        {
            icon.Visible = false;   // otherwise the icon lingers until the mouse passes over it
            icon.Dispose();
        }
    }
}
