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

namespace Clawdock
{
    // The notch: the quick voice chat, the minimized bar, and the text box.
    partial class NotchWindow
    {
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
            // Only when it changes: rebuilding a link under the mouse would lose the click.
            if (now.Text != answerKey) { answerKey = now.Text; ShowWithLinks(answerText, now.Text, ""); }
            answerText.Visibility = now.Text != "" ? Visibility.Visible : Visibility.Collapsed;
            quickStatus.Text = s.QuickStatus;
            quickStatus.Visibility = s.QuickStatus != "" ? Visibility.Visible : Visibility.Collapsed;

            if (now.Heard != turnKey) { turnKey = now.Heard; follow = true; }
            if (follow) Dispatcher.BeginInvoke(new Action(() => quickScroll.ScrollToEnd()), DispatcherPriority.Loaded);
        }

        static void ShowWithLinks(TextBlock box, string text, string after)
        {
            box.Inlines.Clear();
            foreach (var part in AnswerLinks.Split(text))
                box.Inlines.Add(part.Url == null ? (Inline)new Run(part.Text) : LinkTo(part, box.Foreground));
            if (after != "") box.Inlines.Add(new Run(after));
        }

        // A link with a thin underline. The tooltip shows where it really goes.
        static Hyperlink LinkTo(TextPart part, Brush color)
        {
            var line = new TextDecoration(TextDecorationLocation.Underline, new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 0.75), 1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel);
            var link = new Hyperlink(new Run(part.Text))
            {
                Foreground = color, Focusable = false, Cursor = Cursors.Hand, ToolTip = part.Url,
                TextDecorations = new TextDecorationCollection { line },
            };
            link.MouseEnter += (a, b) => link.Foreground = Theme.Clay;
            link.MouseLeave += (a, b) => link.Foreground = color;
            // Not a press on the notch, which would start dragging it.
            link.MouseLeftButtonDown += (a, b) => b.Handled = true;
            link.Click += (a, b) => AnswerLinks.Open(part.Url);
            return link;
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
                ShowWithLinks(a, t.Text, t.Cut ? " …" : "");
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


        void UpdateMini(NotchState s)
        {
            bool dot = settings.Style == "dot";
            Brush color = s.Kind == "alert" ? Theme.Amber
                : s.Kind == "question" ? Theme.Blue
                : s.Kind == "voice" ? (s.Muted ? Theme.Dim : Theme.Green)
                : s.Kind == "quick" ? (s.Listening ? Theme.Green : Theme.Blue)
                : s.Busy ? Theme.Clay
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
            AutomationProperties.SetName(mini, "Clawdock: " + state.Title);
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
    }
}
