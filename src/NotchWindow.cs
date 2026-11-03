using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // The black pill that shows while Claude isn't the window in front. It has
    // three looks: idle (mascot + "Claude"), voice (timer + live bars while Claude
    // uses the mic) and alert (a permission request waiting in Claude Code).
    //
    // Drag it anywhere; near the top edge it snaps back into a notch. Click opens
    // Claude, the hover x hides it for now, right-click has more options.
    class NotchWindow : Window
    {
        const double IdleWidth = 150, VoiceWidth = 280, AlertWidth = 360;
        const double LineHeight = 38, AlertHeight = 56, HoverGrow = 26, SnapDistance = 24;

        static readonly string SettingsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeNotch");
        static readonly string PositionFile = System.IO.Path.Combine(SettingsDir, "position.txt");

        static readonly Brush Clay = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
        static readonly Brush Amber = Frozen(Color.FromRgb(0xF5, 0xA5, 0x24));
        static readonly Brush Dim = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));

        public event Action OpenClaude;
        public event Action ToggleMute;
        public event Action Quit;

        readonly Border shell, closeButton, micButton;
        readonly Mascot mascot;
        readonly TextBlock title, subtitle, timerText, micGlyph;
        readonly StackPanel voicePanel;
        readonly Ellipse alertDot;
        readonly Rectangle[] bars = new Rectangle[5];
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer voiceTimer, alertTimer;
        readonly Random rnd = new Random();

        // Latest state from the watcher.
        bool claudeOpen, claudeInFront, fullscreenApp, voice, muted;

        bool shown, hover, dismissed, docked = true;
        bool alert;
        DateTime alertAt, voiceStarted;
        bool alertFromRequest;

        public NotchWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = AlertWidth + HoverGrow + 40;
            Height = AlertHeight + 24;
            LoadPosition();

            mascot = new Mascot(2.4) { Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };

            title = new TextBlock
            {
                Text = "Claude",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            subtitle = new TextBlock
            {
                Foreground = Dim,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };

            timerText = new TextBlock
            {
                Foreground = Dim,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            voicePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            voicePanel.Children.Add(timerText);
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5,
                    Fill = Clay,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                voicePanel.Children.Add(bars[i]);
            }

            micGlyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            micButton = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Mute / unmute",
                Child = micGlyph,
            };
            micButton.MouseLeftButtonDown += (s, e) => e.Handled = true; // don't start a drag
            micButton.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (ToggleMute != null) ToggleMute(); };
            voicePanel.Children.Add(micButton);

            alertDot = new Ellipse
            {
                Width = 8, Height = 8, Fill = Amber,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            alertDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });

            closeButton = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = "Hide for now",
                Child = new TextBlock
                {
                    Text = "✕", FontSize = 9, Foreground = Dim,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            closeButton.MouseLeftButtonDown += (s, e) => e.Handled = true; // don't start a drag
            closeButton.MouseLeftButtonUp += (s, e) => { e.Handled = true; Dismiss(); };

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 0),
                Children = { voicePanel, alertDot, closeButton },
            };

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(mascot, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(mascot);
            dock.Children.Add(right);
            dock.Children.Add(text);

            shell = new Border
            {
                Width = IdleWidth,
                Height = LineHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Frozen(Color.FromRgb(0x0B, 0x0B, 0x0B)),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 },
                RenderTransform = slide,
                Opacity = 0,
                Cursor = Cursors.Hand,
                ContextMenu = BuildMenu(),
                Child = dock,
            };
            shell.MouseEnter += (s, e) => { hover = true; closeButton.Visibility = Visibility.Visible; Relayout(); };
            shell.MouseLeave += (s, e) => { hover = false; closeButton.Visibility = Visibility.Collapsed; Relayout(); };
            shell.MouseLeftButtonDown += OnMouseDown;

            Content = new Grid { Children = { shell } };
            UpdateCorners();

            voiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            voiceTimer.Tick += (s, e) => TickVoice();
            alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            alertTimer.Tick += (s, e) => { alertTimer.Stop(); ClearAlert(); };

            SourceInitialized += (s, e) => MakeToolWindow();
        }

        // ---- state ---------------------------------------------------------

        public void Update(bool isOpen, bool isInFront, bool isFullscreenApp, bool inVoice, bool micMuted = false)
        {
            muted = micMuted;
            if (isInFront && !claudeInFront)
            {
                dismissed = false;                 // coming back to Claude resets "hide for now"
                if (alert) alertTimer.Start();     // the user can see the prompt now; fade the alert soon
            }
            claudeOpen = isOpen;
            claudeInFront = isInFront;
            fullscreenApp = isFullscreenApp;

            if (inVoice != voice)
            {
                voice = inVoice;
                if (voice) { voiceStarted = DateTime.Now; voiceTimer.Start(); }
                else voiceTimer.Stop();
            }
            Relayout();
        }

        // Called with the raw JSON a Claude Code hook received (see Program.RunHook).
        public void Notify(string json)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (d == null) return;

            string ev = Str(d, "hook_event_name");
            string project = ProjectName(Str(d, "cwd"));
            string detail;

            if (ev == "PermissionRequest")
            {
                detail = DescribeTool(Str(d, "tool_name"), d.ContainsKey("tool_input") ? d["tool_input"] as Dictionary<string, object> : null);
                alertFromRequest = true;
            }
            else if (ev == "Notification")
            {
                // PermissionRequest carries more detail; don't overwrite it with the generic message.
                if (alert && alertFromRequest && (DateTime.Now - alertAt).TotalSeconds < 5) return;
                detail = Str(d, "message");
                alertFromRequest = false;
            }
            else return;

            alert = true;
            alertAt = DateTime.Now;
            dismissed = false;
            title.Text = string.IsNullOrEmpty(project) ? "Needs your permission" : "Needs permission · " + project;
            subtitle.Text = string.IsNullOrEmpty(detail) ? "Open Claude to respond" : detail;
            alertTimer.Stop();
            if (claudeInFront) alertTimer.Start();
            Relayout();
        }

        void ClearAlert()
        {
            alert = false;
            Relayout();
        }

        void Dismiss()
        {
            dismissed = true;
            alert = false;
            Relayout();
        }

        // ---- layout & animation -------------------------------------------

        void Relayout()
        {
            bool showVoice = voice && !alert;
            mascot.Talking = showVoice && !muted;
            mascot.Alert = alert;

            if (!alert) { title.Text = showVoice ? (muted ? "Muted" : "Listening") : "Claude"; }
            micGlyph.Text = muted ? "" : "";   // MicOff / Microphone
            micButton.Background = muted ? Clay : Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A));
            subtitle.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
            voicePanel.Visibility = showVoice ? Visibility.Visible : Visibility.Collapsed;
            alertDot.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
            if (showVoice) TickVoice();

            double w = alert ? AlertWidth : showVoice ? VoiceWidth : IdleWidth;
            double h = alert ? AlertHeight : LineHeight;
            Animate(shell, WidthProperty, w + (hover ? HoverGrow : 0), 260);
            Animate(shell, HeightProperty, h, 260);
            UpdateCorners(h);

            bool wantShown = alert || (claudeOpen && !claudeInFront && !fullscreenApp && !dismissed);
            if (wantShown != shown)
            {
                shown = wantShown;
                if (shown) Show();
                double hiddenY = docked ? -(h + 12) : -8;
                if (shown && slide.Y == 0) slide.Y = hiddenY;

                var move = new DoubleAnimation(shown ? 0 : hiddenY, TimeSpan.FromMilliseconds(shown ? 320 : 200))
                {
                    EasingFunction = shown
                        ? (IEasingFunction)new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                        : new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                var fade = new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 200 : 180));
                fade.Completed += (s, e) => { if (!shown) { Hide(); slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = 0; } };
                slide.BeginAnimation(TranslateTransform.YProperty, move);
                shell.BeginAnimation(OpacityProperty, fade);
            }
        }

        void UpdateCorners(double height = LineHeight)
        {
            double r = height / 2;
            shell.CornerRadius = docked ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        }

        void TickVoice()
        {
            var t = DateTime.Now - voiceStarted;
            timerText.Text = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            for (int i = 0; i < bars.Length; i++)
            {
                double center = 1 - Math.Abs(i - 2) / 3.0; // middle bars run taller
                bars[i].Height = muted ? 3 : 4 + rnd.NextDouble() * 14 * center + rnd.NextDouble() * 3;
                bars[i].Opacity = muted ? 0.4 : 1;
            }
        }

        static void Animate(UIElement el, DependencyProperty prop, double to, int ms)
        {
            el.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        // ---- input ---------------------------------------------------------

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            double startLeft = Left, startTop = Top;
            try { DragMove(); } catch (InvalidOperationException) { }

            if (Math.Abs(Left - startLeft) < 4 && Math.Abs(Top - startTop) < 4)
            {
                Left = startLeft; Top = startTop;
                if (OpenClaude != null) OpenClaude();
                return;
            }
            SnapAndSave();
        }

        ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open Claude", () => { if (OpenClaude != null) OpenClaude(); }));
            menu.Items.Add(Item("Hide for now", Dismiss));
            menu.Items.Add(Item("Move back to top center", () => { CenterTop(); SavePosition(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Quit Claude Notch", () => { if (Quit != null) Quit(); }));
            return menu;
        }

        static MenuItem Item(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => click();
            return item;
        }

        // ---- position ------------------------------------------------------

        void SnapAndSave()
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            Left = Math.Max(vl - Width / 3, Math.Min(Left, vr - Width * 2 / 3));
            Top = Math.Max(vt, Math.Min(Top, vb - Height));

            docked = Top - vt < SnapDistance;
            if (docked) Top = vt;
            UpdateCorners(shell.ActualHeight > 0 ? shell.ActualHeight : LineHeight);
            SavePosition();
        }

        void CenterTop()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            docked = true;
            UpdateCorners(shell != null && shell.ActualHeight > 0 ? shell.ActualHeight : LineHeight);
        }

        void LoadPosition()
        {
            try
            {
                var parts = File.ReadAllText(PositionFile).Split(',');
                Left = double.Parse(parts[0], CultureInfo.InvariantCulture);
                Top = double.Parse(parts[1], CultureInfo.InvariantCulture);
                docked = Top - SystemParameters.VirtualScreenTop < SnapDistance;
                return;
            }
            catch { }
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            docked = true;
        }

        void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(PositionFile, Left.ToString(CultureInfo.InvariantCulture) + "," + Top.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        // ---- helpers -------------------------------------------------------

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static string ProjectName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            try { return System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/')); } catch { return ""; }
        }

        static string DescribeTool(string tool, Dictionary<string, object> input)
        {
            string what = Str(input, "command");
            if (what == "") what = Str(input, "file_path");
            if (what == "") what = Str(input, "url");
            if (what == "") what = Str(input, "description");
            what = what.Replace("\r", " ").Replace("\n", " ");
            if (tool == "") return what;
            return what == "" ? tool : tool + ": " + what;
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Keep the notch out of Alt+Tab and stop it from stealing focus when clicked.
        void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    }
}
