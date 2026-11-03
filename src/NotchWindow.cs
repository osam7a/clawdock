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
    // The black pill that shows while Claude isn't the window in front. Its looks:
    //   Alert   a permission request is waiting in Claude Code
    //   Voice   a voice conversation is running (timer, live bars, mute button)
    //   Session what the session you just left is doing: working time and tasks done / left
    //   Idle    just the mascot and "Claude"
    //
    // Minimized, it always shows. When you only Alt+Tab away it shows only while
    // something is going on (voice, a working session, an alert). Drag it anywhere;
    // near the top edge it snaps back into a notch. Click opens Claude, the hover x
    // hides it (and ends a voice conversation), right-click has more options.
    class NotchWindow : Window
    {
        enum Mode { Idle, Voice, Session, Alert }

        const double IdleWidth = 150, VoiceWidth = 280, SessionWidth = 340, AlertWidth = 360;
        const double LineHeight = 38, AlertHeight = 56, SessionHeight = 62, HoverGrow = 26, SnapDistance = 24;

        static readonly string SettingsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeNotch");
        static readonly string PositionFile = System.IO.Path.Combine(SettingsDir, "position.txt");

        static readonly Brush Clay = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
        static readonly Brush Amber = Frozen(Color.FromRgb(0xF5, 0xA5, 0x24));
        static readonly Brush Dim = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
        static readonly Brush Chip = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A));

        public event Action OpenClaude;
        public event Action ToggleMute;
        public event Action EndVoice;
        public event Action Quit;

        readonly SessionTracker tracker = new SessionTracker();

        readonly Border shell, closeButton, micButton, progressTrack;
        readonly DockPanel dock;
        readonly Mascot mascot;
        readonly TextBlock title, subtitle, timerText, micGlyph;
        readonly StackPanel voicePanel;
        readonly Ellipse alertDot;
        readonly Rectangle[] bars = new Rectangle[5];
        readonly ColumnDefinition progressDone = new ColumnDefinition(), progressLeft = new ColumnDefinition();
        readonly MenuItem endVoiceItem;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer voiceTimer, alertTimer, refreshTimer;
        readonly Random rnd = new Random();

        // Latest state from the watcher.
        bool claudeOpen, claudeInFront, claudeMinimized, fullscreenApp, voice, muted;
        string sessionTitle;

        bool shown, hover, dismissed, docked = true;
        string appliedKey = "";
        DateTime voiceStarted;

        // Pending permission request.
        bool alert, alertFromRequest;
        DateTime alertAt;
        string alertTitle = "", alertDetail = "";

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
            Height = SessionHeight + 24;
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
                Background = Chip,
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
                Background = Chip,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "✕", FontSize = 9, Foreground = Dim,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            closeButton.MouseLeftButtonDown += (s, e) => e.Handled = true; // don't start a drag
            closeButton.MouseLeftButtonUp += (s, e) => { e.Handled = true; CloseClicked(); };

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 0),
                Children = { voicePanel, alertDot, closeButton },
            };

            dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(mascot, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(mascot);
            dock.Children.Add(right);
            dock.Children.Add(text);

            // Thin bar under the text: how many of the session's tasks are done.
            var fill = new Rectangle { Fill = Clay };
            var progressGrid = new Grid();
            progressGrid.ColumnDefinitions.Add(progressDone);
            progressGrid.ColumnDefinitions.Add(progressLeft);
            progressGrid.Children.Add(fill);
            progressTrack = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = Chip,
                ClipToBounds = true,
                Margin = new Thickness(24, 0, 24, 8),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
                Child = progressGrid,
            };

            endVoiceItem = Item("End voice conversation", () => { if (EndVoice != null) EndVoice(); });

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
                Child = new Grid { Children = { dock, progressTrack } },
            };
            shell.MouseEnter += (s, e) => { hover = true; closeButton.Visibility = Visibility.Visible; Apply(); };
            shell.MouseLeave += (s, e) => { hover = false; closeButton.Visibility = Visibility.Collapsed; Apply(); };
            shell.MouseLeftButtonDown += OnMouseDown;

            Content = new Grid { Children = { shell } };
            UpdateCorners();

            voiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            voiceTimer.Tick += (s, e) => TickVoice();
            alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            alertTimer.Tick += (s, e) => { alertTimer.Stop(); ClearAlert(); };
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refreshTimer.Tick += (s, e) => Apply(); // keeps the working time ticking
            refreshTimer.Start();

            SourceInitialized += (s, e) => MakeToolWindow();
        }

        // ---- state ---------------------------------------------------------

        public void Update(bool isOpen, bool isInFront, bool isFullscreenApp, bool inVoice, bool micMuted, bool isMinimized, string title)
        {
            if (isInFront && !claudeInFront)
            {
                dismissed = false;                 // coming back to Claude resets "hide for now"
                if (alert) alertTimer.Start();     // the user can see the prompt now; fade the alert soon
            }
            claudeOpen = isOpen;
            claudeInFront = isInFront;
            claudeMinimized = isMinimized;
            fullscreenApp = isFullscreenApp;
            muted = micMuted;
            sessionTitle = title;

            if (inVoice != voice)
            {
                voice = inVoice;
                if (voice) { voiceStarted = DateTime.Now; voiceTimer.Start(); }
                else voiceTimer.Stop();
            }
            Apply();
        }

        // Called with the raw JSON a Claude Code hook received (see Program.RunHook).
        public void Notify(string json)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (d == null) return;

            tracker.Apply(d);

            string ev = Str(d, "hook_event_name");
            if (ev == "PermissionRequest" || ev == "Notification")
            {
                string project = ProjectName(Str(d, "cwd"));
                string detail;
                if (ev == "PermissionRequest")
                {
                    detail = DescribeTool(Str(d, "tool_name"), d.ContainsKey("tool_input") ? d["tool_input"] as Dictionary<string, object> : null);
                    alertFromRequest = true;
                }
                else
                {
                    // PermissionRequest carries more detail; don't overwrite it with the generic message.
                    if (alert && alertFromRequest && (DateTime.Now - alertAt).TotalSeconds < 5) return;
                    detail = Str(d, "message");
                    alertFromRequest = false;
                }

                alert = true;
                alertAt = DateTime.Now;
                dismissed = false;
                alertTitle = string.IsNullOrEmpty(project) ? "Needs your permission" : "Needs permission · " + project;
                alertDetail = string.IsNullOrEmpty(detail) ? "Open Claude to respond" : detail;
                alertTimer.Stop();
                if (claudeInFront) alertTimer.Start();
            }
            else if (alert && (ev == "Stop" || ev == "UserPromptSubmit" || ev == "PostToolUse"))
            {
                ClearAlert(); // the session moved on, so the request was answered
            }
            Apply();
        }

        void ClearAlert()
        {
            alert = false;
            alertTimer.Stop();
            Apply();
        }

        void Dismiss()
        {
            dismissed = true;
            alert = false;
            Apply();
        }

        // The x: hide the notch, and if a voice conversation is running, end it too.
        void CloseClicked()
        {
            if (voice && EndVoice != null) EndVoice();
            Dismiss();
        }

        // ---- layout & animation -------------------------------------------

        void Apply()
        {
            var s = tracker.Current(DateTime.Now);
            Mode mode = alert ? Mode.Alert : voice ? Mode.Voice : s != null ? Mode.Session : Mode.Idle;

            // Alt+Tab only counts as "leaving a session" if something is going on.
            bool ongoing = alert || voice || (s != null && (s.Busy || s.JustFinished));
            bool wantShown = alert || (claudeOpen && !claudeInFront && !fullscreenApp && !dismissed && (claudeMinimized || ongoing));

            mascot.Talking = (mode == Mode.Voice && !muted) || (mode == Mode.Session && s.Busy);
            mascot.Alert = mode == Mode.Alert;

            switch (mode)
            {
                case Mode.Alert:
                    title.Text = alertTitle;
                    subtitle.Text = alertDetail;
                    break;
                case Mode.Voice:
                    title.Text = muted ? "Muted" : "Listening";
                    break;
                case Mode.Session:
                    title.Text = !string.IsNullOrEmpty(sessionTitle) ? sessionTitle : string.IsNullOrEmpty(s.Project) ? "Claude Code" : s.Project;
                    subtitle.Text = StatusLine(s);
                    break;
                default:
                    title.Text = "Claude";
                    break;
            }

            bool hasTasks = mode == Mode.Session && s.Total > 0;
            if (hasTasks)
            {
                progressDone.Width = new GridLength(s.Done, GridUnitType.Star);
                progressLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
            }

            micGlyph.Text = muted ? "" : "";   // MicOff / Microphone
            micButton.Background = muted ? Clay : Chip;
            closeButton.ToolTip = voice ? "End voice and hide" : "Hide for now";
            endVoiceItem.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
            if (mode == Mode.Voice) TickVoice();

            // Everything below animates, so only redo it when the layout really changes.
            string key = mode + "|" + muted + "|" + hover + "|" + wantShown + "|" + hasTasks;
            if (key == appliedKey) return;
            appliedKey = key;

            subtitle.Visibility = mode == Mode.Alert || mode == Mode.Session ? Visibility.Visible : Visibility.Collapsed;
            voicePanel.Visibility = mode == Mode.Voice ? Visibility.Visible : Visibility.Collapsed;
            alertDot.Visibility = mode == Mode.Alert ? Visibility.Visible : Visibility.Collapsed;
            progressTrack.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
            dock.Margin = new Thickness(0, 0, 0, hasTasks ? 8 : 0);

            double w = mode == Mode.Alert ? AlertWidth : mode == Mode.Session ? SessionWidth : mode == Mode.Voice ? VoiceWidth : IdleWidth;
            double h = mode == Mode.Alert ? AlertHeight : mode == Mode.Session ? SessionHeight : LineHeight;
            Animate(shell, WidthProperty, w + (hover ? HoverGrow : 0), 260);
            Animate(shell, HeightProperty, h, 260);
            UpdateCorners(h);

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
                fade.Completed += (s2, e) => { if (!shown) { Hide(); slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = 0; } };
                slide.BeginAnimation(TranslateTransform.YProperty, move);
                shell.BeginAnimation(OpacityProperty, fade);
            }
        }

        // "Working 4:12 · 3 done · 2 left" / "Done in 4:12 · 5 done"
        static string StatusLine(SessionTracker.Snapshot s)
        {
            string line = s.Busy ? "Working " + SessionTracker.Clock(s.Elapsed)
                : s.Elapsed > TimeSpan.Zero ? "Done in " + SessionTracker.Clock(s.Elapsed)
                : "Idle";
            if (s.Total > 0)
            {
                line += " · " + s.Done + " done";
                if (s.Total - s.Done > 0) line += " · " + (s.Total - s.Done) + " left";
            }
            return line;
        }

        void UpdateCorners(double height = LineHeight)
        {
            double r = Math.Min(height / 2, 22);
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
            menu.Items.Add(endVoiceItem);
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
