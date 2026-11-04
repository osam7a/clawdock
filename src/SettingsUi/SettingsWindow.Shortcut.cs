using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Clawdock
{
    // Recording your own shortcut: click Record, press the keys. While it's listening, the current
    // shortcut is off, so pressing it can be recorded too instead of starting quick voice.
    partial class SettingsWindow
    {
        TextBlock shortcutShown;
        Button recordButton;
        bool recording;

        FrameworkElement BuildRecorder()
        {
            shortcutShown = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            recordButton = new Button { Margin = new Thickness(10, 0, 0, 0), MinWidth = 76 };
            recordButton.Click += (a, b) => { if (recording) StopRecording(); else StartRecording(); };
            recordButton.IsEnabled = s.Shortcut == "custom";
            ShowRecordedShortcut();

            PreviewKeyDown += RecordKeyDown;
            PreviewKeyUp += RecordKeyUp;
            Deactivated += (a, b) => StopRecording();
            Closed += (a, b) => StopRecording();

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Border
            {
                BorderBrush = p.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 0, 12, 0), Width = 190, Child = shortcutShown,
            });
            panel.Children.Add(recordButton);
            return panel;
        }

        void ShowRecordedShortcut()
        {
            shortcutShown.Text = Hotkeys.Describe(s.CustomModifiers, s.CustomKey);
            shortcutShown.Foreground = p.Text;
            recordButton.Content = "Record";
        }

        void StartRecording()
        {
            recording = true;
            Hotkeys.Remove();
            shortcutShown.Text = "Press the keys…";
            shortcutShown.Foreground = p.Clay;
            recordButton.Content = "Cancel";
        }

        void StopRecording()
        {
            if (!recording) return;
            recording = false;
            controller.RestoreShortcut();
            ShowRecordedShortcut();
        }

        static int HeldModifiers()
        {
            int mods = 0;
            var m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Alt) != 0) mods |= 1;
            if ((m & ModifierKeys.Control) != 0) mods |= 2;
            if ((m & ModifierKeys.Shift) != 0) mods |= 4;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= 8;
            return mods;
        }

        void ShowHeldKeys()
        {
            string held = Hotkeys.DescribeModifiers(HeldModifiers());
            shortcutShown.Text = held == "" ? "Press the keys…" : held + "…";
        }

        void RecordKeyDown(object sender, KeyEventArgs e)
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape || key == Key.Tab) { StopRecording(); return; }

            int vk = KeyInterop.VirtualKeyFromKey(key), mods = HeldModifiers();
            if (Hotkeys.IsModifier(vk)) { ShowHeldKeys(); return; }
            if (!Hotkeys.CanUse(mods, vk)) { shortcutShown.Text = "Add Ctrl, Alt, Shift or Win"; return; }

            s.CustomModifiers = mods;
            s.CustomKey = vk;
            StopRecording();
            Changed();
        }

        void RecordKeyUp(object sender, KeyEventArgs e)
        {
            if (!recording) return;
            e.Handled = true;
            ShowHeldKeys();
        }
    }
}
