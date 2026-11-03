using System;
using System.Runtime.InteropServices;
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
    // The black pill that drops down from the top of the screen while Claude is
    // minimized. Widens into a "voice" layout with live bars while the mic is in use.
    class NotchWindow : Window
    {
        const double NotchHeight = 38, IdleWidth = 150, VoiceWidth = 250, HoverGrow = 14;

        public event Action Clicked;

        readonly Border shell;
        readonly Mascot mascot;
        readonly TextBlock label, timerText;
        readonly StackPanel voicePanel;
        readonly Rectangle[] bars = new Rectangle[5];
        readonly TranslateTransform slide = new TranslateTransform(0, -NotchHeight - 12);
        readonly DispatcherTimer voiceTimer;
        readonly Random rnd = new Random();

        bool visible, voice, hover;
        DateTime voiceStarted;

        public NotchWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = VoiceWidth + HoverGrow + 40;
            Height = NotchHeight + 24;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;

            mascot = new Mascot(2.4) { Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };

            label = new TextBlock
            {
                Text = "Claude",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };

            timerText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            voicePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            voicePanel.Children.Add(timerText);
            var barBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5,
                    Fill = barBrush,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                voicePanel.Children.Add(bars[i]);
            }

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(mascot, Dock.Left);
            DockPanel.SetDock(voicePanel, Dock.Right);
            dock.Children.Add(mascot);
            dock.Children.Add(voicePanel);
            dock.Children.Add(label);

            shell = new Border
            {
                Width = IdleWidth,
                Height = NotchHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B)),
                CornerRadius = new CornerRadius(0, 0, 19, 19),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270 },
                RenderTransform = slide,
                Cursor = Cursors.Hand,
                ToolTip = "Open Claude",
                Child = dock,
            };
            shell.MouseEnter += (s, e) => { hover = true; AnimateWidth(); };
            shell.MouseLeave += (s, e) => { hover = false; AnimateWidth(); };
            shell.MouseLeftButtonUp += (s, e) => { if (Clicked != null) Clicked(); };

            Content = new Grid { Children = { shell } };

            voiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            voiceTimer.Tick += (s, e) => TickVoice();

            SourceInitialized += (s, e) => MakeToolWindow();
        }

        public void Update(bool claudeMinimized, bool inVoice)
        {
            if (inVoice != voice)
            {
                voice = inVoice;
                mascot.Talking = voice;
                label.Text = voice ? "Listening" : "Claude";
                voicePanel.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
                if (voice) { voiceStarted = DateTime.Now; TickVoice(); voiceTimer.Start(); }
                else voiceTimer.Stop();
                AnimateWidth();
            }

            if (claudeMinimized != visible)
            {
                visible = claudeMinimized;
                if (visible) Show();
                var anim = new DoubleAnimation(visible ? 0 : -NotchHeight - 12, TimeSpan.FromMilliseconds(visible ? 320 : 220))
                {
                    EasingFunction = visible
                        ? (IEasingFunction)new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                        : new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                anim.Completed += (s, e) => { if (!visible) Hide(); };
                slide.BeginAnimation(TranslateTransform.YProperty, anim);
            }
        }

        void AnimateWidth()
        {
            double target = (voice ? VoiceWidth : IdleWidth) + (hover ? HoverGrow : 0);
            shell.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        void TickVoice()
        {
            var t = DateTime.Now - voiceStarted;
            timerText.Text = string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            for (int i = 0; i < bars.Length; i++)
            {
                double center = 1 - Math.Abs(i - 2) / 3.0; // middle bars run taller
                bars[i].Height = 4 + rnd.NextDouble() * 14 * center + rnd.NextDouble() * 3;
            }
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
