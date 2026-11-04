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
    // The notch: small building blocks for its controls.
    partial class NotchWindow
    {
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
    }
}
