using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clawdock
{
    // Win32 calls shared by several parts of the app.
    static class Native
    {
        // Windows only lets the app the user is using bring a window to the front. A global
        // shortcut doesn't count as using our app, so borrow the foreground thread's input
        // state for a moment.
        public static bool ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (GetForegroundWindow() == hwnd) return true;

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            return GetForegroundWindow() == hwnd;
        }

        public static bool WaitForForeground(IntPtr hwnd, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(50);
            }
            return GetForegroundWindow() == hwnd;
        }

        public static void PressKeys(params ushort[] keys)
        {
            // Press in order, release in reverse: PressKeys(VK_CONTROL, 'V') is Ctrl+V.
            var inputs = new INPUT[keys.Length * 2];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i].type = 1; inputs[i].ki.wVk = keys[i];
                inputs[keys.Length * 2 - 1 - i].type = 1;
                inputs[keys.Length * 2 - 1 - i].ki.wVk = keys[i];
                inputs[keys.Length * 2 - 1 - i].ki.dwFlags = KEYEVENTF_KEYUP;
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // Parent process id of every process, from one snapshot.
        public static Dictionary<int, int> ParentProcesses()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32First(snap, ref e))
                    do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; } while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // Claude's main window: the first visible, titled, unowned window of a claude.exe process.
        public static IntPtr FindClaudeWindow()
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("claude")) { pids.Add((uint)p.Id); p.Dispose(); }
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_SPACE = 0x20, VK_V = 0x56;
        public const uint WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020, SW_RESTORE = 9;
        const uint KEYEVENTF_KEYUP = 2, GW_OWNER = 4, TH32CS_SNAPPROCESS = 2;
        static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
