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
    // The settings pages, except the voice page, which has its own file.
    partial class SettingsWindow
    {
        void BuildGeneralPage()
        {
            hookStatus = new TextBlock { Foreground = p.Sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            hookDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var repair = new Button { Content = "Repair hooks", Margin = new Thickness(12, 0, 0, 0) };
            repair.Click += (a, b) => { controller.RepairHooks(); ShowHookState(); };
            ShowHookState();
            var updateStatus = new TextBlock { Foreground = p.Sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Text = controller.Updater.Status };
            Action showUpdate = () => updateStatus.Text = controller.Updater.Status;
            controller.Updater.Changed += showUpdate;
            Closed += (a, b) => controller.Updater.Changed -= showUpdate;
            var checkNow = new Button { Content = "Check now", Margin = new Thickness(12, 0, 0, 0) };
            checkNow.Click += (a, b) => controller.Updater.CheckNow();
            Page("general", "General",
                Card(
                    Toggle("Start with Windows", "Clawdock starts when you sign in.", s.StartWithWindows, v => s.StartWithWindows = v),
                    Toggle("Answer permission requests", "For Claude app sessions: allow or deny from the notch while you're away. Terminal sessions get a small notice instead and ask in the terminal.", s.ApproveFromNotch, v => s.ApproveFromNotch = v),
                    Toggle("Show Claude's questions", "When a session ends with a question like \"Should I test it?\", answer from the notch. It presses the same button in Claude.", s.ShowQuestions, v => s.ShowQuestions = v)),
                Section("Updates"),
                Card(
                    Toggle("Update automatically", "Checks GitHub for a new release every few hours and installs it when nothing's waiting on you.", s.AutoUpdate, v => s.AutoUpdate = v),
                    Row("Version", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { updateStatus, checkNow } })),
                Section("Claude Code hooks"),
                Card(
                    Toggle("Keep hooks installed", "Hooks tell the notch what sessions are doing. They're repaired every 5 minutes if something removes them.", s.KeepHooksInstalled, v => s.KeepHooksInstalled = v),
                    Row("Status", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { hookDot, hookStatus, repair } })));
        }

        void BuildShortcutPage()
        {
            var recorder = BuildRecorder();
            shortcutNote = new TextBlock { Foreground = p.Clay, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 4, 0), Text = controller.ShortcutError ?? "" };
            Page("shortcut", "Shortcut",
                Card(
                    Row("Start talking to Claude with", "The Copilot key works on most new keyboards.",
                        Combo(new[] { "copilot", "Copilot key", "custom", "My own shortcut", "off", "No shortcut" }, s.Shortcut, v => { s.Shortcut = v; StopRecording(); recordButton.IsEnabled = v == "custom"; })),
                    Row("My own shortcut", "Click Record, then press the keys. Use Ctrl, Alt, Shift or Win with another key, or a function key.", recorder)),
                shortcutNote);
        }

        void BuildNotchPage()
        {
            var monitors = new List<string> { "", "Main display" };
            int n = 1;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (screen.Primary) continue;
                monitors.Add(screen.DeviceName);
                monitors.Add("Display " + (++n) + " (" + screen.Bounds.Width + "×" + screen.Bounds.Height + ")");
            }
            var offset = new Slider { Minimum = 0, Maximum = 1, Value = s.EdgeOffset, Width = 230, SmallChange = 0.05, LargeChange = 0.1 };
            offset.ValueChanged += (a, b) => { s.EdgeOffset = offset.Value; Changed(); };
            Page("notch", "Notch",
                PreviewCard(),
                Card(
                    Row("Style", null, Combo(new[]
                    {
                        "classic", "Classic: mascot and status",
                        "compact", "Compact: one slim line",
                        "line", "Line: a thin bar",
                        "dot", "Dot: a small dot",
                    }, s.Style, v => s.Style = v)),
                    Row("Size when collapsed", "The notch at rest: a session's status, the small bar.", Combo(NotchSizes, s.NotchSizeCollapsed, v => s.NotchSizeCollapsed = v)),
                    Row("Size when open", "Quick voice's chat, permission requests, questions, the list of sessions.", Combo(NotchSizes, s.NotchSizeOpen, v => s.NotchSizeOpen = v)),
                    Row("Screen edge", null, Combo(new[] { "top", "Top", "bottom", "Bottom", "left", "Left", "right", "Right" }, s.Edge, v => s.Edge = v)),
                    Row("Display", null, Combo(monitors.ToArray(), s.Monitor, v => s.Monitor = v)),
                    Row("Position along the edge", "You can also drag the notch: it snaps to the nearest edge.", offset),
                    Toggle("Show all sessions on hover", "Hover over the notch to see every session and jump to one.", s.ExpandOnHover, v => s.ExpandOnHover = v)));
        }

        void BuildWhenPage()
        {
            Page("when", "When it shows",
                Card(
                    Row("Show the notch", null, Combo(new[]
                    {
                        "smart", "When something's going on",
                        "away", "Whenever I'm not in Claude",
                        "alerts", "Only when Claude needs me",
                    }, s.ShowWhen, v => s.ShowWhen = v)),
                    Row("Hiding it with × lasts", null, Combo(new[] { "return", "Until I go back to Claude", "15m", "15 minutes", "1h", "1 hour" }, s.DismissFor, v => s.DismissFor = v)),
                    Row("Keep \"Done\" on the notch for", null, Combo(new[] { "5", "5 seconds", "12", "12 seconds", "30", "30 seconds", "60", "1 minute" },
                        s.FinishedSeconds.ToString(), v => s.FinishedSeconds = int.Parse(v))),
                    Toggle("Show over fullscreen apps", "Games, videos and presentations. When off, permission requests wait and appear once you leave the fullscreen app.", s.ShowOverFullscreen, v => s.ShowOverFullscreen = v)),
                Hint("\"When something's going on\" means a session is working, just finished or needs you, or Claude is minimized. \"Only when Claude needs me\" is permission requests, questions and voice."));
        }
    }
}
