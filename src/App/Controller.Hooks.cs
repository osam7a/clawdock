using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // What Claude Code tells the notch, and answering its permission requests.
    partial class Controller
    {
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
                    p.FullDetail = FullRequest(Json.Obj(d, "tool_input"), Json.Str(d, "cwd"));
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
            p.FullDetail = FullRequest(Json.Obj(d, "tool_input"), "");
            p.AllowReply = HookServer.PreAllow;
            p.DenyReply = HookServer.PreDeny;
            pending.Add(p);
            quick.ApprovalNeeded();
            Refresh();
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
    }
}
