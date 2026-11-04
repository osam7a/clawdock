using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Wrenlet
{
    // Finds the window a terminal session of Claude Code runs in, so the notch can bring it
    // to the front. The claude process itself has no window; the first ancestor process that
    // does is the terminal (Windows Terminal, the classic console, VS Code, ...).
    static class TerminalFocus
    {
        public static IntPtr FindHostWindow(int pid)
        {
            try
            {
                // A classic console window belongs to the shell's process tree directly.
                IntPtr own = MainWindow(pid);
                if (own != IntPtr.Zero) return own;

                var parents = Native.ParentProcesses();
                var seen = new HashSet<int>();
                int cur = pid;
                for (int depth = 0; depth < 12; depth++)
                {
                    int parent;
                    if (!parents.TryGetValue(cur, out parent) || parent <= 4 || !seen.Add(parent)) break;
                    IntPtr h = MainWindow(parent);
                    if (h != IntPtr.Zero) return h;
                    cur = parent;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        public static bool Focus(IntPtr host)
        {
            return host != IntPtr.Zero && Native.IsWindow(host) && Native.ForceForeground(host);
        }

        static IntPtr MainWindow(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    if (name == "explorer") return IntPtr.Zero; // started from a shortcut: no terminal to find
                    return p.MainWindowHandle;
                }
            }
            catch { return IntPtr.Zero; }
        }
    }
}
