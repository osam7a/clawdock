using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // Polls whether the Claude desktop window is minimized and whether Claude is
    // using the microphone (i.e. a voice conversation is running).
    class ClaudeWatcher
    {
        const string MicStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public event Action Changed;
        public bool IsMinimized { get; private set; }
        public bool InVoice { get; private set; }

        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        HashSet<uint> claudePids = new HashSet<uint>();
        DateTime pidsRefreshed = DateTime.MinValue;
        IntPtr claudeWindow;

        public void Start()
        {
            timer.Tick += (s, e) => Poll();
            timer.Start();
            Poll();
        }

        void Poll()
        {
            if ((DateTime.Now - pidsRefreshed).TotalSeconds > 3)
            {
                claudePids = new HashSet<uint>(Process.GetProcessesByName("claude").Select(p => (uint)p.Id));
                pidsRefreshed = DateTime.Now;
            }

            bool anyOpen = false, anyMinimized = false;
            IntPtr minimizedWindow = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!claudePids.Contains(pid)) return true;
                if (IsIconic(h)) { anyMinimized = true; if (minimizedWindow == IntPtr.Zero) minimizedWindow = h; }
                else anyOpen = true;
                return true;
            }, IntPtr.Zero);

            bool minimized = anyMinimized && !anyOpen;
            if (minimized) claudeWindow = minimizedWindow;
            bool voice = MicInUse();

            if (minimized != IsMinimized || voice != InVoice)
            {
                IsMinimized = minimized;
                InVoice = voice;
                if (Changed != null) Changed();
            }
        }

        public void RestoreClaude()
        {
            if (claudeWindow == IntPtr.Zero) return;
            ShowWindow(claudeWindow, SW_RESTORE);
            SetForegroundWindow(claudeWindow);
        }

        // Windows records, per app, when it last started and stopped using the mic.
        // A non-zero start with a zero stop means it is using the mic right now.
        static bool MicInUse()
        {
            try
            {
                using (var store = Registry.CurrentUser.OpenSubKey(MicStore))
                {
                    if (store == null) return false;
                    foreach (var name in store.GetSubKeyNames())
                        if (name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) && Active(store, name)) return true;

                    using (var unpackaged = store.OpenSubKey("NonPackaged"))
                        if (unpackaged != null)
                            foreach (var name in unpackaged.GetSubKeyNames())
                                if (name.IndexOf("claude.exe", StringComparison.OrdinalIgnoreCase) >= 0 && Active(unpackaged, name)) return true;
                }
            }
            catch { }
            return false;
        }

        static bool Active(RegistryKey parent, string name)
        {
            using (var key = parent.OpenSubKey(name))
            {
                if (key == null) return false;
                object start = key.GetValue("LastUsedTimeStart"), stop = key.GetValue("LastUsedTimeStop");
                return start != null && stop != null && Convert.ToInt64(start) != 0 && Convert.ToInt64(stop) == 0;
            }
        }

        const uint GW_OWNER = 4;
        const int SW_RESTORE = 9;
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    }
}
