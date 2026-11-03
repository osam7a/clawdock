using System;
using System.Threading;
using System.Windows;

namespace ClaudeNotch
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
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
    }
}
