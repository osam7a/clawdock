using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clawdock
{
    // The icon in the notification area: the one place Clawdock is always reachable,
    // even when the notch is hidden.
    class TrayIcon : IDisposable
    {
        public event Action OpenSettings, AskClaude, RepairHooks, Quit;

        readonly NotifyIcon icon;
        readonly Icon image;

        public TrayIcon()
        {
            image = DrawMascot();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Settings…", null, (s, e) => Raise(OpenSettings));
            menu.Items.Add("Type to Claude (Ctrl+Alt+Space)", null, (s, e) => Raise(AskClaude));
            menu.Items.Add("Repair Claude Code hooks", null, (s, e) => Raise(RepairHooks));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit Clawdock", null, (s, e) => Raise(Quit));

            icon = new NotifyIcon { Icon = image, Text = "Clawdock", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Raise(OpenSettings); };
        }

        public void SetStatus(string status)
        {
            string text = "Clawdock · " + status;
            if (text.Length > 63) text = text.Substring(0, 62) + "…"; // the tooltip's limit
            if (icon.Text != text) icon.Text = text;
        }

        public void Balloon(string message)
        {
            icon.ShowBalloonTip(4000, "Clawdock", message, ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            image.Dispose();
        }

        static void Raise(Action a) { if (a != null) a(); }

        // The mascot's body, drawn in clay on a transparent 32x32 square.
        static Icon DrawMascot()
        {
            string[] body =
            {
                "..##########..",
                "..##.####.##..",
                "##############",
                "##############",
                "..##########..",
                "..#.#....#.#..",
            };
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var clay = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57)))
                {
                    g.Clear(Color.Transparent);
                    const int px = 2, ox = 2, oy = 4;
                    for (int r = 0; r < body.Length; r++)
                        for (int c = 0; c < body[r].Length; c++)
                            if (body[r][c] == '#') g.FillRectangle(clay, ox + c * px, oy + r * px * 2, px, px * 2);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
