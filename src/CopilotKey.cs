using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeNotch
{
    // Makes the Copilot key open a new Claude voice conversation.
    //
    // The Copilot key sends Left Win + Left Shift + F23. A low-level keyboard hook
    // swallows that F23 so Windows Copilot never sees it, and opens Claude instead.
    // Put a different target in ClaudeNotch.txt next to the exe to override it
    // (e.g. https://claude.ai/new to use the browser instead of the desktop app).
    static class CopilotKey
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int VK_F23 = 0x86, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_MASK = 0xE8; // unassigned key, used to stop Win-release from opening Start
        const uint LLKHF_INJECTED = 0x10;
        const string DefaultTarget = "claude://claude.ai/new";

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

        static readonly HookProc hookProc = HookCallback; // kept alive so the GC can't collect it
        static IntPtr hook;
        static DateTime lastLaunch = DateTime.MinValue;

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
            string target = DefaultTarget;
            try
            {
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClaudeNotch.txt");
                if (File.Exists(cfg))
                {
                    string t = File.ReadAllText(cfg).Trim();
                    if (t.Length > 0) target = t;
                }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                if (target.StartsWith("claude://")) StartVoice();
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("https://claude.ai/new") { UseShellExecute = true }); } catch { }
            }
        }

        // Waits for the new chat to load in the Claude window, then presses its
        // voice mode button through UI Automation. If no voice button turns up,
        // the button names it saw go to ClaudeNotch.log for troubleshooting.
        static void StartVoice()
        {
            var buttonCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            string seen = "";
            Thread.Sleep(700); // let the app switch to the new chat first

            for (int attempt = 0; attempt < 24; attempt++)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName("claude"))
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        var win = AutomationElement.FromHandle(p.MainWindowHandle);
                        AutomationElement best = null;
                        var names = new StringBuilder();

                        foreach (AutomationElement b in win.FindAll(TreeScope.Descendants, buttonCond))
                        {
                            string n = (b.Current.Name ?? "").ToLowerInvariant();
                            names.AppendLine(b.Current.Name);
                            if (n.Contains("voice mode")) { best = b; break; }
                            if (best == null && n.Contains("voice") && !n.Contains("dictat")) best = b;
                        }
                        seen = names.ToString();

                        object pattern;
                        if (best != null && best.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            return;
                        }
                    }
                }
                catch { }
                Thread.Sleep(500);
            }

            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClaudeNotch.log"),
                    DateTime.Now + ": no voice button found. Buttons seen:\r\n" + seen);
            }
            catch { }
        }
    }
}
