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
    // One question and its answer, as the notch shows them.
    class QuickTurn
    {
        public string Heard = "", Text = ""; // what you said; the answer without its card
        public QuickCard Card;
        public bool Cut;                      // you cut the answer off
        public List<string> Notes = new List<string>(); // what it asked permission for, and what came of it
    }

    // The key fact of an answer, shown big. Claude sends it as <card>{json}</card> before the
    // spoken answer. Kind is one of:
    //   value    one figure ("$1,249") with a label and a note; optionally a change ("+2.4%") and
    //            a progress bar (0 to 1)
    //   list     a few rows, name and value
    //   steps    numbered steps
    //   compare  two or three things side by side, one row per attribute
    //   weather  the temperature now with its condition, and a few days ahead
    class QuickCard
    {
        public string Kind = "value", Source = "", Title = "", Value = "", Label = "", Note = "", Change = "", Weather = "";
        public double Progress = -1; // -1: no bar
        public List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        public List<string> Steps = new List<string>();
        public List<string> Columns = new List<string>();
        public List<string[]> Rows = new List<string[]>();  // compare: [attribute, one value per column]
        public List<string[]> Days = new List<string[]>();  // weather: [day, condition, high, low]

        // Whether the change reads as up (true), down (false) or neither (null).
        public bool? ChangeUp
        {
            get
            {
                string c = Change.TrimStart();
                if (c == "") return null;
                if (c[0] == '+' || c[0] == '↑' || c[0] == '▲') return true;
                if (c[0] == '-' || c[0] == '−' || c[0] == '↓' || c[0] == '▼') return false;
                return null;
            }
        }
    }

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
        SpeechRecognitionEngine recognizer; // the old desktop engine, when modern dictation can't be used
        ModernDictation modern;
        bool listeningModern, modernBroken, privacyBlocked; // privacyBlocked: online speech recognition is off
        readonly object listenGate = new object();
        MicRecorder mic;                    // the built-in engine records, then transcribes
        bool listeningLocal, voiced, transcribing;
        int listenId;                       // which press a transcription belongs to
        string runningConfig;               // the settings the running Claude Code was started with
        SpeechSynthesizer voice;
        Process claude;
        Stream claudeIn;
        readonly object gate = new object();
        volatile int ignoreTurns; // answers still coming for questions you cut off
        bool held, holdDecided, stopping;
        DateTime lastSpeech, lastUsed;
        readonly StringBuilder unspoken = new StringBuilder();
        int fed;                            // how much of the answer's speakable text went to unspoken
        readonly List<string> heardParts = new List<string>();
        readonly List<QuickTurn> history = new List<QuickTurn>(); // earlier turns of this conversation

        public QuickVoice(Settings settings)
        {
            this.settings = settings;
            ui = Dispatcher.CurrentDispatcher;
            Heard = Partial = Answer = Status = "";
            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            tick.Tick += (s, e) => OnTick();
            if (settings.QuickEngine == "local") MicRecorder.WarmUp();
        }

        public bool Active { get { return State != Phase.Idle; } }
        public double Level { get; private set; }   // microphone loudness, 0..1 (built-in engine)
        // How loud you are while it listens, 0..1; -1 when it can't be measured (Windows recognition).
        public double InputLevel { get { return State != Phase.Listening ? 0 : listeningLocal ? Level : -1; } }
        public bool MicReady { get; private set; }  // audio is coming in (the microphone takes a moment to start)
        public string HookExe { get; set; }         // ClaudeNotch.exe, for approving tools from the notch
        public Func<bool> KeepOpen { get; set; }    // the answer stays while this is true (you're reading it)
        public Func<bool> Persist { get; set; }     // and doesn't time out at all while this is true (minimized)

        public List<QuickTurn> History { get { return new List<QuickTurn>(history); } }

        // The turn in progress (or just answered).
        public QuickTurn Current
        {
            get
            {
                string heard = State == Phase.Listening ? (Heard + " " + Partial).Trim() : Heard;
                return new QuickTurn { Heard = heard, Text = Display(Answer), Card = ParseCard(Answer), Notes = new List<string>(turnNotes) };
            }
        }

        // Claude is using this, or you're talking to it, rather than an answer just sitting there.
        public bool InUse { get { return Speaking || State == Phase.Listening || State == Phase.Thinking || State == Phase.Answering; } }

        List<string> turnNotes = new List<string>();

        // What happened to a permission request: it stays in this conversation's chat.
        public void AddNote(string note)
        {
            if (Heard != "" && State != Phase.Listening) turnNotes.Add(note);
            else if (history.Count > 0) history[history.Count - 1].Notes.Add(note);
            Raise();
        }

        public static readonly string Folder = Path.Combine(Settings.DataDir, "voice");

        // A hook event from quick voice's own Claude Code session (not one of your sessions).
        public static bool IsVoiceFolder(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return false;
            return cwd.Replace('/', '\\').TrimEnd('\\').StartsWith(Folder, StringComparison.OrdinalIgnoreCase);
        }

        // Tools that only look things up go ahead without asking; anything that acts asks on the notch.
        public static bool SafeTool(string tool)
        {
            switch (tool)
            {
                case "WebSearch": case "WebFetch": case "Read": case "Glob": case "Grep": case "LS":
                case "Skill": case "ToolSearch": case "TodoWrite": case "TaskCreate": case "TaskUpdate": case "TaskList": case "TaskGet":
                    return true;
            }
            if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return false;
            string action = tool.Substring(tool.LastIndexOf("__", StringComparison.Ordinal) + 2).ToLowerInvariant();
            foreach (string verb in new[] { "get", "list", "search", "read", "query", "fetch", "find", "describe", "lookup", "show" })
                if (action.StartsWith(verb)) return true;
            return false;
        }

        // A tool is waiting for Allow / Deny on the notch.
        public void ApprovalNeeded()
        {
            if (!settings.QuickSpeak || (State != Phase.Thinking && State != Phase.Answering)) return;
            lock (unspoken) unspoken.Append("\nI need your OK for that, on the notch.\n");
            SpeakFinishedSentences(false);
        }

        // ---- the shortcut --------------------------------------------------

        public void Press()
        {
            if (State == Phase.Listening) { FinishListening(); return; } // second press: that's all
            StopSpeaking();
            bool cut = (State == Phase.Thinking && !transcribing) || State == Phase.Answering;
            if (cut) Interrupt();
            SaveTurn(cut);
            StartListening();
        }

        // The answer on screen moves up into the history when you ask the next thing.
        void SaveTurn(bool cut)
        {
            if (Heard == "" || (Answer == "" && turnNotes.Count == 0)) return;
            history.Add(new QuickTurn { Heard = Heard, Text = Display(Answer), Card = ParseCard(Answer), Cut = cut, Notes = turnNotes });
            turnNotes = new List<string>();
            if (history.Count > 20) history.RemoveAt(0);
            Heard = Answer = "";
        }

        // × on the notch: stop talking and go away.
        public void Dismiss()
        {
            followUpDue = false;
            if (State == Phase.Listening) { stopping = true; StopRecognizer(); }
            StopSpeaking();
            bool cut = (State == Phase.Thinking && !transcribing) || State == Phase.Answering;
            if (cut) Interrupt();
            SaveTurn(cut);
            transcribing = false;
            State = Phase.Idle;
            tick.Stop();
            Raise();
        }

        // Forgets this private conversation: the chat on the notch and what Claude remembers of it.
        // The panel closes, and the next question starts a new one.
        public void ClearChat()
        {
            followUpDue = followUp = false;
            if (State == Phase.Listening) { stopping = true; StopRecognizer(); }
            StopSpeaking();
            KillClaude();
            ignoreTurns = 0;
            transcribing = false;
            history.Clear();
            turnNotes.Clear();
            Heard = Partial = Answer = Status = "";
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

            heardParts.Clear();
            followUp = followUpDue = false;
            turnNotes.Clear();
            Heard = Partial = Answer = Status = "";
            held = false; holdDecided = false; stopping = false; voiced = false; transcribing = false;
            listenId++;
            Level = 0;
            MicReady = settings.QuickEngine != "local"; // the built-in engine says when audio is flowing
            ListenStarted = lastSpeech = DateTime.Now;
            State = Phase.Listening;
            ThreadPool.QueueUserWorkItem(_ => { try { EnsureClaude(); } catch (Exception e) { Log.Write("Quick voice: " + e.Message); } }); // warm up while you talk
            PrepareVoice();
            tick.Start();
            Raise();

            // The built-in engine (whisper.cpp) when it's chosen and downloaded.
            listeningLocal = settings.QuickEngine == "local" && LocalSpeech.Installed(settings.QuickLocalModel);
            if (listeningLocal) { listeningModern = false; StartLocal(); return; }

            // Windows' modern dictation when it's allowed (much better), else the old engine.
            listeningModern = !modernBroken && (!privacyBlocked || ModernDictation.OnlineSpeechOn);
            if (!listeningModern) { StartLegacy(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool fresh = false;
                try
                {
                    lock (listenGate)
                    {
                        if (modern == null)
                        {
                            fresh = true;
                            var m = new ModernDictation();
                            m.Hypothesis += t => ui.BeginInvoke(new Action(() => OnPartial(t)));
                            m.Result += t => ui.BeginInvoke(new Action(() => OnPhrase(t)));
                            modern = m;
                        }
                        modern.Start();
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Quick voice: modern speech recognition: 0x" + e.HResult.ToString("X") + " " + e.Message);
                    // A recognizer whose start failed stays unusable, so make a new one next time. Only
                    // a brand-new one failing (for a reason other than the privacy setting) means
                    // modern dictation doesn't work here.
                    lock (listenGate) modern = null;
                    if (e.HResult == ModernDictation.PrivacyNotAccepted) privacyBlocked = true;
                    else if (fresh) modernBroken = true;
                    ui.BeginInvoke(new Action(() => { if (State == Phase.Listening && !stopping) { listeningModern = false; StartLegacy(); } }));
                }
            });
        }

        void StartLocal()
        {
            try
            {
                if (mic == null)
                {
                    mic = new MicRecorder();
                    mic.Level += (level, speech) => ui.BeginInvoke(new Action(() =>
                    {
                        if (State != Phase.Listening) return;
                        Level = level;
                        if (!MicReady) { MicReady = true; Raise(); }
                        if (speech) { voiced = true; lastSpeech = DateTime.Now; }
                    }));
                }
                mic.Start();
            }
            catch (Exception e) { Log.Write("Quick voice: microphone: " + e.Message); Fail("Couldn't use the microphone."); }
        }

        // The recording is done: transcribe it on this PC, then ask.
        void FinishLocal()
        {
            byte[] wav = mic.Stop();
            if (!voiced || MicRecorder.Seconds(wav) < 0.3) { Fail("Didn't catch that."); return; }
            State = Phase.Thinking;
            Status = "Transcribing…";
            transcribing = true;
            Raise();
            int id = listenId;
            string model = settings.QuickLocalModel;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text = null;
                var clock = Stopwatch.StartNew();
                try { text = LocalSpeech.Transcribe(wav, model); }
                catch (Exception e) { Log.Write("Quick voice: transcribing: " + e.Message); }
                if (Log.Verbose) Log.Write("Quick voice: transcribed " + MicRecorder.Seconds(wav).ToString("0.0") + " s in " + clock.ElapsedMilliseconds + " ms");
                ui.BeginInvoke(new Action(() =>
                {
                    if (id != listenId || !transcribing) return; // cut off meanwhile
                    transcribing = false;
                    if (string.IsNullOrEmpty(text)) { Fail(text == null ? "Couldn't transcribe that." : "Didn't catch that."); return; }
                    Heard = text;
                    Ask(text);
                }));
            });
        }

        void StartLegacy()
        {
            try { EnsureRecognizer(); }
            catch (Exception e) { Log.Write("Quick voice: speech recognition: " + e.Message); Fail("Windows speech recognition isn't available."); return; }
            try { recognizer.RecognizeAsync(RecognizeMode.Multiple); }
            catch (InvalidOperationException) { } // still running from last time
            catch (Exception e) { Log.Write("Quick voice: " + e.Message); Fail("Couldn't use the microphone."); }
        }

        void OnPartial(string text)
        {
            if (State != Phase.Listening) return;
            Partial = text;
            lastSpeech = DateTime.Now;
            Raise();
        }

        void OnPhrase(string text)
        {
            if (State != Phase.Listening) return;
            heardParts.Add(text);
            Heard = string.Join(" ", heardParts.ToArray());
            Partial = "";
            lastSpeech = DateTime.Now;
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
            engine.SpeechHypothesized += (s, e) => { string t = e.Result.Text; ui.BeginInvoke(new Action(() => OnPartial(t))); };
            engine.SpeechRecognized += (s, e) => { string t = e.Result.Text; ui.BeginInvoke(new Action(() => OnPhrase(t))); };
            engine.SpeechDetected += (s, e) => ui.BeginInvoke(new Action(() => { lastSpeech = DateTime.Now; }));
            engine.RecognizeCompleted += (s, e) => ui.BeginInvoke(new Action(OnRecognizeCompleted));
            recognizer = engine;
        }

        void OnTick()
        {
            var now = DateTime.Now;
            if (State == Phase.Listening && !stopping)
            {
                bool any = heardParts.Count > 0 || Partial != "" || voiced;
                if (!holdDecided && now - ListenStarted >= HoldThreshold) { holdDecided = true; held = Hotkeys.Held; }
                if (held && !Hotkeys.Held) FinishListening();              // let go of the key
                else if (!held && holdDecided && any && now - lastSpeech > EndSilence) FinishListening();
                else if (!any && followUp && now - ListenStarted > FollowUpWait) CancelFollowUp();
                else if (!any && now - ListenStarted > NothingHeard && !Hotkeys.Held) FinishListening();
                else if (now - ListenStarted > MaxListen) FinishListening();
            }
            if (player != null && settings.QuickTts != "windows" && (Speaking || player.Playing))
            {
                bool talking = NaturalSpeaking;
                if (talking != Speaking)
                {
                    Speaking = talking;
                    if (!talking && State == Phase.Done) FinishedAt = now; // linger from the last word
                    Raise();
                }
            }
            // The answer ended on a question and it's been read: listen for the reply.
            if (State == Phase.Done && followUpDue && !Speaking && now - FinishedAt > FollowUpPause) { followUpDue = false; StartFollowUp(); return; }
            if (State == Phase.Done && KeepOpen != null && KeepOpen()) FinishedAt = now;
            // Minimized, the bar stays until you close it (or the conversation times out).
            if (State == Phase.Done && Persist != null && Persist() && now - lastUsed < IdleExit) FinishedAt = now;
            if (State == Phase.Done && !Speaking && now - FinishedAt > Linger) { SaveTurn(false); State = Phase.Idle; Raise(); }
            if (State == Phase.Error && now - FinishedAt > TimeSpan.FromSeconds(8)) { State = Phase.Idle; Raise(); }
            if (State == Phase.Idle) tick.Stop();
        }

        // ---- follow-ups ----------------------------------------------------

        // Claude asked something: the microphone opens by itself, and if you don't answer it
        // closes again and the answer stays on screen as it was.
        static readonly TimeSpan FollowUpPause = TimeSpan.FromMilliseconds(350); // so it doesn't hear the voice's last word
        static readonly TimeSpan FollowUpWait = TimeSpan.FromSeconds(7);
        bool followUpDue, followUp;
        string followUpHeard = "", followUpAnswer = "";

        // Listening for the reply to a question, rather than because you pressed the shortcut.
        public bool FollowingUp { get { return State == Phase.Listening && followUp; } }

        void StartFollowUp()
        {
            followUpHeard = Heard;
            followUpAnswer = Answer;
            SaveTurn(false);
            StartListening();
            followUp = State == Phase.Listening;
            if (followUp) { Status = "Go ahead, I'm listening"; Raise(); }
        }

        // No reply (or Esc): back to the answer as it was.
        public void CancelFollowUp()
        {
            if (!FollowingUp) return;
            stopping = true;
            StopRecognizer();
            followUp = false;
            if (history.Count > 0) { turnNotes = history[history.Count - 1].Notes; history.RemoveAt(history.Count - 1); }
            Heard = followUpHeard;
            Answer = followUpAnswer;
            Partial = Status = "";
            State = Phase.Done;
            FinishedAt = DateTime.Now;
            tick.Start();
            Raise();
        }

        void FinishListening()
        {
            if (stopping) return;
            if (FollowingUp && heardParts.Count == 0 && Partial == "" && !voiced) { CancelFollowUp(); return; }
            stopping = true;
            if (listeningLocal) { FinishLocal(); return; }
            if (listeningModern)
            {
                // Stopping finishes the phrase being spoken; its result is queued before this.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (listenGate) { if (modern != null) modern.Stop(); }
                    ui.BeginInvoke(new Action(OnRecognizeCompleted));
                });
                return;
            }
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
            if (listeningLocal && mic != null) { try { mic.Stop(); } catch { } }
            try { if (recognizer != null) recognizer.RecognizeAsyncCancel(); } catch { }
            if (modern != null) ThreadPool.QueueUserWorkItem(_ => { lock (listenGate) { if (modern != null) modern.Cancel(); } });
        }

        // ---- asking Claude -------------------------------------------------

        void Ask(string text)
        {
            State = Phase.Thinking;
            Status = "Thinking…";
            Answer = "";
            lock (unspoken) { unspoken.Length = 0; sourcesReached = false; fed = 0; hushed = false; }
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
                    // The card rules are in the system prompt, but with all your skills and
                    // connectors loaded they get lost in it, so each question carries a reminder.
                    var content = new List<object>
                    {
                        new Dictionary<string, object> { { "type", "text" }, { "text", text } },
                        new Dictionary<string, object> { { "type", "text" }, { "text", Reminder() } },
                    };
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
                    "--model", settings.QuickModel,
                };
                if (settings.QuickAccess == "full")
                {
                    // Like a normal session: your settings, skills, plugins and connectors. Tools that
                    // act (not just look things up) wait for Allow on the notch.
                    args.AddRange(new[] { "--setting-sources", "user", "--settings", ApprovalHookFile() });
                }
                else
                {
                    args.AddRange(new[]
                    {
                        "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", // nothing else, so nothing to ask about
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
                args.AddRange(new[] { "--append-system-prompt", VoiceInstructions(settings.QuickAccess == "full") });
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
                        ui.BeginInvoke(new Action(() => OnStatus(ToolStatus(name))));
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

        static readonly Regex CardBlock = new Regex(@"<card>([\s\S]*?)</card>\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The answer without its card. While a card is still arriving, it (and anything after it)
        // is held back, so its JSON is never shown or read aloud. Only ever grows as text arrives.
        static string WithoutCard(string s)
        {
            s = CardBlock.Replace(s ?? "", "");
            int open = s.IndexOf("<card", StringComparison.OrdinalIgnoreCase);
            if (open >= 0) s = s.Substring(0, open);
            int lt = s.LastIndexOf('<');
            if (lt >= 0 && "<card>".StartsWith(s.Substring(lt), StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, lt);
            return s;
        }

        // The answer as the notch shows it: no card, no sources, no markdown marks.
        public static string Display(string s)
        {
            s = WithoutSources(WithoutCard(s));
            s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "");
            s = Regex.Replace(s, @"(?m)^\s*#+\s+", "");
            s = Regex.Replace(s, @"[ \t]+", " ");
            return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        }

        public static QuickCard ParseCard(string s)
        {
            var m = CardBlock.Match(s ?? "");
            if (!m.Success) return null;
            Dictionary<string, object> d;
            try { d = Json.ParseObject(m.Groups[1].Value.Trim()); } catch { return null; }
            if (d == null) return null;
            var card = new QuickCard
            {
                Source = m.Groups[1].Value, Title = Json.Str(d, "title"), Value = Json.Str(d, "value"), Label = Json.Str(d, "label"),
                Note = Json.Str(d, "note"), Change = Json.Str(d, "change"), Weather = Json.Str(d, "weather").ToLowerInvariant(),
            };
            card.Progress = Fraction(Json.Str(d, "progress"));
            foreach (var item in List(d, "items"))
            {
                var pair = item as System.Collections.IList;
                var named = item as Dictionary<string, object>;
                string name = "", value = "";
                if (pair != null && pair.Count > 0) { name = Text(pair[0]); value = pair.Count > 1 ? Text(pair[1]) : ""; }
                else if (named != null) { name = Json.Str(named, "name"); value = Json.Str(named, "value"); }
                else name = Text(item);
                if (name != "" && card.Items.Count < 8) card.Items.Add(new KeyValuePair<string, string>(name, value));
            }
            foreach (var step in List(d, "steps"))
            {
                var named = step as Dictionary<string, object>;
                string text = named != null ? Json.Str(named, "text") : Text(step);
                if (text != "" && card.Steps.Count < 8) card.Steps.Add(text);
            }
            foreach (var col in List(d, "columns")) if (card.Columns.Count < 3) card.Columns.Add(Text(col));
            foreach (var row in List(d, "rows"))
            {
                var cells = row as System.Collections.IList;
                if (cells == null || cells.Count < 2 || card.Rows.Count >= 8) continue;
                var r = new string[card.Columns.Count + 1];
                for (int i = 0; i < r.Length; i++) r[i] = i < cells.Count ? Text(cells[i]) : "";
                card.Rows.Add(r);
            }
            foreach (var day in List(d, "days"))
            {
                var cells = day as System.Collections.IList;
                var named = day as Dictionary<string, object>;
                string[] r;
                if (named != null) r = new[] { Json.Str(named, "day"), Json.Str(named, "weather").ToLowerInvariant(), Json.Str(named, "high"), Json.Str(named, "low") };
                else if (cells != null && cells.Count > 0) r = new[] { Text(cells[0]), cells.Count > 1 ? Text(cells[1]).ToLowerInvariant() : "", cells.Count > 2 ? Text(cells[2]) : "", cells.Count > 3 ? Text(cells[3]) : "" };
                else continue;
                if (r[0] != "" && card.Days.Count < 5) card.Days.Add(r);
            }

            if (card.Steps.Count > 0) card.Kind = "steps";
            else if (card.Columns.Count >= 2 && card.Rows.Count > 0) card.Kind = "compare";
            else if (card.Weather != "" || card.Days.Count > 0) card.Kind = "weather";
            else if (card.Items.Count > 0) card.Kind = "list";
            else if (card.Value != "") card.Kind = "value";
            else return null;
            return card;
        }

        static System.Collections.IList List(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is System.Collections.IList ? (System.Collections.IList)v : new object[0];
        }

        static string Text(object v)
        {
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // "0.68", "68" or "68%" as 0.68; -1 when there's none.
        static double Fraction(string s)
        {
            s = (s ?? "").Trim().TrimEnd('%').Trim();
            double f;
            if (s == "" || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) || f < 0) return -1;
            if (f > 1) f /= 100;
            return Math.Min(f, 1);
        }

        // Passes the newly arrived speakable text (no card) on to be read aloud.
        void Feed()
        {
            string clean = WithoutCard(Answer);
            lock (unspoken)
            {
                if (clean.Length <= fed) return;
                unspoken.Append(clean.Substring(fed));
                fed = clean.Length;
            }
        }

        // Stops reading this answer aloud (Esc on the minimized bar); the text still arrives.
        public void Hush()
        {
            hushed = true;
            followUpDue = false;
            StopSpeaking();
            Raise();
        }
        bool hushed;

        void SpeakFinishedSentences(bool all)
        {
            if (!settings.QuickSpeak || hushed) return;
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
            if (UseCloud || UseNatural) { SayNatural(chunk); return; }
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
            lock (toSay) toSay.Clear();
            if (player != null) player.Stop();
            Speaking = false;
        }

        // ---- the natural voice (KittenTTS) ---------------------------------

        AudioPlayer player;
        Thread sayThread;
        readonly Queue<string> toSay = new Queue<string>();
        readonly AutoResetEvent sayWake = new AutoResetEvent(false);
        volatile bool synthesizing, naturalBroken;

        // The natural voice reads when it's picked, or in place of ElevenLabs while that's not working.
        bool UseNatural
        {
            get { return (settings.QuickTts == "natural" || (settings.QuickTts == "elevenlabs" && !UseCloud)) && !naturalBroken && NaturalVoice.Supported && NaturalVoice.Installed; }
        }

        // ---- ElevenLabs ----------------------------------------------------

        // After a failure (offline, a bad key, out of credits) ElevenLabs rests for a few minutes,
        // and the natural or Windows voice reads instead.
        DateTime cloudBrokenUntil = DateTime.MinValue;
        string sealedKey, cloudKey;
        public static string LastCloudError = "";

        bool UseCloud { get { return settings.QuickTts == "elevenlabs" && settings.ElevenLabsKey != "" && DateTime.Now >= cloudBrokenUntil; } }

        string CloudKey
        {
            get
            {
                if (sealedKey != settings.ElevenLabsKey) { sealedKey = settings.ElevenLabsKey; cloudKey = ElevenLabs.Unseal(sealedKey); }
                return cloudKey;
            }
        }

        // Sentences are made into audio one at a time on their own thread, and each plays as soon
        // as it's ready (the next one is made while it plays).
        void SayNatural(string text)
        {
            if (player == null) player = new AudioPlayer();
            if (sayThread == null) { sayThread = new Thread(SayLoop) { IsBackground = true, Name = "Natural voice" }; sayThread.Start(); }
            lock (toSay) toSay.Enqueue(text);
            sayWake.Set();
            if (!Speaking) { Speaking = true; Raise(); }
        }

        void SayLoop()
        {
            string previous = "";
            int lastGeneration = -1;
            while (true)
            {
                string text = null;
                int generation;
                lock (toSay)
                {
                    if (toSay.Count > 0) { text = toSay.Dequeue(); synthesizing = true; }
                    generation = player.Generation;
                }
                if (text == null) { sayWake.WaitOne(); continue; }
                if (generation != lastGeneration) { previous = ""; lastGeneration = generation; } // cut off: a new answer
                bool cloud = UseCloud;
                try
                {
                    int rate;
                    float[] samples;
                    if (cloud) samples = ElevenLabs.Generate(CloudKey, settings.ElevenLabsVoice, text, previous, ElevenLabs.Speed(settings.QuickRate), out rate);
                    else samples = NaturalVoice.Generate(text, settings.QuickNaturalVoice, NaturalVoice.Speed(settings.QuickRate), out rate);
                    player.Enqueue(NaturalVoice.Trim(samples, rate), rate, generation); // dropped if you cut it off meanwhile
                    previous = text;
                    if (cloud) LastCloudError = "";
                }
                catch (Exception e)
                {
                    // ElevenLabs: rest it for a few minutes. The natural voice: something's wrong
                    // with the download or this PC, so the Windows voice reads from now on.
                    if (cloud) { Log.Write("ElevenLabs: " + e.Message); LastCloudError = e.Message; cloudBrokenUntil = DateTime.Now.AddMinutes(5); }
                    else { Log.Write("Natural voice: " + e.Message); naturalBroken = true; }
                    string rest = text;
                    ui.BeginInvoke(new Action(() => { lock (unspoken) unspoken.Insert(0, rest + " "); SpeakFinishedSentences(false); }));
                }
                finally { synthesizing = false; }
            }
        }

        // A changed key or voice gets another try right away.
        public void SettingsChanged()
        {
            cloudBrokenUntil = DateTime.MinValue;
        }

        bool NaturalSpeaking
        {
            get { lock (toSay) { if (toSay.Count > 0) return true; } return synthesizing || (player != null && player.Playing); }
        }

        // How loud the answer being read is right now, 0..1; -1 when it can't be measured (the Windows voice).
        public double OutputLevel
        {
            get { return player != null && (player.Playing || UseNatural || UseCloud) ? player.Level : Speaking ? -1 : 0; }
        }

        // Loads the natural voice ahead of the first answer.
        public void PrepareVoice()
        {
            if (settings.QuickSpeak && UseNatural) NaturalVoice.Preload();
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

        // SHA-256 of earlier default skills (LF line endings): a file still exactly like one of
        // them was never edited, so it gets the new default.
        static readonly string[] OldDefaultSkills = { "4f7fc8aa5a04e0bdb8b387db594b11cb2e3a97642e9b1c185924d92ab2abefc6" };

        // Writes the voice-replies skill if it isn't there yet (never over your edits).
        public static void EnsureSkill()
        {
            try
            {
                if (File.Exists(SkillFile))
                {
                    string old = File.ReadAllText(SkillFile, Encoding.UTF8).Replace("\r\n", "\n");
                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(old))).Replace("-", "").ToLowerInvariant();
                    if (Array.IndexOf(OldDefaultSkills, hash) < 0) return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(SkillFile));
                File.WriteAllText(SkillFile, DefaultSkill, new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Write("Quick voice: couldn't write " + SkillFile + ": " + e.Message); }
        }

        static string VoiceInstructions(bool full)
        {
            EnsureSkill();
            string text = DefaultSkill;
            try { if (File.Exists(SkillFile)) text = File.ReadAllText(SkillFile, Encoding.UTF8); } catch { }
            // Drop the frontmatter: only the rules matter here.
            var m = Regex.Match(text, @"^\s*---[\s\S]*?\n---\s*\n");
            if (m.Success) text = text.Substring(m.Length);
            return "You are answering by voice, through Claude Notch. Your reply is read aloud and shown on a panel at the edge of the screen. Today is "
                + DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) + ".\n\n" + CardFormat + "\n\n" + FollowUps + (full ? ManualSkills() : "") + "\n\n" + text.Trim();
        }

        // A resumed conversation (VOICE_CHATS) keeps the system prompt it was started with, so new
        // instructions (cards, today's date, your edits to the skill) never reach it that way. The
        // first question after each start carries them in full instead; the rest a short reminder.
        volatile bool instructionsSent;

        string Reminder()
        {
            if (instructionsSent) return settings.QuickAccess == "full" && ManualSkills() != "" ? CardReminder.Replace("</system-reminder>", " If this falls under one of the skills listed under Skills you load yourself, read it and follow it.</system-reminder>") : CardReminder;
            instructionsSent = true;
            return "<system-reminder>Claude Notch's current instructions for this conversation. They replace any earlier ones.\n\n"
                + VoiceInstructions(settings.QuickAccess == "full") + "</system-reminder>";
        }

        const string FollowUps =
@"## Follow-ups
When your reply ends with a question mark, the microphone opens by itself right after it's read, so
the user can simply answer out loud. So when you need something from them to go on, or the next step
really is theirs to choose, make one clear question your last sentence, even right after listing what
you need (""Tell me X and Y"" leaves the mic closed; add ""Which room is it for?""). Otherwise end on a statement,
with no closers like ""anything else?"" or ""want more?"".";

        // The user's skills marked disable-model-invocation can't be started through the Skill tool,
        // and a voice has no slash command to type. They're listed for Claude to read and follow when
        // the question calls for one. Anything they do beyond looking things up still asks on the notch.
        static string ManualSkills()
        {
            var lines = new StringBuilder();
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
                var files = new List<string>();
                if (Directory.Exists(root))
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string name = Path.GetFileName(dir);
                        if (name == ".trash") continue;
                        if (name != "synced") { files.Add(Path.Combine(dir, "SKILL.md")); continue; }
                        foreach (string account in Directory.GetDirectories(dir))
                            foreach (string skill in Directory.GetDirectories(account)) files.Add(Path.Combine(skill, "SKILL.md"));
                    }
                foreach (string file in files)
                {
                    if (!File.Exists(file)) continue;
                    var m = Regex.Match(File.ReadAllText(file, Encoding.UTF8), @"^\s*---\r?\n([\s\S]*?)\r?\n---");
                    if (!m.Success || !Regex.IsMatch(m.Groups[1].Value, @"(?m)^disable-model-invocation:\s*true\s*$")) continue;
                    string name = Regex.Match(m.Groups[1].Value, @"(?m)^name:\s*(.+?)\s*$").Groups[1].Value;
                    string about = Regex.Match(m.Groups[1].Value, @"(?m)^description:\s*(.+?)\s*$").Groups[1].Value;
                    lines.Append("- ").Append(name).Append(": ").Append(about).Append(" File: ").Append(file).Append('\n');
                }
            }
            catch (Exception e) { Log.Write("Quick voice: reading skills: " + e.Message); }
            if (lines.Length == 0) return "";
            return "\n\n## Skills you load yourself\nThese are the user's own skills, and the Skill tool can't start them. When a question falls under one's "
                + "description, or the user names it (speech recognition may garble the name), read its file with the Read tool and follow it, using "
                + "the files in its folder. Keep your replies in this voice format. Don't announce that you're loading it.\n" + lines.ToString().TrimEnd();
        }

        const string CardReminder =
            "<system-reminder>Claude Notch: if this answer has a key fact to see at a glance (a figure, the weather, steps, "
            + "a comparison, a short list), your reply must start with one <card>{json}</card> as described under Cards in your "
            + "instructions, then the spoken answer. The voice-replies rule against markup doesn't apply to the card. "
            + "No card for chat or explanations without one.</system-reminder>";

        // How to put the key fact on screen. Part of the app, not the skill, so it can't be edited away.
        const string CardFormat =
@"## Cards
When the answer has a key fact someone would want to see at a glance (a price, amount, number,
time, date, score, the weather, a name, a short list, steps, or a comparison), start your reply with one card, then
the spoken answer. The card is shown big on the panel and never read aloud.

One value:
<card>{""value"": ""$1,249"", ""label"": ""MacBook Air 13-inch, M4"", ""note"": ""Your price list, March 2026""}</card>

One value that moved (change: up with +, down with -) or that's part of a whole (progress: 0 to 1):
<card>{""value"": ""$96,410"", ""label"": ""Bitcoin"", ""change"": ""+2.4% today""}</card>
<card>{""value"": ""$1,360 of $2,000"", ""label"": ""November budget"", ""progress"": 0.68, ""note"": ""12 days left""}</card>

A few items (at most six), each [name, value]; value can be """":
<card>{""title"": ""Flights to Dubai, Friday"", ""items"": [[""flydubai"", ""$189""], [""Royal Jordanian"", ""$214""]]}</card>

Steps to follow, in order (at most six, each a few words):
<card>{""title"": ""Reset the router"", ""steps"": [""Unplug it for 30 seconds"", ""Plug it back in"", ""Wait for the white light""]}</card>

Two or three things side by side (columns), one row per point: [what, then one value per column]:
<card>{""title"": ""iPhone 17 vs Pixel 10"", ""columns"": [""iPhone 17"", ""Pixel 10""], ""rows"": [[""Price"", ""$799"", ""$699""], [""Battery"", ""22 h"", ""24 h""]]}</card>

Weather: weather is one of sun, partly, cloud, rain, storm, snow, fog, wind, night. days (optional,
at most five): [day, weather, high, low]:
<card>{""weather"": ""partly"", ""value"": ""18°C"", ""label"": ""Amman now"", ""note"": ""High 21°, low 12°"", ""days"": [[""Thu"", ""sun"", ""23°"", ""13°""], [""Fri"", ""rain"", ""17°"", ""11°""]]}</card>

- The card is one line of valid JSON between <card> and </card>, at the very start.
- Pick the one card that fits best: a value for one figure, weather for weather, steps for how-to,
  compare when weighing options against each other, a list for anything else with several items.
- value: the exact figure, short (under 20 characters), in digits with its currency or unit:
  ""$1,249"", ""24°C"", ""3:15 PM"", ""Thu 6 Nov"". label: what it is, in a few words.
  note (optional): one short line, like where it's from or a condition.
- The spoken answer after it must still make sense on its own. Say the figure the way people
  say it, and add what matters that the card doesn't show. Never mention the card.
- Choosing between products or options (""X or Y?"") gets a compare card with the facts that
  matter, even when you then say which you'd pick.
- No other card for chat, opinions, explanations or yes/no answers without a figure.";

        const string DefaultSkill =
@"---
name: voice-replies
description: How to answer when the reply will be read aloud (voice assistants, dictation, Claude Notch quick voice). Use whenever an answer will be spoken rather than read.
---

# Voice replies

Your answer is spoken aloud by a text-to-speech voice and shown on a small panel. Write the way a
sharp, friendly assistant talks.

- Lead with the answer itself. No preamble, no restating the question, no ""Great question"".
- When there's one figure that matters (a price, a time, a total), say it first and plainly,
  then the one detail that helps most (""It's twelve hundred and forty-nine dollars, for the sixteen gig model."").
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
