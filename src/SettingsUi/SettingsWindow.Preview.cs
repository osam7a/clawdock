using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeNotch
{
    // The little picture of the notch on the Notch page of the settings.
    partial class SettingsWindow
    {
        // ---- notch preview ---------------------------------------------------

        // A small screen with the notch drawn where it will sit, in the chosen style.
        FrameworkElement PreviewCard()
        {
            var screen = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = p.Line,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(p.ScreenTop, p.ScreenBottom, 90),
                Child = preview,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            previewNotch.Background = Theme.Black;
            preview.Children.Add(previewNotch);
            var stand = new Border { Width = 46, Height = 6, Background = p.Line, CornerRadius = new CornerRadius(0, 0, 3, 3), HorizontalAlignment = HorizontalAlignment.Center };
            var art = new StackPanel { Children = { screen, stand }, HorizontalAlignment = HorizontalAlignment.Left };
            stand.Margin = new Thickness(89, 0, 0, 0);
            stand.HorizontalAlignment = HorizontalAlignment.Left;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = "Preview", FontWeight = FontWeights.SemiBold, Foreground = p.Text, Margin = new Thickness(0, 0, 0, 4) });
            text.Children.Add(new TextBlock { Text = "Where the notch sits and how big it is. It follows the style, edge and position below.", Foreground = p.Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap });

            var grid = new Grid { Margin = new Thickness(18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(art);
            grid.Children.Add(text);
            return new Border { Background = p.Card, BorderBrush = p.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = grid, Margin = new Thickness(0, 0, 0, 16) };
        }

        void UpdatePreview()
        {
            // Sizes are the real notch's, scaled to a 224 px wide screen.
            double along, across;
            switch (s.Style)
            {
                case "compact": along = 36; across = 7; break;
                case "line": along = 26; across = 2.5; break;
                case "dot": along = 6; across = 6; break;
                default: along = 30; across = 11; break;
            }
            bool vertical = s.Edge == "left" || s.Edge == "right";
            double w = vertical ? across : along, h = vertical ? along : across;
            double W = preview.Width, H = preview.Height, t = Math.Max(0, Math.Min(1, s.EdgeOffset));
            double x, y;
            if (vertical) { y = (H - h) * t; x = s.Edge == "left" ? 0 : W - w; }
            else { x = (W - w) * t; y = s.Edge == "top" ? 0 : H - h; }
            previewNotch.Width = w;
            previewNotch.Height = h;
            double r = Math.Min(w, h) / 2;
            switch (s.Edge)
            {
                case "bottom": previewNotch.CornerRadius = new CornerRadius(r, r, 0, 0); break;
                case "left": previewNotch.CornerRadius = new CornerRadius(0, r, r, 0); break;
                case "right": previewNotch.CornerRadius = new CornerRadius(r, 0, 0, r); break;
                default: previewNotch.CornerRadius = new CornerRadius(0, 0, r, r); break;
            }
            if (s.Style == "dot") { previewNotch.CornerRadius = new CornerRadius(3); x = vertical ? (s.Edge == "left" ? 3 : W - w - 3) : x; y = vertical ? y : (s.Edge == "top" ? 3 : H - h - 3); }
            Canvas.SetLeft(previewNotch, x);
            Canvas.SetTop(previewNotch, y);
        }
    }
}
