using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Clawdock
{
    // Deciding what the notch shows.
    partial class Controller
    {
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
            else if (quick.Active && !quick.Shrunk && (alert == null || quick.InUse)) ShowQuick(st); // the minimized bar doesn't hide a request
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
            else if (quick.Active) ShowQuick(st); // a finished answer's bar: anything else comes first

            bool alertOn = st.Kind == "alert";
            bool fullscreenBlock = watcher.FullscreenApp && !settings.ShowOverFullscreen;
            // Permission requests show even when the notch is hidden. Over a fullscreen app they
            // wait (still held) and appear when you leave it, unless you allowed fullscreen.
            // Quick voice's show anyway, like its panel: you just asked for it.
            if (alertOn) st.Visible = !fullscreenBlock || (st.Pending != null && QuickVoice.IsVoiceFolder(st.Pending.Cwd));
            else if (st.Kind == "quick") st.Visible = !quick.Shrunk || !fullscreenBlock; // you just asked for it
            else if (now < dismissedUntil || fullscreenBlock || looking) st.Visible = false;
            else if (voice || st.Kind == "question") st.Visible = true;
            else if (settings.ShowWhen == "alerts") st.Visible = false;
            else if (settings.ShowWhen == "away") st.Visible = watcher.ClaudeOpen || sessions.Count > 0;
            else st.Visible = ongoing || (watcher.ClaudeOpen && watcher.ClaudeMinimized);

            notch.Render(st);
            shownKind = st.Visible ? st.Kind : "";
            Hotkeys.WatchEscape = shownKind == "quick" ? !QuickSmall || quick.Speaking || quick.FollowingUp || quick.Typing || quick.InTurn : shownKind == "alert" || shownKind == "question";
            tray.SetStatus(st.Kind == "alert" ? "Needs permission" : st.Kind == "quick" ? "Quick voice" : st.Kind == "question" ? "Claude asked you something" : voice ? "Voice conversation" : primary != null && primary.Busy ? "Working: " + primary.DisplayName : "Idle");
        }

        bool QuickSmall { get { return settings.QuickMinimized || quick.Shrunk; } }

        // Quick voice as a small bar (remembered for next time), or the whole conversation.
        void ToggleQuickMinimized()
        {
            if (quick.Shrunk) { quick.Unshrink(); return; } // only this answer's bar: your setting stays
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
            st.QuickMinimized = QuickSmall && !quick.Typing;
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
                    st.Title = quick.State == QuickVoice.Phase.Thinking ? (quick.Status != "" ? quick.Status : "Thinking…") : quick.Speaking ? "Speaking" : QuickSmall ? "Ready" : "Claude";
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
    }
}
