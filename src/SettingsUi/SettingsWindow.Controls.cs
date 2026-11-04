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

namespace Clawdock
{
    // Building blocks for the settings pages.
    partial class SettingsWindow
    {
        // ---- building blocks ----------------------------------------------

        readonly Dictionary<string, RadioButton> navItems = new Dictionary<string, RadioButton>();

        RadioButton NavItem(string key, string label, string glyph)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 15, Width = 22, Foreground = p.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var item = new RadioButton
            {
                Style = (Style)Resources["Nav"],
                GroupName = "nav",
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } },
            };
            System.Windows.Automation.AutomationProperties.SetName(item, label); // for screen readers (the icon has no name)
            item.Checked += (a, b) => { icon.Foreground = p.Clay; Show(key); };
            item.Unchecked += (a, b) => icon.Foreground = p.Sub;
            navItems[key] = item;
            return item;
        }

        void Page(string key, string title, params FrameworkElement[] parts)
        {
            // Cards fill the width, up to 640 px, then stay put on the left.
            var stack = new StackPanel();
            foreach (var part in parts) stack.Children.Add(part);
            var page = new Grid { Margin = new Thickness(36, 0, 28, 24), Tag = title };
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 640 });
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            page.Children.Add(stack);
            pages[key] = page;
        }

        TextBlock Section(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = p.Sub, Margin = new Thickness(4, 22, 0, 8) };
        }

        TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, Foreground = p.Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 0), LineHeight = 18 };
        }

        // A rounded card of rows with thin lines between them.
        Border Card(params FrameworkElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    // The line above a row comes and goes with it.
                    var line = new Border { Height = 1, Background = p.Line, Margin = new Thickness(18, 0, 0, 0) };
                    line.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Visibility") { Source = rows[i] });
                    stack.Children.Add(line);
                }
                stack.Children.Add(rows[i]);
            }
            return new Border { Background = p.Card, BorderBrush = p.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = stack };
        }

        // A label (and an optional line of help) on the left, its control on the right.
        FrameworkElement ProjectsFolderBox(Settings s)
        {
            var box = new TextBox { Width = 230, Text = s.ProjectsFolder };
            box.LostFocus += (a, b) =>
            {
                string v = box.Text.Trim();
                if (v != "" && System.IO.Path.IsPathRooted(v)) s.ProjectsFolder = v;
                else box.Text = s.ProjectsFolder;
            };
            return box;
        }

        FrameworkElement Row(string label, string help, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(18, 13, 18, 13) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
            text.Children.Add(new TextBlock { Text = label, Foreground = p.Text, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(help))
                text.Children.Add(new TextBlock { Text = help, Foreground = p.Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            control.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            return grid;
        }

        FrameworkElement Toggle(string label, string help, bool value, Action<bool> changed)
        {
            var c = new CheckBox { IsChecked = value, Style = (Style)Resources["Toggle"] };
            c.Checked += (a, b) => { changed(true); Changed(); };
            c.Unchecked += (a, b) => { changed(false); Changed(); };
            var row = Row(label, help, c);
            // The whole row flips the switch, like the Claude app's settings.
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (a, b) => { if (!c.IsMouseOver) c.IsChecked = c.IsChecked != true; };
            ((Grid)row).Background = Brushes.Transparent;
            return row;
        }

        static string[] VoicePairs()
        {
            var pairs = new List<string> { "", "Windows default" };
            foreach (string v in QuickVoice.InstalledVoices()) { pairs.Add(v); pairs.Add(v.Replace("Microsoft ", "").Replace(" Desktop", "")); }
            return pairs.ToArray();
        }

        static string RateKey(int rate)
        {
            return rate <= -2 ? "-2" : rate <= 0 ? "0" : rate <= 2 ? "1" : "3";
        }

        // pairs: value, label, value, label, ...
        ComboBox Combo(string[] pairs, string selected, Action<string> changed)
        {
            var c = new ComboBox { Width = 230 };
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                var item = new ComboBoxItem { Content = pairs[i + 1], Tag = pairs[i] };
                c.Items.Add(item);
                if (pairs[i] == selected) c.SelectedItem = item;
            }
            if (c.SelectedIndex < 0) c.SelectedIndex = 0;
            c.SelectionChanged += (a, b) => { var item = c.SelectedItem as ComboBoxItem; if (item != null) { changed((string)item.Tag); Changed(); } };
            return c;
        }
    }
}
