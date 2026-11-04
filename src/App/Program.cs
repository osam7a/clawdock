using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace Clawdock
{
    static class Program
    {
        //   Clawdock.exe              install (if needed) and run; if already running, open settings
        //   Clawdock.exe --here       run from where it is, without installing (for testing builds)
        //   Clawdock.exe --settings   open the settings of the running copy
        //   Clawdock.exe --update     install this downloaded update, without any messages (see Updater)
        //   Clawdock.exe --uninstall  remove the app, its hooks and its startup entry
        //   Clawdock.exe --hook       what Claude Code's hooks run (see HookServer)
        //   Clawdock.exe --demo       preview the notch's states without touching Claude (--demo quick: just quick voice; --demo cards: each kind of card)
        [STAThread]
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--hook") { HookServer.RunHookClient(false); return; }
            if (mode == "--voice-hook") { HookServer.RunHookClient(true); return; }
            if (mode == "--demo") { Demo(args.Length > 1 ? args[1] : ""); return; }
            if (mode == "--uninstall")
            {
                Installer.Uninstall();
                MessageBox.Show("Clawdock has been removed.", "Clawdock");
                return;
            }

            Installer.MigrateLegacy();
            bool firstRun = !Settings.Exists;
            var settings = Settings.Load();

            if (mode != "--here" && mode != "--settings" && !Installer.IsInstalledCopy)
            {
                if (!Installer.InstallAndLaunch(settings) && mode != "--update") // an update just tries again later
                    MessageBox.Show("Clawdock couldn't install itself. See " + Log.FilePath, "Clawdock", MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (firstRun) settings.FetchVoices = true;
                if (mode == "--settings") firstRun = true;
                if (firstRun) settings.Save();
                if (Installer.IsInstalledCopy) Installer.EnsureShortcut();

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
        static void Demo(string only)
        {
            bool quickOnly = only == "quick" || only == "cards";
            bool cardsOnly = only == "cards";
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow(new Settings { Transient = true });
            var now = DateTime.Now;
            var working = new SessionInfo { Id = "a", Name = "CoPilot key launcher", Cwd = @"C:\Users\you\Clawdock", Desktop = true, Busy = true, Done = 1, Total = 5 };
            var cli = new SessionInfo { Id = "b", Name = "api refactor", Cwd = @"C:\code\api", Busy = true, Done = 0, Total = 3 };
            var idle = new SessionInfo { Id = "c", Name = "docs", Cwd = @"C:\code\docs" };
            var all = new List<SessionInfo> { working, cli, idle };

            var demoPending = new HookServer.Pending { FullDetail = "Command: git push origin main\n\nDescription: Push the commits\n\nFolder: C:\\code\\Clawdock" };
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
                else if (step < 260) st = new NotchState { Visible = true, Kind = "alert", Title = "Allow Bash? · Clawdock", Detail = "Bash: git push origin main", Pending = demoPending, Owner = demoPending, Choices = new List<string> { "Deny", "Allow" }, Primary = 1, Sessions = all };
                else if (step < 300) st = new NotchState { Visible = true, Kind = "question", Title = "Claude asks · " + working.Name, Detail = "Should I test approve-from-notch with a real Claude Code CLI session?", Owner = demoQuestion, Choices = new List<string> { "Not now", "Yes, test it" }, Primary = 1, Sessions = all };
                else if (cardsOnly && step < 300 + DemoCards.Length * 40) st = DemoCard(step - 300);
                else if (cardsOnly && step < 310 + DemoCards.Length * 40) st = new NotchState { Visible = false };
                else if (cardsOnly) { app.Shutdown(); return; }
                else if (step < 580) st = DemoQuick(step - 300);
                else if (step < 590 && quickOnly) st = new NotchState { Visible = false };
                else if (quickOnly) { app.Shutdown(); return; }
                else if (step < 640) st = new NotchState { Visible = true, Kind = "session", Title = working.Name, JustFinished = true, Done = 5, Total = 5, Detail = "Done in 4:12 · 5 done", Sessions = all };
                else if (step < 680) st = new NotchState { Visible = true, Kind = "idle", Sessions = all };
                else if (step < 690) st = new NotchState { Visible = false };
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

        // Each kind of card, four seconds each, as answers to a question (--demo cards); the last
        // two again in the minimized bar.
        static readonly string[][] DemoCards =
        {
            new[] { "What's Bitcoin at?", "<card>{\"value\": \"$96,410\", \"label\": \"Bitcoin\", \"change\": \"+2.4% today\", \"note\": \"CoinGecko, 2:10 PM\"}</card>It's about ninety-six thousand four hundred, up two point four percent today." },
            new[] { "How's my budget this month?", "<card>{\"value\": \"$1,360 of $2,000\", \"label\": \"November budget\", \"progress\": 0.68, \"note\": \"12 days left\"}</card>You've spent thirteen sixty of two thousand, so about two thirds, with twelve days to go." },
            new[] { "How do I reset my router?", "<card>{\"title\": \"Reset the router\", \"steps\": [\"Unplug it for 30 seconds\", \"Plug it back in\", \"Wait for the white light\", \"Reconnect your laptop to the Wi-Fi\"]}</card>Unplug it for thirty seconds, plug it back in, and wait for the light to turn white." },
            new[] { "iPhone 17 or Pixel 10?", "<card>{\"title\": \"iPhone 17 vs Pixel 10\", \"columns\": [\"iPhone 17\", \"Pixel 10\"], \"rows\": [[\"Price\", \"$799\", \"$699\"], [\"Battery\", \"22 h\", \"24 h\"], [\"Camera\", \"48 MP\", \"50 MP\"], [\"Updates\", \"6 years\", \"7 years\"]]}</card>The Pixel is a hundred dollars cheaper with slightly better battery; the iPhone if you're already on a Mac." },
            new[] { "What's the weather in Amman?", "<card>{\"weather\": \"partly\", \"value\": \"18°C\", \"label\": \"Amman now\", \"note\": \"High 21°, low 12°\", \"days\": [[\"Thu\", \"sun\", \"23°\", \"13°\"], [\"Fri\", \"rain\", \"17°\", \"11°\"], [\"Sat\", \"storm\", \"15°\", \"9°\"], [\"Sun\", \"cloud\", \"19°\", \"10°\"]]}</card>It's eighteen and partly cloudy, with rain coming Friday." },
            new[] { "What's Bitcoin at?", "<card>{\"value\": \"$96,410\", \"label\": \"Bitcoin\", \"change\": \"+2.4%\"}</card>About ninety-six thousand." },
            new[] { "What's the weather in Amman?", "<card>{\"weather\": \"partly\", \"value\": \"18°C\", \"label\": \"Amman now\"}</card>Eighteen and partly cloudy." },
        };

        static NotchState DemoCard(int i)
        {
            int n = i / 40;
            var a = DemoCards[n];
            var st = new NotchState { Visible = true, Kind = "quick", Title = "Claude", QuickMinimized = n >= DemoCards.Length - 2 };
            if (n > 0 && n < 5) st.QuickHistory.Add(new QuickTurn { Heard = DemoCards[n - 1][0], Text = QuickVoice.Display(DemoCards[n - 1][1]), Card = QuickVoice.ParseCard(DemoCards[n - 1][1]) });
            st.QuickNow = new QuickTurn { Heard = a[0], Text = QuickVoice.Display(a[1]), Card = QuickVoice.ParseCard(a[1]) };
            if (st.QuickMinimized) st.Title = "Ready";
            return st;
        }

        // Quick voice in the demo: a question answered with a value card, then a follow-up
        // answered with a list (the answers stream in, card first, like Claude sends them), then
        // another question with the conversation minimized to the small bar.
        static NotchState DemoQuick(int i)
        {
            if (i >= 200)
            {
                const string q3 = "And the Surface?";
                const string a3 = "<card>{\"value\": \"$1,099\", \"label\": \"Surface Laptop 7\"}</card>It's ten ninety-nine, the cheapest of the four.";
                int k = i - 200;
                var bar = new NotchState { Visible = true, Kind = "quick", QuickMinimized = true };
                if (k < 22) { bar.Listening = true; bar.Title = "Listening"; bar.QuickNow = new QuickTurn { Heard = q3.Substring(0, Math.Min(q3.Length, k)) }; }
                else if (k < 32) { bar.Busy = true; bar.Title = "Thinking…"; bar.QuickNow = new QuickTurn { Heard = q3 }; }
                else
                {
                    bar.Answering = bar.Busy = bar.Speaking = k < 70;
                    bar.Title = k < 70 ? "Speaking" : "Ready";
                    bar.QuickNow = new QuickTurn { Heard = q3, Text = QuickVoice.Display(a3), Card = QuickVoice.ParseCard(a3) };
                }
                return bar;
            }
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
                st.Answering = st.Busy = st.Speaking = true;
                st.Title = sofar.Length < a.Length ? "Speaking" : "Claude";
                st.QuickNow = new QuickTurn { Heard = q, Text = QuickVoice.Display(sofar), Card = QuickVoice.ParseCard(sofar) };
            }
            return st;
        }
    }
}
