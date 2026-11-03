using System;
using System.Threading;
using System.Windows;

namespace ClaudeNotch
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--demo") { Demo(); return; }

            bool isNew;
            using (new Mutex(true, "Local\\ClaudeNotch", out isNew))
            {
                if (!isNew) return; // already running

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var notch = new NotchWindow();
                var watcher = new ClaudeWatcher();

                watcher.Changed += () => notch.Update(watcher.IsMinimized, watcher.InVoice);
                notch.Clicked += watcher.RestoreClaude;

                CopilotKey.Install();
                watcher.Start();
                app.Run();
                CopilotKey.Uninstall();
            }
        }

        // Preview the notch without minimizing Claude: idle for 6s, voice for 8s,
        // idle again, then slide away and exit.
        static void Demo()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow();
            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                step++;
                if (step == 1) notch.Update(true, false);
                if (step == 60) notch.Update(true, true);
                if (step == 140) notch.Update(true, false);
                if (step == 190) notch.Update(false, false);
                if (step == 200) app.Shutdown();
            };
            notch.Clicked += app.Shutdown;
            timer.Start();
            app.Run();
        }
    }
}
