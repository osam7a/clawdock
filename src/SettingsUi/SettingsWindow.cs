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
    // Everything you can change about Clawdock. Edits a copy of the settings; nothing
    // takes effect until Save. Styled like the Claude app, light or dark to match Windows.
    partial class SettingsWindow : Window
    {
        public event Action<Settings> Saved;

        readonly Settings s;
        readonly Controller controller;
        readonly Palette p = Palette.Current();
        TextBlock hookStatus, shortcutNote;
        readonly TextBlock pageTitle, unsaved;
        Ellipse hookDot;
        readonly ScrollViewer scroller;
        readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
        readonly Canvas preview = new Canvas { Width = 224, Height = 126 };
        readonly Border previewNotch = new Border();
        readonly Button save;
        bool built, dirty;
        volatile bool closing;
        static AudioPlayer voiceSample; // plays natural voice samples
        static readonly string[] NotchSizes = { "small", "Small", "medium", "Medium", "large", "Large", "xlarge", "Extra large" };

        public SettingsWindow(Settings settings, Controller controller)
        {
            s = settings;
            this.controller = controller;
            Title = "Clawdock settings";
            Width = 820;
            Height = 620;
            MinWidth = 680;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            FontSize = 13;
            Background = p.Bg;
            Foreground = p.Text;
            Resources.MergedDictionaries.Add(p.Styles());
            SourceInitialized += (a, b) => p.ColorTitleBar(new WindowInteropHelper(this).Handle);

            BuildGeneralPage();
            BuildShortcutPage();
            BuildVoicePage();
            BuildNotchPage();
            BuildWhenPage();

            // ---- Sidebar
            var nav = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
            nav.Children.Add(NavItem("general", "General", ""));
            nav.Children.Add(NavItem("shortcut", "Shortcut", ""));
            nav.Children.Add(NavItem("voice", "Voice and prompts", ""));
            nav.Children.Add(NavItem("notch", "Notch", ""));
            nav.Children.Add(NavItem("when", "When it shows", ""));

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 22, 12, 22) };
            var mascot = new Mascot(2.0) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            brand.Children.Add(mascot);
            brand.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Clawdock", FontFamily = p.Serif, FontSize = 17, Foreground = p.Text },
                    new TextBlock { Text = "Settings", FontSize = 12, Foreground = p.Sub },
                }
            });

            var uninstall = new Button { Content = "Uninstall Clawdock…", Style = (Style)Resources["Link"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 12, 16) };
            uninstall.Click += (a, b) =>
            {
                if (MessageBox.Show(this, "Remove Clawdock, its Claude Code hooks and its startup entry?\n\nYour other Claude Code settings are left as they are.",
                    "Uninstall Clawdock", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    Close();
                    controller.Uninstall();
                }
            };

            var side = new DockPanel { Background = p.Side, Width = 230 };
            DockPanel.SetDock(brand, Dock.Top);
            DockPanel.SetDock(uninstall, Dock.Bottom);
            side.Children.Add(brand);
            side.Children.Add(uninstall);
            side.Children.Add(nav);

            // ---- Content and footer
            pageTitle = new TextBlock { FontFamily = p.Serif, FontSize = 26, Foreground = p.Text, Margin = new Thickness(36, 28, 36, 14) };
            scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };

            unsaved = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
            save = new Button { Content = "Save", IsDefault = true, MinWidth = 92, Style = (Style)Resources["Primary"], Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            save.Click += (a, b) => Save();
            var cancel = new Button { Content = "Close", IsCancel = true, MinWidth = 92 };
            cancel.Click += (a, b) => Close(); // IsCancel only closes a window by itself under ShowDialog
            Closed += (a, b) => closing = true; // stops a download in progress
            Closing += (a, b) =>
            {
                if (!dirty) return;
                var answer = MessageBox.Show(this, "Save your changes before closing?", "Clawdock settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save())) b.Cancel = true;
            };
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(36, 12, 28, 14) };
            DockPanel.SetDock(unsaved, Dock.Left);
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            footer.Children.Add(unsaved);
            footer.Children.Add(save);
            footer.Children.Add(cancel);
            var footerBar = new Border { BorderBrush = p.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };

            var main = new DockPanel();
            DockPanel.SetDock(pageTitle, Dock.Top);
            DockPanel.SetDock(footerBar, Dock.Bottom);
            main.Children.Add(pageTitle);
            main.Children.Add(footerBar);
            main.Children.Add(scroller);

            var root = new DockPanel { Background = p.Bg };
            DockPanel.SetDock(side, Dock.Left);
            root.Children.Add(side);
            var divider = new Border { Width = 1, Background = p.Line };
            DockPanel.SetDock(divider, Dock.Left);
            root.Children.Add(divider);
            root.Children.Add(main);
            Content = root;

            ((RadioButton)nav.Children[0]).IsChecked = true;
            UpdatePreview();
            built = true;
        }

        void Changed()
        {
            if (!built) return;
            dirty = true;
            save.IsEnabled = true;
            unsaved.Text = "You have unsaved changes";
            unsaved.Foreground = p.Sub;
            unsaved.Visibility = Visibility.Visible;
            UpdatePreview();
        }

        // Applies the changes and stays open. False if the shortcut couldn't be registered.
        bool Save()
        {
            if (Saved != null) Saved(s);
            shortcutNote.Text = controller.ShortcutError ?? "";
            if (controller.ShortcutError != null) { Show("shortcut"); return false; }
            dirty = false;
            save.IsEnabled = false;
            unsaved.Text = "✓  Saved";
            unsaved.Foreground = Theme.Green;
            return true;
        }

        void Show(string key)
        {
            StopRecording();
            FrameworkElement page;
            if (!pages.TryGetValue(key, out page)) return;
            pageTitle.Text = (string)page.Tag;
            scroller.Content = page;
            scroller.ScrollToTop();
            navItems[key].IsChecked = true;
        }

        void ShowHookState()
        {
            switch (controller.HookState())
            {
                case HookInstaller.State.Installed: hookStatus.Text = "Installed"; hookDot.Fill = Theme.Green; break;
                case HookInstaller.State.Missing: hookStatus.Text = "Not installed"; hookDot.Fill = p.Sub; break;
                case HookInstaller.State.Outdated: hookStatus.Text = "Out of date"; hookDot.Fill = Theme.Amber; break;
                default: hookStatus.Text = "settings.json isn't valid JSON"; hookDot.Fill = p.Clay; break;
            }
            hookStatus.ToolTip = "~/.claude/settings.json";
        }
    }
}
