using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wrenlet
{
    // Follows Claude Code sessions through the hook events the notch receives:
    //   UserPromptSubmit / Stop       -> a turn started / finished (working time)
    //   PostToolUse on the task tools -> the task list (done vs. left)
    //   SessionEnd                    -> forget the session
    // The task list is rebuilt the same way the app edits it: update_plan can replace
    // the whole list or advance / complete / start single steps.
    class SessionTracker
    {
        static readonly TimeSpan BusyExpiry = TimeSpan.FromHours(2);   // a turn with no news for this long is assumed over
        static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(30); // how long a finished session stays on the notch
        public TimeSpan JustFinishedFor = TimeSpan.FromSeconds(12);

        class Item { public string Content, Status, Id; } // Id: set for TaskCreate tasks

        class Session
        {
            public string Id, Project, Cwd;
            public bool Busy;
            public DateTime TurnStart, LastEvent, FinishedAt;
            public TimeSpan LastDuration;
            public List<Item> Items = new List<Item>();
        }

        public class Snapshot
        {
            public string Id, Project, Cwd;
            public bool Busy, JustFinished;
            public TimeSpan Elapsed;
            public DateTime TurnStart, LastEvent;
            public int Done, Total;
        }

        // Rebuilds a session's task list from its transcript, the same way the hook events do, so
        // it also works for sessions that started before the hooks were installed.
        public class TaskReplay
        {
            readonly Session s = new Session();

            public void NewPrompt() { s.Items.RemoveAll(i => i.Id == null || i.Status == "completed"); }
            public void Tool(string name, Dictionary<string, object> input) { ApplyTool(s, name, input, null); }

            public List<KeyValuePair<string, string>> Items()
            {
                var list = new List<KeyValuePair<string, string>>();
                foreach (var i in s.Items) list.Add(new KeyValuePair<string, string>(i.Content, i.Status));
                return list;
            }
        }

        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        public void Apply(Dictionary<string, object> d)
        {
            string ev = Str(d, "hook_event_name");
            if (ev == "") return;

            string id = Str(d, "session_id");
            if (id == "") id = "default";
            DateTime now = DateTime.Now;

            if (ev == "SessionEnd") { sessions.Remove(id); return; }

            Session s;
            bool isNew = !sessions.TryGetValue(id, out s);
            if (isNew) { s = new Session { Id = id }; sessions[id] = s; }

            string cwd = Str(d, "cwd");
            if (cwd != "")
            {
                s.Cwd = cwd;
                try { s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/')); } catch { }
            }
            s.LastEvent = now;

            switch (ev)
            {
                case "UserPromptSubmit":
                    s.Busy = true;
                    s.TurnStart = now;
                    // A turn's plan starts over, but TaskCreate tasks belong to the session: keep the open ones.
                    s.Items.RemoveAll(i => i.Id == null || i.Status == "completed");
                    break;

                case "Stop":
                    Finish(s, now);
                    break;

                case "PostToolUse":
                    // The notch may have started mid-turn; treat the first sign of life as the start.
                    if (isNew) { s.Busy = true; s.TurnStart = now; }
                    ApplyTool(s, Str(d, "tool_name"), d.ContainsKey("tool_input") ? d["tool_input"] as Dictionary<string, object> : null,
                        d.ContainsKey("tool_response") ? d["tool_response"] : null);
                    break;

                case "PermissionRequest":
                case "Notification":
                    if (isNew) { s.Busy = true; s.TurnStart = now; }
                    break;
            }
        }

        // The session the notch should talk about: the busiest recent one, otherwise
        // the one that was active last. Null when nothing has happened for a while.
        public Snapshot Current(DateTime now)
        {
            Session pick = null;
            foreach (var s in sessions.Values)
            {
                if (s.Busy && now - s.LastEvent > BusyExpiry) Finish(s, s.LastEvent);
                if (s.Busy)
                {
                    if (pick == null || !pick.Busy || s.LastEvent > pick.LastEvent) pick = s;
                }
                else if (now - s.LastEvent < RecentFor && (pick == null || (!pick.Busy && s.LastEvent > pick.LastEvent))) pick = s;
            }
            return pick == null ? null : Snap(pick, now);
        }

        // What the hooks know about one session, or null if they never reported it.
        public Snapshot For(string id, DateTime now)
        {
            Session s;
            if (id == null || !sessions.TryGetValue(id, out s)) return null;
            if (s.Busy && now - s.LastEvent > BusyExpiry) Finish(s, s.LastEvent);
            return Snap(s, now);
        }

        // Sessions the hooks reported recently, for those without a live session file.
        public List<Snapshot> Recent(DateTime now)
        {
            var list = new List<Snapshot>();
            foreach (var s in sessions.Values)
            {
                if (s.Busy && now - s.LastEvent > BusyExpiry) Finish(s, s.LastEvent);
                if (s.Busy || now - s.FinishedAt < JustFinishedFor) list.Add(Snap(s, now));
            }
            return list;
        }

        Snapshot Snap(Session s, DateTime now)
        {
            int done = 0;
            foreach (var i in s.Items) if (i.Status == "completed") done++;
            return new Snapshot
            {
                Id = s.Id,
                Project = s.Project,
                Cwd = s.Cwd,
                Busy = s.Busy,
                JustFinished = !s.Busy && now - s.FinishedAt < JustFinishedFor,
                Elapsed = s.Busy ? now - s.TurnStart : s.LastDuration,
                TurnStart = s.TurnStart,
                LastEvent = s.LastEvent,
                Done = done,
                Total = s.Items.Count,
            };
        }

        public static string Clock(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        // ---- internals -----------------------------------------------------

        static void Finish(Session s, DateTime at)
        {
            if (s.Busy) { s.LastDuration = at - s.TurnStart; s.FinishedAt = at; }
            s.Busy = false;
        }

        static void ApplyTool(Session s, string tool, Dictionary<string, object> input, object response)
        {
            if (tool == "mcp__ccd_turn__wrap_up")
            {
                foreach (var i in s.Items) i.Status = "completed"; // wrap_up checks off the list
                return;
            }
            if (input == null) return;

            if (tool == "TodoWrite") { ReplaceItems(s, input, "todos"); return; }
            if (tool == "TaskCreate") { AddTask(s, input, response); return; }
            if (tool == "TaskUpdate") { UpdateTask(s, input); return; }
            if (tool != "mcp__ccd_turn__update_plan") return;

            if (input.ContainsKey("items")) ReplaceItems(s, input, "items");

            object adv;
            if (input.TryGetValue("advance", out adv) && adv is bool && (bool)adv)
            {
                int cur = s.Items.FindIndex(i => i.Status == "in_progress");
                if (cur >= 0) s.Items[cur].Status = "completed";
                int next = s.Items.FindIndex(Math.Max(cur + 1, 0), i => i.Status == "pending");
                if (next < 0) next = s.Items.FindIndex(i => i.Status == "pending");
                if (next >= 0) s.Items[next].Status = "in_progress";
            }

            object done;
            if (input.TryGetValue("done", out done) && done is IEnumerable && !(done is string))
                foreach (object o in (IEnumerable)done)
                {
                    int n = ToInt(o) - 1;
                    if (n >= 0 && n < s.Items.Count) s.Items[n].Status = "completed";
                }

            object start;
            if (input.TryGetValue("start", out start))
            {
                int n = ToInt(start) - 1;
                if (n >= 0 && n < s.Items.Count) s.Items[n].Status = "in_progress";
            }
        }

        static void ReplaceItems(Session s, Dictionary<string, object> input, string key)
        {
            var list = input[key] as IEnumerable;
            if (list == null || list is string) return;
            var items = new List<Item>();
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                items.Add(new Item { Content = Str(d, "content"), Status = Str(d, "status") });
            }
            s.Items = items;
        }

        // Newer Claude Code CLIs track tasks one at a time instead of with TodoWrite. The new
        // task's id comes back in the tool's response; ids count up from 1, so if it can't be
        // read, the next number is a safe guess.
        static void AddTask(Session s, Dictionary<string, object> input, object response)
        {
            string id = TaskId(response);
            if (id == null)
            {
                int max = 0;
                foreach (var i in s.Items) max = Math.Max(max, ToInt(i.Id));
                id = (max + 1).ToString(CultureInfo.InvariantCulture);
            }
            s.Items.RemoveAll(i => i.Id == id);
            s.Items.Add(new Item { Id = id, Content = Str(input, "subject"), Status = "pending" });
        }

        static void UpdateTask(Session s, Dictionary<string, object> input)
        {
            string id = Str(input, "taskId");
            var item = s.Items.Find(i => i.Id == id);
            if (item == null) return;
            string status = Str(input, "status");
            if (status == "deleted") s.Items.Remove(item);
            else if (status == "pending" || status == "in_progress" || status == "completed") item.Status = status;
            if (input.ContainsKey("subject")) item.Content = Str(input, "subject");
        }

        static string TaskId(object response)
        {
            var d = response as Dictionary<string, object>;
            if (d != null)
            {
                var task = d.ContainsKey("task") ? d["task"] as Dictionary<string, object> : null;
                foreach (var source in new[] { task, d })
                    foreach (var key in new[] { "id", "taskId" })
                        if (source != null && source.ContainsKey(key) && source[key] != null) return source[key].ToString();
            }
            var text = response as string;
            if (text != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, @"#(\d+)");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        static int ToInt(object o)
        {
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }
    }
}
