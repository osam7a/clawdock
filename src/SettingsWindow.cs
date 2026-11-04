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
    // Everything you can change about Claude Notch. Edits a copy of the settings; nothing
    // takes effect until Save. Styled like the Claude app, light or dark to match Windows.
    class SettingsWindow : Window
    {
        public event Action<Settings> Saved;

        readonly Settings s;
        readonly Controller controller;
        readonly Palette p = Palette.Current();
        readonly TextBlock hookStatus, shortcutNote, pageTitle, unsaved;
        readonly Ellipse hookDot;
        readonly TextBox recorder;
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
            Title = "Claude Notch settings";
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

            // ---- General
            hookStatus = new TextBlock { Foreground = p.Sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            hookDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var repair = new Button { Content = "Repair hooks", Margin = new Thickness(12, 0, 0, 0) };
            repair.Click += (a, b) => { controller.RepairHooks(); ShowHookState(); };
            ShowHookState();
            Page("general", "General",
                Card(
                    Toggle("Start with Windows", "Claude Notch starts when you sign in.", s.StartWithWindows, v => s.StartWithWindows = v),
                    Toggle("Answer permission requests", "For Claude app sessions: allow or deny from the notch while you're away. Terminal sessions get a small notice instead and ask in the terminal.", s.ApproveFromNotch, v => s.ApproveFromNotch = v),
                    Toggle("Show Claude's questions", "When a session ends with a question like \"Should I test it?\", answer from the notch. It presses the same button in Claude.", s.ShowQuestions, v => s.ShowQuestions = v)),
                Section("Claude Code hooks"),
                Card(
                    Toggle("Keep hooks installed", "Hooks tell the notch what sessions are doing. They're repaired every 5 minutes if something removes them.", s.KeepHooksInstalled, v => s.KeepHooksInstalled = v),
                    Row("Status", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { hookDot, hookStatus, repair } })));

            // ---- Shortcut
            recorder = new TextBox { Width = 230, IsReadOnly = true, Text = Hotkeys.Describe(s.CustomModifiers, s.CustomKey), Cursor = Cursors.Hand };
            recorder.PreviewKeyDown += RecordKey;
            recorder.GotFocus += (a, b) => recorder.Text = "Press the keys…";
            recorder.LostFocus += (a, b) => recorder.Text = Hotkeys.Describe(s.CustomModifiers, s.CustomKey);
            recorder.IsEnabled = s.Shortcut == "custom";
            shortcutNote = new TextBlock { Foreground = p.Clay, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 4, 0), Text = controller.ShortcutError ?? "" };
            Page("shortcut", "Shortcut",
                Card(
                    Row("Start talking to Claude with", "The Copilot key works on most new keyboards.",
                        Combo(new[] { "copilot", "Copilot key", "custom", "My own shortcut", "off", "No shortcut" }, s.Shortcut, v => { s.Shortcut = v; recorder.IsEnabled = v == "custom"; })),
                    Row("My own shortcut", "Click, then press the keys. Needs Ctrl, Alt, Shift or Win, or a function key.", recorder)),
                shortcutNote);

            // ---- Voice and prompts
            var editVoice = new Button { Content = "Edit…" };
            editVoice.Click += (a, b) =>
            {
                QuickVoice.EnsureSkill();
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + QuickVoice.SkillFile + "\""); }
                catch (Exception ex) { Log.Write("Couldn't open " + QuickVoice.SkillFile + ": " + ex.Message); }
            };
            var speechSettings = new Button { Content = ModernDictation.OnlineSpeechOn ? "Windows settings…" : "Turn it on…" };
            speechSettings.Click += (a, b) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("Couldn't open speech settings: " + ex.Message); }
            };
            // The built-in engine: pick a model, download it if it's missing.
            var localStatus = new TextBlock { FontSize = 11.5, Foreground = p.Sub, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
            var download = new Button { Content = "Download", Margin = new Thickness(8, 0, 0, 0) };
            string localModel = s.QuickLocalModel;
            bool downloading = false;
            Action showLocal = () =>
            {
                if (downloading) return;
                bool have = LocalSpeech.Installed(localModel);
                localStatus.Text = have ? "✓  Ready" : "Needs a " + (LocalSpeech.DownloadBytes(localModel) / 1048576) + " MB download.";
                download.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
            };
            var modelCombo = Combo(new[] { "small", "Accurate (190 MB)", "base", "Fast (60 MB)" }, s.QuickLocalModel, v => { s.QuickLocalModel = v; localModel = v; showLocal(); });
            modelCombo.Width = 150;
            download.Click += (a, b) =>
            {
                downloading = true;
                download.IsEnabled = false;
                modelCombo.IsEnabled = false;
                string key = localModel;
                var ui = Dispatcher;
                new System.Threading.Thread(() =>
                {
                    string error = null;
                    try
                    {
                        LocalSpeech.Install(key, f => ui.BeginInvoke(new Action(() => localStatus.Text = "Downloading… " + (int)(f * 100) + "%")), () => closing);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { error = ex.Message; Log.Write("Built-in speech download: " + ex.Message); }
                    ui.BeginInvoke(new Action(() =>
                    {
                        downloading = false;
                        download.IsEnabled = true;
                        modelCombo.IsEnabled = true;
                        showLocal();
                        if (error != null) localStatus.Text = "Download failed: " + error;
                    }));
                }) { IsBackground = true }.Start();
            };
            showLocal();
            var localPanel = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { modelCombo, download } }, localStatus } };

            // The natural voice: pick one of its voices, download it if it's missing, hear it.
            var naturalStatus = new TextBlock { FontSize = 11.5, Foreground = p.Sub, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
            var naturalDownload = new Button { Content = "Download", Margin = new Thickness(8, 0, 0, 0) };
            var naturalPlay = new Button { Content = "▶ Play", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Hear this voice" };
            var naturalPairs = new List<string>();
            for (int i = 0; i < NaturalVoice.Voices.Length; i++) { naturalPairs.Add(i.ToString()); naturalPairs.Add(NaturalVoice.VoiceLabel(i)); }
            int naturalSid = s.QuickNaturalVoice;
            var naturalCombo = Combo(naturalPairs.ToArray(), s.QuickNaturalVoice.ToString(), v => { s.QuickNaturalVoice = int.Parse(v); naturalSid = s.QuickNaturalVoice; });
            naturalCombo.Width = 150;
            bool naturalBusy = false;
            Action showNatural = () =>
            {
                if (naturalBusy) return;
                bool have = NaturalVoice.Installed;
                naturalStatus.Text = !NaturalVoice.Supported ? "Not available on this PC: it needs 64-bit Windows 10 (1803 or later) on an Intel or AMD processor."
                    : have ? "✓  Ready" : "Needs a " + (NaturalVoice.DownloadBytes / 1048576) + " MB download.";
                naturalDownload.Visibility = have || !NaturalVoice.Supported ? Visibility.Collapsed : Visibility.Visible;
                naturalPlay.Visibility = have ? Visibility.Visible : Visibility.Collapsed;
            };
            naturalDownload.Click += (a, b) =>
            {
                naturalBusy = true;
                naturalDownload.IsEnabled = false;
                var ui = Dispatcher;
                new System.Threading.Thread(() =>
                {
                    string error = null;
                    try { NaturalVoice.Install(f => ui.BeginInvoke(new Action(() => naturalStatus.Text = "Downloading… " + (int)(f * 100) + "%")), () => closing); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { error = ex.Message; Log.Write("Natural voice download: " + ex.Message); }
                    ui.BeginInvoke(new Action(() =>
                    {
                        naturalBusy = false;
                        naturalDownload.IsEnabled = true;
                        showNatural();
                        if (error != null) naturalStatus.Text = "Download failed: " + error;
                    }));
                }) { IsBackground = true }.Start();
            };
            naturalPlay.Click += (a, b) =>
            {
                int sid = naturalSid, rate = s.QuickRate;
                naturalPlay.IsEnabled = false;
                var ui = Dispatcher;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        int sampleRate;
                        var samples = NaturalVoice.Generate("Hi, I'm " + NaturalVoice.Voices[sid] + ". This is how I'll read your answers.", sid, NaturalVoice.Speed(rate), out sampleRate);
                        if (voiceSample == null) voiceSample = new AudioPlayer();
                        voiceSample.Stop();
                        voiceSample.Enqueue(samples, sampleRate);
                    }
                    catch (Exception ex) { Log.Write("Natural voice preview: " + ex.Message); ui.BeginInvoke(new Action(() => naturalStatus.Text = "Couldn't play it: " + ex.Message)); }
                    ui.BeginInvoke(new Action(() => naturalPlay.IsEnabled = true));
                });
            };
            showNatural();
            var naturalPanel = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { naturalCombo, naturalDownload, naturalPlay } }, naturalStatus } };

            // ElevenLabs: your API key (kept encrypted), then a voice from your account.
            var elevenKey = new PasswordBox { Width = 190, Password = ElevenLabs.Unseal(s.ElevenLabsKey), ToolTip = "Your ElevenLabs API key (elevenlabs.io → Developers → API keys)" };
            var elevenStatus = new TextBlock { FontSize = 11.5, Foreground = p.Sub, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
            var elevenVoices = new ComboBox { Width = 190 };
            var elevenLoad = new Button { Content = "Check", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Check the key and load your voices" };
            var elevenPlay = new Button { Content = "▶ Play", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Hear this voice" };
            bool fillingVoices = false;
            Action<List<KeyValuePair<string, string>>> fillVoices = list =>
            {
                fillingVoices = true;
                elevenVoices.Items.Clear();
                foreach (var v in list)
                {
                    var item = new ComboBoxItem { Content = v.Value, Tag = v.Key };
                    elevenVoices.Items.Add(item);
                    if (v.Key == (s.ElevenLabsVoice != "" ? s.ElevenLabsVoice : ElevenLabs.DefaultVoice)) elevenVoices.SelectedItem = item;
                }
                if (elevenVoices.SelectedIndex < 0 && elevenVoices.Items.Count > 0) elevenVoices.SelectedIndex = 0;
                fillingVoices = false;
            };
            fillVoices(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(s.ElevenLabsVoice != "" ? s.ElevenLabsVoice : ElevenLabs.DefaultVoice, s.ElevenLabsVoiceName != "" ? s.ElevenLabsVoiceName : "George") });
            elevenVoices.SelectionChanged += (a, b) =>
            {
                var item = elevenVoices.SelectedItem as ComboBoxItem;
                if (item == null || fillingVoices) return;
                s.ElevenLabsVoice = (string)item.Tag;
                s.ElevenLabsVoiceName = (string)item.Content;
                Changed();
            };
            Action showEleven = () =>
            {
                bool hasKey = elevenKey.Password.Trim() != "";
                elevenStatus.Text = !hasKey ? "Paste your API key from elevenlabs.io (Developers → API keys)."
                    : QuickVoice.LastCloudError != "" ? "Last try failed: " + QuickVoice.LastCloudError
                    : "Press Check to load your voices.";
                elevenLoad.IsEnabled = elevenPlay.IsEnabled = hasKey;
            };
            Action loadVoices = () =>
            {
                string key = elevenKey.Password.Trim();
                if (key == "") return;
                elevenLoad.IsEnabled = false;
                elevenStatus.Text = "Checking…";
                var ui = Dispatcher;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    List<KeyValuePair<string, string>> list = null;
                    string error = null;
                    try { list = ElevenLabs.Voices(key); }
                    catch (Exception ex) { error = ex.Message; }
                    ui.BeginInvoke(new Action(() =>
                    {
                        elevenLoad.IsEnabled = true;
                        if (error != null) { elevenStatus.Text = error; return; }
                        if (list.Count > 0) fillVoices(list);
                        QuickVoice.LastCloudError = "";
                        elevenStatus.Text = "✓  The key works · " + list.Count + " voice" + (list.Count == 1 ? "" : "s");
                    }));
                });
            };
            elevenKey.PasswordChanged += (a, b) => { s.ElevenLabsKey = ElevenLabs.Seal(elevenKey.Password.Trim()); Changed(); showEleven(); };
            elevenLoad.Click += (a, b) => loadVoices();
            elevenPlay.Click += (a, b) =>
            {
                string key = elevenKey.Password.Trim(), voice = s.ElevenLabsVoice, name = s.ElevenLabsVoiceName != "" ? s.ElevenLabsVoiceName : "George";
                int rate = s.QuickRate;
                elevenPlay.IsEnabled = false;
                var ui = Dispatcher;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    string error = null;
                    try
                    {
                        int sampleRate;
                        var samples = ElevenLabs.Generate(key, voice, "Hi, I'm " + name + ". This is how I'll read your answers.", null, ElevenLabs.Speed(rate), out sampleRate);
                        if (voiceSample == null) voiceSample = new AudioPlayer();
                        voiceSample.Stop();
                        voiceSample.Enqueue(samples, sampleRate);
                    }
                    catch (Exception ex) { error = ex.Message; Log.Write("ElevenLabs preview: " + ex.Message); }
                    ui.BeginInvoke(new Action(() => { elevenPlay.IsEnabled = true; elevenStatus.Text = error ?? "✓  Playing"; }));
                });
            };
            showEleven();
            if (s.QuickTts == "elevenlabs" && elevenKey.Password != "") loadVoices();
            var elevenKeyPanel = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { elevenKey, elevenLoad } }, elevenStatus } };
            var elevenVoicePanel = new StackPanel { Orientation = Orientation.Horizontal, Children = { elevenVoices, elevenPlay } };

            // Only the rows for the speech recognition you picked are shown.
            var localModelRow = Row("Built-in model", "English only. Downloaded once from the whisper.cpp project, then checked.", localPanel);
            var windowsSpeechRow = Row("Windows speech recognition", ModernDictation.OnlineSpeechOn
                    ? "Uses Windows online speech recognition, the same as Win+H voice typing."
                    : "Turn on Online speech recognition in Windows for better accuracy. Until then an older offline engine is used.",
                speechSettings);
            Action showEngineRows = () =>
            {
                localModelRow.Visibility = s.QuickEngine == "local" ? Visibility.Visible : Visibility.Collapsed;
                windowsSpeechRow.Visibility = s.QuickEngine == "windows" ? Visibility.Visible : Visibility.Collapsed;
            };
            showEngineRows();

            // Only the rows for the reading voice you picked are shown.
            var naturalRow = Row("Natural voice", "Downloaded once from the sherpa-onnx project, then checked. Until it's ready, the Windows voice reads.", naturalPanel);
            var elevenKeyRow = Row("ElevenLabs API key", "Answers are sent to ElevenLabs to be read and use your account's credits. The key is stored encrypted for your Windows user.", elevenKeyPanel);
            var elevenVoiceRow = Row("ElevenLabs voice", "The voices on your account. If ElevenLabs can't be reached, the natural or Windows voice reads instead.", elevenVoicePanel);
            var windowsVoiceRow = Row("Windows voice", null, Combo(VoicePairs(), s.QuickVoiceName, v => s.QuickVoiceName = v));
            Action showVoiceRows = () =>
            {
                naturalRow.Visibility = s.QuickTts == "natural" ? Visibility.Visible : Visibility.Collapsed;
                elevenKeyRow.Visibility = elevenVoiceRow.Visibility = s.QuickTts == "elevenlabs" ? Visibility.Visible : Visibility.Collapsed;
                windowsVoiceRow.Visibility = s.QuickTts == "windows" ? Visibility.Visible : Visibility.Collapsed;
            };
            showVoiceRows();

            Page("voice", "Voice and prompts",
                Card(
                    Row("When I press the shortcut", VoiceLauncher.HasMicrophone() ? "A microphone is available." : "No microphone found right now, so voice choices open Claude's quick entry instead.",
                        Combo(new[] { "quick", "Quick voice: answer on the notch", "voice-or-type", "Claude voice, or type without a mic", "voice", "Claude voice", "type", "Claude quick entry (Ctrl+Alt+Space)" }, s.VoiceMode, v => s.VoiceMode = v))),
                Hint("Quick voice is push to talk: hold the shortcut while you speak (or press it and talk), and Claude's answer appears on the notch and is read aloud. Press again to cut it off. It runs Claude Code in the background, so you don't need the app open."),
                Section("Quick voice"),
                Card(
                    Row("Speech recognition", "Built in runs Whisper on this PC: accurate, and nothing leaves your computer.",
                        Combo(new[] { "local", "Built in (Whisper)", "windows", "Windows speech recognition" }, s.QuickEngine, v => { s.QuickEngine = v; showEngineRows(); })),
                    localModelRow,
                    windowsSpeechRow,
                    Row("What it can use", "With skills and connectors, anything beyond looking things up asks for your OK on the notch first. In Auto mode, Claude Code's safety checks decide instead, and it runs on Sonnet if Haiku is picked.",
                        Combo(new[] { "web", "Web search only (fastest)", "full", "Skills and connectors, ask me", "auto", "Skills and connectors, Auto mode" }, s.QuickAccess, v => s.QuickAccess = v)),
                    Row("New projects folder", "Ask quick voice to start a new project and it's made here, after you press Allow.", ProjectsFolderBox(s)),
                    Toggle("Start new projects in Auto mode", "Claude Code's safety checks decide what can run, rather than asking you each time. Otherwise they start in the Claude app's default mode.", s.NewProjectAuto, v => s.NewProjectAuto = v),
                    Row("History", "VOICE_CHATS keeps one ongoing conversation in your Claude Code history, picked up each time.",
                        Combo(new[] { "private", "Private, not saved", "history", "One chat: VOICE_CHATS" }, s.QuickHistory, v => s.QuickHistory = v)),
                    Row("Model", "Haiku answers fastest.",
                        Combo(new[] { "haiku", "Haiku", "sonnet", "Sonnet", "opus", "Opus" }, s.QuickModel, v => s.QuickModel = v)),
                    Toggle("Read answers aloud", "Otherwise answers only appear on the notch.", s.QuickSpeak, v => s.QuickSpeak = v),
                    Toggle("Listen for my reply", "When Claude ends on a question, the mic opens by itself once it's read. It closes again if you say nothing.", s.QuickFollowUp, v => s.QuickFollowUp = v),
                    Row("Reading voice", "Natural runs on this PC, so nothing leaves it. ElevenLabs sounds the most human, in the cloud with your own account.",
                        Combo(new[] { "natural", "Natural (KittenTTS)", "elevenlabs", "ElevenLabs", "windows", "Windows voice" }, s.QuickTts, v =>
                        {
                            s.QuickTts = v;
                            showVoiceRows();
                            if (v == "elevenlabs" && elevenKey.Password != "" && elevenVoices.Items.Count <= 1) loadVoices();
                        })),
                    naturalRow,
                    elevenKeyRow,
                    elevenVoiceRow,
                    windowsVoiceRow,
                    Row("Speaking speed", null,
                        Combo(new[] { "-2", "Slower", "0", "Normal", "1", "A little faster", "3", "Fast" }, RateKey(s.QuickRate), v => s.QuickRate = int.Parse(v))),
                    Row("How Claude answers", "Short, spoken-style answers. The rules are a skill in your Claude folder: edit them to change the style.", editVoice)));

            // ---- Notch
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

            // ---- When it shows
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
                    new TextBlock { Text = "Claude Notch", FontFamily = p.Serif, FontSize = 17, Foreground = p.Text },
                    new TextBlock { Text = "Settings", FontSize = 12, Foreground = p.Sub },
                }
            });

            var uninstall = new Button { Content = "Uninstall Claude Notch…", Style = (Style)Resources["Link"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 12, 16) };
            uninstall.Click += (a, b) =>
            {
                if (MessageBox.Show(this, "Remove Claude Notch, its Claude Code hooks and its startup entry?\n\nYour other Claude Code settings are left as they are.",
                    "Uninstall Claude Notch", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
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
                var answer = MessageBox.Show(this, "Save your changes before closing?", "Claude Notch settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
            Changed();
        }

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

    // Claude's colors, light or dark, and the control styles built from them.
    class Palette
    {
        public bool Dark;
        public Brush Bg, Side, Card, Line, Text, Sub, Hover, Sel, Field, Clay, ClayHover, Off;
        public Color ScreenTop, ScreenBottom;
        public readonly FontFamily Serif = new FontFamily("Georgia, Cambria, Times New Roman");
        readonly Dictionary<string, string> hex = new Dictionary<string, string>();

        public static Palette Current()
        {
            bool dark = false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (key != null) dark = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { }
            return new Palette(dark);
        }

        Palette(bool dark)
        {
            Dark = dark;
            Bg = Make("BG", dark ? "#262624" : "#FAF9F5");
            Side = Make("SIDE", dark ? "#1F1E1D" : "#F0EEE6");
            Card = Make("CARD", dark ? "#30302E" : "#FFFFFF");
            Line = Make("LINE", dark ? "#3E3E3A" : "#E5E2D8");
            Text = Make("TEXT", dark ? "#F5F4EE" : "#141413");
            Sub = Make("SUB", dark ? "#A6A49B" : "#6F6E69");
            Hover = Make("HOVER", dark ? "#3A3A37" : "#ECE9E0");
            Sel = Make("SEL", dark ? "#34332F" : "#E6E2D7");
            Field = Make("FIELD", dark ? "#262624" : "#FFFFFF");
            Clay = Make("CLAY", "#D97757");
            ClayHover = Make("CLAYHOVER", dark ? "#E08A6D" : "#C6613F");
            Off = Make("OFF", dark ? "#5A5954" : "#CFCBC0");
            ScreenTop = (Color)ColorConverter.ConvertFromString(dark ? "#3A3936" : "#E9E5DA");
            ScreenBottom = (Color)ColorConverter.ConvertFromString(dark ? "#2C2B29" : "#DAD5C7");
        }

        Brush Make(string token, string color)
        {
            hex[token] = color;
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            return b;
        }

        // Dark or light title bar in the sidebar's color (Windows 11; ignored elsewhere).
        public void ColorTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = Dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                var c = ((SolidColorBrush)Side).Color;
                int caption = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                var t = ((SolidColorBrush)Text).Color;
                int text = t.R | (t.G << 8) | (t.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public ResourceDictionary Styles()
        {
            string xaml = StylesXaml;
            foreach (var pair in hex) xaml = xaml.Replace("#" + pair.Key + "'", pair.Value + "'");
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        const string StylesXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='Text' Color='#TEXT'/>
  <SolidColorBrush x:Key='Sub' Color='#SUB'/>
  <SolidColorBrush x:Key='Card' Color='#CARD'/>
  <SolidColorBrush x:Key='Line' Color='#LINE'/>
  <SolidColorBrush x:Key='Hover' Color='#HOVER'/>
  <SolidColorBrush x:Key='Sel' Color='#SEL'/>
  <SolidColorBrush x:Key='Field' Color='#FIELD'/>
  <SolidColorBrush x:Key='Clay' Color='#CLAY'/>
  <SolidColorBrush x:Key='ClayHover' Color='#CLAYHOVER'/>
  <SolidColorBrush x:Key='Off' Color='#OFF'/>

  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Card}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='Padding' Value='14,7'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Primary' TargetType='Button'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='14,7'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='{StaticResource Clay}' CornerRadius='8' Padding='{TemplateBinding Padding}' BorderThickness='1' BorderBrush='{StaticResource Clay}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource ClayHover}'/><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource ClayHover}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Text}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Link' TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Sub}'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='Transparent' CornerRadius='6' Padding='6,5'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Nav' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Margin' Value='0,1'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RadioButton'>
        <Border x:Name='b' Background='Transparent' CornerRadius='8' Padding='10,8'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Sel}'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Toggle' TargetType='CheckBox'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <Border x:Name='track' Width='38' Height='22' CornerRadius='11' Background='{StaticResource Off}' BorderThickness='2' BorderBrush='Transparent'>
          <Ellipse x:Name='knob' Width='14' Height='14' Fill='White' HorizontalAlignment='Left' Margin='2,0,0,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='track' Property='Background' Value='{StaticResource Clay}'/>
            <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='knob' Property='Margin' Value='0,0,2,0'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='track' Property='BorderBrush' Value='{StaticResource Text}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Field}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='CaretBrush' Value='{StaticResource Text}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Clay}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='TextBox'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8'>
          <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='8,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='PasswordBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Field}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='CaretBrush' Value='{StaticResource Text}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Clay}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='PasswordBox'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8'>
          <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='8,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBoxItem'>
        <Border x:Name='b' Background='Transparent' CornerRadius='6' Padding='10,7'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBox'>
        <Grid>
          <ToggleButton x:Name='tb' Focusable='False' ClickMode='Press' Cursor='Hand'
              IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
            <ToggleButton.Template>
              <ControlTemplate TargetType='ToggleButton'>
                <Border x:Name='b' Background='{StaticResource Field}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='8'>
                  <TextBlock Text='&#xE70D;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10' Foreground='{StaticResource Sub}'
                      HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
                  <Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>
          <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' Margin='12,0,32,0'
              VerticalAlignment='Center' HorizontalAlignment='Left'/>
          <Border x:Name='ring' BorderThickness='1' BorderBrush='Transparent' CornerRadius='8' IsHitTestVisible='False'/>
          <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' VerticalOffset='4'
              AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
            <Border Background='{StaticResource Card}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='10' Padding='4'
                MinWidth='{TemplateBinding ActualWidth}' MaxHeight='320'>
              <ScrollViewer HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'>
                <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
              </ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='ring' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='Slider'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='22' Background='Transparent'>
          <Border Height='4' CornerRadius='2' Background='{StaticResource Off}' VerticalAlignment='Center' Margin='8,0'/>
          <Track x:Name='PART_Track'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Grid Background='Transparent'><Border Height='4' CornerRadius='2' Background='{StaticResource Clay}' Margin='8,0,0,0'/></Grid></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse x:Name='e' Width='18' Height='18' Fill='White' Stroke='{StaticResource Clay}' StrokeThickness='4'/></ControlTemplate></Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='10'/>
    <Setter Property='MinWidth' Value='10'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Track x:Name='PART_Track' IsDirectionReversed='True'>
          <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
          <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border CornerRadius='3' Background='{StaticResource Off}' Margin='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        </Track>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }
}
