using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace ClaudeNotch
{
    // Sends a typed prompt to Claude, either:
    //   desktop  into the Claude app's prompt box: the chat or Code session that is open, or a
    //            new chat if the open view has no prompt box
    //   cli      as the first prompt of a new `claude` session in a terminal, in the chosen folder
    static class PromptSender
    {
        // Reports a problem on the UI thread (shown under the notch).
        public static event Action<string> Failed;

        public static void ToDesktop(string text, string newChatLink)
        {
            var t = new Thread(() =>
            {
                try { if (!SendToDesktop(text, newChatLink)) Fail("Couldn't find Claude's prompt box. Your text is on the clipboard."); }
                catch (Exception e) { Log.Write("Prompt: " + e.Message); Fail("Couldn't send to Claude."); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // the clipboard needs STA
            t.Start();
        }

        static bool SendToDesktop(string text, string newChatLink)
        {
            IntPtr hwnd = Native.FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                VoiceLauncher.OpenLink(newChatLink);
                hwnd = VoiceLauncher.WaitForClaudeWindow(15000);
                if (hwnd == IntPtr.Zero) { SetClipboard(text); return false; }
            }

            var win = AutomationElement.FromHandle(hwnd);
            Native.ForceForeground(hwnd);
            var box = FindPromptBox(win, 400);
            if (box == null)
            {
                VoiceLauncher.OpenLink(newChatLink);
                box = FindPromptBox(win, 10000);
            }
            if (box == null) { SetClipboard(text); return false; }

            Native.ForceForeground(hwnd);
            if (!Native.WaitForForeground(hwnd, 2000)) { SetClipboard(text); return false; }
            try { box.SetFocus(); } catch { }
            Thread.Sleep(80);

            // Paste rather than type, so long and multi-line prompts arrive intact. Keys are only
            // sent while Claude is in front, so they can never land in another app.
            string previous = GetClipboard();
            SetClipboard(text);
            if (Native.GetForegroundWindow() != hwnd) return false;
            Native.PressKeys(Native.VK_CONTROL, Native.VK_V);
            Thread.Sleep(150);
            if (Native.GetForegroundWindow() != hwnd) return false;
            Native.PressKeys(Native.VK_RETURN);
            Thread.Sleep(400);
            if (previous != null) SetClipboard(previous);
            return true;
        }

        // Claude's prompt box is a ProseMirror editor, exposed as an Edit control.
        static AutomationElement FindPromptBox(AutomationElement win, int timeoutMs)
        {
            var edits = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var clock = Stopwatch.StartNew();
            do
            {
                try
                {
                    AutomationElement fallback = null;
                    foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, edits))
                    {
                        var c = e.Current;
                        if (!c.IsKeyboardFocusable || !c.IsEnabled || c.IsOffscreen) continue;
                        if ((c.ClassName ?? "").IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0) return e;
                        string n = (c.Name ?? "").ToLowerInvariant();
                        if (fallback == null && (n.Contains("prompt") || n.Contains("message") || n.Contains("reply"))) fallback = e;
                    }
                    if (fallback != null) return fallback;
                }
                catch { }
                Thread.Sleep(150);
            } while (clock.ElapsedMilliseconds < timeoutMs);
            return null;
        }

        static string GetClipboard()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; }
        }

        static void SetClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return; } catch { Thread.Sleep(50); } // another app may hold it briefly
            }
        }

        // ---- terminal ------------------------------------------------------

        // Opens a terminal in the folder and starts `claude` with the prompt. The prompt goes
        // through a temp file and an encoded PowerShell command, so no quoting can break out
        // of it or run it as a command.
        public static void ToTerminal(string text, string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (FindOnPath("claude") == null)
                {
                    Fail("Claude Code (the claude command) isn't installed or isn't on PATH.");
                    return;
                }

                string tmp = Path.Combine(Path.GetTempPath(), "claude-notch-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(tmp, text, new UTF8Encoding(false));

                // Windows PowerShell 5.1 passes arguments to native programs without escaping
                // embedded quotes, so escape them (and backslashes in front of them) ourselves.
                string script =
                    "$f = '" + tmp.Replace("'", "''") + "'\n" +
                    "$p = [IO.File]::ReadAllText($f)\n" +
                    "Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue\n" +
                    "$p = $p -replace '(\\\\*)\"', '$1$1\\\"' -replace '(\\\\+)$', '$1$1'\n" +
                    "claude $p\n";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                string ps = "powershell.exe -NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand " + encoded;

                string wt = FindOnPath("wt");
                if (wt != null)
                    Process.Start(new ProcessStartInfo(wt, "-d \"" + folder.TrimEnd('\\') + "\" " + ps) { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("powershell.exe", ps.Substring("powershell.exe ".Length))
                    { UseShellExecute = true, WorkingDirectory = folder });
            }
            catch (Exception e)
            {
                Log.Write("Terminal prompt: " + e.Message);
                Fail("Couldn't open a terminal.");
            }
        }

        static string FindOnPath(string name)
        {
            var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
            foreach (string dir in dirs)
            {
                if (dir.Trim().Length == 0) continue;
                foreach (string ext in new[] { ".exe", ".cmd", ".bat" })
                {
                    try
                    {
                        string p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), name + ext);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        static void Fail(string message)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() => { if (Failed != null) Failed(message); }));
        }
    }
}
