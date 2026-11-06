using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace Clawdock
{
    // Starts a new project: a folder of its own and a Claude Code session in it, which then opens
    // in the Claude app. Only ever after you press Allow on the notch.
    static class ProjectStarter
    {
        const string SetupMessage = "The user just started this project from Clawdock, and their first request comes next. Reply with just: Ready.";
        const int LongestName = 60;
        static readonly string[] ModeNames = { "Manual", "Accept edits", "Plan", "Bypass permissions" };
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        const string HaikuModel = "claude-haiku-5-5"; // the "haiku" alias can mean an older one, too old for auto mode
        public static readonly string[] Models = { "opus", "sonnet", "haiku", "fable" };
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };

        public static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Claude Projects"); }
        }

        // The name as a single folder name, or "" if nothing usable is left. Never a path.
        public static string CleanName(string name)
        {
            var t = new StringBuilder();
            var bad = Path.GetInvalidFileNameChars();
            foreach (char c in name ?? "")
                if (Array.IndexOf(bad, c) < 0 && !char.IsControl(c)) t.Append(c);
            string s = t.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length > LongestName) s = s.Substring(0, LongestName).TrimEnd('.', ' ');
            if (s.Trim('.', ' ') == "" || Reserved.Contains(s.Split('.')[0].TrimEnd())) return "";
            return s;
        }

        // Never move into a folder that already has something in it.
        public static bool IsFree(string folder)
        {
            if (File.Exists(folder)) return false;
            if (!Directory.Exists(folder)) return true;
            return Directory.GetFileSystemEntries(folder).Length == 0;
        }

        // Makes the folder and a session in it. Its setup turn has no tools, and uses the model
        // given (or your default), which the session keeps. Returns the session's id, or null. Blocks for seconds.
        public static string Create(string folder, string name, string model)
        {
            try
            {
                string exe = QuickVoice.FindClaude();
                if (exe == null) { Log.Write("New project: Claude Code wasn't found"); return null; }
                if (!IsFree(folder)) { Log.Write("New project: the folder isn't empty"); return null; }
                Directory.CreateDirectory(folder);
                string made = RunSetup(exe, folder, name, model == "haiku" ? HaikuModel : model);
                // That model name won't last forever, and the alias may still work when it's gone.
                return made == null && model == "haiku" ? RunSetup(exe, folder, name, model) : made;
            }
            catch (Exception e) { Log.Write("New project: " + e.Message); return null; }
        }

        static string RunSetup(string exe, string folder, string name, string model)
        {
            try
            {
                string id = Guid.NewGuid().ToString();
                var args = new List<string> { "-p", "--session-id", id, "-n", name, "--tools", "", "--strict-mcp-config" };
                if (model != "") args.AddRange(new[] { "--model", model });
                var psi = new ProcessStartInfo(exe, QuickVoice.JoinArgs(args))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Write("New project: " + e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var bytes = new UTF8Encoding(false).GetBytes(SetupMessage);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return null; }
                    return p.ExitCode == 0 ? id : null;
                }
            }
            catch (Exception e) { Log.Write("New project: " + e.Message); return null; }
        }

        // Sets the open chat's permission mode to Auto with the app's own selector, the button after
        // the model button. A session opened from a link starts in the app's default mode. Call only
        // once that chat is confirmed open: this acts on whichever chat is showing.
        public static bool SetAutoMode()
        {
            try
            {
                IntPtr hwnd = Native.FindClaudeWindow();
                if (hwnd == IntPtr.Zero) return false;
                var win = AutomationElement.FromHandle(hwnd);
                var clock = Stopwatch.StartNew();
                AutomationElement selector = null;
                do
                {
                    selector = FindModeSelector(win);
                    if (selector != null) break;
                    Thread.Sleep(200);
                } while (clock.ElapsedMilliseconds < 6000);
                if (selector == null) { Log.Write("New project: the mode selector wasn't found"); return false; }
                if (selector.Current.Name == "Auto") return true;

                object expand;
                if (!selector.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand)) return false;
                ((ExpandCollapsePattern)expand).Expand();
                var radios = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton);
                clock.Restart();
                do
                {
                    foreach (AutomationElement item in win.FindAll(TreeScope.Descendants, radios))
                    {
                        if (!(item.Current.Name ?? "").StartsWith("Auto ", StringComparison.Ordinal)) continue;
                        object invoke;
                        if (!item.TryGetCurrentPattern(InvokePattern.Pattern, out invoke)) break;
                        ((InvokePattern)invoke).Invoke();
                        Thread.Sleep(300);
                        var after = FindModeSelector(win);
                        return after != null && after.Current.Name == "Auto";
                    }
                    Thread.Sleep(150);
                } while (clock.ElapsedMilliseconds < 2000);
                ((ExpandCollapsePattern)expand).Collapse();
                Log.Write("New project: Auto isn't offered in this chat");
                return false;
            }
            catch (Exception e) { Log.Write("New project: " + e.Message); return false; }
        }

        static AutomationElement FindModeSelector(AutomationElement win)
        {
            var buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            bool afterModel = false;
            foreach (AutomationElement b in win.FindAll(TreeScope.Descendants, buttons))
            {
                string name = b.Current.Name ?? "";
                if (afterModel) return name == "Auto" || Array.IndexOf(ModeNames, name) >= 0 ? b : null;
                if (name.StartsWith("Model:", StringComparison.Ordinal) && !b.Current.IsOffscreen) afterModel = true;
            }
            return null;
        }
    }
}
