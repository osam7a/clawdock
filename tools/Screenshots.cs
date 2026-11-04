using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WBrushes = System.Windows.Media.Brushes;
using WColor = System.Windows.Media.Color;
using WBrush = System.Windows.Media.SolidColorBrush;

namespace Clawdock.Tools
{
    // Regenerates docs/screenshots: build.ps1 -Screenshots. Puts the real notch on the real screen with
    // sample data, in front of a plain backdrop that hides your desktop, and captures it.
    static class Screenshots
    {
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(0xEC, 0xE9, 0xE1);
        static string outDir;

        static void Pump(int ms)
        {
            var f = new DispatcherFrame();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); f.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(f);
        }

        static Bitmap Screen()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var bmp = new Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            return bmp;
        }

        // The screen cropped to whatever isn't backdrop, with a margin.
        static void Save(string name)
        {
            using (var bmp = Screen())
            {
                int l = bmp.Width, t = bmp.Height, r = -1, bottom = -1;
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var px = new int[bmp.Width * bmp.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
                int back = Back.ToArgb();
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if ((px[y * bmp.Width + x] & 0xFFFFFF) != (back & 0xFFFFFF)) { if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > bottom) bottom = y; }
                if (r < 0) { Console.WriteLine("EMPTY " + name); return; }
                int m = 28;
                var area = Rectangle.Intersect(new Rectangle(l - m, t - m, r - l + 1 + 2 * m, bottom - t + 1 + 2 * m), new Rectangle(0, 0, bmp.Width, bmp.Height));
                using (var crop = bmp.Clone(area, bmp.PixelFormat)) crop.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
                Console.WriteLine(name + " " + area.Width + "x" + area.Height);
            }
        }

        static Settings Config(string style = "classic", string edge = "top", string size = "medium", string open = "medium")
        {
            return new Settings { Transient = true, Style = style, Edge = edge, NotchSizeCollapsed = size, NotchSizeOpen = open };
        }

        static void Shot(string name, Settings settings, NotchState state, bool hover = false, bool details = false, int wait = 900)
        {
            var notch = new NotchWindow(settings);
            notch.Show();
            notch.Render(state);
            if (hover || details)
            {
                typeof(NotchWindow).GetField("hover", Any).SetValue(notch, true);
                typeof(NotchWindow).GetMethod("Refresh", Any).Invoke(notch, null);
            }
            if (details) typeof(NotchWindow).GetMethod("ToggleDetails", Any).Invoke(notch, null);
            Pump(wait);
            Save(name);
            notch.Close();
            Pump(120);
        }

        static List<SessionInfo> Sessions()
        {
            var t = TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(12));
            return new List<SessionInfo>
            {
                new SessionInfo { Id = "a", Name = "Checkout flow", Cwd = @"C:\code\shop", Desktop = true, Busy = true, Done = 2, Total = 5, Elapsed = t },
                new SessionInfo { Id = "b", Name = "API refactor", Cwd = @"C:\code\api", Busy = true, Done = 0, Total = 3, Elapsed = t + TimeSpan.FromMinutes(7) },
                new SessionInfo { Id = "c", Name = "Docs cleanup", Cwd = @"C:\code\docs", Desktop = true, Updated = DateTime.Now.AddMinutes(-9) },
            };
        }

        static NotchState Working()
        {
            return new NotchState { Visible = true, Kind = "session", Title = "Checkout flow", Busy = true, Done = 2, Total = 5, Detail = "Working 4:12 · 2 done · 3 left", Short = "4:12 · 2/5", Mood = MascotMood.Working, Sessions = Sessions() };
        }

        static NotchState Alert(HookServer.Pending p, string title, string detail)
        {
            return new NotchState { Visible = true, Kind = "alert", Title = title, Detail = detail, Pending = p, Owner = p, Choices = new List<string> { "Deny", "Allow" }, Primary = 1, Mood = MascotMood.Alert, Sessions = Sessions() };
        }

        static HookServer.Pending Push()
        {
            return new HookServer.Pending { Tool = "Bash", Detail = "Bash: git push origin main", FullDetail = "Command: git push origin main\n\nDescription: Push the checkout commits\n\nFolder: C:\\code\\shop" };
        }

        static NotchState Quick(string heard, string answer, QuickTurn before = null)
        {
            var st = new NotchState { Visible = true, Kind = "quick", Title = "Claude", QuickCanClear = true };
            if (before != null) st.QuickHistory.Add(before);
            st.QuickNow = new QuickTurn { Heard = heard, Text = QuickVoice.Display(answer), Card = QuickVoice.ParseCard(answer) };
            return st;
        }

        static void Moods()
        {
            var moods = (MascotMood[])Enum.GetValues(typeof(MascotMood));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Background = WBrushes.Black };
            foreach (var mood in moods)
            {
                var m = new Mascot(6) { Mood = mood };
                typeof(Mascot).GetField("tick", Any).SetValue(m, mood == MascotMood.Sleeping ? 60 : mood == MascotMood.Thinking ? 66 : 52);
                typeof(Mascot).GetMethod("Step", Any).Invoke(m, null);
                var cell = new System.Windows.Controls.Canvas { Width = 250, Height = 130 };
                System.Windows.Controls.Canvas.SetLeft(m, 82);
                System.Windows.Controls.Canvas.SetTop(m, 34);
                cell.Children.Add(m);
                var label = new System.Windows.Controls.TextBlock { Text = mood.ToString(), Foreground = new WBrush(WColor.FromRgb(0xCC, 0xCC, 0xCC)), Width = 250, TextAlignment = TextAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 15 };
                System.Windows.Controls.Canvas.SetTop(label, 102);
                cell.Children.Add(label);
                grid.Children.Add(cell);
            }
            int rows = (moods.Length + 3) / 4;
            var size = new System.Windows.Size(1000, rows * 130);
            grid.Measure(size);
            grid.Arrange(new Rect(size));
            grid.UpdateLayout();
            var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, rows * 130, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rt.Render(grid);
            var e = new System.Windows.Media.Imaging.PngBitmapEncoder();
            e.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rt));
            using (var f = File.Create(Path.Combine(outDir, "mascot-moods.png"))) e.Save(f);
        }

        static void SettingsShot()
        {
            var s = Config();
            var c = new Controller(s);
            var cT = typeof(Controller);
            ((Window)cT.GetField("notch", Any).GetValue(c)).Hide();
            var w = new SettingsWindow(s, c) { WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
            w.Show();
            Pump(1500);
            RECT r;
            GetWindowRect(new WindowInteropHelper(w).Handle, out r);
            foreach (string page in new[] { "notch" })
            {
                typeof(SettingsWindow).GetMethod("Show", Any, null, new[] { typeof(string) }, null).Invoke(w, new object[] { page });
                Pump(700);
                using (var bmp = Screen())
                using (var crop = bmp.Clone(Rectangle.Intersect(new Rectangle(r.L, r.T, r.R - r.L, r.B - r.T), new Rectangle(0, 0, bmp.Width, bmp.Height)), bmp.PixelFormat))
                    crop.Save(Path.Combine(outDir, "settings-" + page + ".png"), ImageFormat.Png);
            }
            w.Close();
            ((IDisposable)cT.GetField("tray", Any).GetValue(c)).Dispose();
            ((Window)cT.GetField("notch", Any).GetValue(c)).Close();
            cT.GetField("quick", Any).FieldType.GetMethod("Quit").Invoke(cT.GetField("quick", Any).GetValue(c), null);
        }

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDpiAwareness(2);
            outDir = args[0];
            Directory.CreateDirectory(outDir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var backdrop = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, 
                Background = new WBrush(WColor.FromRgb(Back.R, Back.G, Back.B)), Left = 0, Top = 0, WindowState = WindowState.Maximized,
            };
            backdrop.Show();
            Pump(500);

            try
            {
                Moods();

                // Sessions
                Shot("working", Config(), Working());
                Shot("sessions", Config(), Working(), hover: true);

                // Asking you
                Shot("permission", Config(), Alert(Push(), "Allow Bash? · shop", "Bash: git push origin main"));
                Shot("permission-details", Config(), Alert(Push(), "Allow Bash? · shop", "Bash: git push origin main"), details: true);
                Shot("question", Config(), new NotchState { Visible = true, Kind = "question", Title = "Claude asks · Checkout flow", Detail = "The tests pass. Should I open the pull request?", Owner = new Question(), Choices = new List<string> { "Not yet", "Open it" }, Primary = 1, Mood = MascotMood.Question, Sessions = Sessions() });

                // Quick voice
                var listening = new NotchState { Visible = true, Kind = "quick", Listening = true, Title = "Listening", VoiceStarted = DateTime.Now.AddSeconds(-3), QuickNow = new QuickTurn { Heard = "What's the weather in Lisbon this week" }, Mood = MascotMood.Listening };
                Shot("quick-listening", Config(), listening);
                const string weather = "<card>{\"weather\": \"partly\", \"value\": \"18°C\", \"label\": \"Lisbon now\", \"note\": \"High 21°, low 12°\", \"days\": [[\"Thu\", \"sun\", \"23°\", \"13°\"], [\"Fri\", \"rain\", \"17°\", \"11°\"], [\"Sat\", \"cloud\", \"19°\", \"12°\"]]}</card>It's eighteen degrees and partly cloudy, with rain on Friday.";
                Shot("quick-weather", Config(), Quick("What's the weather in Lisbon this week?", weather));
                Shot("quick-compare", Config(), Quick("Compare the two plans", "<card>{\"title\": \"Basic vs Pro\", \"columns\": [\"Basic\", \"Pro\"], \"rows\": [[\"Price\", \"$10\", \"$25\"], [\"Storage\", \"50 GB\", \"500 GB\"], [\"Seats\", \"1\", \"5\"]]}</card>Pro costs more, but has ten times the storage."));
                var typing = Quick("What's Bitcoin at?", "<card>{\"value\": \"$96,410\", \"label\": \"Bitcoin\", \"change\": \"+2.4% today\"}</card>About ninety-six thousand.");
                typing.QuickTyping = true;
                Shot("quick-typing", Config(), typing);

                // New project
                var project = new HookServer.Pending { Tool = "starting a new project", Detail = "Recipe Box: Build a small recipe site with search", FullDetail = "Project: Recipe Box\n\nFolder: C:\\Users\\you\\Documents\\Claude Projects\\Recipe Box\n\nPermission mode: Auto\n\nModel: sonnet\n\nEffort: medium\n\nFirst message:\nBuild a small recipe site with search and a favorites list." };
                Shot("new-project", Config(), Alert(project, "Allow starting a new project?", project.Detail), details: true);

                // Styles
                foreach (string style in new[] { "classic", "compact", "line", "dot" })
                    Shot("style-" + style, Config(style), Working());
                Shot("edge-left", Config("classic", "left"), Working());
                Shot("edge-bottom", Config("classic", "bottom"), Working());
                foreach (string size in new[] { "small", "xlarge" })
                    Shot("size-" + size, Config("classic", "top", size, size), Working());

                SettingsShot();
            }
            catch (Exception e) { Console.WriteLine(e); }
            backdrop.Close();
            app.Shutdown();
        }
    }
}




