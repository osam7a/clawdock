using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Wrenlet
{
    // Reads the live session files Claude Code keeps in ~/.claude/sessions/<pid>.json, one
    // per running session (desktop app and terminal alike). Each says which process owns it,
    // the session's name and folder, and whether it is busy right now.
    //
    // A crashed session can leave its file behind, so a file only counts while its process
    // is still alive and started when the file says it did (the pid may have been reused).
    class SessionScanner
    {
        public class Entry
        {
            public string Id, Name, Cwd, Status;
            public int Pid;
            public bool Desktop;
            public DateTime StatusSince, Updated;
            public IntPtr Host; // terminal window running a CLI session (IntPtr.Zero if unknown)
        }

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        volatile List<Entry> latest = new List<Entry>();
        readonly Dictionary<int, IntPtr> hosts = new Dictionary<int, IntPtr>();

        public List<Entry> Latest { get { return latest; } }

        public void Start()
        {
            new Thread(() =>
            {
                while (true)
                {
                    try { latest = Scan(); } catch (Exception e) { Log.Write("Session scan: " + e.Message); }
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true, Name = "session scanner", Priority = ThreadPriority.BelowNormal }.Start();
        }

        List<Entry> Scan()
        {
            var list = new List<Entry>();
            if (!Directory.Exists(Dir)) return list;
            var alive = new HashSet<int>();

            foreach (string file in Directory.GetFiles(Dir, "*.json"))
            {
                Dictionary<string, object> d;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 256 * 1024) continue;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs)) d = Json.ParseObject(r.ReadToEnd());
                }
                catch { continue; } // being written right now; next round
                if (d == null) continue;

                int pid = (int)Json.Long(d, "pid");
                if (pid <= 0 || !IsAlive(pid, Json.Str(d, "procStart"))) continue;
                alive.Add(pid);

                var e = new Entry
                {
                    Id = Json.Str(d, "sessionId"),
                    Name = Json.Str(d, "name"),
                    Cwd = Json.Str(d, "cwd"),
                    Status = Json.Str(d, "status"),
                    Pid = pid,
                    Desktop = Json.Str(d, "entrypoint") == "claude-desktop",
                    StatusSince = FromMs(Json.Long(d, "statusUpdatedAt")),
                    Updated = FromMs(Math.Max(Json.Long(d, "updatedAt"), Json.Long(d, "statusUpdatedAt"))),
                };
                if (e.Id == "") continue;
                if (!e.Desktop) e.Host = HostFor(pid);
                list.Add(e);
            }

            foreach (int pid in new List<int>(hosts.Keys)) if (!alive.Contains(pid)) hosts.Remove(pid);
            return list;
        }

        IntPtr HostFor(int pid)
        {
            IntPtr h;
            if (hosts.TryGetValue(pid, out h) && (h == IntPtr.Zero || Native.IsWindow(h))) return h;
            h = TerminalFocus.FindHostWindow(pid);
            hosts[pid] = h;
            return h;
        }

        static bool IsAlive(int pid, string procStart)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    if (name != "claude" && name != "node" && name != "bun") return false;
                    long expected;
                    if (long.TryParse(procStart, out expected) && expected > 0)
                    {
                        long actual = p.StartTime.ToFileTimeUtc();
                        if (Math.Abs(actual - expected) > TimeSpan.TicksPerSecond * 5) return false;
                    }
                    return !p.HasExited;
                }
            }
            catch { return false; } // gone, or not ours to look at
        }

        static DateTime FromMs(long ms)
        {
            return ms <= 0 ? DateTime.MinValue : Epoch.AddMilliseconds(ms).ToLocalTime();
        }
    }
}
