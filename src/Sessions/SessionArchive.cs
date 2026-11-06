using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Clawdock
{
    // Quick voice's memory of every Claude Code session on this PC, running or finished: a short
    // digest of each transcript and one index of them, in the voice folder, for it to read and
    // search. The first run reads everything in the background. After that only transcripts
    // that changed are read again.
    static class SessionArchive
    {
        public const string IndexFile = "sessions-past.md";
        public const string DigestFolder = "past";
        const int DigestEvents = 200;
        const int SaveIndexEvery = 20;

        static int running;

        class Known { public string Title; public bool Archived; }

        class Row { public string Title, Project, File, First, Last, Active; public bool Archived; }

        public static void RefreshSoon(string folder)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            new Thread(() =>
            {
                try { Refresh(folder); }
                catch (Exception e) { Log.Write("Session archive: " + e.Message); }
                finally { Interlocked.Exchange(ref running, 0); }
            }) { IsBackground = true, Name = "session archive", Priority = ThreadPriority.BelowNormal }.Start();
        }

        static void Refresh(string folder)
        {
            string digests = Path.Combine(folder, DigestFolder);
            Directory.CreateDirectory(digests);
            // Digests from before keys were hidden are written again.
            string scrubbed = Path.Combine(digests, ".scrubbed-1");
            if (!File.Exists(scrubbed))
            {
                foreach (string f in Directory.GetFiles(digests, "*.md")) File.Delete(f);
                File.WriteAllText(scrubbed, "");
            }
            var known = ReadAppSessions();
            var files = Transcripts();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Row>();
            int done = 0;
            foreach (var t in files)
            {
                string id = Path.GetFileNameWithoutExtension(t.FullName);
                string name = (id.Length > 8 ? id.Substring(0, 8) : id) + ".md";
                string path = Path.Combine(digests, name);
                keep.Add(name);
                try
                {
                    if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < t.LastWriteTimeUtc)
                        File.WriteAllText(path, DigestText(t.FullName, t.LastWriteTime), new UTF8Encoding(false));
                    var row = ReadRow(path, name);
                    Known k;
                    if (known.TryGetValue(id, out k))
                    {
                        if (!string.IsNullOrEmpty(k.Title)) row.Title = k.Title;
                        row.Archived = k.Archived;
                    }
                    if (row.First != "") rows.Add(row);
                }
                catch (Exception e) { Log.Write("Session archive: " + e.Message); }
                if (++done % SaveIndexEvery == 0 && done < files.Count) WriteIndex(folder, rows, done, files.Count);
            }
            WriteIndex(folder, rows, done, done);
            foreach (string f in Directory.GetFiles(digests, "*.md"))
                if (!keep.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { }
        }

        // The sessions' transcripts, newest first, without quick voice's own (it knows those) or subagents'.
        static List<FileInfo> Transcripts()
        {
            var list = new List<FileInfo>();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (dir.EndsWith("-Clawdock-voice", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("-ClaudeNotch-voice", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("-Wrenlet-voice", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string f in Directory.GetFiles(dir, "*.jsonl")) list.Add(new FileInfo(f));
            }
            list.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return list;
        }

        // The titles the Claude app shows in its sidebar, by the transcripts they belong to.
        static Dictionary<string, Known> ReadAppSessions()
        {
            var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude") };
            string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
            if (Directory.Exists(store)) roots.AddRange(Directory.GetDirectories(store));
            foreach (string root in roots)
            {
                string dir = Path.Combine(root, "claude-code-sessions");
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var d = Json.ParseObject(File.ReadAllText(file));
                        if (d == null) continue;
                        var k = new Known { Title = Json.Str(d, "title"), Archived = Json.Str(d, "isArchived") == "True" };
                        string id = Json.Str(d, "cliSessionId");
                        if (id != "") known[id] = k;
                        object prior;
                        if (d.TryGetValue("priorCliSessionIds", out prior) && prior is System.Collections.IEnumerable && !(prior is string))
                            foreach (object o in (System.Collections.IEnumerable)prior)
                                if (o is string) known[(string)o] = k;
                    }
                    catch { } // being written right now; next time
                }
            }
            return known;
        }

        static string DigestText(string transcript, DateTime active)
        {
            var d = SessionBrief.ReadWhole(transcript, DigestEvents);
            var t = new StringBuilder();
            t.Append("Title: " + d.Title + "\n");
            t.Append("Project: " + SessionBrief.Project(d.Cwd) + " (" + d.Cwd + ")\n");
            t.Append("First asked: " + SessionBrief.Short(d.FirstAsked, 160) + "\n");
            t.Append("Last asked: " + SessionBrief.Short(d.Asked, 160) + "\n");
            // Its last message, not the file's date: closing a session touches the file too.
            var last = d.Events.Count > 0 ? d.Events[d.Events.Count - 1].At : DateTime.MinValue;
            if (last != DateTime.MinValue) active = last.ToLocalTime();
            t.Append("Last active: " + active.ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture) + "\n");
            if (d.Model != "") t.Append("Model: " + d.Model + "\n");
            if (d.Tasks.Count > 0)
            {
                t.Append("Task list when it was last active:\n");
                foreach (var i in d.Tasks)
                    t.Append("  - " + (i.Value == "completed" ? "[done] " : i.Value == "in_progress" ? "[now] " : "[next] ") + i.Key + "\n");
            }
            t.Append("Its last words: " + SessionBrief.Short(d.Said, 800) + "\n\n");
            t.Append("# History, oldest first\n\n");
            SessionBrief.AppendEvents(t, d);
            return t.ToString();
        }

        static Row ReadRow(string path, string name)
        {
            var row = new Row { File = DigestFolder + "/" + name, Active = "", Title = "", Project = "", First = "", Last = "" };
            using (var r = new StreamReader(path, Encoding.UTF8))
                for (int i = 0; i < 5; i++)
                {
                    string line = r.ReadLine();
                    if (line == null) break;
                    if (line.StartsWith("Title: ")) row.Title = line.Substring(7);
                    else if (line.StartsWith("Project: ")) row.Project = line.Substring(9);
                    else if (line.StartsWith("First asked: ")) row.First = line.Substring(13);
                    else if (line.StartsWith("Last asked: ")) row.Last = line.Substring(12);
                    else if (line.StartsWith("Last active: ")) row.Active = line.Substring(13);
                }
            return row;
        }

        static void WriteIndex(string folder, List<Row> rows, int done, int total)
        {
            var t = new StringBuilder("# Every Claude Code session on this PC, most recent first\n\n");
            if (done < total) t.Append("Still being read: " + done + " of " + total + " so far, so this list isn't complete yet.\n\n");
            foreach (var r in rows)
            {
                t.Append("- " + (r.Title != "" ? r.Title : "Untitled") + " | " + r.Project + " | last active " + r.Active
                    + (r.Archived ? " | archived" : "") + " | " + r.File + "\n  First asked: " + r.First + "\n");
                if (r.Last != r.First) t.Append("  Last asked: " + r.Last + "\n");
            }
            File.WriteAllText(Path.Combine(folder, IndexFile), t.ToString(), new UTF8Encoding(false));
        }
    }
}
