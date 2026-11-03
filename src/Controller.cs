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
    class Controller
    {
        static readonly TimeSpan HoldPermissionFor = TimeSpan.FromMinutes(9); // the hook itself times out at 10
        static readonly TimeSpan AlertFadeAfterSeen = TimeSpan.FromSeconds(6);

        class Alert { public string SessionId, Title, Detail; public DateTime At, SeenAt; }

        readonly Settings settings;
        readonly ClaudeWatcher watcher = new ClaudeWatcher();
        readonly SessionScanner scanner = new SessionScanner();
        readonly SessionHub hub = new SessionHub();
        readonly HookServer server = new HookServer();
        readonly NotchWindow notch;
        readonly PromptWindow prompt = new PromptWindow();
        readonly TrayIcon tray;
        readonly DispatcherTimer refreshTimer, hookTimer;
        readonly List<HookServer.Pending> pending = new List<HookServer.Pending>();

        SettingsWindow settingsWindow;
        Alert alert;
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

            notch.OpenPrimary += OpenPrimary;
            notch.OpenSession += OpenSession;
            notch.Answer += (p, allow) => Answer(p, allow);
            notch.ToggleMute += watcher.ToggleMute;
            notch.EndVoice += watcher.EndVoice;
            notch.Dismiss += Dismiss;
            notch.OpenSettings += OpenSettings;
            notch.Quit += Quit;

            tray.OpenSettings += OpenSettings;
            tray.AskClaude += () => ShowPrompt(null);
            tray.RepairHooks += () => { tray.Balloon(RepairHooks() ? "Claude Code hooks are installed." : "Couldn't update ~/.claude/settings.json. See the log."); };
            tray.Quit += Quit;

            prompt.Send += (text, target) =>
            {
                if (target == "cli") PromptSender.ToTerminal(text, settings.CliFolder);
                else PromptSender.ToDesktop(text, settings.NewChatLink);
            };
            PromptSender.Failed += msg => tray.Balloon(msg);

            server.HookReceived += OnHook;
            server.PendingGone += p => { pending.Remove(p); Refresh(); };
            server.CommandReceived += cmd => { if (cmd == "settings") OpenSettings(); else if (cmd == "quit") Quit(); };
            watcher.Changed += Refresh;
            Hotkeys.Pressed += OnShortcut;

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
            KeepHooks();
            refreshTimer.Start();
            hookTimer.Start();
            Refresh();
        }

        // ---- hooks ---------------------------------------------------------

        void OnHook(Dictionary<string, object> d, HookServer.Pending p)
        {
            hub.Tracker.Apply(d);
            string ev = Json.Str(d, "hook_event_name");
            string sid = Json.Str(d, "session_id");

            // The session moved on, so any question it asked was answered somewhere else.
            if (ev == "Stop" || ev == "UserPromptSubmit" || ev == "PostToolUse" || ev == "SessionEnd")
            {
                ReleasePending(sid);
                if (alert != null && alert.SessionId == sid) alert = null;
            }

            if (ev == "PermissionRequest")
            {
                string detail = DescribeTool(Json.Str(d, "tool_name"), Json.Obj(d, "tool_input"));
                bool looking = LookingAt(sid);
                if (p != null)
                {
                    p.Detail = detail;
                    p.FullDetail = FullCommand(Json.Obj(d, "tool_input"));
                    if (settings.ApproveFromNotch && !looking)
                    {
                        pending.Add(p);
                        alert = null;
                    }
                    else
                    {
                        p.Respond(HookServer.NoDecision); // Claude asks in its own window
                        if (!looking) ShowAlert(sid, d, detail);
                    }
                }
                else if (!looking) ShowAlert(sid, d, detail);
            }
            else if (ev == "Notification")
            {
                bool covered = pending.Exists(x => x.SessionId == sid) || (alert != null && alert.SessionId == sid && (DateTime.Now - alert.At).TotalSeconds < 5);
                if (!covered && !LookingAt(sid)) ShowAlert(sid, d, Json.Str(d, "message"));
            }
            Refresh();
        }

        void ShowAlert(string sid, Dictionary<string, object> d, string detail)
        {
            string project = ProjectName(Json.Str(d, "cwd"));
            alert = new Alert
            {
                SessionId = sid,
                At = DateTime.Now,
                Title = project == "" ? "Needs your permission" : "Needs permission · " + project,
                Detail = string.IsNullOrEmpty(detail) ? "Open Claude to respond" : detail,
            };
        }

        void ReleasePending(string sid)
        {
            foreach (var p in pending.ToArray())
                if (sid == null || p.SessionId == sid) { p.Respond(HookServer.NoDecision); pending.Remove(p); }
        }

        void Answer(HookServer.Pending p, bool allow)
        {
            if (!pending.Contains(p)) return; // already handed back to Claude
            p.Respond(allow ? HookServer.Allow : HookServer.Deny);
            pending.Remove(p);
            Refresh();
        }

        // ---- what the notch shows -----------------------------------------

        void Refresh()
        {
            var now = DateTime.Now;
            sessions = hub.Build(scanner.Latest, now);
            bool looking = watcher.ClaudeInFront || ForegroundIsTerminalOf(null);
            if (looking && !wasLooking && settings.DismissFor == "return") dismissedUntil = DateTime.MinValue;
            wasLooking = looking;

            // A held request goes back to Claude's own prompt when you go and look at it, or
            // before the hook times out.
            foreach (var p in pending.ToArray())
            {
                if (p.Settled) { pending.Remove(p); continue; }
                if (now - p.At > HoldPermissionFor || LookingAt(p.SessionId)) { p.Respond(HookServer.NoDecision); pending.Remove(p); }
            }
            if (alert != null)
            {
                if (LookingAt(alert.SessionId)) { if (alert.SeenAt == DateTime.MinValue) alert.SeenAt = now; }
                if ((alert.SeenAt != DateTime.MinValue && now - alert.SeenAt > AlertFadeAfterSeen) || now - alert.At > TimeSpan.FromMinutes(10)) alert = null;
            }

            bool voice = watcher.InVoice;
            if (voice && !wasVoice) voiceStarted = now;
            wasVoice = voice;

            var primary = Primary();
            var st = new NotchState { Sessions = sessions, Muted = watcher.MicMuted, VoiceStarted = voiceStarted };
            bool ongoing = watcher.Working || sessions.Exists(x => x.Busy || x.JustFinished);

            if (pending.Count > 0)
            {
                var p = pending[0];
                st.Kind = "alert";
                st.Pending = p;
                st.MorePending = pending.Count - 1;
                string project = ProjectName(p.Cwd);
                st.Title = "Allow " + (p.Tool == "" ? "this" : p.Tool) + "?" + (project == "" ? "" : " · " + project);
                st.Detail = string.IsNullOrEmpty(p.Detail) ? "Claude Code wants to continue" : p.Detail;
            }
            else if (alert != null)
            {
                st.Kind = "alert";
                st.Title = alert.Title;
                st.Detail = alert.Detail;
            }
            else if (voice) st.Kind = "voice";
            else if (primary != null)
            {
                st.Kind = "session";
                st.Title = primary.DisplayName;
                st.Busy = primary.Busy;
                st.JustFinished = primary.JustFinished;
                st.Done = primary.Done;
                st.Total = primary.Total;
                st.Detail = StatusLine(primary);
                st.Short = NotchWindow.RowStatus(primary);
            }
            else if (watcher.Working)
            {
                // A turn running in the app that no session file or hook reports (e.g. a chat).
                st.Kind = "session";
                st.Busy = true;
                st.Title = string.IsNullOrEmpty(watcher.SessionTitle) ? "Claude" : watcher.SessionTitle;
                st.Detail = "Working " + SessionTracker.Clock(now - watcher.WorkingSince);
                st.Short = SessionTracker.Clock(now - watcher.WorkingSince);
            }

            bool alertOn = st.Kind == "alert";
            bool fullscreenBlock = watcher.FullscreenApp && !settings.ShowOverFullscreen;
            if (alertOn) st.Visible = true; // permission requests show even when hidden or over fullscreen
            else if (now < dismissedUntil || fullscreenBlock || looking) st.Visible = false;
            else if (voice) st.Visible = true;
            else if (settings.ShowWhen == "alerts") st.Visible = false;
            else if (settings.ShowWhen == "away") st.Visible = watcher.ClaudeOpen || sessions.Count > 0;
            else st.Visible = ongoing || (watcher.ClaudeOpen && watcher.ClaudeMinimized);

            notch.Render(st);
            tray.SetStatus(st.Kind == "alert" ? "Needs permission" : voice ? "Voice conversation" : primary != null && primary.Busy ? "Working: " + primary.DisplayName : "Idle");
        }

        // The session the notch talks about: the one asking for permission, else the app
        // session you just left (by its title), else the one working, else the one that just finished.
        SessionInfo Primary()
        {
            if (pending.Count > 0)
            {
                var p = sessions.Find(x => x.Id == pending[0].SessionId);
                if (p != null) return p;
            }
            string title = watcher.SessionTitle;
            SessionInfo s = null;
            if (!string.IsNullOrEmpty(title)) s = sessions.Find(x => x.Desktop && x.Name == title && (x.Busy || x.JustFinished));
            if (s == null) s = sessions.Find(x => x.Busy);
            if (s == null) s = sessions.Find(x => x.JustFinished);
            if (s == null && watcher.ClaudeMinimized && !string.IsNullOrEmpty(title))
                s = sessions.Find(x => x.Desktop && x.Name == title && DateTime.Now - x.Updated < TimeSpan.FromMinutes(30));
            return s;
        }

        static string StatusLine(SessionInfo s)
        {
            string line = s.Busy ? "Working " + SessionTracker.Clock(s.Elapsed)
                : s.Elapsed > TimeSpan.Zero ? "Done in " + SessionTracker.Clock(s.Elapsed)
                : "Idle";
            if (s.Total > 0)
            {
                line += " · " + s.Done + " done";
                if (s.Total - s.Done > 0) line += " · " + (s.Total - s.Done) + " left";
            }
            return line;
        }

        // Is the user looking at this session right now (its app window or its terminal)?
        bool LookingAt(string sid)
        {
            var s = sid == null ? null : sessions.Find(x => x.Id == sid);
            if (s == null) return watcher.ClaudeInFront || ForegroundIsTerminalOf(null);
            if (s.Desktop) return watcher.ClaudeInFront;
            return ForegroundIsTerminalOf(s);
        }

        bool ForegroundIsTerminalOf(SessionInfo only)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (only != null) return only.Host != IntPtr.Zero && only.Host == fg;
            foreach (var s in sessions) if (!s.Desktop && s.Host != IntPtr.Zero && s.Host == fg) return true;
            return false;
        }

        // ---- actions -------------------------------------------------------

        void OpenPrimary()
        {
            var s = Primary();
            if (s != null) OpenSession(s);
            else watcher.RestoreClaude();
        }

        void OpenSession(SessionInfo s)
        {
            if (!s.Desktop && s.Host != IntPtr.Zero)
            {
                if (!TerminalFocus.Focus(s.Host)) tray.Balloon("Couldn't bring that terminal to the front.");
                return;
            }
            if (!s.Desktop && s.Host == IntPtr.Zero && s.Pid > 0)
            {
                tray.Balloon("Couldn't find the window for " + s.DisplayName + ".");
                return;
            }
            watcher.RestoreClaude();
            if (!string.IsNullOrEmpty(s.Name) && s.Name != watcher.SessionTitle) SelectInSidebar(s.Name);
        }

        // Best effort: click the session's entry in Claude's sidebar. Sidebar entries are plain
        // buttons named after the session, so a session called e.g. "Settings" could match an
        // action button: only click when exactly one candidate exists, never in a toolbar.
        static void SelectInSidebar(string name)
        {
            IntPtr h = Native.FindClaudeWindow();
            if (h == IntPtr.Zero) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var win = AutomationElement.FromHandle(h);
                    var named = new PropertyCondition(AutomationElement.NameProperty, name);
                    var walker = TreeWalker.ControlViewWalker;
                    var candidates = new List<AutomationElement>();
                    foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, named))
                    {
                        var type = e.Current.ControlType;
                        if (type != ControlType.Button && type != ControlType.ListItem && type != ControlType.TreeItem) continue;
                        var parent = walker.GetParent(e);
                        if (parent != null && (parent.Current.ControlType == ControlType.ToolBar || parent.Current.ControlType == ControlType.MenuBar)) continue;
                        candidates.Add(e);
                    }
                    if (candidates.Count != 1) return;
                    object pattern;
                    if (candidates[0].TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
                    else if (candidates[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select();
                }
                catch { }
            });
        }

        void Dismiss()
        {
            if (watcher.InVoice) watcher.EndVoice();
            ReleasePending(null);
            alert = null;
            var d = settings.DismissDuration;
            dismissedUntil = d == TimeSpan.Zero ? DateTime.MaxValue : DateTime.Now + d;
            Refresh();
        }

        void OnShortcut()
        {
            if (watcher.InVoice) return; // already talking
            string mode = settings.VoiceMode;
            if (mode == "type") ShowPrompt(null);
            else if (mode == "voice-or-type" && !VoiceLauncher.HasMicrophone()) ShowPrompt("No microphone found, so type to Claude instead.");
            else VoiceLauncher.StartAsync(settings.NewChatLink);
        }

        void ShowPrompt(string message)
        {
            prompt.Open(notch.PromptPosition(444, 150), settings.PromptTarget, message);
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
            if (!settings.ApproveFromNotch) ReleasePending(null);
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

        static string FullCommand(Dictionary<string, object> input)
        {
            string what = Json.Str(input, "command");
            if (what == "") what = Json.Str(input, "file_path");
            if (what == "") what = Json.Str(input, "url");
            return what.Length > 4000 ? what.Substring(0, 4000) + "…" : what;
        }

        static string DescribeTool(string tool, Dictionary<string, object> input)
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
