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

                watcher.Changed += () => notch.Update(watcher.ClaudeOpen, watcher.ClaudeInFront, watcher.FullscreenApp, watcher.InVoice);
                notch.OpenClaude += watcher.RestoreClaude;
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
        // output, so Claude Code carries on with its normal permission prompt.
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

        // Preview the notch without touching Claude: idle, voice, a permission
        // alert, then idle again before it slides away and exits.
        static void Demo()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var notch = new NotchWindow();
            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) =>
            {
                step++;
                if (step == 1) notch.Update(true, false, false, false);
                if (step == 50) notch.Update(true, false, false, true);
                if (step == 120) notch.Notify("{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\Users\\\\you\\\\Claude-Notch\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git push origin main\"}}");
                if (step == 200) notch.Update(true, true, false, false);  // "switch back to Claude": alert fades after 6s
                if (step == 270) notch.Update(true, false, false, false);
                if (step == 300) notch.Update(false, false, false, false);
                if (step == 310) app.Shutdown();
            };
            notch.OpenClaude += app.Shutdown;
            notch.Quit += app.Shutdown;
            timer.Start();
            app.Run();
        }
    }
}
