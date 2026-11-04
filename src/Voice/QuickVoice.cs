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

namespace Clawdock
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
    partial class QuickVoice
    {
        public enum Phase { Idle, Listening, Thinking, Answering, Done, Error }

        public event Action Changed;

        public Phase State { get; private set; }
        public string Heard { get; private set; }   // what you said (final words so far, then the question)
        public string Partial { get; private set; } // the words being recognized right now
        public string Answer { get; private set; }
        public string Status { get; private set; }  // "Searching the web…", an error, …
        public bool Speaking { get; private set; }
        public MascotMood ThinkingMood { get; private set; } // before the answer: thinking, or the tool it's using
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
        public string HookExe { get; set; }         // Clawdock.exe, for approving tools from the notch
        public Func<List<SessionBrief.Entry>> LiveSessions { get; set; } // your other sessions, for the snapshot
        public event Action<string, string> SessionMessageWanted;  // session id, message: needs your OK before it's sent
        public event Action<string, string, string, string> NewProjectWanted; // name, model, effort, first message: needs your OK before it's started

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

        // What came of a message to another session, for Claude to know at the next question.
        string tellNext;
        public void TellNext(string text) { tellNext = text; }

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
            Typing = false; // back to talking
            StopSpeaking();
            bool cut = (State == Phase.Thinking && !transcribing) || State == Phase.Answering;
            if (cut) Interrupt();
            SaveTurn(cut);
            StartListening();
        }

        // You're talking, or it's still working on what you said. Esc cancels this, even minimized.
        public bool InTurn { get { return State == Phase.Listening || State == Phase.Thinking || State == Phase.Answering; } }

        // The answer on screen moves up into the history when you ask the next thing.
        void SaveTurn(bool cut)
        {
            if (Heard == "" || (Answer == "" && turnNotes.Count == 0)) return;
            history.Add(new QuickTurn { Heard = Heard, Text = Display(Answer), Card = ParseCard(Answer), Cut = cut, Notes = turnNotes });
            turnNotes = new List<string>();
            if (history.Count > 200) history.RemoveAt(0);
            Heard = Answer = "";
        }

        // × on the notch: stop talking and go away.
        public void Dismiss()
        {
            followUpDue = false;
            Typing = false;
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

        public void Quit()
        {
            StopSpeaking();
            StopRecognizer();
            KillClaude();
        }


    }
}
