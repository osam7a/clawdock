using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace ClaudeNotch
{
    static class Program
    {
        //   ClaudeNotch.exe              install (if needed) and run; if already running, open settings
        //   ClaudeNotch.exe --here       run from where it is, without installing (for testing builds)
        //   ClaudeNotch.exe --settings   open the settings of the running copy
        //   ClaudeNotch.exe --uninstall  remove the app, its hooks and its startup entry
        //   ClaudeNotch.exe --hook       what Claude Code's hooks run (see HookServer)
        //   ClaudeNotch.exe --demo       preview the notch's states without touching Claude
        [STAThread]
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--hook") { HookServer.RunHookClient(); return; }
            if (mode == "--demo") { Demo(); return; }
            if (mode == "--uninstall")
            {
                Installer.Uninstall();
                MessageBox.Show("Claude Notch has been removed.", "Claude Notch");
                return;
            }

            bool firstRun = !Settings.Exists;
            var settings = Settings.Load();

            if (mode != "--here" && mode != "--settings" && !Installer.IsInstalledCopy)
            {
                if (!Installer.InstallAndLaunch(settings))
                    MessageBox.Show("Claude Notch couldn't install itself. See " + Log.FilePath, "Claude Notch", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool isNew;
            using (new Mutex(true, HookServer.MutexName, out isNew))
            {
                if (!isNew)
                {
                    HookServer.SendCommand("settings"); // already running: show its settings instead
                    return;
                }
                if (mode == "--settings") firstRun = true;
                if (firstRun) settings.Save();

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Write("Unhandled: " + e.Exception); e.Handled = true; };
                var controller = new Controller(settings);
                app.Startup += (s, e) =>
                {
                    controller.Start();
                    if (firstRun) app.Dispatcher.BeginInvoke(new Action(controller.OpenSettings));
                };
                app.Run();
            }
        }

        // Preview the notch's states without touching Claude (about 40 seconds).
        static void Demo()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow(new Settings());
            var now = DateTime.Now;
            var working = new SessionInfo { Id = "a", Name = "CoPilot key launcher", Cwd = @"C:\Users\you\Claude-Notch", Desktop = true, Busy = true, Done = 1, Total = 5 };
            var cli = new SessionInfo { Id = "b", Name = "api refactor", Cwd = @"C:\code\api", Busy = true, Done = 0, Total = 3 };
            var idle = new SessionInfo { Id = "c", Name = "docs", Cwd = @"C:\code\docs" };
            var all = new List<SessionInfo> { working, cli, idle };

            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                step++;
                var t = DateTime.Now - now;
                working.Elapsed = t; cli.Elapsed = t + TimeSpan.FromMinutes(3);
                if (step == 100) working.Done = 3;
                NotchState st = null;
                if (step < 140) st = new NotchState { Visible = step > 5, Kind = "session", Title = working.Name, Busy = true, Done = working.Done, Total = 5, Detail = "Working " + SessionTracker.Clock(t) + " · " + working.Done + " done · " + (5 - working.Done) + " left", Sessions = all };
                else if (step < 220) st = new NotchState { Visible = true, Kind = "voice", Muted = step > 175 && step < 195, VoiceStarted = now.AddSeconds(14), Sessions = all };
                else if (step < 300) st = new NotchState { Visible = true, Kind = "alert", Title = "Allow Bash? · Claude-Notch", Detail = "Bash: git push origin main", Pending = new HookServer.Pending(), Sessions = all };
                else if (step < 360) st = new NotchState { Visible = true, Kind = "session", Title = working.Name, JustFinished = true, Done = 5, Total = 5, Detail = "Done in 4:12 · 5 done", Sessions = all };
                else if (step < 400) st = new NotchState { Visible = true, Kind = "idle", Sessions = all };
                else if (step < 410) st = new NotchState { Visible = false };
                else { app.Shutdown(); return; }
                notch.Render(st);
            };
            notch.OpenPrimary += app.Shutdown;
            notch.Quit += app.Shutdown;
            notch.Answer += (p, allow) => { };
            notch.Show();
            timer.Start();
            app.Run();
        }
    }
}
