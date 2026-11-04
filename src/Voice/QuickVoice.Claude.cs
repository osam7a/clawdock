using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // Quick voice: the background Claude Code session it asks.
    partial class QuickVoice
    {
        // ---- asking Claude -------------------------------------------------

        void Ask(string text)
        {
            State = Phase.Thinking;
            Status = "Thinking…";
            Answer = "";
            bool typed = typedTurn;
            typedTurn = false;
            ThinkingMood = MascotMood.Thinking;
            var live = Snapshot();
            string told = tellNext;
            tellNext = null;
            lock (unspoken) { unspoken.Length = 0; sourcesReached = false; fed = 0; hushed = typed; }
            lastUsed = DateTime.Now;
            Raise();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureClaude();
                    // An answer you cut off still ends with a result; wait for it so this one isn't mistaken
                    // for it. (If the interrupt came after that answer had finished, nothing more arrives.)
                    for (int waited = 0; ignoreTurns > 0 && waited < 3000; waited += 50) Thread.Sleep(50);
                    ignoreTurns = 0;
                    SessionArchive.RefreshSoon(Folder);
                    try { SessionBrief.Write(live, Folder); }
                    catch (Exception e) { Log.Write("Session brief: " + e.Message); }
                    // The card rules are in the system prompt, but with all your skills and
                    // connectors loaded they get lost in it, so each question carries a reminder.
                    var content = new List<object>
                    {
                        new Dictionary<string, object> { { "type", "text" }, { "text", text } },
                        new Dictionary<string, object> { { "type", "text" }, { "text", Reminder() } },
                    };
                    if (typed) content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", TypedReminder } });
                    if (told != null) content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", "<system-reminder>" + told + "</system-reminder>" } });
                    var msg = new Dictionary<string, object>
                    {
                        { "type", "user" },
                        { "message", new Dictionary<string, object> { { "role", "user" }, { "content", content } } },
                    };
                    Send(msg);
                }
                catch (Exception e)
                {
                    Log.Write("Quick voice: " + e.Message);
                    ui.BeginInvoke(new Action(() => Fail(e is FileNotFoundException ? "Claude Code isn't installed." : "Couldn't start Claude Code.")));
                }
            });
        }

        List<SessionBrief.Entry> Snapshot()
        {
            if (LiveSessions == null) return new List<SessionBrief.Entry>();
            return ui.CheckAccess() ? LiveSessions() : (List<SessionBrief.Entry>)ui.Invoke(LiveSessions);
        }

        // Stops the answer in progress; whatever it still sends is ignored.
        void Interrupt()
        {
            lock (gate)
            {
                if (claude == null || claude.HasExited) return;
                ignoreTurns++;
                try
                {
                    Send(new Dictionary<string, object>
                    {
                        { "type", "control_request" },
                        { "request_id", "notch-" + Guid.NewGuid().ToString("N") },
                        { "request", new Dictionary<string, object> { { "subtype", "interrupt" } } },
                    });
                }
                catch { }
            }
        }

        void Send(Dictionary<string, object> msg)
        {
            string line = Json.Write(msg).Replace("\n", "") + "\n"; // one message per line
            var bytes = new UTF8Encoding(false).GetBytes(line);
            lock (gate)
            {
                if (claudeIn == null) throw new IOException("Claude Code isn't running");
                claudeIn.Write(bytes, 0, bytes.Length);
                claudeIn.Flush();
            }
        }

        void EnsureClaude()
        {
            lock (gate)
            {
                string config = settings.QuickModel + "|" + settings.QuickAccess + "|" + settings.QuickHistory;
                if (claude != null && !claude.HasExited && DateTime.Now - lastUsed < IdleExit && config == runningConfig) return;
                KillClaude();
                // A private conversation starts over, so what it said before is gone from the notch too.
                if (settings.QuickHistory != "history" && runningConfig != null)
                    ui.BeginInvoke(new Action(() => { history.Clear(); Raise(); }));
                string exe = FindClaude();
                if (exe == null) throw new FileNotFoundException("claude.exe");
                string folder = Folder;
                Directory.CreateDirectory(folder);
                var args = new List<string>
                {
                    "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                    "--include-partial-messages", "--verbose",
                    // "haiku" can mean a model too old for auto mode, and then nothing would be allowed to run.
                    "--model", settings.QuickAccess == "auto" && settings.QuickModel == "haiku" ? "sonnet" : settings.QuickModel,
                };
                if (settings.QuickAccess == "full")
                {
                    // Like a normal session: your settings, skills, plugins and connectors. Tools that
                    // act (not just look things up) wait for Allow on the notch.
                    args.AddRange(new[] { "--setting-sources", "user", "--settings", ApprovalHookFile() });
                }
                else if (settings.QuickAccess == "auto")
                {
                    // The same, but Claude Code's safety checks decide. What they block doesn't run.
                    args.AddRange(new[] { "--setting-sources", "user", "--permission-mode", "auto" });
                }
                else
                {
                    args.AddRange(new[]
                    {
                        "--tools", "WebSearch,WebFetch,Read,Grep", // only in this folder, for the session files, so nothing to ask about
                        "--allowedTools", "WebSearch,WebFetch,Read(./" + SessionBrief.FileName + "),Read(./session-*.md),Read(./" + SessionArchive.IndexFile + "),Read(./" + SessionArchive.DigestFolder + "/**)",
                        "--setting-sources", "",          // no hooks or plugins: it isn't one of your sessions
                        "--strict-mcp-config", "--disable-slash-commands",
                    });
                }
                if (settings.QuickHistory == "history")
                {
                    // One conversation, VOICE_CHATS, picked up again each time.
                    string id = HistorySession();
                    if (id != null) args.AddRange(new[] { "--resume", id });
                    else
                    {
                        id = Guid.NewGuid().ToString();
                        try { File.WriteAllText(HistoryIdFile, id); } catch { }
                        args.AddRange(new[] { "--session-id", id, "-n", "VOICE_CHATS" });
                    }
                }
                else args.Add("--no-session-persistence");
                args.AddRange(new[] { "--append-system-prompt", VoiceInstructions(settings.QuickAccess != "web") });
                runningConfig = config;
                var psi = new ProcessStartInfo(exe, JoinArgs(args))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                var p = Process.Start(psi);
                claude = p;
                instructionsSent = settings.QuickHistory != "history"; // a new conversation has them in its system prompt
                claudeIn = p.StandardInput.BaseStream;
                ignoreTurns = 0;
                lastUsed = DateTime.Now;
                var reader = new Thread(() => ReadOutput(p)) { IsBackground = true, Name = "Quick voice output" };
                reader.Start();
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && Log.Verbose) Log.Write("Quick voice stderr: " + e.Data); };
                p.BeginErrorReadLine();
            }
        }

        static string HistoryIdFile { get { return Path.Combine(Folder, "VOICE_CHATS.id"); } }

        // The VOICE_CHATS session to resume, if it's still in Claude Code's history.
        static string HistorySession()
        {
            try
            {
                if (!File.Exists(HistoryIdFile)) return null;
                string id = File.ReadAllText(HistoryIdFile).Trim();
                Guid g;
                if (!Guid.TryParse(id, out g)) return null;
                string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                return Directory.Exists(projects) && Directory.GetFiles(projects, id + ".jsonl", SearchOption.AllDirectories).Length > 0 ? id : null;
            }
            catch { return null; }
        }

        // A settings file adding one hook: before each tool runs, ask the notch.
        string ApprovalHookFile()
        {
            string exe = string.IsNullOrEmpty(HookExe) ? Installer.CurrentExe : HookExe;
            var hook = new Dictionary<string, object> { { "type", "command" }, { "command", "\"" + exe.Replace('\\', '/') + "\" --voice-hook" }, { "timeout", 600 } };
            var root = new Dictionary<string, object>
            {
                { "hooks", new Dictionary<string, object> { { "PreToolUse", new List<object> { new Dictionary<string, object> { { "matcher", "*" }, { "hooks", new List<object> { hook } } } } } } },
            };
            string file = Path.Combine(Folder, "notch-approval.json");
            File.WriteAllText(file, Json.Write(root), new UTF8Encoding(false));
            return file;
        }

        void KillClaude()
        {
            lock (gate)
            {
                if (claude == null) return;
                try { if (!claude.HasExited) claude.Kill(); } catch { }
                try { claude.Dispose(); } catch { }
                claude = null;
                claudeIn = null;
            }
        }

        void ReadOutput(Process p)
        {
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> d;
                    try { d = Json.ParseObject(line); } catch { continue; }
                    if (d != null) OnMessage(d);
                }
            }
            catch (Exception e) { Log.Write("Quick voice: " + e.Message); }
            ui.BeginInvoke(new Action(() =>
            {
                if (claude != p) return; // replaced on purpose
                lock (gate) { claude = null; claudeIn = null; }
                if (State == Phase.Thinking || State == Phase.Answering) Fail("Claude Code stopped. Try again.");
            }));
        }

        // Runs on the reader thread.
        void OnMessage(Dictionary<string, object> d)
        {
            string type = Json.Str(d, "type");
            if (type == "stream_event")
            {
                var ev = Json.Obj(d, "event");
                string evType = Json.Str(ev, "type");
                if (evType == "content_block_delta")
                {
                    var delta = Json.Obj(ev, "delta");
                    if (Json.Str(delta, "type") == "text_delta") { string t = Json.Str(delta, "text"); ui.BeginInvoke(new Action(() => OnText(t))); }
                }
                else if (evType == "content_block_start")
                {
                    string block = Json.Str(Json.Obj(ev, "content_block"), "type");
                    if (block == "tool_use" || block == "server_tool_use")
                    {
                        string name = Json.Str(Json.Obj(ev, "content_block"), "name");
                        ui.BeginInvoke(new Action(() => { ThinkingMood = SessionActivity.ForTool(name); OnStatus(name == "Read" && settings.QuickAccess == "web" ? "Checking your sessions…" : ToolStatus(name)); }));
                    }
                }
            }
            else if (type == "result")
            {
                bool error = Json.Str(d, "is_error") == "True" || Json.Str(d, "subtype") != "success";
                string text = Json.Str(d, "result");
                ui.BeginInvoke(new Action(() => OnResult(error, text)));
            }
        }

        static string ToolStatus(string tool)
        {
            if (tool == "WebFetch" || tool == "web_fetch") return "Reading a page…";
            if (tool == "WebSearch" || tool == "web_search") return "Searching the web…";
            if (tool == "Skill") return "Opening a skill…";
            if (tool == "Read" || tool == "Glob" || tool == "Grep" || tool == "LS") return "Reading your files…";
            if (tool != null && tool.StartsWith("mcp__")) return "Using a connector…";
            return "Working on it…";
        }

        void OnStatus(string status)
        {
            if (ignoreTurns > 0 || (State != Phase.Thinking && State != Phase.Answering)) return;
            if (Answer != "" && !Answer.EndsWith(" ")) Answer += " "; // more text may follow this tool
            Status = status;
            Raise();
        }

        void OnText(string t)
        {
            if (ignoreTurns > 0 || (State != Phase.Thinking && State != Phase.Answering)) return;
            if (State == Phase.Thinking && Answer != "" && !Answer.EndsWith(" ")) Answer += " "; // text after a search
            State = Phase.Answering;
            Status = "";
            Answer += t;
            Feed();
            SpeakFinishedSentences(false);
            Raise();
        }

        void OnResult(bool error, string text)
        {
            if (ignoreTurns > 0) { ignoreTurns--; return; }
            if (State != Phase.Thinking && State != Phase.Answering) return;
            lastUsed = DateTime.Now;
            if (error || text.IndexOf("Failed to authenticate", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("/login", StringComparison.Ordinal) >= 0)
            {
                bool auth = text.IndexOf("authenticat", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0;
                if (error || auth)
                {
                    Log.Write("Quick voice: " + (text.Length > 300 ? text.Substring(0, 300) : text));
                    Fail(auth ? "Sign in to Claude Code first: run  claude auth login" : "Claude couldn't answer. Try again.");
                    if (auth) KillClaude();
                    return;
                }
            }
            if (Answer == "" && text != "") Answer = text;
            Feed();
            SpeakFinishedSentences(true);
            State = Phase.Done;
            Status = "";
            FinishedAt = DateTime.Now;
            followUpDue = settings.QuickFollowUp && settings.QuickSpeak && !hushed && Display(Answer).TrimEnd('"', '\u201D', ' ').EndsWith("?");
            tick.Start();
            Raise();
            var ask = AskBlock.Match(Answer);
            if (ask.Success && SessionMessageWanted != null) SessionMessageWanted(ask.Groups[1].Value.Trim(), ask.Groups[2].Value.Trim());
            var project = ProjectBlock.Match(Answer);
            if (project.Success && NewProjectWanted != null)
            {
                var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in ProjectAttribute.Matches(project.Groups[1].Value)) given[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                string wantedName, wantedModel, wantedEffort;
                given.TryGetValue("name", out wantedName);
                given.TryGetValue("model", out wantedModel);
                given.TryGetValue("effort", out wantedEffort);
                NewProjectWanted(wantedName ?? "", wantedModel ?? "", wantedEffort ?? "", project.Groups[2].Value.Trim());
            }
        }

        void Fail(string message)
        {
            StopSpeaking();
            State = Phase.Error;
            Status = message;
            FinishedAt = DateTime.Now;
            tick.Start();
            Raise();
        }


        // ---- finding claude ------------------------------------------------

        public static string FindClaude()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            };
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim() != "") candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            foreach (string c in candidates) { try { if (File.Exists(c)) return c; } catch { } }
            return null;
        }

        // Windows command-line quoting (the rules CommandLineToArgvW and the C runtime follow).
        public static string JoinArgs(List<string> args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a != "" && a.IndexOfAny(new[] { ' ', '\t', '\n', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                    else { sb.Append('\\', slashes); sb.Append(c); }
                    slashes = 0;
                }
                sb.Append('\\', slashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
