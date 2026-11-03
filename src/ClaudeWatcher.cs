using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // Polls the state of the Claude desktop app: whether it has a window open,
    // whether that window is the one in front, whether some other app is
    // fullscreen, and whether a voice conversation is running (and muted).
    class ClaudeWatcher
    {
        const string MicStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        // Claude's voice screen has a mic toggle with one of these names; the
        // chat screen doesn't, so finding it means a voice conversation is on.
        internal const string MicOnButton = "Turn off microphone", MicOffButton = "Turn on microphone";

        public event Action Changed;
        public bool ClaudeOpen { get; private set; }
        public bool ClaudeInFront { get; private set; }
        public bool FullscreenApp { get; private set; }
        public bool InVoice { get; private set; }
        public bool MicMuted { get; private set; }

        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        HashSet<uint> claudePids = new HashSet<uint>();
        DateTime pidsRefreshed = DateTime.MinValue;
        IntPtr claudeWindow;

        // Written by the UI Automation thread, read by Poll.
        volatile bool probeVoice, probeMuted;
        volatile IntPtr probeWindow;

        public void Start()
        {
            timer.Tick += (s, e) => Poll();
            timer.Start();
            new Thread(ProbeVoiceLoop) { IsBackground = true }.Start();
            Poll();
        }

        // UI Automation lookups can take tens of milliseconds, so they run on their
        // own thread instead of the UI timer.
        void ProbeVoiceLoop()
        {
            var cond = new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, MicOnButton),
                new PropertyCondition(AutomationElement.NameProperty, MicOffButton));
            while (true)
            {
                bool voice = false, muted = false;
                try
                {
                    IntPtr h = probeWindow;
                    if (h != IntPtr.Zero)
                    {
                        var el = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, cond);
                        if (el != null) { voice = true; muted = el.Current.Name == MicOffButton; }
                    }
                }
                catch { }
                probeVoice = voice;
                probeMuted = muted;
                Thread.Sleep(700);
            }
        }

        // Presses Claude's own mic toggle, so mute state stays in sync with the app.
        public void ToggleMute()
        {
            IntPtr h = claudeWindow;
            if (h == IntPtr.Zero) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var cond = new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, MicOnButton),
                        new PropertyCondition(AutomationElement.NameProperty, MicOffButton));
                    var el = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, cond);
                    object pattern;
                    if (el != null && el.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        probeMuted = !probeMuted;
                    }
                }
                catch { }
            });
        }

        void Poll()
        {
            if ((DateTime.Now - pidsRefreshed).TotalSeconds > 3)
            {
                claudePids = new HashSet<uint>(Process.GetProcessesByName("claude").Select(p => (uint)p.Id));
                pidsRefreshed = DateTime.Now;
            }

            IntPtr open = IntPtr.Zero, minimized = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!claudePids.Contains(pid)) return true;
                if (IsIconic(h)) { if (minimized == IntPtr.Zero) minimized = h; }
                else if (open == IntPtr.Zero) open = h;
                return true;
            }, IntPtr.Zero);

            IntPtr fg = GetForegroundWindow();
            uint fgPid;
            GetWindowThreadProcessId(fg, out fgPid);

            bool claudeOpen = open != IntPtr.Zero || minimized != IntPtr.Zero;
            bool inFront = claudeOpen && claudePids.Contains(fgPid) && !IsIconic(fg);
            bool fullscreen = !inFront && IsFullscreen(fg);
            if (claudeOpen) claudeWindow = open != IntPtr.Zero ? open : minimized;
            probeWindow = claudeOpen ? claudeWindow : IntPtr.Zero;
            bool voice = claudeOpen && (probeVoice || MicInUse());
            bool muted = voice && probeMuted;

            if (claudeOpen != ClaudeOpen || inFront != ClaudeInFront || fullscreen != FullscreenApp || voice != InVoice || muted != MicMuted)
            {
                ClaudeOpen = claudeOpen;
                ClaudeInFront = inFront;
                FullscreenApp = fullscreen;
                InVoice = voice;
                MicMuted = muted;
                if (Changed != null) Changed();
            }
        }

        public void RestoreClaude()
        {
            if (claudeWindow == IntPtr.Zero) return;
            if (IsIconic(claudeWindow)) ShowWindow(claudeWindow, SW_RESTORE);
            SetForegroundWindow(claudeWindow);
        }

        // A window that covers its whole monitor (games, videos, presentations),
        // ignoring the desktop itself.
        static bool IsFullscreen(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;

            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        // Windows records, per app, when it last started and stopped using the mic.
        // A non-zero start with a zero stop means it is using the mic right now.
        // The Claude app doesn't always show up here, so this only backs up the
        // UI Automation check above.
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

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        const uint GW_OWNER = 4;
        const int SW_RESTORE = 9;
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    }
}
