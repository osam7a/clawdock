using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // Ties everything together: watches Claude and the sessions, handles hook events and
    // permission requests, decides what the notch shows and when, and reacts to the shortcut,
    // the notch, the tray icon and the settings window.
    partial class Controller
    {
        const int MostDetail = 20000;
        static readonly TimeSpan HoldPermissionFor = TimeSpan.FromMinutes(9); // the hook itself times out at 10
        static readonly TimeSpan AlertFadeAfterSeen = TimeSpan.FromSeconds(6);

        class Alert { public string SessionId, Title, Detail; public bool Terminal; public DateTime At, SeenAt; }

        readonly Settings settings;
        readonly ClaudeWatcher watcher = new ClaudeWatcher();
        readonly SessionScanner scanner = new SessionScanner();
        readonly SessionHub hub = new SessionHub();
        readonly HookServer server = new HookServer();
        readonly NotchWindow notch;
        readonly QuickVoice quick;
        readonly TrayIcon tray;
        readonly DispatcherTimer refreshTimer, hookTimer;
        readonly List<HookServer.Pending> pending = new List<HookServer.Pending>();
        readonly List<Question> questions = new List<Question>();

        SettingsWindow settingsWindow;
        readonly List<Alert> alerts = new List<Alert>(); // one per session; the newest shows
        readonly SessionActivity activity = new SessionActivity();
        string closedSession;
        DateTime closedUntil;
        List<SessionInfo> sessions = new List<SessionInfo>();
        DateTime dismissedUntil = DateTime.MinValue, voiceStarted;
        bool wasLooking, wasVoice;
        public string ShortcutError { get; private set; }

        public Controller(Settings settings)
        {
            this.settings = settings;
            hub.FinishedFor = TimeSpan.FromSeconds(settings.FinishedSeconds);
            notch = new NotchWindow(settings);
            tray = new TrayIcon();
            quick = new QuickVoice(settings) { HookExe = HookExe, KeepOpen = () => notch.Hovering || quick.Typing, Persist = () => settings.QuickMinimized };
            quick.Changed += Refresh;
            notch.ToggleMinimize += ToggleQuickMinimized;
            notch.ClearChat += ClearQuickChat;
            notch.QuickLevel = () => quick.State == QuickVoice.Phase.Listening ? quick.InputLevel : quick.OutputLevel;

            notch.OpenPrimary += OpenPrimary;
            notch.OpenSession += OpenSession;
            notch.Choose += OnChoose;
            ChoiceSender.Failed += msg => tray.Balloon(msg);
            notch.ToggleMute += watcher.ToggleMute;
            notch.EndVoice += watcher.EndVoice;
            notch.Dismiss += Dismiss;
            notch.OpenSettings += OpenSettings;
            notch.Quit += Quit;

            tray.OpenSettings += OpenSettings;
            tray.AskClaude += () => PromptSender.OpenQuickEntry(settings.NewChatLink);
            tray.RepairHooks += () => { tray.Balloon(RepairHooks() ? "Claude Code hooks are installed." : "Couldn't update ~/.claude/settings.json. See the log."); };
            tray.Quit += Quit;

            PromptSender.Failed += msg => tray.Balloon(msg);

            server.HookReceived += OnHook;
            server.PendingGone += p =>
            {
                if (pending.Remove(p) && QuickVoice.IsVoiceFolder(p.Cwd)) quick.AddNote("Stopped before you answered: " + p.Detail);
                Refresh();
            };
            server.CommandReceived += cmd => { if (cmd == "settings") OpenSettings(); else if (cmd == "quit") Quit(); };
            watcher.Changed += Refresh;
            Hotkeys.Pressed += OnShortcut;
            Hotkeys.DoublePressed += OnShortcutTwice;
            notch.SendTyped += text => quick.SendTyped(text);
            notch.OpenChat += () => quick.OpenInClaude();
            quick.LiveSessions = LiveSessions;
            quick.SessionMessageWanted += OnSessionMessageWanted;
            quick.NewProjectWanted += OnNewProjectWanted;
            Hotkeys.Escape += OnEscape;

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            refreshTimer.Tick += (s, e) => Refresh();
            hookTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            hookTimer.Tick += (s, e) =>
            {
                KeepHooks();
                // Windows quietly drops a low-level keyboard hook that was ever too slow to
                // answer, so re-arm the shortcut now and then.
                if (settings.Shortcut == "copilot") Hotkeys.Apply(settings);
            };
        }

        public void Start()
        {
            server.Start(Dispatcher.CurrentDispatcher);
            scanner.Start();
            watcher.Start();
            ShortcutError = Hotkeys.Apply(settings);
            if (ShortcutError != null) tray.Balloon(ShortcutError);
            if (settings.VoiceMode == "quick") { QuickVoice.EnsureSkill(); SessionArchive.RefreshSoon(QuickVoice.Folder); }
            KeepHooks();
            refreshTimer.Start();
            hookTimer.Start();
            Refresh();
        }

        // ---- settings ------------------------------------------------------

        public void OpenSettings()
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow(settings.Clone(), this);
            settingsWindow.Saved += ApplySettings;
            settingsWindow.Closed += (s, e) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        void ApplySettings(Settings changed)
        {
            bool hooksWere = settings.KeepHooksInstalled;
            settings.CopyFrom(changed);
            settings.Save();
            hub.FinishedFor = TimeSpan.FromSeconds(settings.FinishedSeconds);
            ShortcutError = Hotkeys.Apply(settings);
            notch.ApplySettings();
            if (Installer.IsInstalledCopy) Installer.SetStartWithWindows(settings.StartWithWindows);
            if (settings.KeepHooksInstalled) KeepHooks();
            else if (hooksWere) HookInstaller.Uninstall();
            if (!settings.ApproveFromNotch)
                foreach (var p in pending.ToArray())
                    if (!QuickVoice.IsVoiceFolder(p.Cwd)) HandBack(p);
            if (settings.VoiceMode == "quick") QuickVoice.EnsureSkill();
            if (settings.VoiceMode == "quick" && settings.QuickEngine == "local") MicRecorder.WarmUp();
            quick.SettingsChanged();
            Refresh();
        }

        public string HookExe { get { return Installer.IsInstalledCopy ? Installer.InstalledExe : Installer.CurrentExe; } }

        public HookInstaller.State HookState() { return HookInstaller.Check(HookExe); }

        public bool RepairHooks() { return HookInstaller.Install(HookExe); }

        // Puts the hooks back if something removed or changed them (only for the installed copy,
        // so a test build doesn't take them over).
        void KeepHooks()
        {
            if (!settings.KeepHooksInstalled || !Installer.IsInstalledCopy) return;
            var state = HookInstaller.Check(Installer.InstalledExe);
            if (state == HookInstaller.State.Missing || state == HookInstaller.State.Outdated) HookInstaller.Install(Installer.InstalledExe);
        }

        public void Uninstall()
        {
            ReleasePending(null);
            Installer.RemoveEverything();
            Quit();
        }

        public void Quit()
        {
            quick.Quit();
            ReleasePending(null);
            Hotkeys.Remove();
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ---- helpers -------------------------------------------------------

        static string ProjectName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            try { return System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/')); } catch { return ""; }
        }

        // Everything the tool was given, for the request's details: nothing left out but what's too long to show.
        internal static string FullRequest(Dictionary<string, object> input, string cwd)
        {
            var t = new System.Text.StringBuilder();
            if (input != null)
                foreach (var kv in input)
                {
                    string value = kv.Value as string ?? (kv.Value == null ? "" : Json.Pretty(Json.Write(kv.Value)).Trim());
                    value = value.Replace("\r\n", "\n");
                    t.Append(InputLabel(kv.Key)).Append(value.IndexOf('\n') >= 0 ? ":\n" : ": ").Append(value).Append("\n\n");
                }
            if (cwd != "") t.Append("Folder: ").Append(cwd);
            string all = t.ToString().TrimEnd();
            int over = all.Length - MostDetail;
            return over > 0 ? all.Substring(0, MostDetail) + "\n\n… and " + over + " more characters. Open the chat to see them." : all;
        }

        static string InputLabel(string key)
        {
            switch (key)
            {
                case "command": return "Command";
                case "description": return "Description";
                case "file_path": return "File";
                case "old_string": return "Replace";
                case "new_string": return "With";
                case "content": return "Contents";
                case "url": return "URL";
                case "prompt": return "Prompt";
                default: return key;
            }
        }

        internal static string DescribeTool(string tool, Dictionary<string, object> input)
        {
            string what = Json.Str(input, "command");
            if (what == "") what = Json.Str(input, "file_path");
            if (what == "") what = Json.Str(input, "url");
            if (what == "") what = Json.Str(input, "description");
            what = what.Replace("\r", " ").Replace("\n", " ");
            if (what.Length > 200) what = what.Substring(0, 200) + "…";
            if (tool == "") return what;
            return what == "" ? tool : tool + ": " + what;
        }
    }
}
