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
    // Quick voice: push to talk, answered on the notch.
    //
    // The shortcut starts listening (Windows speech recognition). Hold it and let go when you're
    // done, or press it once and stop talking. What you said goes to a Claude Code session
    // running in the background (`claude -p` with stream-json in and out, kept warm between
    // questions so follow-ups have context), and its answer streams onto the notch and is read
    // aloud sentence by sentence. Pressing the shortcut again while it answers cuts it off and
    // listens again.
    //
    // The answers follow ~/.claude/skills/voice-replies/SKILL.md (written on first use, yours to
    // edit), passed as an appended system prompt so it always applies.
    class QuickVoice
    {
        public enum Phase { Idle, Listening, Thinking, Answering, Done, Error }

        public event Action Changed;

        public Phase State { get; private set; }
        public string Heard { get; private set; }   // what you said (final words so far, then the question)
        public string Partial { get; private set; } // the words being recognized right now
        public string Answer { get; private set; }
        public string Status { get; private set; }  // "Searching the web…", an error, …
        public bool Speaking { get; private set; }
        public DateTime ListenStarted { get; private set; }
        public DateTime FinishedAt { get; private set; }

        static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(400); // held this long: release ends it
        static readonly TimeSpan EndSilence = TimeSpan.FromMilliseconds(1400);   // press-and-talk: this much quiet ends it
        static readonly TimeSpan NothingHeard = TimeSpan.FromSeconds(6);
        static readonly TimeSpan MaxListen = TimeSpan.FromSeconds(40);
        static readonly TimeSpan Linger = TimeSpan.FromSeconds(20);           // the answer stays this long after it is read
        static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(15);

        readonly Settings settings;
        readonly Dispatcher ui;
        readonly DispatcherTimer tick;
        SpeechRecognitionEngine recognizer;
        SpeechSynthesizer voice;
        Process claude;
        Stream claudeIn;
        readonly object gate = new object();
        volatile int ignoreTurns; // answers still coming for questions you cut off
        bool held, holdDecided, stopping;
        DateTime lastSpeech, lastUsed;
        readonly StringBuilder unspoken = new StringBuilder();
        readonly List<string> heardParts = new List<string>();

        public QuickVoice(Settings settings)
        {
            this.settings = settings;
            ui = Dispatcher.CurrentDispatcher;
            Heard = Partial = Answer = Status = "";
            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            tick.Tick += (s, e) => OnTick();
        }

        public bool Active { get { return State != Phase.Idle; } }

        // ---- the shortcut --------------------------------------------------

        public void Press()
        {
            if (State == Phase.Listening) { FinishListening(); return; } // second press: that's all
            StopSpeaking();
            if (State == Phase.Thinking || State == Phase.Answering) Interrupt();
            StartListening();
        }

        // × on the notch: stop talking and go away.
        public void Dismiss()
        {
            if (State == Phase.Listening) { stopping = true; StopRecognizer(); }
            StopSpeaking();
            if (State == Phase.Thinking || State == Phase.Answering) Interrupt();
            State = Phase.Idle;
            tick.Stop();
            Raise();
        }

        public void Quit()
        {
            StopSpeaking();
            StopRecognizer();
            KillClaude();
        }

        // ---- listening -----------------------------------------------------

        void StartListening()
        {
            if (!VoiceLauncher.HasMicrophone()) { Fail("No microphone found."); return; }
            try { EnsureRecognizer(); }
            catch (Exception e) { Log.Write("Quick voice: speech recognition: " + e.Message); Fail("Windows speech recognition isn't available."); return; }

            heardParts.Clear();
            Heard = Partial = Answer = Status = "";
            held = false; holdDecided = false; stopping = false;
            ListenStarted = lastSpeech = DateTime.Now;
            State = Phase.Listening;
            try { recognizer.RecognizeAsync(RecognizeMode.Multiple); }
            catch (InvalidOperationException) { } // still running from last time
            catch (Exception e) { Log.Write("Quick voice: " + e.Message); Fail("Couldn't use the microphone."); return; }
            ThreadPool.QueueUserWorkItem(_ => { try { EnsureClaude(); } catch (Exception e) { Log.Write("Quick voice: " + e.Message); } }); // warm up while you talk
            tick.Start();
            Raise();
        }

        void EnsureRecognizer()
        {
            if (recognizer != null) return;
            RecognizerInfo info = null;
            foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (r.Culture.Equals(CultureInfo.CurrentUICulture)) { info = r; break; }
                if (info == null || r.Culture.Name == "en-US") info = r;
            }
            if (info == null) throw new InvalidOperationException("no recognizer installed");
            var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechHypothesized += (s, e) => ui.BeginInvoke(new Action(() =>
            {
                if (State != Phase.Listening) return;
                Partial = e.Result.Text;
                lastSpeech = DateTime.Now;
                Raise();
            }));
            engine.SpeechRecognized += (s, e) => ui.BeginInvoke(new Action(() =>
            {
                if (State != Phase.Listening) return;
                heardParts.Add(e.Result.Text);
                Heard = string.Join(" ", heardParts.ToArray());
                Partial = "";
                lastSpeech = DateTime.Now;
                Raise();
            }));
            engine.SpeechDetected += (s, e) => ui.BeginInvoke(new Action(() => { lastSpeech = DateTime.Now; }));
            engine.RecognizeCompleted += (s, e) => ui.BeginInvoke(new Action(OnRecognizeCompleted));
            recognizer = engine;
        }

        void OnTick()
        {
            var now = DateTime.Now;
            if (State == Phase.Listening && !stopping)
            {
                bool any = heardParts.Count > 0 || Partial != "";
                if (!holdDecided && now - ListenStarted >= HoldThreshold) { holdDecided = true; held = Hotkeys.Held; }
                if (held && !Hotkeys.Held) FinishListening();              // let go of the key
                else if (!held && holdDecided && any && now - lastSpeech > EndSilence) FinishListening();
                else if (!any && now - ListenStarted > NothingHeard && !Hotkeys.Held) FinishListening();
                else if (now - ListenStarted > MaxListen) FinishListening();
            }
            if (State == Phase.Done && !Speaking && now - FinishedAt > Linger) { State = Phase.Idle; Raise(); }
            if (State == Phase.Error && now - FinishedAt > TimeSpan.FromSeconds(8)) { State = Phase.Idle; Raise(); }
            if (State == Phase.Idle) tick.Stop();
        }

        void FinishListening()
        {
            if (stopping) return;
            stopping = true;
            // Stopping lets the words being recognized finish; RecognizeCompleted follows.
            try { recognizer.RecognizeAsyncStop(); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (s, e) => { t.Stop(); if (State == Phase.Listening) { StopRecognizer(); OnRecognizeCompleted(); } };
            t.Start();
        }

        void OnRecognizeCompleted()
        {
            if (State != Phase.Listening || !stopping) return;
            string text = string.Join(" ", heardParts.ToArray()).Trim();
            if (text == "") text = Partial.Trim();
            Partial = "";
            if (text == "") { Fail("Didn't catch that."); return; }
            Heard = text;
            Ask(text);
        }

        void StopRecognizer()
        {
            try { if (recognizer != null) recognizer.RecognizeAsyncCancel(); } catch { }
        }

        // ---- asking Claude -------------------------------------------------

        void Ask(string text)
        {
            State = Phase.Thinking;
            Status = "Thinking…";
            Answer = "";
            lock (unspoken) { unspoken.Length = 0; sourcesReached = false; }
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
                    var msg = new Dictionary<string, object>
                    {
                        { "type", "user" },
                        { "message", new Dictionary<string, object> { { "role", "user" }, { "content", text } } },
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
                if (claude != null && !claude.HasExited && DateTime.Now - lastUsed < IdleExit) return;
                KillClaude();
                string exe = FindClaude();
                if (exe == null) throw new FileNotFoundException("claude.exe");
                string folder = Path.Combine(Settings.DataDir, "voice");
                Directory.CreateDirectory(folder);
                var args = new List<string>
                {
                    "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                    "--include-partial-messages", "--verbose",
                    "--model", settings.QuickModel,
                    "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", // nothing else, so nothing to ask about
                    "--setting-sources", "",          // no hooks or plugins: it isn't one of your sessions
                    "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence",
                    "--append-system-prompt", VoiceInstructions(),
                };
                var psi = new ProcessStartInfo(exe, JoinArgs(args))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                var p = Process.Start(psi);
                claude = p;
                claudeIn = p.StandardInput.BaseStream;
                ignoreTurns = 0;
                lastUsed = DateTime.Now;
                var reader = new Thread(() => ReadOutput(p)) { IsBackground = true, Name = "Quick voice output" };
                reader.Start();
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && Log.Verbose) Log.Write("Quick voice stderr: " + e.Data); };
                p.BeginErrorReadLine();
            }
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
                        ui.BeginInvoke(new Action(() => OnStatus(name == "WebFetch" || name == "web_fetch" ? "Reading a page…" : "Searching the web…")));
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

        void OnStatus(string status)
        {
            if (ignoreTurns > 0 || (State != Phase.Thinking && State != Phase.Answering)) return;
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
            lock (unspoken) unspoken.Append(t);
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
            if (Answer == "" && text != "") { Answer = text; lock (unspoken) unspoken.Append(text); }
            SpeakFinishedSentences(true);
            State = Phase.Done;
            Status = "";
            FinishedAt = DateTime.Now;
            tick.Start();
            Raise();
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

        // ---- speaking ------------------------------------------------------

        static readonly Regex SentenceEnd = new Regex(@"[.!?…](?=\s)|\n", RegexOptions.Compiled);
        static readonly Regex SourcesStart = new Regex(@"(^|\n)[\s*#_]*Sources?\s*[:*]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        bool sourcesReached;

        // The answer without the sources list a web search adds at the end.
        public static string WithoutSources(string s)
        {
            var m = SourcesStart.Match(s ?? "");
            return m.Success ? s.Substring(0, m.Index).TrimEnd() : s;
        }

        void SpeakFinishedSentences(bool all)
        {
            if (!settings.QuickSpeak) return;
            string chunk;
            lock (unspoken)
            {
                string s = unspoken.ToString();
                int cut = all ? s.Length : -1;
                if (!all) { var m = SentenceEnd.Match(s); Match last = null; while (m.Success) { last = m; m = m.NextMatch(); } if (last != null) cut = last.Index + last.Length; }
                if (cut <= 0) return;
                chunk = s.Substring(0, cut);
                unspoken.Remove(0, cut);
                if (sourcesReached) return;
                // Web search answers end with a list of sources: skip it, it's links.
                var src = SourcesStart.Match(chunk);
                if (src.Success) { chunk = chunk.Substring(0, src.Index); sourcesReached = true; }
            }
            chunk = Speakable(chunk);
            if (chunk == "") return;
            try
            {
                if (voice == null)
                {
                    voice = new SpeechSynthesizer();
                    voice.SetOutputToDefaultAudioDevice();
                    voice.SpeakStarted += (s, e) => ui.BeginInvoke(new Action(() => { Speaking = true; Raise(); }));
                    voice.StateChanged += (s, e) => ui.BeginInvoke(new Action(() =>
                    {
                        bool now = voice.State == SynthesizerState.Speaking;
                        if (now == Speaking) return;
                        Speaking = now;
                        if (!now && State == Phase.Done) FinishedAt = DateTime.Now; // linger from the last word
                        Raise();
                    }));
                }
                if (!string.IsNullOrEmpty(settings.QuickVoiceName)) { try { voice.SelectVoice(settings.QuickVoiceName); } catch { } }
                voice.Rate = settings.QuickRate;
                voice.SpeakAsync(chunk);
                Speaking = true;
            }
            catch (Exception e) { Log.Write("Quick voice: speech: " + e.Message); }
        }

        void StopSpeaking()
        {
            lock (unspoken) unspoken.Length = 0;
            try { if (voice != null) voice.SpeakAsyncCancelAll(); } catch { }
            Speaking = false;
        }

        // Markdown and links read badly aloud, even though the instructions ask for none.
        public static string Speakable(string s)
        {
            s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [text](link) -> text
            s = Regex.Replace(s, @"https?://\S+", "");
            s = Regex.Replace(s, @"```[\s\S]*?```", " ");
            s = Regex.Replace(s, @"(?m)^\s*(#+|[-*•]|\d+[.)])\s+", "");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "").Replace("*", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string[] InstalledVoices()
        {
            try
            {
                using (var s = new SpeechSynthesizer())
                {
                    var names = new List<string>();
                    foreach (var v in s.GetInstalledVoices()) if (v.Enabled) names.Add(v.VoiceInfo.Name);
                    return names.ToArray();
                }
            }
            catch { return new string[0]; }
        }

        void Raise()
        {
            if (Changed != null) Changed();
        }

        // ---- the instructions ----------------------------------------------

        public static string SkillFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "voice-replies", "SKILL.md"); }
        }

        // Writes the voice-replies skill if it isn't there yet (never over your edits).
        public static void EnsureSkill()
        {
            try
            {
                if (File.Exists(SkillFile)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(SkillFile));
                File.WriteAllText(SkillFile, DefaultSkill, new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Write("Quick voice: couldn't write " + SkillFile + ": " + e.Message); }
        }

        static string VoiceInstructions()
        {
            EnsureSkill();
            string text = DefaultSkill;
            try { if (File.Exists(SkillFile)) text = File.ReadAllText(SkillFile, Encoding.UTF8); } catch { }
            // Drop the frontmatter: only the rules matter here.
            var m = Regex.Match(text, @"^\s*---[\s\S]*?\n---\s*\n");
            if (m.Success) text = text.Substring(m.Length);
            return "You are answering by voice, through Claude Notch. Your reply is shown on a small notch and read aloud. Today is "
                + DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) + ".\n\n" + text.Trim();
        }

        const string DefaultSkill =
