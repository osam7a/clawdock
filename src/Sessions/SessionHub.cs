using System;
using System.Collections.Generic;
using System.IO;

namespace Clawdock
{
    // One running Claude Code session as the notch shows it.
    class SessionInfo
    {
        public string Id, Name, Cwd;
        public bool Desktop, Busy, JustFinished;
        public int Pid, Done, Total;
        public DateTime Since, Updated;
        public TimeSpan Elapsed;
        public IntPtr Host;

        public string Project
        {
            get
            {
                if (string.IsNullOrEmpty(Cwd)) return "";
                try { return Path.GetFileName(Cwd.TrimEnd('\\', '/')); } catch { return ""; }
            }
        }

        public string DisplayName
        {
            get { return !string.IsNullOrEmpty(Name) ? Name : Project != "" ? Project : "Claude Code"; }
        }
    }

    // Merges the two sources into one list:
    //   - the live session files (every running session, desktop and terminal, even ones that
    //     started before the hooks were installed): who is running and whether it is busy
    //   - the hook events (SessionTracker): task counts and exact turn timing
    class SessionHub
    {
        public readonly SessionTracker Tracker = new SessionTracker();

        readonly Dictionary<string, DateTime> busySince = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> finishedAt = new Dictionary<string, DateTime>();
        readonly Dictionary<string, TimeSpan> lastDuration = new Dictionary<string, TimeSpan>();

        public TimeSpan FinishedFor
        {
            get { return Tracker.JustFinishedFor; }
            set { Tracker.JustFinishedFor = value; }
        }

        public List<SessionInfo> Build(List<SessionScanner.Entry> scanned, DateTime now)
        {
            var list = new List<SessionInfo>();
            var seen = new HashSet<string>();

            foreach (var e in scanned)
            {
                if (!seen.Add(e.Id)) continue;
                var hook = Tracker.For(e.Id, now);
                bool busy = e.Status == "busy";

                // Turn timing: the hooks know the exact start; otherwise the file's status time.
                DateTime since;
                if (busy)
                {
                    if (hook != null && hook.Busy) since = hook.TurnStart;
                    else if (!busySince.TryGetValue(e.Id, out since)) since = e.StatusSince > DateTime.MinValue ? e.StatusSince : now;
                    busySince[e.Id] = since;
                }
                else
                {
                    DateTime started;
                    if (busySince.TryGetValue(e.Id, out started))
                    {
                        finishedAt[e.Id] = now;
                        lastDuration[e.Id] = now - started;
                        busySince.Remove(e.Id);
                    }
                    since = DateTime.MinValue;
                }

                DateTime fin;
                TimeSpan dur;
                bool justFinished = !busy && finishedAt.TryGetValue(e.Id, out fin) && now - fin < FinishedFor;
                lastDuration.TryGetValue(e.Id, out dur);

                list.Add(new SessionInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    Cwd = e.Cwd != "" ? e.Cwd : hook != null ? hook.Cwd : "",
                    Desktop = e.Desktop,
                    Pid = e.Pid,
                    Host = e.Host,
                    Busy = busy,
                    JustFinished = justFinished,
                    Since = since,
                    Elapsed = busy ? now - since : dur,
                    Updated = e.Updated,
                    Done = hook != null ? hook.Done : 0,
                    Total = hook != null ? hook.Total : 0,
                });
            }

            // Sessions the hooks know about but that have no live file (older Claude Code).
            foreach (var h in Tracker.Recent(now))
            {
                if (seen.Contains(h.Id) || h.Id == "default") continue;
                list.Add(new SessionInfo
                {
                    Id = h.Id,
                    Cwd = h.Cwd,
                    Busy = h.Busy,
                    JustFinished = h.JustFinished,
                    Since = h.TurnStart,
                    Elapsed = h.Elapsed,
                    Updated = h.LastEvent,
                    Done = h.Done,
                    Total = h.Total,
                });
            }

            foreach (string id in new List<string>(finishedAt.Keys))
                if (now - finishedAt[id] > TimeSpan.FromHours(1)) { finishedAt.Remove(id); lastDuration.Remove(id); }

            // Working sessions first (longest running on top), then the most recently active.
            list.Sort((a, b) =>
            {
                if (a.Busy != b.Busy) return a.Busy ? -1 : 1;
                if (a.Busy) return a.Since.CompareTo(b.Since);
                if (a.JustFinished != b.JustFinished) return a.JustFinished ? -1 : 1;
                return b.Updated.CompareTo(a.Updated);
            });
            return list;
        }
    }
}
