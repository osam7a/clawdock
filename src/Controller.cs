using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Wrenlet
{
    // Ties everything together: watches Claude and the sessions, handles hook events and
    // permission requests, decides what the notch shows and when, and reacts to the shortcut,
    // the notch, the tray icon and the settings window.
    class Controller
    {
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
            if (settings.VoiceMode == "quick") QuickVoice.EnsureSkill();
            KeepHooks();
            refreshTimer.Start();
            hookTimer.Start();
            Refresh();
        }

        // ---- hooks ---------------------------------------------------------

        void OnHook(Dictionary<string, object> d, HookServer.Pending p)
        {
            if (QuickVoice.IsVoiceFolder(Json.Str(d, "cwd"))) { OnVoiceHook(d, p); return; }
            hub.Tracker.Apply(d);
            string ev = Json.Str(d, "hook_event_name");
            string sid = Json.Str(d, "session_id");

            // The session moved on, so any question it asked was answered somewhere else. Not
            // PostToolUse: it only comes for the task-list tools, often sent in the same message
            // as the tool that's waiting, so it says nothing about that tool.
            if (ev == "Stop" || ev == "UserPromptSubmit" || ev == "SessionEnd")
            {
                ReleasePending(sid);
                alerts.RemoveAll(a => a.SessionId == sid);
            }
            // A new prompt (typed in Claude, or a button pressed there) answers its question.
            if (ev == "UserPromptSubmit" || ev == "SessionEnd") questions.RemoveAll(q => q.SessionId == sid);
            if (ev == "UserPromptSubmit") ChoiceSender.PromptSubmitted(sid);
            if (ev == "PostToolUse" && Json.Str(d, "tool_name") == "mcp__ccd_turn__wrap_up") OnWrapUp(sid, d);

            if (ev == "PermissionRequest")
            {
                string detail = DescribeTool(Json.Str(d, "tool_name"), Json.Obj(d, "tool_input"));
                bool looking = LookingAt(sid);
                // Only app sessions are held for Allow / Deny. A terminal session keeps waiting
                // on the hook instead of asking, so it's handed straight back and the notch
                // just says it needs you.
                var session = sessions.Find(x => x.Id == sid);
                bool desktop = session != null && session.Desktop;
                if (p != null)
                {
                    p.Detail = detail;
                    p.FullDetail = FullCommand(Json.Obj(d, "tool_input"));
                    if (settings.ApproveFromNotch && !looking && desktop)
                    {
                        pending.Add(p);
                        alerts.RemoveAll(a => a.SessionId == sid);
                    }
                    else
                    {
                        p.Respond(HookServer.NoDecision); // Claude asks in its own window
                        if (!looking) ShowAlert(sid, Json.Str(d, "cwd"), detail);
                    }
                }
                else if (!looking) ShowAlert(sid, Json.Str(d, "cwd"), detail);
            }
            else if (ev == "Notification")
            {
                bool covered = pending.Exists(x => x.SessionId == sid) || alerts.Exists(a => a.SessionId == sid)
                    || (sid == closedSession && DateTime.Now < closedUntil); // you just closed it: it's in Claude now
                if (!covered && !LookingAt(sid)) ShowAlert(sid, Json.Str(d, "cwd"), Json.Str(d, "message"));
            }
            Refresh();
        }

        // Stays until it's been answered, you go to it, or you close it: Claude keeps waiting.
        void ShowAlert(string sid, string cwd, string detail)
        {
            string project = ProjectName(cwd);
            var session = sessions.Find(x => x.Id == sid);
            bool terminal = session != null && !session.Desktop;
            alerts.RemoveAll(a => a.SessionId == sid);
            alerts.Add(new Alert
            {
                SessionId = sid,
                At = DateTime.Now,
                Terminal = terminal,
                Title = project == "" ? "Needs your permission" : "Needs permission · " + project,
                Detail = string.IsNullOrEmpty(detail) ? (terminal ? "Answer in the terminal" : "Open Claude to respond") : detail,
            });
        }

        // The end of a turn in the desktop app: a recap and, sometimes, a question with one or
        // two buttons. Keep the question (one per session) so the notch can ask it too.
        void OnWrapUp(string sid, Dictionary<string, object> d)
        {
            questions.RemoveAll(old => old.SessionId == sid);
            var input = Json.Obj(d, "tool_input");
            string ask = Json.Str(input, "ask").Trim();
            var actions = input != null && input.ContainsKey("actions") ? input["actions"] as System.Collections.IEnumerable : null;
            if (!settings.ShowQuestions || ask == "" || actions == null || actions is string) return;

            var q = new Question { SessionId = sid, Ask = ask, Cwd = Json.Str(d, "cwd"), At = DateTime.Now };
            foreach (object o in actions)
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                string action = Json.Str(a, "action");
                q.Labels.Add(ChoiceSender.LabelFor(Json.Str(a, "label"), action));
                q.Actions.Add(action == "" ? null : action);
                if (q.Labels.Count == 3) break;
            }
            if (q.Labels.Count > 0) questions.Add(q);
        }

        // Quick voice's own session isn't one of yours: only its tool approvals matter. Look-ups go
        // ahead; anything that acts waits for Allow / Deny on the notch.
        void OnVoiceHook(Dictionary<string, object> d, HookServer.Pending p)
        {
            if (p == null) return;
            string tool = Json.Str(d, "tool_name");
            // A saved quick voice chat opened in Claude asks in its own window.
            if (Json.Str(d, "hook_event_name") != "PreToolUse") { p.Respond(HookServer.NoDecision); return; }
            if (QuickVoice.SafeTool(tool)) { p.Respond(HookServer.PreAllow); return; }
            p.Detail = DescribeTool(tool, Json.Obj(d, "tool_input"));
            p.FullDetail = FullCommand(Json.Obj(d, "tool_input"));
            p.AllowReply = HookServer.PreAllow;
            p.DenyReply = HookServer.PreDeny;
            pending.Add(p);
            quick.ApprovalNeeded();
            Refresh();
        }

        // ---- quick voice and your other sessions ---------------------------

        List<SessionBrief.Entry> LiveSessions()
        {
            var list = new List<SessionBrief.Entry>();
            foreach (var s in sessions)
            {
                if (QuickVoice.IsVoiceFolder(s.Cwd)) continue;
                var request = pending.Find(p => p.SessionId == s.Id);
                var question = questions.Find(q => q.SessionId == s.Id);
                list.Add(new SessionBrief.Entry
                {
                    Id = s.Id, Name = s.Name, Cwd = s.Cwd, Desktop = s.Desktop, Busy = s.Busy, Elapsed = s.Elapsed, Updated = s.Updated,
                    Waiting = request != null ? "permission for " + request.Detail : question != null ? "an answer to: " + question.Ask : null,
                });
            }
            return list;
        }

        // Quick voice wants to say something to one of your sessions. That can interrupt it, so
        // nothing goes out until you press Allow, and you see the exact message first.
        void OnSessionMessageWanted(string id, string message)
        {
            var s = sessions.Find(x => x.Id == id && !QuickVoice.IsVoiceFolder(x.Cwd));
            if (s == null || message == "")
            {
                quick.AddNote("Couldn't find that session, so nothing was sent");
                quick.TellNext("Your message wasn't sent: that session isn't running.");
                return;
            }
            string sid = s.Id, name = s.DisplayName;
            var request = new HookServer.Pending
            {
                SessionId = "", Tool = "sending a message", Cwd = QuickVoice.Folder,
                Detail = "To " + name + (s.Busy ? " (working)" : "") + ": " + message, FullDetail = message,
            };
            request.Decided = allow =>
            {
                if (allow) DeliverMessage(sid, name, message);
                else quick.TellNext("The user didn't allow your message to " + name + ", so it wasn't sent.");
            };
            pending.Add(request);
            Refresh();
        }

        void DeliverMessage(string id, string name, string message)
        {
            var s = sessions.Find(x => x.Id == id);
            if (s == null)
            {
                quick.AddNote("Not sent: " + name + " has ended");
                quick.TellNext("Your message wasn't sent: " + name + " has ended.");
                return;
            }
            // Only into the chat it was meant for: unless that chat is confirmed open, you paste it.
            bool typeable = s.Desktop && !string.IsNullOrEmpty(s.Name) && sessions.FindAll(x => x.Name == s.Name).Count == 1;
            OpenSession(s);
            if (!typeable) { OnClipboard(name, message); return; }
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool open = false;
                for (int waited = 0; !open && waited < 4000; waited += 200)
                {
                    open = watcher.SessionTitle == s.Name;
                    if (!open) Thread.Sleep(200);
                }
                ui.BeginInvoke(new Action(() =>
                {
                    if (!open) { OnClipboard(name, message); return; }
                    PromptSender.ToDesktop(message, null);
                    quick.AddNote("Sent to " + name);
                    quick.TellNext("Your message to " + name + " was sent. Its reply will show up in the session snapshot.");
                }));
            });
        }

        void OnClipboard(string name, string message)
        {
            try { System.Windows.Clipboard.SetText(message); } catch { }
            quick.AddNote("On your clipboard: paste it into " + name);
            quick.TellNext("Your message to " + name + " couldn't be typed for the user. It's on their clipboard for them to paste, so it hasn't been sent yet.");
        }

        void OnChoose(object owner, int index)
        {
            var p = owner as HookServer.Pending;
            if (p != null) { Answer(p, index == 1); return; } // Deny, Allow

            var a = owner as Alert;
            if (a != null)
            {
                var target = sessions.Find(x => x.Id == a.SessionId);
                alerts.Remove(a);
                if (target != null) OpenSession(target); else watcher.RestoreClaude();
                Refresh();
                return;
            }

            var q = owner as Question;
            if (q == null || !questions.Contains(q)) return;
            // The notch lists the recommended choice last (on the right, like Allow).
            int choice = q.Labels.Count - 1 - index;
            if (choice < 0 || choice >= q.Labels.Count) return;
            var s = sessions.Find(x => x.Id == q.SessionId);
            string name = s != null ? s.Name : null;
            questions.Remove(q);
            ChoiceSender.Answer(q, choice, settings.NewChatLink,
                () => { if (s != null) OpenSession(s); else watcher.RestoreClaude(); },
                string.IsNullOrEmpty(name) || name == watcher.SessionTitle ? (Action)null : () => SelectInSidebar(name));
            Refresh();
        }

        void ReleasePending(string sid)
        {
            foreach (var p in pending.ToArray())
                if (sid == null || p.SessionId == sid) HandBack(p);
        }

        // To Claude's own prompt in that chat. Quick voice has no prompt to go back to, so there
        // it's a no, kept in the quick voice chat.
        void HandBack(HookServer.Pending p)
        {
            if (QuickVoice.IsVoiceFolder(p.Cwd)) { Decline(p, "Not allowed"); return; }
            p.Respond(HookServer.NoDecision);
            pending.Remove(p);
            closedSession = p.SessionId;
            closedUntil = DateTime.Now.AddSeconds(10);
        }

        void Decline(HookServer.Pending p, string why)
        {
            p.Respond(p.DenyReply);
            pending.Remove(p);
            quick.AddNote(why + ": " + p.Detail);
            if (p.Decided != null) p.Decided(false);
        }

        void Answer(HookServer.Pending p, bool allow)
        {
            if (!pending.Contains(p)) return; // already handed back to Claude
            if (!allow && QuickVoice.IsVoiceFolder(p.Cwd)) { Decline(p, "Not allowed"); Refresh(); return; }
            if (QuickVoice.IsVoiceFolder(p.Cwd)) quick.AddNote("Allowed: " + p.Detail);
            p.Respond(allow ? p.AllowReply : p.DenyReply);
            pending.Remove(p);
            if (allow && p.Decided != null) p.Decided(true);
            Refresh();
        }

        // ---- what the notch shows -----------------------------------------

        void Refresh()
        {
            var now = DateTime.Now;
            sessions = hub.Build(scanner.Latest, now);
            sessions.RemoveAll(x => QuickVoice.IsVoiceFolder(x.Cwd)); // quick voice's own session
            bool looking = watcher.ClaudeInFront || ForegroundIsTerminalOf(null);
            if (looking && !wasLooking && settings.DismissFor == "return") dismissedUntil = DateTime.MinValue;
            wasLooking = looking;

            // A held request goes back to Claude's own prompt when you go and look at that chat.
            // Before the hook times out it does too, and the notch keeps saying it's waiting.
            // Quick voice's are only yours to answer here: looking at Claude changes nothing.
            foreach (var p in pending.ToArray())
            {
                if (p.Settled) { pending.Remove(p); continue; }
                bool voiceRequest = QuickVoice.IsVoiceFolder(p.Cwd);
                bool expired = now - p.At > HoldPermissionFor;
                if (expired && voiceRequest) Decline(p, "No answer in time");
                else if (expired) { p.Respond(HookServer.NoDecision); pending.Remove(p); ShowAlert(p.SessionId, p.Cwd, p.Detail); }
                else if (!voiceRequest && LookingAt(p.SessionId)) { p.Respond(HookServer.NoDecision); pending.Remove(p); }
            }
            questions.RemoveAll(q => now - q.At > TimeSpan.FromHours(12) || !SessionAlive(q.SessionId));
            var question = questions.Count > 0 ? questions[questions.Count - 1] : null;
            foreach (var a in alerts.ToArray())
            {
                if (a.SeenAt == DateTime.MinValue && LookingAt(a.SessionId, true)) a.SeenAt = now;
                bool seen = a.SeenAt != DateTime.MinValue && now - a.SeenAt > AlertFadeAfterSeen;
                bool ended = now - a.At > TimeSpan.FromMinutes(1) && !SessionAlive(a.SessionId);
                if (seen || ended || now - a.At > TimeSpan.FromHours(12)) alerts.Remove(a);
            }
            var alert = alerts.Count > 0 ? alerts[alerts.Count - 1] : null;

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
                string project = QuickVoice.IsVoiceFolder(p.Cwd) ? "Quick voice" : ProjectName(p.Cwd);
                st.Title = "Allow " + (p.Tool == "" ? "this" : p.Tool) + "?" + (project == "" ? "" : " · " + project);
                st.Detail = string.IsNullOrEmpty(p.Detail) ? "Claude Code wants to continue" : p.Detail;
                st.Owner = p;
                st.Choices = new List<string> { "Deny", "Allow" };
                st.Primary = 1;
            }
            else if (quick.Active && (alert == null || quick.InUse)) ShowQuick(st); // the minimized bar doesn't hide a request
            else if (alert != null)
            {
                st.Kind = "alert";
                st.Title = alert.Title;
                st.Detail = alert.Detail;
                st.Owner = alert;
                st.Choices = new List<string> { alert.Terminal ? "Open terminal" : "Open Claude" };
                st.Primary = 0;
                st.MorePending = alerts.Count - 1;
            }
            else if (voice)
            {
                st.Kind = "voice";
                st.Mood = st.Muted ? MascotMood.Idle : MascotMood.Listening;
            }
            else if (question != null)
            {
                var qs = sessions.Find(x => x.Id == question.SessionId);
                st.Kind = "question";
                st.Title = "Claude asks · " + (qs != null ? qs.DisplayName : ProjectName(question.Cwd));
                st.Detail = question.Ask;
                st.Owner = question;
                st.Choices = new List<string>(question.Labels);
                st.Choices.Reverse();
                st.Primary = st.Choices.Count - 1;
                st.MorePending = questions.Count - 1;
            }
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
                st.Mood = primary.Busy ? activity.For(primary.Id, primary.Cwd)
                    : primary.JustFinished ? MascotMood.Done
                    : MascotMood.Sleeping;
            }
            else if (watcher.Working)
            {
                // A turn running in the app that no session file or hook reports (e.g. a chat).
                st.Kind = "session";
                st.Busy = true;
                st.Title = string.IsNullOrEmpty(watcher.SessionTitle) ? "Claude" : watcher.SessionTitle;
                st.Detail = "Working " + SessionTracker.Clock(now - watcher.WorkingSince);
                st.Short = SessionTracker.Clock(now - watcher.WorkingSince);
                st.Mood = MascotMood.Thinking;
            }

            bool alertOn = st.Kind == "alert";
            bool fullscreenBlock = watcher.FullscreenApp && !settings.ShowOverFullscreen;
            // Permission requests show even when the notch is hidden. Over a fullscreen app they
            // wait (still held) and appear when you leave it, unless you allowed fullscreen.
            // Quick voice's show anyway, like its panel: you just asked for it.
            if (alertOn) st.Visible = !fullscreenBlock || (st.Pending != null && QuickVoice.IsVoiceFolder(st.Pending.Cwd));
            else if (st.Kind == "quick") st.Visible = true; // you just asked for it
            else if (now < dismissedUntil || fullscreenBlock || looking) st.Visible = false;
            else if (voice || st.Kind == "question") st.Visible = true;
            else if (settings.ShowWhen == "alerts") st.Visible = false;
            else if (settings.ShowWhen == "away") st.Visible = watcher.ClaudeOpen || sessions.Count > 0;
            else st.Visible = ongoing || (watcher.ClaudeOpen && watcher.ClaudeMinimized);

            notch.Render(st);
            shownKind = st.Visible ? st.Kind : "";
            Hotkeys.WatchEscape = shownKind == "quick" ? !settings.QuickMinimized || quick.Speaking || quick.FollowingUp || quick.Typing : shownKind == "alert" || shownKind == "question";
            tray.SetStatus(st.Kind == "alert" ? "Needs permission" : st.Kind == "quick" ? "Quick voice" : st.Kind == "question" ? "Claude asked you something" : voice ? "Voice conversation" : primary != null && primary.Busy ? "Working: " + primary.DisplayName : "Idle");
        }

        // Quick voice as a small bar (remembered for next time), or the whole conversation.
        void ToggleQuickMinimized()
        {
            settings.QuickMinimized = !settings.QuickMinimized;
            settings.Save();
            Refresh();
        }

        // Nothing it was waiting to do goes ahead, and no note is left behind.
        void ClearQuickChat()
        {
            foreach (var p in pending.ToArray())
                if (QuickVoice.IsVoiceFolder(p.Cwd)) { p.Respond(p.DenyReply); pending.Remove(p); }
            quick.ClearChat();
            Refresh();
        }

        // Quick voice is a small chat: earlier turns, then what you just said and the answer.
        void ShowQuick(NotchState st)
        {
            st.Kind = "quick";
            st.QuickCanClear = settings.QuickHistory != "history";
            st.QuickNow = quick.Current;
            st.QuickHistory = quick.History;
            st.QuickTyping = quick.Typing;
            st.QuickMinimized = settings.QuickMinimized && !quick.Typing;
            st.Speaking = quick.Speaking;
            switch (quick.State)
            {
                case QuickVoice.Phase.Listening:
                    st.Listening = true;
                    st.Mood = MascotMood.Listening;
                    st.VoiceStarted = quick.ListenStarted;
                    st.Title = "Listening";
                    st.QuickStatus = st.QuickNow.Heard != "" ? "" : !quick.MicReady ? "One moment…" : Hotkeys.Held ? "Go ahead, let go when you're done" : "Go ahead, I'm listening";
                    break;
                case QuickVoice.Phase.Error:
                    st.Title = "Quick voice";
                    st.QuickStatus = quick.Status;
                    break;
                default:
                    st.Answering = quick.Answer != "";
                    st.Busy = quick.Speaking || quick.State == QuickVoice.Phase.Thinking;
                    st.Mood = quick.Speaking || quick.State == QuickVoice.Phase.Answering ? MascotMood.Speaking
                        : quick.State == QuickVoice.Phase.Thinking ? quick.ThinkingMood
                        : MascotMood.Idle;
                    st.Title = quick.State == QuickVoice.Phase.Thinking ? (quick.Status != "" ? quick.Status : "Thinking…") : quick.Speaking ? "Speaking" : settings.QuickMinimized ? "Ready" : "Claude";
                    st.QuickStatus = quick.State == QuickVoice.Phase.Thinking ? quick.Status : quick.Notice;
                    break;
            }
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
            if (questions.Count > 0)
            {
                var q = sessions.Find(x => x.Id == questions[questions.Count - 1].SessionId);
                if (q != null) return q;
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

        // Is the user looking at this session right now (that chat in the app, or its terminal)?
        // Claude being in front on another chat doesn't count: the prompt wouldn't be on screen.
        // When it can't be told, the answer is ifUnsure, and a request that stays on the notch
        // can still be answered there.
        bool LookingAt(string sid, bool ifUnsure = false)
        {
            var s = sid == null ? null : sessions.Find(x => x.Id == sid);
            if (s == null) return ifUnsure && (watcher.ClaudeInFront || ForegroundIsTerminalOf(null));
            if (!s.Desktop) return ForegroundIsTerminalOf(s);
            if (!watcher.ClaudeInFront) return false;
            if (string.IsNullOrEmpty(s.Name) || string.IsNullOrEmpty(watcher.SessionTitle)) return ifUnsure;
            return s.Name == watcher.SessionTitle;
        }

        bool SessionAlive(string sid)
        {
            return scanner.Latest.Count == 0 || sessions.Exists(x => x.Id == sid);
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
            // During quick voice a click is about the conversation: the small bar opens up again.
            if (quick.Active && pending.Count == 0 && shownKind != "alert")
            {
                if (settings.QuickMinimized) ToggleQuickMinimized();
                return;
            }
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
            // A request from one of your chats is up, over quick voice: × closes only that.
            if (shownKind == "alert" && quick.Active && (pending.Count == 0 || !QuickVoice.IsVoiceFolder(pending[0].Cwd)))
            {
                CloseShownRequest();
                Refresh();
                return;
            }
            if (quick.Active)
            {
                // Stop quick voice, and say no to anything it was waiting to do.
                foreach (var p in pending.ToArray())
                    if (QuickVoice.IsVoiceFolder(p.Cwd)) Decline(p, "Not allowed");
                quick.Dismiss();
                return;
            }
            if (watcher.InVoice) watcher.EndVoice();
            ReleasePending(null);
            alerts.Clear();
            questions.Clear(); // they're still in Claude; the notch just stops asking
            var d = settings.DismissDuration;
            dismissedUntil = d == TimeSpan.Zero ? DateTime.MaxValue : DateTime.Now + d;
            Refresh();
        }

        string shownKind = "";

        // The request on the notch, and only that one, so any others waiting behind it stay:
        // to Claude's own prompt (quick voice's is a no, noted in its chat).
        void CloseShownRequest()
        {
            if (pending.Count > 0) HandBack(pending[0]);
            else if (alerts.Count > 0) alerts.RemoveAt(alerts.Count - 1);
        }

        // Esc closes what's up on the notch: the quick voice panel (as its ×), or a permission
        // request or question (nothing allowed or denied, and the notch isn't hidden for later).
        // On the minimized bar it only stops the reading.
        void OnEscape()
        {
            if (shownKind == "quick")
            {
                if (quick.FollowingUp) quick.CancelFollowUp();
                else if (quick.Typing) Dismiss();
                else if (settings.QuickMinimized) quick.Hush();
                else Dismiss();
            }
            else if (shownKind == "alert") CloseShownRequest();
            else if (shownKind == "question" && questions.Count > 0) questions.RemoveAt(questions.Count - 1);
            Refresh();
        }

        // Twice in a row: the text box instead of the microphone the first press opened.
        void OnShortcutTwice()
        {
            if (settings.VoiceMode == "quick") quick.OpenTyping();
        }

        void OnShortcut()
        {
            if (settings.VoiceMode == "quick")
            {
                // This cuts off the question that's waiting, so say so before its turn moves up.
                if (quick.State == QuickVoice.Phase.Thinking || quick.State == QuickVoice.Phase.Answering)
                    foreach (var p in pending.ToArray())
                        if (QuickVoice.IsVoiceFolder(p.Cwd)) Decline(p, "Stopped before you answered");
                quick.Press();
                return;
            }
            if (watcher.InVoice) return; // already talking
            string mode = settings.VoiceMode;
            // Typing goes to Claude's own quick entry (Ctrl+Alt+Space).
            if (mode == "type" || (mode == "voice-or-type" && !VoiceLauncher.HasMicrophone())) PromptSender.OpenQuickEntry(settings.NewChatLink);
            else VoiceLauncher.StartAsync(settings.NewChatLink);
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
