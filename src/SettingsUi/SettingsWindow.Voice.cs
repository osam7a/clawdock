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
    // The voice and prompts page of the settings.
    partial class SettingsWindow
    {
        void BuildVoicePage()
        {
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
        }
    }
}
