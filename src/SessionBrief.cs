using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeNotch
{
    // What quick voice knows about your other sessions: a snapshot written just before each
    // question, which it reads when you ask about them. Everything comes from files Claude Code
    // already keeps (the transcripts, shared for reading), so the sessions never notice.
    static class SessionBrief
    {
        public class Entry
        {
            public string Id, Name, Cwd, Waiting;
            public bool Desktop, Busy;
            public TimeSpan Elapsed;
            public DateTime Updated;
        }

        public const string FileName = "sessions-now.md";
        const string DetailPrefix = "session-";
        const int TailBytes = 2 * 1024 * 1024;
        const int LongestLine = 300 * 1024; // saved API requests: huge, and not part of the story
        const int DetailEvents = 40;

        public class Event { public DateTime At; public string Who, Text; }

        public class Digest
        {
            public string Model = "", Asked = "", FirstAsked = "", Said = "", Now = "", Title = "", Cwd = "";
            public List<string> Subagents = new List<string>();
            public List<KeyValuePair<string, string>> Tasks = new List<KeyValuePair<string, string>>();
            public List<Event> Events = new List<Event>();
            public bool Found;
        }

        public static void Write(List<Entry> sessions, string folder)
        {
            var text = new StringBuilder();
            text.Append("# Claude sessions as of " + DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture) + "\n\n");
            if (sessions.Count == 0) text.Append("No other sessions are running.\n");
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { };
            foreach (var s in sessions)
            {
                var d = Read(SessionActivity.Transcript(s.Id, s.Cwd));
                string detail = DetailFile(s.Id);
                keep.Add(detail);
                Describe(text, s, d, detail);
                File.WriteAllText(Path.Combine(folder, detail), DetailText(s, d), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(folder, FileName), text.ToString(), new UTF8Encoding(false));
            foreach (string f in Directory.GetFiles(folder, DetailPrefix + "*.md"))
                if (!keep.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { }
        }

        static string DetailFile(string id)
        {
            return DetailPrefix + (id.Length > 8 ? id.Substring(0, 8) : id) + ".md";
        }

        static void Describe(StringBuilder t, Entry s, Digest d, string detail)
        {
            t.Append("## " + Title(s) + "\n");
            t.Append("- Session id: " + s.Id + "\n");
            t.Append("- Runs in: " + (s.Desktop ? "the Claude app" : "a terminal") + "\n");
            t.Append("- Project: " + Project(s.Cwd) + " (" + s.Cwd + ")\n");
            t.Append("- State: " + State(s) + "\n");
            if (!d.Found) { t.Append("- Its transcript can't be read, so that's all that's known.\n\n"); return; }
            if (d.Model != "") t.Append("- Model: " + d.Model + "\n");
            if (d.Asked != "") t.Append("- Last asked: " + Short(d.Asked, 300) + "\n");
            if (s.Busy && d.Now != "") t.Append("- Doing now: " + d.Now + "\n");
            foreach (string a in d.Subagents) t.Append("- Subagent running: " + a + "\n");
            if (d.Tasks.Count > 0)
            {
                int done = 0;
                foreach (var i in d.Tasks) if (i.Value == "completed") done++;
                t.Append("- Task list, " + done + " of " + d.Tasks.Count + " done:\n");
                foreach (var i in d.Tasks)
                    t.Append("  - " + (i.Value == "completed" ? "[done] " : i.Value == "in_progress" ? "[now] " : "[next] ") + i.Key + "\n");
            }
            else t.Append("- Task list: none\n");
            if (d.Said != "") t.Append("- Latest from Claude: " + Short(d.Said, 400) + "\n");
            t.Append("- More history: " + detail + "\n\n");
        }

        static string DetailText(Entry s, Digest d)
        {
            var t = new StringBuilder("# Recent history of " + Title(s) + "\n\n");
            AppendEvents(t, d);
            return t.ToString();
        }

        public static void AppendEvents(StringBuilder t, Digest d)
        {
            foreach (var e in d.Events)
                t.Append((e.At == DateTime.MinValue ? "" : e.At.ToLocalTime().ToString("d MMM h:mm tt ", CultureInfo.InvariantCulture)) + e.Who + ": " + e.Text + "\n");
        }

        static string Title(Entry s)
        {
            return !string.IsNullOrEmpty(s.Name) ? s.Name : Project(s.Cwd) != "" ? Project(s.Cwd) : "Unnamed session";
        }

        // A worktree's project is the repository it belongs to, not the worktree's own name.
        public static string Project(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            string path = cwd.Replace('/', '\\').TrimEnd('\\');
            int tree = path.IndexOf("\\.claude\\worktrees\\", StringComparison.OrdinalIgnoreCase);
            if (tree > 0) path = path.Substring(0, tree);
            try { return Path.GetFileName(path); } catch { return ""; }
        }

        static string State(Entry s)
        {
            if (!string.IsNullOrEmpty(s.Waiting)) return "waiting for you: " + s.Waiting;
            if (s.Busy) return "working for " + SessionTracker.Clock(s.Elapsed);
            var idle = DateTime.Now - s.Updated;
            if (s.Updated == DateTime.MinValue || idle < TimeSpan.Zero) return "idle";
            return "idle, last active " + (idle.TotalMinutes < 1 ? "just now" : idle.TotalHours < 1 ? (int)idle.TotalMinutes + " minutes ago" : (int)idle.TotalHours + " hours ago");
        }

        public static string Short(string s, int most)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length <= most ? s : s.Substring(0, most) + "…";
        }

        // ---- reading a transcript ------------------------------------------

        static string[] Tail(string file)
        {
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, f.Length - TailBytes);
                f.Seek(start, SeekOrigin.Begin);
                var buf = new byte[f.Length - start];
                int got = f.Read(buf, 0, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, got).Split('\n');
            }
        }

        static Digest Read(string file)
        {
            var d = new Digest();
            string[] lines;
            try
            {
                if (!File.Exists(file)) return d;
                lines = Tail(file);
            }
            catch (Exception e) { Log.Write("Session brief: " + e.Message); return d; }
            d.Found = true;
            Parse(d, lines, DetailEvents, true);
            return d;
        }

        // All of a transcript, line by line, for the archive. What Claude said and was asked, not its tools.
        public static Digest ReadWhole(string file, int events)
        {
            var d = new Digest { Found = true };
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f, Encoding.UTF8))
                Parse(d, LinesOf(r), events, false);
            return d;
        }

        static IEnumerable<string> LinesOf(StreamReader r)
        {
            string line;
            while ((line = r.ReadLine()) != null) yield return line;
        }

        static void Parse(Digest d, IEnumerable<string> lines, int events, bool withTools)
        {
            var tasks = new SessionTracker.TaskReplay();
            var open = new Dictionary<string, string>();   // tools still running: id -> what it's doing
            var agents = new Dictionary<string, string>(); // the subagents among them
            foreach (string line in lines)
            {
                if (line.Length > LongestLine || line.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) >= 0) continue;
                if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"custom-title\"", StringComparison.Ordinal) < 0) continue;
                Dictionary<string, object> row;
                try { row = Json.ParseObject(line); } catch { continue; } // the first one is cut off
                if (row == null) continue;
                string type = Json.Str(row, "type");
                if (type == "custom-title") { d.Title = Json.Str(row, "customTitle"); continue; }
                if (type != "user" && type != "assistant") continue;
                if (d.Cwd == "") d.Cwd = Json.Str(row, "cwd");
                var message = Json.Obj(row, "message");
                if (message == null) continue;
                DateTime at;
                if (!DateTime.TryParse(Json.Str(row, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) at = DateTime.MinValue;
                if (type == "user") ReadUser(d, message, at, tasks, open, agents, Json.Str(row, "isMeta") == "True");
                else ReadAssistant(d, message, at, tasks, open, agents, withTools);
            }
            d.Tasks = tasks.Items();
            d.Subagents.AddRange(agents.Values);
            foreach (string doing in open.Values) d.Now = "running " + doing; // the latest one
            if (d.Now == "") d.Now = "thinking";
            if (d.Events.Count > events) d.Events.RemoveRange(0, d.Events.Count - events);
        }

        static readonly Regex Injected = new Regex(@"<(system-reminder|command-[a-z]+|local-command-[a-z]+|ide_[a-z_]+)[\s>][\s\S]*?</\1>", RegexOptions.Compiled);

        static void ReadUser(Digest d, Dictionary<string, object> message, DateTime at, SessionTracker.TaskReplay tasks,
            Dictionary<string, string> open, Dictionary<string, string> agents, bool meta)
        {
            var said = new StringBuilder();
            object content;
            message.TryGetValue("content", out content);
            if (content is string) said.Append((string)content);
            else if (content is IEnumerable)
                foreach (object o in (IEnumerable)content)
                {
                    var block = o as Dictionary<string, object>;
                    if (block == null) continue;
                    string kind = Json.Str(block, "type");
                    if (kind == "text") said.Append(Json.Str(block, "text")).Append(' ');
                    else if (kind == "tool_result")
                    {
                        string id = Json.Str(block, "tool_use_id");
                        open.Remove(id);
                        agents.Remove(id);
                    }
                }
            if (meta) return;
            string text = Injected.Replace(said.ToString(), "").Trim();
            if (text == "") return;
            if (text.StartsWith("This session is being continued from a previous conversation", StringComparison.Ordinal)) return; // the app resuming, not you
            d.Asked = text;
            if (d.FirstAsked == "") d.FirstAsked = text;
            tasks.NewPrompt();
            d.Events.Add(new Event { At = at, Who = "You", Text = Short(text, 2000) });
        }

        static void ReadAssistant(Digest d, Dictionary<string, object> message, DateTime at, SessionTracker.TaskReplay tasks,
            Dictionary<string, string> open, Dictionary<string, string> agents, bool withTools)
        {
            string model = Json.Str(message, "model");
            if (model != "" && !model.StartsWith("<")) d.Model = model;
            object content;
            message.TryGetValue("content", out content);
            if (!(content is IEnumerable) || content is string) return;
            foreach (object o in (IEnumerable)content)
            {
                var block = o as Dictionary<string, object>;
                if (block == null) continue;
                string kind = Json.Str(block, "type");
                if (kind == "text")
                {
                    string text = Json.Str(block, "text").Trim();
                    if (text == "") continue;
                    d.Said = text;
                    d.Events.Add(new Event { At = at, Who = "Claude", Text = Short(text, 1500) });
                }
                else if (kind == "tool_use")
                {
                    string name = Json.Str(block, "name");
                    var input = Json.Obj(block, "input");
                    tasks.Tool(name, input);
                    if (name == "mcp__ccd_turn__update_plan" || name == "mcp__ccd_turn__wrap_up" || name == "TodoWrite") continue;
                    string doing = DescribeTool(name, input);
                    string id = Json.Str(block, "id");
                    open[id] = doing;
                    if (name == "Agent" || name == "Task") agents[id] = doing;
                    if (withTools) d.Events.Add(new Event { At = at, Who = "Tool", Text = doing });
                }
            }
        }

        // Its own description where it wrote one: shorter than a command, and less likely to hold a secret.
        static string DescribeTool(string name, Dictionary<string, object> input)
        {
            string shortName = name.StartsWith("mcp__") ? name.Substring(name.LastIndexOf("__", StringComparison.Ordinal) + 2) : name;
            if (input == null) return shortName;
            foreach (string key in new[] { "description", "file_path", "pattern", "query", "url" })
            {
                string v = Json.Str(input, key);
                if (v == "") continue;
                if (key == "file_path") try { v = Path.GetFileName(v); } catch { }
                return shortName + " (" + Short(v, 120) + ")";
            }
            return shortName;
        }
    }
}
