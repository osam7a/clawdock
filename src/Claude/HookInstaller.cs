using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Clawdock
{
    // Keeps Claude Code's hooks in ~/.claude/settings.json pointing at the installed exe.
    //
    // Only handlers whose command runs "Clawdock.exe --hook" are ever touched: everything
    // else in the file (other hooks, other settings) is written back as it was read. The
    // first time the file is changed, a copy is kept next to it as
    // settings.json.clawdock-backup. If the file can't be parsed it is left alone.
    static class HookInstaller
    {
        public enum State { Installed, Missing, Outdated, Unreadable }

        static readonly string ClaudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        public static readonly string SettingsPath = Path.Combine(ClaudeDir, "settings.json");
        static readonly string BackupPath = SettingsPath + ".clawdock-backup";

        // event -> (matcher or null, timeout in seconds)
        static readonly object[][] Wanted =
        {
            new object[] { "UserPromptSubmit", null, 5 },
            new object[] { "Stop", null, 5 },
            new object[] { "SessionEnd", null, 5 },
            new object[] { "PostToolUse", "mcp__ccd_turn__update_plan|mcp__ccd_turn__wrap_up|TodoWrite|TaskCreate|TaskUpdate", 5 },
            new object[] { "Notification", "permission_prompt", 5 },
            // Waits while the notch shows Allow / Deny. The notch itself gives up well before this.
            new object[] { "PermissionRequest", "*", 600 },
        };

        public static string Command(string exePath)
        {
            return "\"" + exePath.Replace('\\', '/') + "\" --hook";
        }

        static bool IsOurs(object handler)
        {
            var h = handler as IDictionary<string, object>;
            if (h == null) return false;
            string cmd = Json.Str(h, "command");
            return (cmd.IndexOf("Clawdock.exe", StringComparison.OrdinalIgnoreCase) >= 0 || cmd.IndexOf("ClaudeNotch.exe", StringComparison.OrdinalIgnoreCase) >= 0 || cmd.IndexOf("Wrenlet.exe", StringComparison.OrdinalIgnoreCase) >= 0) // its earlier names
                && cmd.IndexOf("--hook", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static State Check(string exePath)
        {
            Dictionary<string, object> root;
            if (!TryRead(out root)) return State.Unreadable;
            var expected = Merge(Copy(root), exePath, true);
            if (Json.Write(expected) == Json.Write(root)) return State.Installed;
            return CountOurs(root) == 0 ? State.Missing : State.Outdated;
        }

        // Adds or repairs our handlers. Returns false (and logs why) if the file was left alone.
        public static bool Install(string exePath) { return Rewrite(exePath, true); }

        public static bool Uninstall() { return Rewrite(null, false); }

        // Claude Code writes this file too. Only replace it if it still holds what was read,
        // otherwise read it again and redo the change, so neither write is lost.
        static bool Rewrite(string exePath, bool add)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Dictionary<string, object> root;
                string raw;
                if (!TryRead(out root, out raw))
                {
                    Log.Write("Hooks not changed: " + SettingsPath + " could not be read as JSON.");
                    return false;
                }
                string before = Json.Write(root);
                var after = Merge(Copy(root), exePath, add);
                string text = Json.Write(after);
                if (text == before) return true;

                string tmp = SettingsPath + ".clawdock-tmp";
                try
                {
                    Directory.CreateDirectory(ClaudeDir);
                    if (File.Exists(SettingsPath) && !File.Exists(BackupPath)) File.Copy(SettingsPath, BackupPath);
                    File.WriteAllText(tmp, text);
                    if (CurrentText() != raw) { File.Delete(tmp); Thread.Sleep(200); continue; } // changed meanwhile
                    if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null);
                    else File.Move(tmp, SettingsPath);
                    return true;
                }
                catch (Exception e)
                {
                    try { File.Delete(tmp); } catch { }
                    Log.Write("Hooks not changed: " + e.Message);
                    return false;
                }
            }
            Log.Write("Hooks not changed: " + SettingsPath + " kept changing.");
            return false;
        }

        static string CurrentText()
        {
            return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        }

        static bool TryRead(out Dictionary<string, object> root)
        {
            string raw;
            return TryRead(out root, out raw);
        }

        static bool TryRead(out Dictionary<string, object> root, out string raw)
        {
            root = null;
            raw = null;
            try
            {
                raw = CurrentText();
                if (raw == null || raw.Trim().Length == 0) { root = new Dictionary<string, object>(); return true; }
                root = Json.ParseObject(raw);
                return root != null;
            }
            catch { return false; }
        }

        // Removes every handler of ours, drops groups and events that end up empty, then
        // (when adding) appends one canonical group per event.
        internal static Dictionary<string, object> Merge(Dictionary<string, object> root, string exePath, bool add)
        {
            var hooks = Json.Obj(root, "hooks");
            if (hooks == null)
            {
                if (!add) return root;
                hooks = new Dictionary<string, object>();
                root["hooks"] = hooks;
            }

            // Rebuilt into a new map rather than edited in place: removing and re-adding keys
            // in a Dictionary scrambles their order, which would rewrite the file for nothing.
            var rebuilt = new Dictionary<string, object>();
            foreach (var kv in hooks)
            {
                string ev = kv.Key;
                var groups = kv.Value as IList;
                if (groups == null) { rebuilt[ev] = kv.Value; continue; }
                var keptGroups = new List<object>();
                foreach (object g in groups)
                {
                    var group = g as Dictionary<string, object>;
                    var handlers = group != null && group.ContainsKey("hooks") ? group["hooks"] as IList : null;
                    if (handlers == null) { keptGroups.Add(g); continue; }
                    var kept = new List<object>();
                    foreach (object h in handlers) if (!IsOurs(h)) kept.Add(h);
                    if (kept.Count == handlers.Count) { keptGroups.Add(g); continue; }
                    if (kept.Count == 0) continue;
                    group["hooks"] = kept;
                    keptGroups.Add(group);
                }
                if (keptGroups.Count > 0) rebuilt[ev] = keptGroups;
            }
            hooks = rebuilt;
            root["hooks"] = hooks;

            if (add)
            {
                foreach (var w in Wanted)
                {
                    string ev = (string)w[0];
                    var group = new Dictionary<string, object>();
                    if (w[1] != null) group["matcher"] = w[1];
                    group["hooks"] = new List<object>
                    {
                        new Dictionary<string, object> { { "type", "command" }, { "command", Command(exePath) }, { "timeout", w[2] } },
                    };
                    var groups = hooks.ContainsKey(ev) ? hooks[ev] as IList : null;
                    var list = groups != null ? new List<object>(Enumerate(groups)) : new List<object>();
                    list.Add(group);
                    hooks[ev] = list;
                }
            }
            else if (hooks.Count == 0) root.Remove("hooks");
            return root;
        }

        internal static int CountOurs(Dictionary<string, object> root)
        {
            int n = 0;
            var hooks = Json.Obj(root, "hooks");
            if (hooks == null) return 0;
            foreach (var groups in hooks.Values)
            {
                var gl = groups as IList;
                if (gl == null) continue;
                foreach (object g in gl)
                {
                    var group = g as Dictionary<string, object>;
                    var handlers = group != null && group.ContainsKey("hooks") ? group["hooks"] as IList : null;
                    if (handlers != null) foreach (object h in handlers) if (IsOurs(h)) n++;
                }
            }
            return n;
        }

        static IEnumerable<object> Enumerate(IList list) { foreach (object o in list) yield return o; }

        // Deep copy through JSON so Merge can't change the original we compare against.
        static Dictionary<string, object> Copy(Dictionary<string, object> d)
        {
            return Json.ParseObject(Json.Write(d));
        }
    }
}
