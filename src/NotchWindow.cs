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
        public string Kind = "idle";        // idle | session | voice | alert | question | quick
        public bool Listening, Answering;   // quick voice: hearing you / reading out the answer
        public QuickTurn QuickNow;          // quick voice: the turn in progress, and the ones before it
        public List<QuickTurn> QuickHistory = new List<QuickTurn>();
        public string QuickStatus = "";     // "Thinking…", "Searching the web…", an error
        public bool QuickMinimized, Speaking; // quick voice as a small bar; the answer is being read out
        public bool QuickCanClear;            // a private chat, which can be cleared
        public bool QuickTyping;              // the text box is open
        public MascotMood Mood;               // what the mascot acts out
        public string Title = "Claude", Detail = "", Short = ""; // Short: "4:12 · 3/5", for the compact style
        public bool Busy, JustFinished, Muted;
        public int Done, Total;
        public DateTime VoiceStarted;
        public HookServer.Pending Pending;  // a request waiting for Allow / Deny
        public int MorePending;
        // Buttons under the text: Deny / Allow for a permission request, or the choices of a
        // question Claude asked. Owner is what they answer; Primary is drawn in accent.
        public object Owner;
        public List<string> Choices = new List<string>();
        public int Primary = -1;
        public List<SessionInfo> Sessions = new List<SessionInfo>();
    }

    // The notch: a black card docked to an edge of the screen.
    //
    // Styles
    //   classic  mascot, title and a status line (the original look)
    //   compact  one slim line: mascot and title
    //   line     a thin colored bar on the edge; hover to see the card (it opens by itself when Claude needs you)
    //   dot      a small colored dot; hover to see the card (likewise)
    // Bar and dot colors: gray idle, accent pulsing while working, green in voice (or just
    // finished), amber pulsing when Claude needs permission.
    //
    // Hovering the card lists every running session (click one to jump to it). A permission
    // request shows Allow / Deny. Drag it anywhere: it snaps to the nearest screen edge.
    class NotchWindow : Window
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
        readonly StackPanel quickHistory;
        readonly TextBlock quickNotes;
        readonly TextBox input;
        readonly Border inputBar;
        bool typing;
        IntPtr beforeTyping; // the window you were in, to go back to
        readonly Border youBubble, cardHost;
        readonly TextBlock youText, answerText, quickStatus;
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
                    Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.Accent,
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
            miniGrid.Children.Add(new Rectangle { Fill = Theme.Accent });
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

            var fill = new Rectangle { Fill = Theme.Accent };
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
                Child = new StackPanel { Children = { header, progressTrack, quickScroll, inputBar, actions, list } },
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

        // ---- state ---------------------------------------------------------

        public void Render(NotchState s)
        {
            state = s;
            bool bars = s.Kind == "voice" || (s.Kind == "quick" && (s.Listening || s.Speaking));
            if (bars && !voiceTimer.IsEnabled) voiceTimer.Start();
            if (!bars) voiceTimer.Stop();
            Refresh();
        }

        void Refresh()
        {
            var s = state;
            bool alert = s.Kind == "alert";
            bool asking = alert || s.Kind == "question";
            bool quick = s.Kind == "quick";
            bool listening = quick && s.Listening;
            bool hoverExpand = hover && settings.ExpandOnHover;
            // The bar and dot open into the card by themselves when something needs you: a permission
            // request, a question, or quick voice.
            bool expanded = !Mini || hoverExpand || asking || quick;
            bool showList = hoverExpand && s.Sessions.Count > 0 && !asking && !quick;
            bool showActions = s.Owner != null && s.Choices.Count > 0 && expanded;
            if (!showActions) actionsFor = null;
            else if (actionsFor != s.Owner) { actionsFor = s.Owner; actionsSince = DateTime.Now; BuildChoices(s); }

            mascot.Mood = alert ? MascotMood.Alert : s.Kind == "question" ? MascotMood.Question : s.Mood;

            bool isOpen = asking || (quick && !s.QuickMinimized) || showList || showActions;
            double size = SizeOf(isOpen ? settings.NotchSizeOpen : settings.NotchSizeCollapsed);
            if (size != sizeNow)
            {
                sizeNow = size;
                var grow = new DoubleAnimation(size, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                sizing.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                sizing.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }

            title.Inlines.Clear();
            title.Inlines.Add(new Run(s.Title));
            bool showSub = (s.Kind == "session" || asking) && !string.IsNullOrEmpty(s.Detail);
            if (Compact && s.Kind == "session")
            {
                string brief = !string.IsNullOrEmpty(s.Short) ? s.Short : s.Detail;
                if (s.Total > 0 && brief != null) { int dot = brief.IndexOf(" · "); if (dot > 0) brief = brief.Substring(0, dot); }
                if (!string.IsNullOrEmpty(brief)) title.Inlines.Add(new Run("  " + brief) { Foreground = Theme.Dim, FontWeight = FontWeights.Normal });
                showSub = false;
            }
            subtitle.Text = s.Detail;
            // A question can be a full sentence: let it wrap onto a second line.
            subtitle.TextWrapping = s.Kind == "question" ? TextWrapping.Wrap : TextWrapping.NoWrap;
            subtitle.MaxHeight = s.Kind == "question" ? 34 : double.PositiveInfinity;
            // The line is cut to fit; hover shows the whole command before you allow it.
            card.ToolTip = alert && s.Pending != null && !string.IsNullOrEmpty(s.Pending.FullDetail) ? s.Pending.FullDetail : null;
            if (s.Kind == "voice") title.Inlines.Clear();
            if (s.Kind == "voice") title.Inlines.Add(new Run(s.Muted ? "Muted" : "Listening"));
            // Minimized, the bar shows the answer's key figure (a price, a time) next to what it's doing.
            bool minimized = quick && s.QuickMinimized;
            var barCard = minimized && !listening && s.QuickNow != null ? s.QuickNow.Card : null;
            if (barCard != null)
            {
                title.Inlines.Clear();
                if ((barCard.Kind == "value" || barCard.Kind == "weather") && barCard.Value != "")
                {
                    KeyValuePair<string, Brush> sign;
                    if (barCard.Kind == "weather" && WeatherSigns.TryGetValue(barCard.Weather, out sign))
                        title.Inlines.Add(new Run(sign.Key + " ") { FontFamily = Theme.Symbols, Foreground = sign.Value, FontSize = Compact ? 13 : 15 });
                    title.Inlines.Add(new Run(barCard.Value) { FontSize = Compact ? 14 : 16, FontWeight = FontWeights.Bold });
                    if (barCard.Change != "") { title.Inlines.Add(new Run("  ")); title.Inlines.Add(ChangeRun(barCard, 12)); }
                    if (barCard.Label != "") title.Inlines.Add(new Run("  " + barCard.Label) { Foreground = Theme.Dim, FontWeight = FontWeights.Normal, FontSize = 12 });
                }
                else if (barCard.Title != "") title.Inlines.Add(new Run(barCard.Title));
                else if (barCard.Kind == "list") title.Inlines.Add(new Run(barCard.Items[0].Key + "  " + barCard.Items[0].Value));
                else if (barCard.Kind == "steps") title.Inlines.Add(new Run(barCard.Steps.Count + " steps"));
                else if (barCard.Kind == "compare") title.Inlines.Add(new Run(string.Join(" vs ", barCard.Columns)));
                else title.Inlines.Add(new Run(barCard.Label));
            }
            bool speakingBars = quick && s.Speaking && !listening;
            foreach (var b in bars) b.Fill = speakingBars ? Theme.Accent : quick ? Theme.Green : Theme.Accent;
            minGlyph.Text = minimized ? "" : ""; // FullScreen / ChromeMinimize
            minButton.ToolTip = minimized ? "Show the whole conversation" : "Minimize to a small bar (keeps listening for the shortcut)";
            AutomationProperties.SetName(minButton, minimized ? "Expand" : "Minimize");

            bool hasTasks = s.Kind == "session" && s.Total > 0 && !Compact;
            bool compactHasTasks = s.Kind == "session" && s.Total > 0 && Compact;
            if (compactHasTasks)
            {
                miniDone.Width = new GridLength(s.Done, GridUnitType.Star);
                miniLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
                compactCount.Text = s.Done + "/" + s.Total;
            }
            if (hasTasks)
            {
                progressDone.Width = new GridLength(s.Done, GridUnitType.Star);
                progressLeft.Width = new GridLength(Math.Max(s.Total - s.Done, 0), GridUnitType.Star);
            }
            micGlyph.Text = s.Muted ? "" : ""; // MicOff / Microphone
            micButton.Background = s.Muted ? Theme.Accent : Theme.Chip;
            closeButton.ToolTip = s.Kind == "voice" ? "End voice and hide" : quick ? "Stop and hide" : "Hide for now";
            endVoiceItem.Visibility = s.Kind == "voice" ? Visibility.Visible : Visibility.Collapsed;
            moreText.Text = s.MorePending > 0 ? "+" + s.MorePending + " more" : "";
            if (s.Kind == "voice" || listening || speakingBars) TickVoice();
            if (showList) BuildList(s.Sessions);
            if (quick && !minimized) UpdateQuick(s);
            UpdateMini(s);

            string key = s.Kind + "|" + listening + "|" + minimized + "|" + speakingBars + "|" + (barCard != null) + "|" + hover + "|" + s.Visible + "|" + hasTasks + "|" + compactHasTasks + "|" + showList + "|" + showActions + "|" + showSub + "|" + settings.Style + "|" + settings.Edge;
            if (key == layoutKey) return;
            layoutKey = key;

            card.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            mini.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            subtitle.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
            voicePanel.Visibility = s.Kind == "voice" || listening || speakingBars ? Visibility.Visible : Visibility.Collapsed;
            timerText.Visibility = s.Kind == "voice" || (listening && !minimized) ? Visibility.Visible : Visibility.Collapsed;
            openChatButton.Visibility = quick && hover && !minimized ? Visibility.Visible : Visibility.Collapsed;
            inputBar.Visibility = quick && s.QuickTyping ? Visibility.Visible : Visibility.Collapsed;
            SetTyping(quick && s.QuickTyping);
            minButton.Visibility = quick && hover ? Visibility.Visible : Visibility.Collapsed;
            clearButton.Visibility = quick && hover && !minimized && s.QuickCanClear ? Visibility.Visible : Visibility.Collapsed;
            micButton.Visibility = quick ? Visibility.Collapsed : Visibility.Visible;
            alertDot.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
            quickScroll.Visibility = quick && !minimized ? Visibility.Visible : Visibility.Collapsed;
            quickScroll.MaxHeight = Math.Min(Compact ? 380 : 460, WorkArea().Height / SizeOf(settings.NotchSizeOpen) - 200); // large sizes still fit the screen
            alertDot.Fill = alert ? Theme.Amber : Theme.Blue;
            closeButton.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            progressTrack.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
            compactTasks.Visibility = compactHasTasks ? Visibility.Visible : Visibility.Collapsed;
            actions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

            // A notice (a permission request to answer elsewhere) is a small card with one button.
            bool notice = alert && s.Pending == null;
            double w = (notice ? (Compact ? 270 : 300) : CardWidth(s.Kind)) + (hover && !Mini && !quick ? 26 : 0); // quick: don't reflow what you're reading
            if (compactHasTasks) w += 56;
            if (minimized) w = (Compact ? 290 : 320) + (barCard != null ? 90 : 0);
            if (showList || (showActions && !notice)) w = Math.Max(w, Compact ? 330 : 360);
            Animate(card, WidthProperty, w, 220);
            if (s.Kind == "question" || quick) header.Height = double.NaN; // the text may wrap: fit it
            else header.Height = Compact ? 30 : s.Kind == "session" || alert ? (hasTasks ? 54 : 56) : 38;
            header.Margin = s.Kind == "question" || quick ? (Compact || minimized ? new Thickness(0, 7, 0, 7) : new Thickness(0, 10, 0, 6)) : new Thickness(0);
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
            if (Compact) return kind == "quick" ? 420 : kind == "alert" || kind == "question" ? 330 : kind == "session" ? 280 : kind == "voice" ? 230 : 110;
            return kind == "quick" ? 460 : kind == "question" ? 380 : kind == "alert" ? 360 : kind == "session" ? 340 : kind == "voice" ? 280 : 150;
        }

        // ---- quick voice ---------------------------------------------------

        void UpdateQuick(NotchState s)
        {
            var now = s.QuickNow ?? new QuickTurn();
            bool follow = !hover || quickScroll.VerticalOffset >= quickScroll.ScrollableHeight - 4; // you scrolled up to read: stay there

            var sb = new StringBuilder();
            foreach (var t in s.QuickHistory) sb.Append(t.Heard).Append('|').Append(t.Text.Length).Append('|').Append(t.Notes.Count).Append('\n');
            if (sb.ToString() != historyKey)
            {
                historyKey = sb.ToString();
                quickHistory.Children.Clear();
                foreach (var t in s.QuickHistory) quickHistory.Children.Add(PastTurn(t));
                if (s.QuickHistory.Count > 0) quickHistory.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 2, 0, 12) });
                follow = true;
            }

            youText.Text = now.Heard;
            youText.Foreground = s.Listening ? Theme.Dim : Brushes.White; // still hearing you
            youBubble.Visibility = now.Heard != "" ? Visibility.Visible : Visibility.Collapsed;
            string ck = now.Card == null ? "" : now.Card.Source;
            if (ck != cardKey) { cardKey = ck; cardHost.Child = now.Card == null ? null : CardView(now.Card, false); }
            cardHost.Visibility = now.Card != null ? Visibility.Visible : Visibility.Collapsed;
            cardHost.Margin = new Thickness(0, now.Heard != "" ? 10 : 0, 0, 0);
            quickNotes.Text = string.Join("\n", now.Notes);
            quickNotes.Visibility = now.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            answerText.Text = now.Text;
            answerText.Visibility = now.Text != "" ? Visibility.Visible : Visibility.Collapsed;
            quickStatus.Text = s.QuickStatus;
            quickStatus.Visibility = s.QuickStatus != "" ? Visibility.Visible : Visibility.Collapsed;

            if (now.Heard != turnKey) { turnKey = now.Heard; follow = true; }
            if (follow) Dispatcher.BeginInvoke(new Action(() => quickScroll.ScrollToEnd()), DispatcherPriority.Loaded);
        }

        // An earlier question and its answer, smaller and dimmer.
        FrameworkElement PastTurn(QuickTurn t)
        {
            var q = Wrapped(12, Theme.Dim);
            q.Text = t.Heard;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(new Border
            {
                Background = Theme.Chip, CornerRadius = new CornerRadius(12, 12, 4, 12), Padding = new Thickness(10, 5, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(72, 0, 0, 6), Child = q,
            });
            if (t.Card != null) panel.Children.Add(CardView(t.Card, true));
            if (t.Text != "")
            {
                var a = Wrapped(12.5, Theme.Dim);
                a.Text = t.Text + (t.Cut ? " …" : "");
                a.LineHeight = 18;
                a.MaxHeight = 54; // three lines
                a.TextTrimming = TextTrimming.WordEllipsis;
                a.Margin = new Thickness(2, t.Card != null ? 6 : 0, 2, 0);
                panel.Children.Add(a);
            }
            if (t.Notes.Count > 0)
            {
                var n = Wrapped(11.5, Theme.Dim);
                n.Text = string.Join("\n", t.Notes);
                n.Margin = new Thickness(2, 6, 2, 0);
                panel.Children.Add(n);
            }
            return panel;
        }

        // The key fact, big. Small is the shorter version shown in the history.
        static FrameworkElement CardView(QuickCard c, bool small)
        {
            var stack = new StackPanel();
            if (c.Kind == "list") ListCard(stack, c, small);
            else if (c.Kind == "steps") StepsCard(stack, c, small);
            else if (c.Kind == "compare") CompareCard(stack, c, small);
            else if (c.Kind == "weather") WeatherCard(stack, c, small);
            else ValueCard(stack, c, small);
            if (c.Note != "" && !small && c.Kind != "weather") { var n = Wrapped(12, Theme.Dim); n.Text = c.Note; n.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(n); }
            return new Border
            {
                Background = Theme.Raised, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(small ? 10 : 14),
                Padding = small ? new Thickness(10, 6, 10, 7) : new Thickness(16, 12, 16, 14),
                Child = stack,
            };
        }

        // The change after a value, colored: green when it went up, red when it went down.
        static Run ChangeRun(QuickCard c, double size)
        {
            bool? up = c.ChangeUp;
            char first = c.Change.TrimStart()[0];
            string arrow = up == true && first == '+' ? "▲ " : up == false && (first == '-' || first == '−') ? "▼ " : "";
            string text = arrow != "" ? c.Change.TrimStart().Substring(1).TrimStart() : c.Change;
            return new Run(arrow + text)
            {
                FontSize = size, FontWeight = FontWeights.SemiBold,
                Foreground = up == true ? Theme.Green : up == false ? Theme.Red : Theme.Dim,
            };
        }

        // One figure with its label; a change next to it and a progress bar under it.
        static void ValueCard(StackPanel stack, QuickCard c, bool small)
        {
            if (c.Label != "") { var l = Wrapped(small ? 11 : 12.5, Theme.Dim); l.Text = c.Label; stack.Children.Add(l); }
            var v = Wrapped(small ? 17 : 30, Brushes.White);
            v.FontWeight = FontWeights.Bold;
            v.Text = c.Value;
            v.Margin = new Thickness(0, small ? 0 : 1, 0, 0);
            stack.Children.Add(v);
            if (c.Change != "")
            {
                var ch = Wrapped(small ? 11.5 : 14, Brushes.White);
                ch.Inlines.Add(ChangeRun(c, small ? 11.5 : 14));
                stack.Children.Add(ch);
            }
            if (c.Progress >= 0)
            {
                var track = new Grid { Height = small ? 4 : 6, Margin = new Thickness(0, small ? 4 : 8, 0, small ? 1 : 2) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(c.Progress, 0.0001), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - c.Progress, 0.0001), GridUnitType.Star) });
                var back = new Border { Background = Theme.Line, CornerRadius = new CornerRadius(3) };
                Grid.SetColumnSpan(back, 2);
                track.Children.Add(back);
                track.Children.Add(new Border { Background = c.Progress >= 1 ? Theme.Green : Theme.Accent, CornerRadius = new CornerRadius(3) });
                stack.Children.Add(track);
            }
        }

        static void CardTitle(StackPanel stack, string text, bool small)
        {
            if (text == "") return;
            var title = Wrapped(small ? 11 : 12.5, Theme.Dim);
            title.FontWeight = FontWeights.SemiBold;
            title.Text = text;
            title.Margin = new Thickness(0, 0, 0, small ? 2 : 4);
            stack.Children.Add(title);
        }

        static void More(StackPanel stack, int hidden)
        {
            if (hidden <= 0) return;
            var more = Wrapped(11, Theme.Dim);
            more.Text = "+" + hidden + " more";
            stack.Children.Add(more);
        }

        // A few rows: the name, and its value bold on the right.
        static void ListCard(StackPanel stack, QuickCard c, bool small)
        {
            CardTitle(stack, c.Title, small);
            int rows = small ? Math.Min(c.Items.Count, 3) : c.Items.Count;
            for (int i = 0; i < rows; i++)
            {
                var name = Wrapped(small ? 12 : 14, Brushes.White);
                name.Text = c.Items[i].Key;
                var value = Wrapped(small ? 12 : 14, Brushes.White);
                value.Text = c.Items[i].Value;
                value.FontWeight = FontWeights.Bold;
                value.TextAlignment = TextAlignment.Right;
                value.Margin = new Thickness(12, 0, 0, 0);
                value.MaxWidth = 200;
                var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, small ? 2 : 6, 0, small ? 2 : 6) };
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(value);
                row.Children.Add(name);
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Theme.Line });
                stack.Children.Add(row);
            }
            More(stack, c.Items.Count - rows);
        }

        // Numbered steps, each number in a small circle (the first in accent).
        static void StepsCard(StackPanel stack, QuickCard c, bool small)
        {
            CardTitle(stack, c.Title, small);
            int rows = small ? Math.Min(c.Steps.Count, 3) : c.Steps.Count;
            for (int i = 0; i < rows; i++)
            {
                double d = small ? 16 : 22;
                var number = new Border
                {
                    Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Background = i == 0 ? Theme.Accent : Theme.Chip,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, small ? 8 : 10, 0),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(), FontFamily = Theme.Font, FontSize = small ? 9.5 : 11.5, FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                var text = Wrapped(small ? 12 : 14, Brushes.White);
                text.Text = c.Steps[i];
                text.Margin = new Thickness(0, small ? 0 : 1, 0, 0);
                var row = new DockPanel { Margin = new Thickness(0, small ? 2 : 5, 0, small ? 2 : 5) };
                DockPanel.SetDock(number, Dock.Left);
                row.Children.Add(number);
                row.Children.Add(text);
                stack.Children.Add(row);
            }
            More(stack, c.Steps.Count - rows);
        }

        // Two or three things side by side: their names on top, then one row per point.
        static void CompareCard(StackPanel stack, QuickCard c, bool small)
        {
            CardTitle(stack, c.Title, small);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
            foreach (var col in c.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = small ? Math.Min(c.Rows.Count, 2) : c.Rows.Count;
            for (int r = 0; r <= rows; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (r > 0)
                {
                    var line = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Top };
                    Grid.SetRow(line, r);
                    Grid.SetColumnSpan(line, c.Columns.Count + 1);
                    grid.Children.Add(line);
                }
                for (int k = 0; k <= c.Columns.Count; k++)
                {
                    if (r == 0 && k == 0) continue;
                    bool head = r == 0, what = k == 0;
                    var cell = Wrapped(head || what ? (small ? 11 : 12.5) : (small ? 12 : 14), head ? Brushes.White : what ? Theme.Dim : Brushes.White);
                    cell.Text = head ? c.Columns[k - 1] : c.Rows[r - 1][k];
                    cell.FontWeight = head ? FontWeights.SemiBold : what ? FontWeights.Normal : FontWeights.Bold;
                    cell.Margin = new Thickness(k == 0 ? 0 : 8, small ? 2 : 6, 0, small ? 2 : 6);
                    cell.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, k);
                    grid.Children.Add(cell);
                }
            }
            stack.Children.Add(grid);
            More(stack, c.Rows.Count - rows);
        }

        // The weather now (its sign, the temperature, where), then a few days ahead.
        static void WeatherCard(StackPanel stack, QuickCard c, bool small)
        {
            var now = new DockPanel();
            var sign = WeatherSign(c.Weather, small ? 22 : 40);
            sign.Margin = new Thickness(0, 0, small ? 8 : 14, 0);
            sign.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(sign, Dock.Left);
            now.Children.Add(sign);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (c.Label != "") { var l = Wrapped(small ? 11 : 12.5, Theme.Dim); l.Text = c.Label; right.Children.Add(l); }
            if (c.Value != "") { var v = Wrapped(small ? 17 : 30, Brushes.White); v.FontWeight = FontWeights.Bold; v.Text = c.Value; right.Children.Add(v); }
            if (c.Note != "" && !small) { var n = Wrapped(12, Theme.Dim); n.Text = c.Note; right.Children.Add(n); }
            now.Children.Add(right);
            stack.Children.Add(now);
            if (small || c.Days.Count == 0) return;
            stack.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 10, 0, 0) });
            var days = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = c.Days.Count, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var d in c.Days)
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var name = Wrapped(11.5, Theme.Dim);
                name.Text = d[0];
                name.TextAlignment = TextAlignment.Center;
                col.Children.Add(name);
                var s = WeatherSign(d[1], 18);
                s.HorizontalAlignment = HorizontalAlignment.Center;
                s.Margin = new Thickness(0, 3, 0, 3);
                col.Children.Add(s);
                var t = Wrapped(12.5, Brushes.White);
                t.TextAlignment = TextAlignment.Center;
                t.Inlines.Add(new Run(d[2]) { FontWeight = FontWeights.Bold });
                if (d[3] != "") t.Inlines.Add(new Run(" " + d[3]) { Foreground = Theme.Dim });
                col.Children.Add(t);
                days.Children.Add(col);
            }
            stack.Children.Add(days);
        }

        // The sign for each kind of weather (Segoe UI Symbol), with its color.
        static readonly Dictionary<string, KeyValuePair<string, Brush>> WeatherSigns = new Dictionary<string, KeyValuePair<string, Brush>>
        {
            { "sun", new KeyValuePair<string, Brush>("☀", Theme.Amber) },
            { "partly", new KeyValuePair<string, Brush>("⛅", Theme.Amber) },
            { "cloud", new KeyValuePair<string, Brush>("☁", Brushes.White) },
            { "rain", new KeyValuePair<string, Brush>("☔", Theme.Blue) },
            { "storm", new KeyValuePair<string, Brush>("⛈", Theme.Amber) },
            { "snow", new KeyValuePair<string, Brush>("❄", Brushes.White) },
            { "fog", new KeyValuePair<string, Brush>("\U0001F32B", Theme.Dim) },
            { "wind", new KeyValuePair<string, Brush>("\U0001F32C", Theme.Dim) },
            { "night", new KeyValuePair<string, Brush>("☾", Theme.Blue) },
        };

        static TextBlock WeatherSign(string weather, double size)
        {
            KeyValuePair<string, Brush> sign;
            if (!WeatherSigns.TryGetValue(weather ?? "", out sign)) sign = WeatherSigns["cloud"];
            return new TextBlock { Text = sign.Key, FontFamily = Theme.Symbols, FontSize = size, Foreground = sign.Value };
        }

        void UpdateMini(NotchState s)
        {
            bool dot = settings.Style == "dot";
            Brush color = s.Kind == "alert" ? Theme.Amber
                : s.Kind == "question" ? Theme.Blue
                : s.Kind == "voice" ? (s.Muted ? Theme.Dim : Theme.Green)
                : s.Kind == "quick" ? (s.Listening ? Theme.Green : Theme.Blue)
                : s.Busy ? Theme.Accent
                : s.JustFinished ? Theme.Green
                : Theme.Idle;
            bool pulse = s.Kind == "alert" || s.Kind == "question" || s.Kind == "quick" || s.Busy;
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
            // Rebuild only when the rows themselves change. A working session's clock ticks every
            // second, so its text is updated in place (rebuilding made hovering flicker).
            var sb = new StringBuilder();
            int shownCount = Math.Min(sessions.Count, 5);
            for (int i = 0; i < shownCount; i++)
            {
                var x = sessions[i];
                sb.Append(x.Id).Append(x.Busy).Append(x.JustFinished).Append(x.Done).Append('/').Append(x.Total).Append(x.DisplayName).Append(x.Cwd).Append('|');
            }
            sb.Append(sessions.Count);
            if (sb.ToString() == listKey)
            {
                for (int i = 0; i < shownCount && i < rowStatus.Count; i++) rowStatus[i].Text = RowStatus(sessions[i]);
                return;
            }
            listKey = sb.ToString();

            list.Children.Clear();
            rowStatus.Clear();
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
            // A laptop for an app session, a terminal for a CLI one, colored by what it's doing.
            var dot = new TextBlock
            {
                FontFamily = Theme.Icons, FontSize = 13, Width = 16, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(3, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Text = s.Desktop ? "" : "", // Laptop / CommandPrompt
                Foreground = s.Busy ? Theme.Accent : s.JustFinished ? Theme.Green : Theme.Idle,
            };
            var name = Text(12, Brushes.White, FontWeights.Normal);
            name.Text = s.DisplayName;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 190;
            // The project it runs in, unless the name already says it.
            string project = string.Equals((s.Cwd ?? "").TrimEnd('\\', '/'), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) ? "~" : s.Project;
            var where = Text(11, Theme.Dim, FontWeights.Normal);
            where.Text = project != "" && !string.Equals(project, s.DisplayName, StringComparison.OrdinalIgnoreCase) ? project : "";
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            where.MaxWidth = 120;
            where.Margin = new Thickness(8, 0, 0, 0);
            where.VerticalAlignment = VerticalAlignment.Center;
            var status = Text(11, Theme.Dim, FontWeights.Normal);
            status.Text = RowStatus(s);
            status.Margin = new Thickness(8, 0, 6, 0);
            status.VerticalAlignment = VerticalAlignment.Center;
            rowStatus.Add(status);

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

        // The buttons for the current request or question, the primary one in accent.
        void BuildChoices(NotchState s)
        {
            while (actions.Children.Count > 1) actions.Children.RemoveAt(1); // keep the "+N more" text
            var owner = s.Owner;
            for (int i = 0; i < s.Choices.Count; i++)
            {
                int index = i;
                actions.Children.Add(PillButton(s.Choices[i], i == s.Primary ? Theme.Accent : Theme.Chip, () => Pick(owner, index)));
            }
        }

        // Answers only what the buttons were showing, only while they're visible, and not in
        // the first moments after it appeared, so something that pops up under the cursor
        // (or a click meant for the previous one) can't be answered by accident.
        void Pick(object owner, int index)
        {
            if (owner == null || owner != actionsFor || owner != state.Owner || actions.Visibility != Visibility.Visible) return;
            var p = owner as HookServer.Pending;
            if (p != null && p.Settled) return;
            if ((DateTime.Now - actionsSince).TotalMilliseconds < 700) return;
            if (Choose != null) Choose(owner, index);
        }

        void TickVoice()
        {
            var t = DateTime.Now - state.VoiceStarted;
            timerText.Text = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            // Quick voice follows how loud you (or the answer) really are, when that's known.
            double level = state.Kind == "quick" && QuickLevel != null ? QuickLevel() : -1;
            for (int i = 0; i < bars.Length; i++)
            {
                double center = 1 - Math.Abs(i - 2) / 3.0; // middle bars run taller
                if (level >= 0) bars[i].Height = 3 + Math.Min(1, level * 1.6) * 15 * (0.45 + 0.55 * center) * (0.7 + rnd.NextDouble() * 0.6);
                else bars[i].Height = state.Muted ? 3 : 4 + rnd.NextDouble() * 14 * center + rnd.NextDouble() * 3;
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

        static TextBlock Wrapped(double size, Brush color)
        {
            return new TextBlock { FontFamily = Theme.Font, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap };
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

        // Enter sends; Shift+Enter is a new line.
        void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            string text = input.Text;
            if (text.Trim() == "") return;
            input.Clear();
            if (SendTyped != null) SendTyped(text);
        }

        // The notch never takes focus, except while its text box is open. Then it goes back to
        // the window you were in.
        void SetTyping(bool on)
        {
            if (on == typing) return;
            typing = on;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE);
            if (on)
            {
                beforeTyping = Native.GetForegroundWindow();
                // After the card has appeared: a window that isn't showing can't take focus.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!typing) return;
                    Native.ForceForeground(hwnd);
                    Activate();
                    input.Focus();
                    Keyboard.Focus(input);
                }), DispatcherPriority.Loaded);
            }
            else
            {
                bool hadFocus = Native.GetForegroundWindow() == hwnd;
                if (hadFocus && beforeTyping != IntPtr.Zero && Native.IsWindow(beforeTyping)) Native.ForceForeground(beforeTyping);
                beforeTyping = IntPtr.Zero;
            }
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
