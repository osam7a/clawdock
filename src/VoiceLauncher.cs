using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Wrenlet
{
    // Starts a Claude voice conversation:
    //   - if voice is already running, nothing happens
    //   - if the open view has Claude's "Use voice mode" button (a chat is open), it presses
    //     that, so the conversation carries on in the existing chat
    //   - otherwise (e.g. you're on the Code tab) it opens a new chat first
    // Once voice is up, Claude is minimized so the conversation carries on from the notch.
    static class VoiceLauncher
    {
        const string VoiceButton = "Use voice mode";
        static int running; // 1 while a launch is in progress

        public static void StartAsync(string newChatLink)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Interlocked.Exchange(ref running, 1) == 1) return; // already handling a press
                try { Start(newChatLink); }
                catch (Exception e) { Log.Write("Voice: " + e.Message); }
                finally { Interlocked.Exchange(ref running, 0); }
            });
        }

        // A capture device exists and Windows isn't blocking apps from using it.
        public static bool HasMicrophone()
        {
            try
            {
                if (waveInGetNumDevs() == 0) return false;
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    using (var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"))
                        if (key != null && string.Equals(key.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return true; } // can't tell: let Claude try
        }

        static void Start(string newChatLink)
        {
            IntPtr hwnd = Native.FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                OpenLink(newChatLink); // Claude isn't running: launching it opens a new chat
                hwnd = WaitForClaudeWindow(15000);
                if (hwnd == IntPtr.Zero) return;
            }

            var win = AutomationElement.FromHandle(hwnd);
            if (InVoice(win)) return;
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE); // a hidden window can stall the connection

            // The open view is already loaded, so a short look tells whether it has a voice
            // button; only after opening a new chat is it worth waiting for one to appear.
            if (!PressVoiceButton(win, 400))
            {
                OpenLink(newChatLink);
                if (!PressVoiceButton(win, 12000)) { LogButtons(win); return; }
            }
            TuckAwayOnceVoiceStarts(win, hwnd);
        }

        public static void OpenLink(string link)
        {
            // Settings only accepts claude:// links, so this can't be pointed at an arbitrary program.
            if (link == null || !link.StartsWith("claude://", StringComparison.OrdinalIgnoreCase)) link = "claude://claude.ai/new";
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }

        public static IntPtr WaitForClaudeWindow(int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 500)
            {
                Thread.Sleep(500);
                IntPtr h = Native.FindClaudeWindow();
                if (h != IntPtr.Zero) { Thread.Sleep(2500); return h; } // let the first screen load
            }
            return IntPtr.Zero;
        }

        static bool InVoice(AutomationElement win)
        {
            try
            {
                return win.FindFirst(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, ClaudeWatcher.MicOnButton),
                    new PropertyCondition(AutomationElement.NameProperty, ClaudeWatcher.MicOffButton))) != null;
            }
            catch { return false; }
        }

        static bool PressVoiceButton(AutomationElement win, int timeoutMs)
        {
            var exact = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, VoiceButton));
            var clock = Stopwatch.StartNew();
            do
            {
                try
                {
                    var button = win.FindFirst(TreeScope.Descendants, exact);
                    object pattern;
                    if (button != null && button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        return true;
                    }
                }
                catch { }
                Thread.Sleep(100);
            } while (clock.ElapsedMilliseconds < timeoutMs); // always at least one look
            return false;
        }

        // Once Claude's voice screen is up, minimize Claude so the conversation carries on from
        // the notch. If voice never comes up, Claude stays in front so you can see why.
        static void TuckAwayOnceVoiceStarts(AutomationElement win, IntPtr hwnd)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 8000)
            {
                if (InVoice(win))
                {
                    Thread.Sleep(250);
                    // ShowWindow(SW_MINIMIZE) is ignored by Claude's window; the system command works.
                    Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MINIMIZE, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(100);
            }
        }

        static void LogButtons(AutomationElement win)
        {
            if (!Log.Verbose) { Log.Write("No voice button found. Set CLAUDE_NOTCH_DEBUG=1 to record the buttons seen."); return; }
            try
            {
                var sb = new StringBuilder("No voice button found. Buttons seen:");
                foreach (AutomationElement b in win.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    sb.Append(" | ").Append(b.Current.Name);
                Log.Write(sb.ToString());
            }
            catch { }
        }

        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
    }
}
