using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace ClaudeNotch
{
    static class Program
    {
        const string PipeName = "ClaudeNotch";

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--hook") { RunHook(); return; }
            if (args.Length > 0 && args[0] == "--demo") { Demo(); return; }

            bool isNew;
            using (new Mutex(true, "Local\\ClaudeNotch", out isNew))
            {
                if (!isNew) return; // already running

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var notch = new NotchWindow();
                var watcher = new ClaudeWatcher();

                watcher.Changed += () => notch.Update(watcher.ClaudeOpen, watcher.ClaudeInFront, watcher.FullscreenApp,
                    watcher.InVoice, watcher.MicMuted, watcher.ClaudeMinimized, watcher.SessionTitle,
                    watcher.Working, watcher.WorkingSince);
                notch.OpenClaude += watcher.RestoreClaude;
                notch.ToggleMute += watcher.ToggleMute;
                notch.EndVoice += watcher.EndVoice;
                notch.Quit += app.Shutdown;

                new Thread(() => ListenForHooks(notch)) { IsBackground = true }.Start();
                CopilotKey.Install();
                watcher.Start();
                app.Run();
                CopilotKey.Uninstall();
            }
        }

        // `ClaudeNotch.exe --hook` is what the Claude Code hooks run. It forwards the
        // hook's JSON (on stdin) to the running notch and exits straight away with no
        // output, so Claude Code carries on with its normal behaviour.
        static void RunHook()
        {
            try
            {
                string input;
                using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) input = reader.ReadToEnd();
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(500);
                    var bytes = Encoding.UTF8.GetBytes(input);
                    pipe.Write(bytes, 0, bytes.Length);
                }
            }
            catch { } // notch not running: nothing to show
        }

        static void ListenForHooks(NotchWindow notch)
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                    {
                        server.WaitForConnection();
                        string json;
                        using (var reader = new StreamReader(server, Encoding.UTF8)) json = reader.ReadToEnd();
                        notch.Dispatcher.BeginInvoke(new Action(() => notch.Notify(json)));
                    }
                }
                catch { Thread.Sleep(500); }
            }
        }

        static string Hook(string ev, string extra)
        {
            return "{\"hook_event_name\":\"" + ev + "\",\"session_id\":\"demo\",\"cwd\":\"C:\\\\Users\\\\you\\\\Claude-Notch\""
                + (extra.Length == 0 ? "" : "," + extra) + "}";
        }

        static string Plan(string input)
        {
            return Hook("PostToolUse", "\"tool_name\":\"mcp__ccd_turn__update_plan\",\"tool_input\":{" + input + "}");
        }

        // Preview the notch without touching Claude (about 40 seconds):
        //   alt-tabbed with nothing going on (stays hidden) -> a session starts working with five
        //   tasks -> voice (with a mute) -> a permission request -> back in Claude -> the session
        //   finishes -> minimized -> hidden again.
        static void Demo()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow();
            const string title = "CoPilot key launcher";
            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                step++;
                // args: open, inFront, fullscreen, voice, muted, minimized, title
                if (step == 1) notch.Update(true, false, false, false, false, false, title);   // Alt+Tab, nothing going on: hidden
                if (step == 20) notch.Notify(Hook("UserPromptSubmit", "\"prompt\":\"go\""));
                if (step == 30) notch.Notify(Plan("\"items\":[{\"content\":\"Read\",\"status\":\"in_progress\"},{\"content\":\"Build\",\"status\":\"pending\"},{\"content\":\"Test\",\"status\":\"pending\"},{\"content\":\"Docs\",\"status\":\"pending\"},{\"content\":\"Commit\",\"status\":\"pending\"}]"));
                if (step == 70 || step == 100) notch.Notify(Plan("\"advance\":true"));
                if (step == 140) notch.Update(true, false, false, true, false, false, title);   // voice
                if (step == 175) notch.Update(true, false, false, true, true, false, title);    // muted
                if (step == 195) notch.Update(true, false, false, true, false, false, title);
                if (step == 220) notch.Update(true, false, false, false, false, false, title);  // voice ended
                if (step == 235) notch.Notify(Hook("PermissionRequest", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git push origin main\"}"));
                if (step == 275) notch.Update(true, true, false, false, false, false, title);   // back in Claude: the alert fades after 6s
                if (step == 300) notch.Update(true, false, false, false, false, false, title);  // away again
                if (step == 315) notch.Notify(Plan("\"advance\":true"));
                if (step == 330) notch.Notify(Hook("PostToolUse", "\"tool_name\":\"mcp__ccd_turn__wrap_up\",\"tool_input\":{}"));
                if (step == 335) notch.Notify(Hook("Stop", ""));                                // finished: "Done in ..."
                if (step == 375) notch.Update(true, false, false, false, false, true, title);   // minimized: stays
                if (step == 410) notch.Update(false, false, false, false, false, false, title);
                if (step == 420) app.Shutdown();
            };
            notch.OpenClaude += app.Shutdown;
            notch.Quit += app.Shutdown;
            timer.Start();
            app.Run();
        }
    }
}