@"---
name: voice-replies
description: How to answer when the reply will be read aloud (voice assistants, dictation, Claude Notch quick voice). Use whenever an answer will be spoken rather than read.
---

# Voice replies

Your answer is spoken aloud by a text-to-speech voice and shown in a small box. Write the way a
sharp, friendly assistant talks.

- Lead with the answer itself. No preamble, no restating the question, no ""Great question"".
- Keep it to one to three short sentences, about 40 words. Go up to about 100 words only when
  asked for detail, steps, or an explanation.
- Plain spoken sentences only: no markdown, headings, bullet points, tables, code blocks, emoji,
  or URLs. Never spell out symbols.
- Say numbers, dates, times, and units the way people say them: ""about three and a half
  kilometers"", ""quarter past nine"", ""twenty-two degrees"".
- For a list, give at most three items in one sentence, and offer the rest (""want more?"").
- If the request is ambiguous, pick the most likely meaning and answer it; mention the
  assumption in a few words instead of asking back.
- For anything current (news, weather, prices, scores, opening hours), search the web, then give
  the result, not the process. Never say ""let me search"" or narrate what you're doing, and
  don't add a list of sources or links (say where it's from in a few words if it matters).
- If you can't do something from here (it needs files, apps, or the screen), say so in one
  sentence and suggest the quickest way to do it.
- Sound natural and warm, a little personality is fine, but stay brief.
";

        // ---- finding claude ------------------------------------------------

        static string FindClaude()
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
        static string JoinArgs(List<string> args)
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
