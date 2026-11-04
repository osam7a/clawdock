using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClaudeNotch
{
    // What a running session is doing right now, for the mascot: the tail of its transcript says
    // whether Claude is in the middle of a tool or thinking about what to do next. Read at most
    // every couple of seconds, and only for the session on the notch.
    class SessionActivity
    {
        static readonly TimeSpan Every = TimeSpan.FromSeconds(1.5);
        const int TailBytes = 64 * 1024;

        string lastId;
        DateTime lastRead;
        MascotMood mood = MascotMood.Working;

        public MascotMood For(string sessionId, string cwd)
        {
            var now = DateTime.Now;
            if (sessionId == lastId && now - lastRead < Every) return mood;
            lastId = sessionId;
            lastRead = now;
            try { mood = Read(Transcript(sessionId, cwd)); }
            catch { mood = MascotMood.Working; }
            return mood;
        }

        // Claude Code keeps it under the project's folder, with every other character as a dash.
        public static string Transcript(string sessionId, string cwd)
        {
            var dir = new StringBuilder();
            foreach (char c in cwd) dir.Append(char.IsLetterOrDigit(c) ? c : '-');
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", "projects", dir.ToString(), sessionId + ".jsonl");
        }

        static MascotMood Read(string file)
        {
            if (!File.Exists(file)) return MascotMood.Working;
            string[] lines;
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, f.Length - TailBytes);
                f.Seek(start, SeekOrigin.Begin);
                var buf = new byte[f.Length - start];
                int got = f.Read(buf, 0, buf.Length);
                lines = Encoding.UTF8.GetString(buf, 0, got).Split('\n');
            }
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                Dictionary<string, object> d;
                try { d = Json.ParseObject(lines[i]); } catch { continue; } // the first one is cut off
                if (d == null) continue;
                string type = Json.Str(d, "type");
                if (type == "user") return MascotMood.Thinking; // a result came back, or you said something
                if (type != "assistant") continue;
                var message = Json.Obj(d, "message");
                var content = message != null && message.ContainsKey("content") ? message["content"] as System.Collections.IEnumerable : null;
                string tool = null;
                if (content != null && !(content is string))
                    foreach (object o in content)
                    {
                        var block = o as Dictionary<string, object>;
                        if (block != null && Json.Str(block, "type") == "tool_use") tool = Json.Str(block, "name");
                    }
                return tool == null ? MascotMood.Thinking : ForTool(tool);
            }
            return MascotMood.Working;
        }

        public static MascotMood ForTool(string tool)
        {
            switch (tool)
            {
                case "WebSearch": case "WebFetch": case "web_search": case "web_fetch": case "ToolSearch":
                    return MascotMood.Searching;
                case "Read": case "Glob": case "Grep": case "LS": case "Skill":
                    return MascotMood.Reading;
            }
            return MascotMood.Working;
        }
    }
}
