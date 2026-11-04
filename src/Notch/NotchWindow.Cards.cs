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
    // The notch: how each kind of answer card is drawn.
    partial class NotchWindow
    {
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
                track.Children.Add(new Border { Background = c.Progress >= 1 ? Theme.Green : Theme.Clay, CornerRadius = new CornerRadius(3) });
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

        // Numbered steps, each number in a small circle (the first in clay).
        static void StepsCard(StackPanel stack, QuickCard c, bool small)
        {
            CardTitle(stack, c.Title, small);
            int rows = small ? Math.Min(c.Steps.Count, 3) : c.Steps.Count;
            for (int i = 0; i < rows; i++)
            {
                double d = small ? 16 : 22;
                var number = new Border
                {
                    Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Background = i == 0 ? Theme.Clay : Theme.Chip,
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
    }
}
