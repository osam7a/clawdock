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
    // The notch: a black card docked to an edge of the screen.
    //
    // Styles
    //   classic  mascot, title and a status line (the original look)
    //   compact  one slim line: mascot and title
    //   line     a thin colored bar on the edge; hover to see the card (it opens by itself when Claude needs you)
    //   dot      a small colored dot; hover to see the card (likewise)
    // Bar and dot colors: gray idle, clay pulsing while working, green in voice (or just
    // finished), amber pulsing when Claude needs permission.
    //
    // Hovering the card lists every running session (click one to jump to it). A permission
    // request shows Allow / Deny. Drag it anywhere: it snaps to the nearest screen edge.
    partial class NotchWindow : Window
    {
        // Room for the biggest card at the chosen sizes; the rest is transparent and clicks pass through.
        double WinW { get { return 560 * LargestSize; } }
        double WinH { get { return 640 * LargestSize; } }
        double LargestSize { get { return Math.Max(SizeOf(settings.NotchSizeCollapsed), SizeOf(settings.NotchSizeOpen)); } }

        static double SizeOf(string size)
        {
            return size == "small" ? 0.85 : size == "large" ? 1.2 : size == "xlarge" ? 1.4 : 1;
        }

        readonly ScaleTransform sizing = new ScaleTransform(1, 1);
        double sizeNow = 1;

        public event Action OpenPrimary, ToggleMute, EndVoice, OpenSettings, Quit, Dismiss, ToggleMinimize, ClearChat;
        public Func<double> QuickLevel; // quick voice: how loud you are, or the answer being read (0..1; -1 unknown)
        public event Action<SessionInfo> OpenSession;
        public event Action<object, int> Choose; // owner, index into Choices
        public event Action<string> SendTyped;   // what you typed in the text box
        public event Action OpenChat;            // see this quick voice chat in the Claude app

        readonly Settings settings;
        readonly Grid holder;
        readonly Border card, mini, progressTrack;
        readonly Button closeButton, micButton, minButton, clearButton, openChatButton;
        readonly TextBlock minGlyph;
        readonly DockPanel header;
        readonly TextBlock title, subtitle, timerText, micGlyph, moreText;
        readonly StackPanel voicePanel, actions, list;
        readonly Ellipse alertDot;
        readonly Rectangle[] bars = new Rectangle[5];
        readonly ColumnDefinition progressDone = new ColumnDefinition(), progressLeft = new ColumnDefinition();
        // Compact style: a short bar and "4/6" at the right of the line, instead of the full-width bar.
        readonly ColumnDefinition miniDone = new ColumnDefinition(), miniLeft = new ColumnDefinition();
        readonly StackPanel compactTasks;
        readonly TextBlock compactCount;
        // Quick voice: earlier turns, then what you said, the answer's card, and the answer.
        readonly ScrollViewer quickScroll;
        readonly ScrollViewer detailScroll;
        readonly TextBlock detailText;
        object detailsFor;
        bool detailsOpen;
        readonly StackPanel quickHistory;
        readonly TextBlock quickNotes;
        readonly TextBox input;
        readonly Border inputBar;
        bool typing;
        IntPtr beforeTyping; // the window you were in, to go back to
        readonly Border youBubble, cardHost;
        readonly TextBlock youText, answerText, quickStatus;
        string answerKey = "";
        string historyKey = "", cardKey = "", turnKey = "";
        readonly MenuItem endVoiceItem;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer voiceTimer, leaveTimer, topTimer;
        readonly Random rnd = new Random();
        Mascot mascot;

        NotchState state = new NotchState();
        object actionsFor; // what the buttons are answering right now
        DateTime actionsSince;
        bool shown, hover;
        string layoutKey = "", listKey = "", miniKey = "";
        readonly List<TextBlock> rowStatus = new List<TextBlock>(); // the status text of each session row
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
            minGlyph = new TextBlock { FontFamily = Theme.Icons, FontSize = 9, Foreground = Theme.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            minButton = RoundButton(18, minGlyph, "Minimize", () => { if (ToggleMinimize != null) ToggleMinimize(); });
            minButton.Margin = new Thickness(8, 0, 0, 0);
            clearButton = RoundButton(18, new TextBlock
            {
                Text = "", FontFamily = Theme.Icons, FontSize = 9, Foreground = Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, "Clear this chat", () => { if (ClearChat != null) ClearChat(); });
            clearButton.Margin = new Thickness(8, 0, 0, 0);
            clearButton.ToolTip = "Clear this chat: it's gone from here, and Claude forgets it too";
            openChatButton = RoundButton(18, new TextBlock
            {
                Text = "", FontFamily = Theme.Icons, FontSize = 9, Foreground = Theme.Dim,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, "Open this chat in Claude", () => { if (OpenChat != null) OpenChat(); });
            openChatButton.Margin = new Thickness(8, 0, 0, 0);
            openChatButton.ToolTip = "Open this chat in the Claude app";

            var miniGrid = new Grid();
            miniGrid.ColumnDefinitions.Add(miniDone);
            miniGrid.ColumnDefinitions.Add(miniLeft);
            miniGrid.Children.Add(new Rectangle { Fill = Theme.Clay });
            var miniBar = new Border { Width = 34, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Chip, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = miniGrid };
            compactCount = Text(11, Theme.Dim, FontWeights.SemiBold);
            compactCount.Margin = new Thickness(6, 0, 0, 0);
            compactCount.VerticalAlignment = VerticalAlignment.Center;
            compactTasks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed, Children = { miniBar, compactCount } };

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Children = { compactTasks, voicePanel, alertDot, openChatButton, clearButton, minButton, closeButton } };
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

            list = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };

            quickHistory = new StackPanel();
            youText = Wrapped(13.5, Brushes.White);
            youBubble = new Border
            {
                Background = Theme.Chip, CornerRadius = new CornerRadius(14, 14, 4, 14), Padding = new Thickness(12, 7, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(48, 0, 0, 0), Child = youText,
            };
            cardHost = new Border { Margin = new Thickness(0, 10, 0, 0) };
            answerText = Wrapped(14.5, Brushes.White);
            answerText.LineHeight = 21;
            answerText.Margin = new Thickness(2, 10, 2, 0);
            quickStatus = Wrapped(12.5, Theme.Dim);
            quickStatus.Margin = new Thickness(2, 10, 2, 0);
            quickNotes = Wrapped(12, Theme.Dim);
            quickNotes.Margin = new Thickness(2, 10, 2, 0);
            quickScroll = new ScrollViewer
            {
                Content = new StackPanel { Margin = new Thickness(16, 2, 16, 14), Children = { quickHistory, youBubble, cardHost, quickNotes, answerText, quickStatus } },
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // the wheel still scrolls
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false, Visibility = Visibility.Collapsed,
            };

            // A permission request's details, opened by clicking the card.
            detailText = new TextBlock { FontFamily = Theme.Mono, FontSize = 12, LineHeight = 17, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
            detailScroll = new ScrollViewer
            {
                Content = detailText, Margin = new Thickness(14, 0, 14, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // "Scroll for more" says when there's more
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false, Visibility = Visibility.Collapsed,
            };
            detailScroll.ScrollChanged += (s, e) => UpdateActionsText();

            input = new TextBox
            {
                Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 13.5, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 96,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AutomationProperties.SetName(input, "Message Claude");
            input.PreviewKeyDown += OnInputKey;
            var inputHint = Text(13.5, Theme.Dim, FontWeights.Normal);
            inputHint.Text = "Message Claude…";
            inputHint.IsHitTestVisible = false;
            inputHint.Margin = new Thickness(2, 0, 0, 0);
            input.TextChanged += (s, e) => inputHint.Visibility = input.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            inputBar = new Border
            {
                Background = Theme.Chip, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(14, 2, 14, 14), Visibility = Visibility.Collapsed,
                Child = new Grid { Children = { input, inputHint } },
            };

            card = new Border
            {
                Width = 150, // animations need a starting value
                Background = Theme.Black,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 },
                Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { header, progressTrack, detailScroll, quickScroll, inputBar, actions, list } },
            };
            mini = new Border { Cursor = Cursors.Hand };
            AutomationProperties.SetName(mini, "Claude Notch");

            endVoiceItem = Item("End voice conversation", () => { if (EndVoice != null) EndVoice(); });
            holder = new Grid { RenderTransform = slide, LayoutTransform = sizing, Opacity = 0, Children = { card, mini }, ContextMenu = BuildMenu() };
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

        public bool Hovering { get { return hover; } }

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
            Width = WinW;
            Height = WinH;
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

        // ---- input ---------------------------------------------------------

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            double startLeft = Left, startTop = Top;
            try { DragMove(); } catch (InvalidOperationException) { }

            if (Math.Abs(Left - startLeft) < 4 && Math.Abs(Top - startTop) < 4)
            {
                Left = startLeft; Top = startTop;
                if (CanOpenDetails) ToggleDetails();
                else if (OpenPrimary != null) OpenPrimary();
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
