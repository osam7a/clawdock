using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Clawdock
{
    // The local channel between Claude Code's hooks (and later launches of the exe) and the
    // running notch.
    //
    // A named pipe that only the current Windows user can open (network clients are refused
    // outright). Each connection sends two length-prefixed UTF-8 frames: a kind, then a body.
    //   "hook"       a hook event; the hook process exits right away
    //   "hook-wait"  a PermissionRequest; the hook process waits for one reply frame, which it
    //                prints as the hook's output (allow / deny), or "{}" for no decision
    //   "cmd"        "settings" or "quit", from a second launch of the exe
    class HookServer
    {
        public const string MutexName = "Local\\Clawdock";
        public static readonly string PipeName = "Clawdock-" + WindowsIdentity.GetCurrent().User.Value;
        const int MaxFrame = 8 * 1024 * 1024;

        public const string Allow = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}";
        public const string Deny = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from Clawdock.\"}}}";
        public const string NoDecision = "{}";
        // Quick voice approves its tools before they run (its background session has no prompt to answer).
        public const string PreAllow = "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\",\"permissionDecisionReason\":\"Allowed from Clawdock.\"}}";
        public const string PreDeny = "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"Denied from Clawdock.\"}}";

        // Raised on the UI thread. Pending is set for a request that is waiting for an answer.
        public event Action<Dictionary<string, object>, Pending> HookReceived;
        public event Action<string> CommandReceived;
        public event Action<Pending> PendingGone;

        Dispatcher ui;

        public class Pending
        {
            static int nextId;
            public readonly int Id = Interlocked.Increment(ref nextId);
            public string SessionId, Tool, Detail, FullDetail, Cwd;
            public string AllowReply = Allow, DenyReply = Deny;
            public Action<bool> Decided; // a request of the notch's own, with no hook waiting
            public DateTime At = DateTime.Now;
            internal NamedPipeServerStream Pipe;
            int settled;

            public bool Settled { get { return settled != 0; } }

            // Sends the answer back to the waiting hook. Only the first call counts.
            public void Respond(string json)
            {
                if (Interlocked.Exchange(ref settled, 1) == 1) return;
                var pipe = Pipe;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { WriteFrame(pipe, json); pipe.Flush(); pipe.WaitForPipeDrain(); }
                    catch { }
                    finally { try { pipe.Dispose(); } catch { } }
                });
            }

            internal bool MarkGone() { return Interlocked.Exchange(ref settled, 1) == 0; }
        }

        public static bool IsRunning()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
            m.Dispose();
            return true;
        }

        public void Start(Dispatcher dispatcher)
        {
            ui = dispatcher;
            new Thread(AcceptLoop) { IsBackground = true, Name = "hook pipe" }.Start();
        }

        static PipeSecurity Security()
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        void AcceptLoop()
        {
            var security = Security();
            while (true)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
                    pipe.WaitForConnection();
                    var conn = pipe;
                    pipe = null;
                    ThreadPool.QueueUserWorkItem(_ => Serve(conn));
                }
                catch (Exception e)
                {
                    if (pipe != null) pipe.Dispose();
                    Log.Write("Hook pipe: " + e.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        void Serve(NamedPipeServerStream pipe)
        {
            bool keepOpen = false;
            try
            {
                string kind = ReadFrame(pipe);
                string body = ReadFrame(pipe);
                if (kind == null || body == null) return;

                if (kind == "cmd")
                {
                    ui.BeginInvoke(new Action(() => { if (CommandReceived != null) CommandReceived(body); }));
                    return;
                }
                if (kind != "hook" && kind != "hook-wait") return;

                Dictionary<string, object> d;
                try { d = Json.ParseObject(body); } catch { d = null; }
                if (d == null) { if (kind == "hook-wait") WriteFrame(pipe, NoDecision); return; }

                Pending pending = null;
                if (kind == "hook-wait")
                {
                    pending = new Pending
                    {
                        Pipe = pipe,
                        SessionId = Json.Str(d, "session_id"),
                        Tool = Json.Str(d, "tool_name"),
                        Cwd = Json.Str(d, "cwd"),
                    };
                    keepOpen = true;
                    WatchForHangUp(pending);
                }
                ui.BeginInvoke(new Action(() =>
                {
                    if (HookReceived != null) HookReceived(d, pending);
                    else if (pending != null) pending.Respond(NoDecision);
                }));
            }
            catch { }
            finally { if (!keepOpen) try { pipe.Dispose(); } catch { } }
        }

        // The hook goes away without an answer when Claude Code moves on (the prompt was
        // answered in Claude, the turn was interrupted, or the hook timed out).
        void WatchForHangUp(Pending pending)
        {
            var buf = new byte[1];
            try
            {
                pending.Pipe.BeginRead(buf, 0, 1, ar =>
                {
                    int n = 0;
                    try { n = pending.Pipe.EndRead(ar); } catch { }
                    if (n == 0 && pending.MarkGone())
                    {
                        try { pending.Pipe.Dispose(); } catch { }
                        ui.BeginInvoke(new Action(() => { if (PendingGone != null) PendingGone(pending); }));
                    }
                }, null);
            }
            catch { }
        }

        // ---- client side ---------------------------------------------------

        // `Clawdock.exe --hook`: forwards the hook JSON on stdin to the notch. For a
        // PermissionRequest it waits for the notch's answer and prints it. `--voice-hook` (quick voice's
        // PreToolUse) always waits. If the notch isn't
        // running, it prints nothing, so Claude Code behaves as if there were no hook.
        public static void RunHookClient(bool alwaysWait)
        {
            try
            {
                string input = ReadStdin(MaxFrame);
                if (input == null) return;
                bool wait = alwaysWait || input.IndexOf("\"PermissionRequest\"", StringComparison.Ordinal) >= 0;

                using (var pipe = Connect(500))
                {
                    if (Log.Verbose) Log.Write("Hook relay: connected=" + (pipe != null) + " wait=" + wait + " bytes=" + input.Length);
                    if (pipe == null) return;
                    WriteFrame(pipe, wait ? "hook-wait" : "hook");
                    WriteFrame(pipe, input);
                    pipe.Flush();
                    if (!wait) return;

                    string reply = ReadFrame(pipe);
                    if (Log.Verbose) Log.Write("Hook relay reply: " + (reply ?? "(none)"));
                    if (!string.IsNullOrEmpty(reply) && reply != NoDecision)
                    {
                        var stdout = Console.OpenStandardOutput();
                        var bytes = new UTF8Encoding(false).GetBytes(reply);
                        stdout.Write(bytes, 0, bytes.Length);
                        stdout.Flush();
                    }
                }
            }
            catch (Exception e) { Log.Write("Hook relay: " + e.Message); }
        }

        public static bool SendCommand(string command)
        {
            try
            {
                using (var pipe = Connect(1000))
                {
                    if (pipe == null) return false;
                    WriteFrame(pipe, "cmd");
                    WriteFrame(pipe, command);
                    pipe.Flush();
                    return true;
                }
            }
            catch { return false; }
        }

        // Connects and checks that the other end really is Clawdock (not some other
        // program that took the pipe name first) before sending it anything.
        static NamedPipeClientStream Connect(int timeoutMs)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(timeoutMs);
                uint pid;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)) throw new IOException();
                using (var server = Process.GetProcessById((int)pid))
                    if (!string.Equals(server.ProcessName, "Clawdock", StringComparison.OrdinalIgnoreCase)) throw new IOException();
                return pipe;
            }
            catch (Exception e)
            {
                if (!(e is TimeoutException)) Log.Write("Pipe connect: " + e.GetType().Name + " " + e.Message);
                pipe.Dispose();
                return null;
            }
        }

        static string ReadStdin(int limit)
        {
            var stdin = Console.OpenStandardInput();
            var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > limit) return null; // too big to be worth showing
                ms.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿'); // some callers add a byte-order mark
        }

        static void WriteFrame(Stream s, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            s.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            s.Write(bytes, 0, bytes.Length);
        }

        static string ReadFrame(Stream s)
        {
            var len = ReadExactly(s, 4);
            if (len == null) return null;
            int n = BitConverter.ToInt32(len, 0);
            if (n < 0 || n > MaxFrame) return null;
            var body = ReadExactly(s, n);
            return body == null ? null : Encoding.UTF8.GetString(body);
        }

        static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return null;
                got += r;
            }
            return buf;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    }
}
