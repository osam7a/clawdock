using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // The shortcut that starts talking (or typing) to Claude. Either:
    //   copilot  the Copilot key, which sends Left Win + Left Shift + F23. A low-level keyboard
    //            hook swallows that F23 so Windows' own Copilot never sees it.
    //   custom   any key combination, through RegisterHotKey
    //   off      nothing
    static class Hotkeys
    {
        public static event Action Pressed;

        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_HOTKEY = 0x0312;
        const int VK_F23 = 0x86, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const ushort VK_MASK = 0xE8; // unassigned key, used to stop Win-release from opening Start
        const uint LLKHF_INJECTED = 0x10, MOD_NOREPEAT = 0x4000;
        const int HotkeyId = 0xC1A0;

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        static readonly HookProc hookProc = HookCallback; // kept alive so the GC can't collect it
        static IntPtr hook;
        static HwndSource hotkeyWindow;
        static bool registered;
        static DateTime lastPress = DateTime.MinValue;
        static Dispatcher ui;

        // Applies the shortcut from settings. Returns an error to show, or null.
        public static string Apply(Settings s)
        {
            ui = Dispatcher.CurrentDispatcher;
            Remove();
            if (s.Shortcut == "copilot")
            {
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
                return hook == IntPtr.Zero ? "Couldn't listen for the Copilot key." : null;
            }
            if (s.Shortcut == "custom")
            {
                if (s.CustomKey == 0) return "No shortcut set.";
                if (hotkeyWindow == null)
                {
                    hotkeyWindow = new HwndSource(new HwndSourceParameters("ClaudeNotchHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                    hotkeyWindow.AddHook(WndProc);
                }
                registered = RegisterHotKey(hotkeyWindow.Handle, HotkeyId, (uint)s.CustomModifiers | MOD_NOREPEAT, (uint)s.CustomKey);
                return registered ? null : Describe(s.CustomModifiers, s.CustomKey) + " is already used by another app.";
            }
            return null;
        }

        public static void Remove()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (registered && hotkeyWindow != null) { UnregisterHotKey(hotkeyWindow.Handle, HotkeyId); registered = false; }
        }

        public static string Describe(int modifiers, int vk)
        {
            if (vk == 0) return "None";
            string s = "";
            if ((modifiers & 2) != 0) s += "Ctrl + ";
            if ((modifiers & 1) != 0) s += "Alt + ";
            if ((modifiers & 4) != 0) s += "Shift + ";
            if ((modifiers & 8) != 0) s += "Win + ";
            var key = KeyInterop.KeyFromVirtualKey(vk);
            string name = key.ToString();
            if (key >= Key.D0 && key <= Key.D9) name = name.Substring(1);
            return s + name;
        }

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) { handled = true; Fire(); }
            return IntPtr.Zero;
        }

        static void Fire()
        {
            if ((DateTime.Now - lastPress).TotalMilliseconds < 800) return; // ignore auto-repeat and double presses
            lastPress = DateTime.Now;
            if (ui != null) ui.BeginInvoke(new Action(() => { if (Pressed != null) Pressed(); }));
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
                        if (winDown) Native.PressKeys(VK_MASK); // Win then counts as a modifier: no Start menu on release
                        Fire();
                    }
                    return (IntPtr)1; // swallow F23 down and up
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    }
}
