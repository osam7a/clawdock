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
        //   ClaudeNotch.exe --demo       preview the notch's states without touching Claude (--demo quick: just quick voice)
        [STAThread]
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--hook") { HookServer.RunHookClient(false); return; }
            if (mode == "--voice-hook") { HookServer.RunHookClient(true); return; }
            if (mode == "--demo") { Demo(args.Length > 1 && args[1] == "quick"); return; }
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
        static void Demo(bool quickOnly)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow(new Settings { Transient = true });
            var now = DateTime.Now;
            var working = new SessionInfo { Id = "a", Name = "CoPilot key launcher", Cwd = @"C:\Users\you\Claude-Notch", Desktop = true, Busy = true, Done = 1, Total = 5 };
            var cli = new SessionInfo { Id = "b", Name = "api refactor", Cwd = @"C:\code\api", Busy = true, Done = 0, Total = 3 };
            var idle = new SessionInfo { Id = "c", Name = "docs", Cwd = @"C:\code\docs" };
            var all = new List<SessionInfo> { working, cli, idle };

            var demoPending = new HookServer.Pending();
            var demoQuestion = new Question();
            int step = quickOnly ? 299 : 0;
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
                else if (step < 260) st = new NotchState { Visible = true, Kind = "alert", Title = "Allow Bash? · Claude-Notch", Detail = "Bash: git push origin main", Pending = demoPending, Owner = demoPending, Choices = new List<string> { "Deny", "Allow" }, Primary = 1, Sessions = all };
                else if (step < 300) st = new NotchState { Visible = true, Kind = "question", Title = "Claude asks · " + working.Name, Detail = "Should I test approve-from-notch with a real Claude Code CLI session?", Owner = demoQuestion, Choices = new List<string> { "Not now", "Yes, test it" }, Primary = 1, Sessions = all };
                else if (step < 500) st = DemoQuick(step - 300);
                else if (step < 510 && quickOnly) st = new NotchState { Visible = false };
                else if (quickOnly) { app.Shutdown(); return; }
                else if (step < 560) st = new NotchState { Visible = true, Kind = "session", Title = working.Name, JustFinished = true, Done = 5, Total = 5, Detail = "Done in 4:12 · 5 done", Sessions = all };
                else if (step < 600) st = new NotchState { Visible = true, Kind = "idle", Sessions = all };
                else if (step < 610) st = new NotchState { Visible = false };
                else { app.Shutdown(); return; }
                notch.Render(st);
            };
            notch.OpenPrimary += app.Shutdown;
            notch.Quit += app.Shutdown;
            notch.Choose += (owner, index) => { };
            notch.Show();
            timer.Start();
            app.Run();
        }

        // Quick voice in the demo: a question answered with a value card, then a follow-up
        // answered with a list (the answers stream in, card first, like Claude sends them).
        static NotchState DemoQuick(int i)
        {
            const string q1 = "How much is the MacBook Air M4 on my price list?";
            const string a1 = "<card>{\"value\": \"$1,249\", \"label\": \"MacBook Air 13-inch, M4, 16 GB\", \"note\": \"Your price list, updated March 2026\"}</card>"
                + "It's twelve hundred and forty-nine dollars for the sixteen gig model. The twenty-four gig one is fourteen forty-nine.";
            const string q2 = "And the other laptops?";
            const string a2 = "<card>{\"title\": \"Laptops on your price list\", \"items\": [[\"MacBook Air 13, M4\", \"$1,249\"], [\"MacBook Air 15, M4\", \"$1,449\"], [\"MacBook Pro 14, M4 Pro\", \"$1,999\"], [\"Surface Laptop 7\", \"$1,099\"]]}</card>"
                + "There are four. The cheapest is the Surface Laptop at ten ninety-nine, and the MacBook Pro tops out at nineteen ninety-nine.";
            var st = new NotchState { Visible = true, Kind = "quick" };
            bool second = i >= 100;
            string q = second ? q2 : q1, a = second ? a2 : a1;
            int j = second ? i - 100 : i;
            if (second) st.QuickHistory.Add(new QuickTurn { Heard = q1, Text = QuickVoice.Display(a1), Card = QuickVoice.ParseCard(a1) });
            if (j < 22)
            {
                st.Listening = true;
                st.VoiceStarted = DateTime.Now.AddSeconds(-j / 10.0);
                st.Title = "Listening";
                string heard = q.Substring(0, Math.Min(q.Length, j * q.Length / 16));
                st.QuickNow = new QuickTurn { Heard = heard };
                if (heard == "") st.QuickStatus = "Go ahead, I'm listening";
            }
            else if (j < 34)
            {
                st.Busy = true;
                st.Title = second ? "Thinking…" : "Searching your files…";
                st.QuickNow = new QuickTurn { Heard = q };
            }
            else
            {
                string sofar = a.Substring(0, Math.Min(a.Length, (j - 34) * 20));
                st.Answering = st.Busy = true;
                st.Title = sofar.Length < a.Length ? "Speaking" : "Claude";
                st.QuickNow = new QuickTurn { Heard = q, Text = QuickVoice.Display(sofar), Card = QuickVoice.ParseCard(sofar) };
            }
            return st;
        }
    }
}
