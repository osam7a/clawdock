using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudeNotch
{
    // A small box under the notch for typing a quick prompt, used when there's no microphone
    // or when typing is the chosen way to talk to Claude. Enter sends, Shift+Enter adds a
    // line, Esc closes. What you typed is kept if you close it without sending.
    class PromptWindow : Window
    {
        public event Action<string, string> Send; // text, target ("desktop" | "cli")

        readonly TextBox box;
        readonly TextBlock note, hint;
        readonly Border desktopChip, cliChip;
        string target;

        public PromptWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 420;
            Title = "Ask Claude";

            var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
            note = new TextBlock
            {
                Foreground = Theme.Amber, FontFamily = font, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 8),
                Visibility = Visibility.Collapsed,
            };
            box = new TextBox
            {
                FontFamily = font, FontSize = 14,
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                Background = Theme.Chip, BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 40, MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AutomationProperties.SetName(box, "Prompt for Claude");
            box.PreviewKeyDown += OnKey;

            desktopChip = Chip("Claude app", () => SetTarget("desktop"));
            cliChip = Chip("Terminal", () => SetTarget("cli"));
            hint = new TextBlock
            {
                Text = "Enter to send · Esc to close", Foreground = Theme.Dim, FontFamily = font, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            };
            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Children = { desktopChip, cliChip } };
            DockPanel.SetDock(chips, Dock.Left);
            bottom.Children.Add(chips);
            bottom.Children.Add(hint);

            Content = new Border
            {
                Margin = new Thickness(12),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(14),
                Background = Theme.Black,
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.4, Direction = 270 },
                Child = new StackPanel { Children = { note, box, bottom } },
            };

            Deactivated += (s, e) => Hide();
        }

        public void Open(Point topLeft, string defaultTarget, string message)
        {
            SetTarget(defaultTarget);
            note.Text = message ?? "";
            note.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            Left = topLeft.X;
            Top = topLeft.Y;
            Show();
            Activate();
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            box.Focus();
            box.SelectAll();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Hide(); return; }
            if (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                string text = box.Text.Trim();
                if (text.Length == 0) return;
                box.Text = "";
                Hide();
                if (Send != null) Send(text, target);
            }
        }

        void SetTarget(string t)
        {
            target = t == "cli" ? "cli" : "desktop";
            desktopChip.Background = target == "desktop" ? Theme.Clay : Theme.Chip;
            cliChip.Background = target == "cli" ? Theme.Clay : Theme.Chip;
        }

        static Border Chip(string text, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 11, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") },
            };
            AutomationProperties.SetName(b, "Send to " + text);
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }
    }
}
