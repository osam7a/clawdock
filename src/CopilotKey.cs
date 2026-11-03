using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeNotch
{
    // Makes the Copilot key start a Claude voice conversation.
    //
    // The Copilot key sends Left Win + Left Shift + F23. A low-level keyboard hook
    // swallows that F23 so Windows Copilot never sees it, then:
    //   - if voice is already running, nothing happens
    //   - if the open view has Claude's "Use voice mode" button (a chat is open), it
    //     presses that, so the conversation carries on in the existing chat
    //   - otherwise (e.g. you're on the Code tab) it opens a new chat first
    // Once voice is up, Claude is minimized so the conversation carries on from the notch.
    //
    // Put a different target in ClaudeNotch.txt next to the exe to override the
    // new-chat link (e.g. https://claude.ai/new to use the browser; no voice auto-start then).
    static class CopilotKey
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int VK_F23 = 0x86, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_MASK = 0xE8; // unassigned key, used to stop Win-release from opening Start
        const uint LLKHF_INJECTED = 0x10;
        const string DefaultTarget = "claude://claude.ai/new";
        const string VoiceButton = "Use voice mode";

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        // ShowWindow(SW_MINIMIZE) is ignored by the Claude window; the system command works.
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        const uint WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020, SW_RESTORE = 9;

        static readonly HookProc hookProc = HookCallback; // kept alive so the GC can't collect it
        static IntPtr hook;
        static DateTime lastLaunch = DateTime.MinValue;
        static int running; // 1 while a launch is in progress

        public static void Install()
        {
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        }

        public static void Uninstall()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }

        static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = (int)wParam;

                if (k.vkCode == VK_F23 && (k.flags & LLKHF_INJECTED) == 0)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                        if (winDown) TapMaskKey();

                        // Ignore auto-repeat while the key is held.
                        if ((DateTime.Now - lastLaunch).TotalMilliseconds > 800)
                        {
                            lastLaunch = DateTime.Now;
                            ThreadPool.QueueUserWorkItem(_ => Launch());
                        }
                    }
                    return (IntPtr)1; // swallow F23 down and up
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // Pressing an unassigned key while Win is held makes Windows treat Win as a
        // modifier, so releasing it doesn't pop open the Start menu.
        static void TapMaskKey()
        {
            var inputs = new INPUT[2];
            inputs[0].type = 1; inputs[0].ki.wVk = VK_MASK;
            inputs[1].type = 1; inputs[1].ki.wVk = VK_MASK; inputs[1].ki.dwFlags = 2; // KEYEVENTF_KEYUP
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        static void Launch()
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return; // already handling a key press
            try
            {
                string target = DefaultTarget;
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClaudeNotch.txt");
                if (File.Exists(cfg))
                {
                    string t = File.ReadAllText(cfg).Trim();
                    if (t.Length > 0) target = t;
                }

                if (!target.StartsWith("claude://", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return;
                }
                StartVoice(target);
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("https://claude.ai/new") { UseShellExecute = true }); } catch { }
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        static void StartVoice(string newChatLink)
        {
            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                // Claude isn't running: launching it opens a new chat.
                Process.Start(new ProcessStartInfo(newChatLink) { UseShellExecute = true });
                hwnd = WaitForClaudeWindow(15000);
                if (hwnd == IntPtr.Zero) return;
            }

            var win = AutomationElement.FromHandle(hwnd);
            if (InVoice(win)) return;                            // already talking to Claude
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);    // a hidden window can stall the voice connection

            // Continue in whatever chat is open; only start a new one if this view has no voice button.
            if (!PressVoiceButton(win, 2500))
            {
                Process.Start(new ProcessStartInfo(newChatLink) { UseShellExecute = true });
                if (!PressVoiceButton(win, 12000)) { LogButtons(win); return; }
            }
            TuckAwayOnceVoiceStarts(win, hwnd);
        }

        static IntPtr FindClaudeWindow()
        {
            foreach (var p in Process.GetProcessesByName("claude"))
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            return IntPtr.Zero;
        }

        static IntPtr WaitForClaudeWindow(int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 500)
            {
                Thread.Sleep(500);
                IntPtr h = FindClaudeWindow();
                if (h != IntPtr.Zero) { Thread.Sleep(2500); return h; } // let the first screen load
            }
            return IntPtr.Zero;
        }

        static bool InVoice(AutomationElement win)
        {
            try { return win.FindFirst(TreeScope.Descendants, MicToggle()) != null; } catch { return false; }
        }

        static Condition MicToggle()
        {
            return new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, ClaudeWatcher.MicOnButton),
                new PropertyCondition(AutomationElement.NameProperty, ClaudeWatcher.MicOffButton));
        }

        // Polls for the voice button and presses it. False if it never shows up.
        static bool PressVoiceButton(AutomationElement win, int timeoutMs)
        {
            var exact = new PropertyCondition(AutomationElement.NameProperty, VoiceButton);
            for (int waited = 0; waited < timeoutMs; waited += 300)
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
                Thread.Sleep(300);
            }
            return false;
        }

        // Once Claude's voice screen is up (its mic toggle exists), minimize Claude so
        // the conversation carries on from the notch. If voice never comes up, Claude
        // stays in front so you can see what happened.
        static void TuckAwayOnceVoiceStarts(AutomationElement win, IntPtr hwnd)
        {
            for (int i = 0; i < 25; i++)
            {
                Thread.Sleep(300);
                if (InVoice(win))
                {
                    Thread.Sleep(400); // let voice finish connecting before hiding the window
                    PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
                    return;
                }
            }
        }

        // If no voice button turned up, the button names it saw go to ClaudeNotch.log for troubleshooting.
        static void LogButtons(AutomationElement win)
        {
            try
            {
                var sb = new StringBuilder(DateTime.Now + ": no voice button found. Buttons seen:\r\n");
                foreach (AutomationElement b in win.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    sb.AppendLine(b.Current.Name);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClaudeNotch.log"), sb.ToString());
            }
            catch { }
        }
    }
}
