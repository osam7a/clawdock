using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // What the notch should show; built by the Controller every refresh.
    class NotchState
    {
        public bool Visible;
        public string Kind = "idle";        // idle | session | voice | alert
        public string Title = "Claude", Detail = "", Short = ""; // Short: "4:12 · 3/5", for the compact style
        public bool Busy, JustFinished, Muted;
        public int Done, Total;
        public DateTime VoiceStarted;
        public HookServer.Pending Pending;  // a request waiting for Allow / Deny
        public int MorePending;
        public List<SessionInfo> Sessions = new List<SessionInfo>();
    }

    // The notch: a black card docked to an edge of the screen.
    //
    // Styles
    //   classic  mascot, title and a status line (the original look)
    //   compact  one slim line: mascot and title
    //   line     a thin colored bar on the edge; hover to see the card
    //   dot      a small colored dot; hover to see the card
    // Bar and dot colors: gray idle, clay pulsing while working, green in voice (or just
    // finished), amber pulsing when Claude needs permission.
    //
    // Hovering the card lists every running session (click one to jump to it). A permission
    // request shows Allow / Deny. Drag it anywhere: it snaps to the nearest screen edge.
    class NotchWindow : Window
    {
        const double WinW = 460, WinH = 440;

        public event Action OpenPrimary, ToggleMute, EndVoice, OpenSettings, Quit, Dismiss;
        public event Action<SessionInfo> OpenSession;
        public event Action<HookServer.Pending, bool> Answer; // pending, allow

        readonly Settings settings;
        readonly Grid holder;
        readonly Border card, mini, progressTrack;
        readonly Button closeButton, micButton;
        readonly DockPanel header;
        readonly TextBlock title, subtitle, timerText, micGlyph, moreText;
        readonly StackPanel voicePanel, actions, list;
        readonly Ellipse alertDot;
        readonly Rectangle[] bars = new Rectangle[5];
        readonly ColumnDefinition progressDone = new ColumnDefinition(), progressLeft = new ColumnDefinition();
        readonly MenuItem endVoiceItem;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer voiceTimer, leaveTimer, topTimer;
        readonly Random rnd = new Random();
        Mascot mascot;

        NotchState state = new NotchState();
        HookServer.Pending actionsFor; // the request the Allow / Deny buttons are showing
        DateTime actionsSince;
        bool shown, hover;
        string layoutKey = "", listKey = "", miniKey = "";
        double scale = 1;

        public NotchWindow(Settings settings)
        {
            this.settings = settings;
            // Until the window first shows, use the system DPI (the prompt box is placed from it too).
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = WinW;
            Height = WinH;
            Title = "Claude Notch";

            title = Text(13, Brushes.White, FontWeights.SemiBold);
            subtitle = Text(12, Theme.Dim, FontWeights.Normal);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };

            timerText = Text(12, Theme.Dim, FontWeights.Normal);
            timerText.Margin = new Thickness(0, 0, 10, 0);
            timerText.VerticalAlignment = VerticalAlignment.Center;
            voicePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            voicePanel.Children.Add(timerText);
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.Clay,
                    Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                voicePanel.Children.Add(bars[i]);
            }
            micGlyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            micButton = RoundButton(22, micGlyph, "Mute or unmute", () => { if (ToggleMute != null) ToggleMute(); });
            micButton.Margin = new Thickness(10, 0, 0, 0);
            voicePanel.Children.Add(micButton);

            alertDot = new Ellipse { Width = 8, Height = 8, Fill = Theme.Amber, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Pulse(alertDot, true);

            closeButton = RoundButton(18, new TextBlock
            {
                Text = "✕", FontSize = 9, Foreground = Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, "Hide", () => { if (Dismiss != null) Dismiss(); });
            closeButton.Margin = new Thickness(8, 0, 0, 0);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Children = { voicePanel, alertDot, closeButton } };
            header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
            header.Children.Add(text);

            var fill = new Rectangle { Fill = Theme.Clay };
            var progressGrid = new Grid();
            progressGrid.ColumnDefinitions.Add(progressDone);
            progressGrid.ColumnDefinitions.Add(progressLeft);
            progressGrid.Children.Add(fill);
            progressTrack = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Chip, ClipToBounds = true,
                Margin = new Thickness(24, 0, 24, 8), Child = progressGrid,
            };

            moreText = Text(11, Theme.Dim, FontWeights.Normal);
            moreText.VerticalAlignment = VerticalAlignment.Center;
            moreText.Margin = new Thickness(4, 0, 0, 0);
            actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 2, 12, 10) };
            actions.Children.Add(moreText);
            actions.Children.Add(PillButton("Deny", Theme.Chip, () => AnswerPending(false)));
            actions.Children.Add(PillButton("Allow", Theme.Clay, () => AnswerPending(true)));

            list = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };

            card = new Border
            {
                Width = 150, // animations need a starting value
                Background = Theme.Black,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 },
                Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { header, progressTrack, actions, list } },
            };
            mini = new Border { Cursor = Cursors.Hand };
            AutomationProperties.SetName(mini, "Claude Notch");

            endVoiceItem = Item("End voice conversation", () => { if (EndVoice != null) EndVoice(); });
            holder = new Grid { RenderTransform = slide, Opacity = 0, Children = { card, mini }, ContextMenu = BuildMenu() };
            holder.MouseEnter += (s, e) => { leaveTimer.Stop(); if (!hover) { hover = true; Refresh(); } };
            holder.MouseLeave += (s, e) => leaveTimer.Start();
            holder.MouseLeftButtonDown += OnMouseDown;
            Content = new Grid { Children = { holder } };

            voiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            voiceTimer.Tick += (s, e) => TickVoice();
            leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            leaveTimer.Tick += (s, e) => { leaveTimer.Stop(); if (!holder.IsMouseOver && hover) { hover = false; Refresh(); } };
            topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            topTimer.Tick += (s, e) => { if (shown) KeepOnTop(); };
            topTimer.Start();

            SourceInitialized += (s, e) =>
            {
                MakeToolWindow();
                var src = PresentationSource.FromVisual(this);
                if (src != null) scale = src.CompositionTarget.TransformToDevice.M11;
                Place();
            };
            ApplySettings();
        }

        // ---- settings ------------------------------------------------------

        bool Mini { get { return settings.Style == "line" || settings.Style == "dot"; } }
        bool Compact { get { return settings.Style == "compact"; } }
        bool Vertical { get { return settings.Edge == "left" || settings.Edge == "right"; } }

        public void ApplySettings()
        {
            if (mascot != null) header.Children.Remove(mascot);
            mascot = new Mascot(Compact ? 1.7 : 2.4) { Margin = new Thickness(Compact ? 12 : 16, 0, Compact ? 8 : 10, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(mascot, Dock.Left);
            header.Children.Insert(0, mascot);
            header.Height = Compact ? 30 : double.NaN;
            title.FontSize = Compact ? 12 : 13;

            string edge = settings.Edge;
            holder.HorizontalAlignment = edge == "left" ? HorizontalAlignment.Left : edge == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            holder.VerticalAlignment = edge == "top" ? VerticalAlignment.Top : edge == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            var shadow = (DropShadowEffect)card.Effect;
            shadow.Direction = edge == "bottom" ? 90 : edge == "left" ? 0 : edge == "right" ? 180 : 270;

            layoutKey = listKey = miniKey = "";
            if (IsLoaded || PresentationSource.FromVisual(this) != null) Place();
            Refresh();
        }

        // Puts the window against the chosen edge of the chosen monitor.
        public void Place()
        {
            Rect wa = WorkArea();
            double off = settings.EdgeOffset;
            double left, top, shift = 0;
            if (!Vertical)
            {
                double cx = wa.Left + off * wa.Width;
                left = Math.Max(wa.Left, Math.Min(cx - WinW / 2, wa.Right - WinW));
                top = settings.Edge == "top" ? wa.Top : wa.Bottom - WinH;
                shift = cx - (left + WinW / 2);
            }
            else
            {
                double cy = wa.Top + off * wa.Height;
                top = Math.Max(wa.Top, Math.Min(cy - WinH / 2, wa.Bottom - WinH));
                left = settings.Edge == "left" ? wa.Left : wa.Right - WinW;
                shift = cy - (top + WinH / 2);
            }
            shift = Math.Max(-30, Math.Min(30, shift)); // keep room for the widest card
            Left = left;
            Top = top;
            if (!Vertical) holder.Margin = shift > 0 ? new Thickness(2 * shift, 0, 0, 0) : new Thickness(0, 0, -2 * shift, 0);
            else holder.Margin = shift > 0 ? new Thickness(0, 2 * shift, 0, 0) : new Thickness(0, 0, 0, -2 * shift);
        }

        System.Windows.Forms.Screen TargetScreen()
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
                if (s.DeviceName == settings.Monitor) return s;
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        Rect WorkArea()
        {
            var r = TargetScreen().WorkingArea;
            return new Rect(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
        }

        // Where the prompt box goes: just inside the notch, toward the middle of the screen.
        public Point PromptPosition(double w, double h)
        {
            Rect wa = WorkArea();
            double off = settings.EdgeOffset, gap = 54;
            double x, y;
            switch (settings.Edge)
            {
                case "bottom": x = wa.Left + off * wa.Width - w / 2; y = wa.Bottom - gap - h; break;
                case "left": x = wa.Left + gap; y = wa.Top + off * wa.Height - h / 2; break;
                case "right": x = wa.Right - gap - w; y = wa.Top + off * wa.Height - h / 2; break;
                default: x = wa.Left + off * wa.Width - w / 2; y = wa.Top + gap; break;
            }
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            return new Point(x, y);
        }

        // ---- state ---------------------------------------------------------

        public void Render(NotchState s)
        {
            bool voiceWas = state.Kind == "voice";
            state = s;
            if (s.Kind == "voice" && !voiceWas) voiceTimer.Start();
            if (s.Kind != "voice") voiceTimer.Stop();
            Refresh();
        }

        void Refresh()
        {
            var s = state;
            bool alert = s.Kind == "alert";
            bool hoverExpand = hover && settings.ExpandOnHover;
            bool expanded = !Mini || hoverExpand || (hover && alert);
            bool showList = hoverExpand && s.Sessions.Count > 0 && !alert;
            bool showActions = alert && s.Pending != null && expanded;
            if (!showActions) actionsFor = null;
            else if (actionsFor != s.Pending) { actionsFor = s.Pending; actionsSince = DateTime.Now; }

            mascot.Talking = (s.Kind == "voice" && !s.Muted) || (s.Kind == "session" && s.Busy);
            mascot.Alert = alert;

            title.Inlines.Clear();
            title.Inlines.Add(new Run(s.Title));
            bool showSub = (s.Kind == "session" || alert) && !string.IsNullOrEmpty(s.Detail);
            if (Compact && s.Kind == "session")
            {
                string brief = !string.IsNullOrEmpty(s.Short) ? s.Short : s.Detail;
                if (!string.IsNullOrEmpty(brief)) title.Inlines.Add(new Run("  " + brief) { Foreground = Theme.Dim, FontWeight = FontWeights.Normal });
                showSub = false;
            }
            subtitle.Text = s.Detail;
            // The line is cut to fit; hover shows the whole command before you allow it.
            card.ToolTip = alert && s.Pending != null && !string.IsNullOrEmpty(s.Pending.FullDetail) ? s.Pending.FullDetail : null;
            if (s.Kind == "voice") title.Inlines.Clear();
            if (s.Kind == "voice") title.Inlines.Add(new Run(s.Muted ? "Muted" : "Listening"));

            bool hasTasks = s.Kind == "session" && s.Total > 0 && !Compact;
            if (hasTasks)
            {
                progressDone.Width = new GridLength(s.Done, GridUnitType.Star);
                progressLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
            }
            micGlyph.Text = s.Muted ? "" : ""; // MicOff / Microphone
            micButton.Background = s.Muted ? Theme.Clay : Theme.Chip;
            closeButton.ToolTip = s.Kind == "voice" ? "End voice and hide" : "Hide for now";
            endVoiceItem.Visibility = s.Kind == "voice" ? Visibility.Visible : Visibility.Collapsed;
            moreText.Text = s.MorePending > 0 ? "+" + s.MorePending + " more" : "";
            if (s.Kind == "voice") TickVoice();
            if (showList) BuildList(s.Sessions);
            UpdateMini(s);

            string key = s.Kind + "|" + hover + "|" + s.Visible + "|" + hasTasks + "|" + showList + "|" + showActions + "|" + showSub + "|" + settings.Style + "|" + settings.Edge;
            if (key == layoutKey) return;
            layoutKey = key;

            card.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            mini.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            subtitle.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
            voicePanel.Visibility = s.Kind == "voice" ? Visibility.Visible : Visibility.Collapsed;
            alertDot.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
            closeButton.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            progressTrack.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
            actions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

            double w = CardWidth(s.Kind) + (hover && !Mini ? 26 : 0);
            if (showList || showActions) w = Math.Max(w, Compact ? 330 : 360);
            Animate(card, WidthProperty, w, 220);
            if (!Compact) header.Height = s.Kind == "session" || alert ? (hasTasks ? 54 : 56) : 38;
            double r = Compact ? 14 : 18;
            switch (settings.Edge)
            {
                case "bottom": card.CornerRadius = new CornerRadius(r, r, 0, 0); break;
                case "left": card.CornerRadius = new CornerRadius(0, r, r, 0); break;
                case "right": card.CornerRadius = new CornerRadius(r, 0, 0, r); break;
                default: card.CornerRadius = new CornerRadius(0, 0, r, r); break;
            }

            if (s.Visible != shown)
            {
                shown = s.Visible;
                if (shown) Show();
                double away = 90;
                bool horizontalSlide = Vertical;
                double sign = settings.Edge == "top" || settings.Edge == "left" ? -1 : 1;
                var prop = horizontalSlide ? TranslateTransform.XProperty : TranslateTransform.YProperty;
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.BeginAnimation(TranslateTransform.YProperty, null);
                slide.X = 0; slide.Y = 0;
                if (shown) { if (horizontalSlide) slide.X = sign * away; else slide.Y = sign * away; }

                var move = new DoubleAnimation(shown ? 0 : sign * away, TimeSpan.FromMilliseconds(shown ? 320 : 200))
                {
                    EasingFunction = shown
                        ? (IEasingFunction)new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                        : new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                var fade = new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 200 : 180));
                fade.Completed += (s2, e) => { if (!shown) { Hide(); hover = false; } };
                slide.BeginAnimation(prop, move);
                holder.BeginAnimation(OpacityProperty, fade);
            }
        }

        double CardWidth(string kind)
        {
            if (Compact) return kind == "alert" ? 330 : kind == "session" ? 280 : kind == "voice" ? 230 : 110;
            return kind == "alert" ? 360 : kind == "session" ? 340 : kind == "voice" ? 280 : 150;
        }

        void UpdateMini(NotchState s)
        {
            bool dot = settings.Style == "dot";
            Brush color = s.Kind == "alert" ? Theme.Amber
                : s.Kind == "voice" ? (s.Muted ? Theme.Dim : Theme.Green)
                : s.Busy ? Theme.Clay
                : s.JustFinished ? Theme.Green
                : Theme.Idle;
            bool pulse = s.Kind == "alert" || s.Busy;
            string key = settings.Style + "|" + settings.Edge + "|" + color.GetHashCode() + "|" + pulse;
            if (key == miniKey) return;
            miniKey = key;

            mini.Background = color;
            if (dot)
            {
                mini.Width = mini.Height = 12;
                mini.CornerRadius = new CornerRadius(6);
                mini.Margin = new Thickness(6);
            }
            else
            {
                mini.Width = Vertical ? 5 : 120;
                mini.Height = Vertical ? 120 : 5;
                mini.CornerRadius = new CornerRadius(2.5);
                mini.Margin = new Thickness(0);
            }
            Pulse(mini, pulse);
            AutomationProperties.SetName(mini, "Claude Notch: " + state.Title);
        }

        // Rows for every running session: a status dot, the name, where it runs, and what it's doing.
        void BuildList(List<SessionInfo> sessions)
        {
            var sb = new StringBuilder();
            int shownCount = Math.Min(sessions.Count, 5);
            for (int i = 0; i < shownCount; i++)
                sb.Append(sessions[i].Id).Append(sessions[i].Busy).Append(RowStatus(sessions[i])).Append(sessions[i].DisplayName).Append('|');
            sb.Append(sessions.Count);
            if (sb.ToString() == listKey) return;
            listKey = sb.ToString();

            list.Children.Clear();
            list.Children.Add(new Border { Height = 1, Background = Theme.Chip, Margin = new Thickness(8, 0, 8, 6) });
            for (int i = 0; i < shownCount; i++) list.Children.Add(Row(sessions[i]));
            if (sessions.Count > shownCount)
            {
                var more = Text(11, Theme.Dim, FontWeights.Normal);
                more.Text = "+" + (sessions.Count - shownCount) + " more";
                more.Margin = new Thickness(14, 2, 0, 0);
                list.Children.Add(more);
            }
        }

        FrameworkElement Row(SessionInfo s)
        {
            var dot = new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = s.Busy ? Theme.Clay : s.JustFinished ? Theme.Green : Theme.Idle,
            };
            var name = Text(12, Brushes.White, FontWeights.Normal);
            name.Text = s.DisplayName;
            var where = Text(10, Theme.Dim, FontWeights.Normal);
            where.Text = s.Desktop ? "APP" : "CLI";
            where.Margin = new Thickness(8, 1, 0, 0);
            var status = Text(11, Theme.Dim, FontWeights.Normal);
            status.Text = RowStatus(s);
            status.Margin = new Thickness(8, 0, 6, 0);
            status.VerticalAlignment = VerticalAlignment.Center;

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(status, Dock.Right);
            var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { name, where } };
            dock.Children.Add(dot);
            dock.Children.Add(status);
            dock.Children.Add(middle);

            var row = new Border { Child = dock, Padding = new Thickness(4, 6, 4, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.ToolTip = (s.Desktop ? "Open in the Claude app" : "Open the terminal") + (string.IsNullOrEmpty(s.Cwd) ? "" : "\n" + s.Cwd);
            AutomationProperties.SetName(row, s.DisplayName + ", " + status.Text);
            row.MouseEnter += (a, b) => row.Background = Theme.Chip;
            row.MouseLeave += (a, b) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (a, b) => b.Handled = true;
            row.MouseLeftButtonUp += (a, b) => { b.Handled = true; if (OpenSession != null) OpenSession(s); };
            return row;
        }

        public static string RowStatus(SessionInfo s)
        {
            string t = s.Busy ? SessionTracker.Clock(s.Elapsed) : s.JustFinished ? "done" : "idle";
            if (s.Total > 0) t += " · " + s.Done + "/" + s.Total;
            return t;
        }

        // Answers only the request the buttons were showing, only while they're visible, and
        // not in the first moments after it appeared, so a request that pops up under the
        // cursor (or a click meant for the previous one) can't be approved by accident.
        void AnswerPending(bool allow)
        {
            var p = actionsFor;
            if (p == null || p != state.Pending || p.Settled || actions.Visibility != Visibility.Visible) return;
            if ((DateTime.Now - actionsSince).TotalMilliseconds < 700) return;
            if (Answer != null) Answer(p, allow);
        }

        void TickVoice()
        {
            var t = DateTime.Now - state.VoiceStarted;
            timerText.Text = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            for (int i = 0; i < bars.Length; i++)
            {
                double center = 1 - Math.Abs(i - 2) / 3.0; // middle bars run taller
                bars[i].Height = state.Muted ? 3 : 4 + rnd.NextDouble() * 14 * center + rnd.NextDouble() * 3;
                bars[i].Opacity = state.Muted ? 0.4 : 1;
            }
        }

        // ---- input ---------------------------------------------------------

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            double startLeft = Left, startTop = Top;
            try { DragMove(); } catch (InvalidOperationException) { }

            if (Math.Abs(Left - startLeft) < 4 && Math.Abs(Top - startTop) < 4)
            {
                Left = startLeft; Top = startTop;
                if (OpenPrimary != null) OpenPrimary();
                return;
            }
            SnapToNearestEdge();
        }

        // After a drag: the edge closest to where the notch was dropped, and how far along it.
        void SnapToNearestEdge()
        {
            var p = holder.TranslatePoint(new Point(holder.ActualWidth / 2, holder.ActualHeight / 2), this);
            double cx = Left + p.X, cy = Top + p.Y;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(cx * scale), (int)(cy * scale)));
            var r = screen.WorkingArea;
            var wa = new Rect(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

            double dTop = Math.Abs(cy - wa.Top), dBottom = Math.Abs(wa.Bottom - cy), dLeft = Math.Abs(cx - wa.Left), dRight = Math.Abs(wa.Right - cx);
            double min = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
            settings.Edge = min == dTop ? "top" : min == dBottom ? "bottom" : min == dLeft ? "left" : "right";
            settings.EdgeOffset = Vertical
                ? Math.Max(0, Math.Min(1, (cy - wa.Top) / wa.Height))
                : Math.Max(0, Math.Min(1, (cx - wa.Left) / wa.Width));
            settings.Monitor = screen.Primary ? "" : screen.DeviceName;
            settings.Save();
            ApplySettings();
        }

        ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open Claude", () => { if (OpenPrimary != null) OpenPrimary(); }));
            menu.Items.Add(endVoiceItem);
            menu.Items.Add(Item("Hide for now", () => { if (Dismiss != null) Dismiss(); }));
            menu.Items.Add(Item("Move back to top center", () =>
            {
                settings.Edge = "top"; settings.EdgeOffset = 0.5; settings.Monitor = "";
                settings.Save();
                ApplySettings();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Settings…", () => { if (OpenSettings != null) OpenSettings(); }));
            menu.Items.Add(Item("Quit Claude Notch", () => { if (Quit != null) Quit(); }));
            return menu;
        }

        // ---- helpers -------------------------------------------------------

        static MenuItem Item(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => click();
            return item;
        }

        static TextBlock Text(double size, Brush color, FontWeight weight)
        {
            return new TextBlock { FontFamily = Theme.Font, FontSize = size, Foreground = color, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        // Real buttons (so screen readers and UI Automation can press them), drawn as a rounded
        // chip. The template binds the chip's color to the button's Background.
        static Button ChipButton(UIElement content, string name, Brush background, CornerRadius radius, Thickness padding, Action click)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, radius);
            border.SetValue(Border.PaddingProperty, padding);
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var b = new Button
            {
                Content = content, Background = background, Cursor = Cursors.Hand, Focusable = false,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = name,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            };
            AutomationProperties.SetName(b, name);
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.Click += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        static Button RoundButton(double size, UIElement content, string name, Action click)
        {
            var b = ChipButton(content, name, Theme.Chip, new CornerRadius(size / 2), new Thickness(0), click);
            b.Width = b.Height = size;
            return b;
        }

        static Button PillButton(string label, Brush background, Action click)
        {
            var text = new TextBlock { Text = label, Foreground = Brushes.White, FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold };
            var b = ChipButton(text, label, background, new CornerRadius(11), new Thickness(14, 3, 14, 4), click);
            b.Margin = new Thickness(8, 0, 0, 0);
            return b;
        }

        static void Pulse(UIElement el, bool on)
        {
            el.BeginAnimation(OpacityProperty, on
                ? new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }

        static void Animate(UIElement el, DependencyProperty prop, double to, int ms)
        {
            el.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Keep the notch out of Alt+Tab and stop it from stealing focus when clicked.
        void MakeToolWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        // Another always-on-top window (or a shell flyout) can push the notch behind it, so
        // while it's meant to be visible it re-asserts its place without taking focus.
        void KeepOnTop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
