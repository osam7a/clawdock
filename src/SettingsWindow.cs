using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeNotch
{
    // Everything you can change about Claude Notch. Edits a copy of the settings; nothing
    // takes effect until Save.
    class SettingsWindow : Window
    {
        public event Action<Settings> Saved;

        readonly Settings s;
        readonly Controller controller;
        readonly TextBlock hookStatus, shortcutNote;
        readonly TextBox recorder, folderBox;
        readonly Slider offset;

        public SettingsWindow(Settings settings, Controller controller)
        {
            s = settings;
            this.controller = controller;
            Title = "Claude Notch settings";
            Width = 560;
            Height = 720;
            MinWidth = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            FontSize = 13;

            var body = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };

            // General
            body.Children.Add(Heading("General"));
            body.Children.Add(Check("Start Claude Notch when I sign in to Windows", s.StartWithWindows, v => s.StartWithWindows = v));
            body.Children.Add(Check("Answer permission requests from the notch (Allow / Deny)", s.ApproveFromNotch, v => s.ApproveFromNotch = v));
            body.Children.Add(Help("Only while you're away from Claude. If you go back to Claude, it asks in its own window as usual."));
            body.Children.Add(Check("Show Claude's questions on the notch, with the same choices", s.ShowQuestions, v => s.ShowQuestions = v));
            body.Children.Add(Help("When a session in the Claude app ends with a question like \"Should I test it?\", pick an answer from the notch. It presses the same button in Claude."));
            body.Children.Add(Check("Keep Claude Code hooks installed", s.KeepHooksInstalled, v => s.KeepHooksInstalled = v));
            hookStatus = Help("");
            var repair = new Button { Content = "Repair hooks", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            repair.Click += (a, b) => { controller.RepairHooks(); ShowHookState(); };
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 6), Children = { repair, hookStatus } });
            hookStatus.Margin = new Thickness(10, 3, 0, 0);
            ShowHookState();

            // Shortcut
            body.Children.Add(Heading("Shortcut"));
            recorder = new TextBox { Width = 220, IsReadOnly = true, Text = Hotkeys.Describe(s.CustomModifiers, s.CustomKey), Padding = new Thickness(4, 2, 4, 2) };
            recorder.PreviewKeyDown += RecordKey;
            recorder.GotFocus += (a, b) => recorder.Text = "Press the keys…";
            recorder.LostFocus += (a, b) => recorder.Text = Hotkeys.Describe(s.CustomModifiers, s.CustomKey);
            var shortcut = Combo(new[] { "copilot", "Copilot key", "custom", "My own shortcut", "off", "No shortcut" }, s.Shortcut, v => { s.Shortcut = v; recorder.IsEnabled = v == "custom"; });
            recorder.IsEnabled = s.Shortcut == "custom";
            body.Children.Add(Row("Start talking to Claude with", shortcut));
            body.Children.Add(Row("Custom shortcut", recorder));
            shortcutNote = Help(controller.ShortcutError ?? "");
            body.Children.Add(shortcutNote);

            // Voice & prompts
            body.Children.Add(Heading("Voice and typed prompts"));
            body.Children.Add(Row("When I press the shortcut", Combo(new[]
            {
                "voice-or-type", "Voice, or type if there's no microphone",
                "type", "Always type a prompt",
                "voice", "Always voice",
            }, s.VoiceMode, v => s.VoiceMode = v)));
            body.Children.Add(Help(VoiceLauncher.HasMicrophone() ? "A microphone is available." : "No microphone found right now, so the shortcut opens a prompt box."));
            body.Children.Add(Row("Send typed prompts to", Combo(new[]
            {
                "desktop", "The Claude app (the open chat or session)",
                "cli", "A new Claude Code session in a terminal",
            }, s.PromptTarget, v => s.PromptTarget = v)));
            folderBox = new TextBox { Text = s.CliFolder, Width = 250, Padding = new Thickness(4, 2, 4, 2) };
            folderBox.TextChanged += (a, b) => s.CliFolder = folderBox.Text;
            var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            browse.Click += (a, b) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = folderBox.Text, Description = "Folder for new terminal sessions" })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) folderBox.Text = dlg.SelectedPath;
            };
            body.Children.Add(Row("Terminal sessions start in", new StackPanel { Orientation = Orientation.Horizontal, Children = { folderBox, browse } }));

            // Notch
            body.Children.Add(Heading("Notch"));
            body.Children.Add(Row("Style", Combo(new[]
            {
                "classic", "Classic: mascot and status",
                "compact", "Compact: one slim line",
                "line", "Line: a thin bar, hover for details",
                "dot", "Dot: a small dot, hover for details",
            }, s.Style, v => s.Style = v)));
            body.Children.Add(Row("Screen edge", Combo(new[] { "top", "Top", "bottom", "Bottom", "left", "Left", "right", "Right" }, s.Edge, v => s.Edge = v)));
            var monitors = new List<string> { "", "Main display" };
            int n = 1;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (screen.Primary) continue;
                monitors.Add(screen.DeviceName);
                monitors.Add("Display " + (++n) + " (" + screen.Bounds.Width + "×" + screen.Bounds.Height + ")");
            }
            body.Children.Add(Row("Display", Combo(monitors.ToArray(), s.Monitor, v => s.Monitor = v)));
            offset = new Slider { Minimum = 0, Maximum = 1, Value = s.EdgeOffset, Width = 250, SmallChange = 0.05, LargeChange = 0.1 };
            offset.ValueChanged += (a, b) => s.EdgeOffset = offset.Value;
            body.Children.Add(Row("Position along the edge", offset));
            body.Children.Add(Help("You can also drag the notch: it snaps to the nearest edge."));
            body.Children.Add(Check("Show all sessions when I hover over the notch", s.ExpandOnHover, v => s.ExpandOnHover = v));

            // When to show
            body.Children.Add(Heading("When it shows"));
            body.Children.Add(Row("Show the notch", Combo(new[]
            {
                "smart", "When something's going on, or Claude is minimized",
                "away", "Whenever I'm not looking at Claude",
                "alerts", "Only when Claude needs me (permissions, questions) and voice",
            }, s.ShowWhen, v => s.ShowWhen = v)));
            body.Children.Add(Row("Hiding it with × lasts", Combo(new[]
            {
                "return", "Until I go back to Claude",
                "15m", "15 minutes",
                "1h", "1 hour",
            }, s.DismissFor, v => s.DismissFor = v)));
            body.Children.Add(Row("Keep \"Done\" on the notch for", Combo(new[] { "5", "5 seconds", "12", "12 seconds", "30", "30 seconds", "60", "1 minute" },
                s.FinishedSeconds.ToString(), v => s.FinishedSeconds = int.Parse(v))));
            body.Children.Add(Check("Show over fullscreen apps (games, videos, presentations)", s.ShowOverFullscreen, v => s.ShowOverFullscreen = v));
            body.Children.Add(Help("Permission requests always show."));

            // Buttons
            var save = new Button { Content = "Save", IsDefault = true, Width = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
            save.Click += (a, b) =>
            {
                if (Saved != null) Saved(s);
                shortcutNote.Text = controller.ShortcutError ?? "";
                if (controller.ShortcutError == null) Close();
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 3, 0, 3) };
            var uninstall = new Button { Content = "Uninstall…", Padding = new Thickness(10, 3, 10, 3) };
            uninstall.Click += (a, b) =>
            {
                if (MessageBox.Show(this, "Remove Claude Notch, its Claude Code hooks and its startup entry?\n\nYour other Claude Code settings are left as they are.",
                    "Uninstall Claude Notch", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    Close();
                    controller.Uninstall();
                }
            };
            var buttons = new DockPanel { Margin = new Thickness(24, 10, 24, 16), LastChildFill = false };
            DockPanel.SetDock(uninstall, Dock.Left);
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            buttons.Children.Add(uninstall);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }

        void ShowHookState()
        {
            switch (controller.HookState())
            {
                case HookInstaller.State.Installed: hookStatus.Text = "Installed in ~/.claude/settings.json"; break;
                case HookInstaller.State.Missing: hookStatus.Text = "Not installed"; break;
                case HookInstaller.State.Outdated: hookStatus.Text = "Installed, but out of date"; break;
                default: hookStatus.Text = "~/.claude/settings.json isn't valid JSON, so it was left alone"; break;
            }
        }

        // Records a key combination: at least one modifier (or a function key) plus a key.
        void RecordKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;
            if (key == Key.Escape || key == Key.Tab) { Keyboard.ClearFocus(); return; }

            int mods = 0;
            var m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Alt) != 0) mods |= 1;
            if ((m & ModifierKeys.Control) != 0) mods |= 2;
            if ((m & ModifierKeys.Shift) != 0) mods |= 4;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= 8;
            bool functionKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !functionKey) { recorder.Text = "Add Ctrl, Alt, Shift or Win"; return; }

            s.CustomModifiers = mods;
            s.CustomKey = KeyInterop.VirtualKeyFromKey(key);
            recorder.Text = Hotkeys.Describe(mods, s.CustomKey);
        }

        // ---- building blocks ----------------------------------------------

        static TextBlock Heading(string text)
        {
            return new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        }

        static TextBlock Help(string text)
        {
            return new TextBlock { Text = text, Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 0, 6) };
        }

        static CheckBox Check(string text, bool value, Action<bool> changed)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            c.Checked += (a, b) => changed(true);
            c.Unchecked += (a, b) => changed(false);
            return c;
        }

        static FrameworkElement Row(string label, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            control.HorizontalAlignment = HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            Grid.SetColumn(control, 1);
            grid.Children.Add(l);
            grid.Children.Add(control);
            return grid;
        }

        // pairs: value, label, value, label, ...
        static ComboBox Combo(string[] pairs, string selected, Action<string> changed)
        {
            var c = new ComboBox { MinWidth = 250 };
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                var item = new ComboBoxItem { Content = pairs[i + 1], Tag = pairs[i] };
                c.Items.Add(item);
                if (pairs[i] == selected) c.SelectedItem = item;
            }
            if (c.SelectedIndex < 0) c.SelectedIndex = 0;
            c.SelectionChanged += (a, b) => { var item = c.SelectedItem as ComboBoxItem; if (item != null) changed((string)item.Tag); };
            return c;
        }
    }
}
