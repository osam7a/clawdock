using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ClaudeNotch
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
        static readonly TimeSpan JustFinishedFor = TimeSpan.FromSeconds(12);

        class Item { public string Content, Status; }

        class Session
        {
            public string Id, Project;
            public bool Busy;
            public DateTime TurnStart, LastEvent, FinishedAt;
            public TimeSpan LastDuration;
            public List<Item> Items = new List<Item>();
        }

        public class Snapshot
        {
            public string Project;
            public bool Busy, JustFinished;
            public TimeSpan Elapsed;
            public int Done, Total;
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
                try { s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/')); } catch { }
            }
            s.LastEvent = now;

            switch (ev)
            {
                case "UserPromptSubmit":
                    s.Busy = true;
                    s.TurnStart = now;
                    s.Items.Clear();
                    break;

                case "Stop":
                    Finish(s, now);
                    break;

                case "PostToolUse":
                    // The notch may have started mid-turn; treat the first sign of life as the start.
                    if (isNew) { s.Busy = true; s.TurnStart = now; }
                    ApplyTool(s, Str(d, "tool_name"), d.ContainsKey("tool_input") ? d["tool_input"] as Dictionary<string, object> : null);
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
            if (pick == null) return null;

            int done = 0;
            foreach (var i in pick.Items) if (i.Status == "completed") done++;
            return new Snapshot
            {
                Project = pick.Project,
                Busy = pick.Busy,
                JustFinished = !pick.Busy && now - pick.FinishedAt < JustFinishedFor,
                Elapsed = pick.Busy ? now - pick.TurnStart : pick.LastDuration,
                Done = done,
                Total = pick.Items.Count,
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

        static void ApplyTool(Session s, string tool, Dictionary<string, object> input)
        {
            if (tool == "mcp__ccd_turn__wrap_up")
            {
                foreach (var i in s.Items) i.Status = "completed"; // wrap_up checks off the list
                return;
            }
            if (input == null) return;

            if (tool == "TodoWrite") { ReplaceItems(s, input, "todos"); return; }
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
