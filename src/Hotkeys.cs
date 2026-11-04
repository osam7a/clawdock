using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Wrenlet
{
    // The shortcut that starts talking to Claude (or opens its quick entry). Either:
    //   copilot  the Copilot key, which sends Left Win + Left Shift + F23. A low-level keyboard
    //            hook swallows that F23 so Windows' own Copilot never sees it.
    //   custom   any key combination, through RegisterHotKey
    //   off      nothing
    static class Hotkeys
    {
        public static event Action Pressed;
        public static event Action DoublePressed; // a second press right after Pressed
        public static event Action Escape; // Esc, while WatchEscape is on

        const double DoublePressWithin = 450; // ms
        static bool doubled;

        // Listens for Esc (on its own, no modifiers) while something is up on the notch. Esc
        // still reaches the app you're in; the hook only watches.
        public static bool WatchEscape
        {
            set
            {
                if (value == (escHook != IntPtr.Zero)) return;
                if (value) { ui = ui ?? Dispatcher.CurrentDispatcher; escHook = SetWindowsHookEx(WH_KEYBOARD_LL, escProc, GetModuleHandle(null), 0); }
                else { UnhookWindowsHookEx(escHook); escHook = IntPtr.Zero; }
            }
        }

        const int VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        static readonly HookProc escProc = EscCallback;
        static IntPtr escHook;

        static IntPtr EscCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && ((int)wParam == WM_KEYDOWN))
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool modifier = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                    || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                if (k.vkCode == VK_ESCAPE && !modifier && ui != null)
                    ui.BeginInvoke(new Action(() => { if (Escape != null) Escape(); }));
            }
            return CallNextHookEx(escHook, nCode, wParam, lParam);
        }

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
        static volatile bool f23Down; // the Copilot key is held (its F23 is swallowed, so Windows doesn't track it)
        static DateTime lastF23Event = DateTime.MinValue;
        static int customKey;

        // Is the shortcut still held down? Push-to-talk listens until it's let go.
        public static bool Held
        {
            get { return hook != IntPtr.Zero ? f23Down : registered && customKey != 0 && (GetAsyncKeyState(customKey) & 0x8000) != 0; }
        }

        // Applies the shortcut from settings. Returns an error to show, or null.
        public static string Apply(Settings s)
        {
            ui = Dispatcher.CurrentDispatcher;
            Remove();
            customKey = s.CustomKey;
            if (s.Shortcut == "copilot")
            {
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
                return hook == IntPtr.Zero ? "Couldn't listen for the Copilot key." : null;
            }
            if (s.Shortcut == "custom")
            {
                if (s.CustomKey == 0) return "No shortcut set.";
                if ((s.CustomModifiers & 0xF) == (1 | 2) && s.CustomKey == 0x20) return "Ctrl+Alt+Space is Claude's own quick entry shortcut. Pick another.";
                if (hotkeyWindow == null)
                {
                    hotkeyWindow = new HwndSource(new HwndSourceParameters("WrenletHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
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
            double gap = (DateTime.Now - lastPress).TotalMilliseconds;
            if (gap < DoublePressWithin && !doubled)
            {
                lastPress = DateTime.Now;
                doubled = true;
                if (ui != null) ui.BeginInvoke(new Action(() => { if (DoublePressed != null) DoublePressed(); }));
                return;
            }
            if (gap < 800) return; // ignore auto-repeat and stray presses
            lastPress = DateTime.Now;
            doubled = false;
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
                        // Held keys repeat every ~30 ms. A gap means a new press, in case a key-up was missed.
                        bool repeat = f23Down && (DateTime.UtcNow - lastF23Event).TotalMilliseconds < 1000;
                        lastF23Event = DateTime.UtcNow;
                        f23Down = true;
                        if (repeat) return (IntPtr)1;
                        bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                        if (winDown) Native.PressKeys(VK_MASK); // Win then counts as a modifier: no Start menu on release
                        Fire();
                    }
                    else f23Down = false;
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
