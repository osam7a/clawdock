using System;
using System.IO;
using System.Web.Script.Serialization;

namespace ClaudeNotch
{
    // Everything the settings window can change. Saved as JSON next to the installed exe's
    // data (%LOCALAPPDATA%\ClaudeNotch\settings.json); unknown or missing keys fall back
    // to the defaults below, so older files keep working.
    class Settings
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeNotch");
        static readonly string FilePath = Path.Combine(DataDir, "settings.json");

        // Shortcut
        public string Shortcut { get; set; }          // "copilot" | "custom" | "off"
        public int CustomModifiers { get; set; }      // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
        public int CustomKey { get; set; }            // virtual-key code

        // Voice and typed prompts
        public string VoiceMode { get; set; }         // "voice-or-type" | "type" | "voice" | "quick" ("type": Claude's quick entry)
        public string NewChatLink { get; set; }

        // Quick voice: push to talk, answered by a Claude Code session on the notch
        public string QuickModel { get; set; }        // "haiku" | "sonnet" | "opus"
        public bool QuickSpeak { get; set; }          // read answers aloud
        public string QuickVoiceName { get; set; }    // a Windows voice; empty = default
        public int QuickRate { get; set; }            // -5 (slow) .. 5 (fast)
        public string QuickEngine { get; set; }       // speech recognition: "windows" | "local" (whisper.cpp on this PC)
        public string QuickLocalModel { get; set; }   // "small" | "base"
        public string QuickAccess { get; set; }       // "web": web search only | "full": your skills and connectors
        public string QuickHistory { get; set; }      // "private": not saved | "history": one VOICE_CHATS session
        public string QuickTts { get; set; }          // the voice that reads answers: "windows" | "natural" (KittenTTS on this PC)
        public int QuickNaturalVoice { get; set; }    // which natural voice, 0..7
        public bool QuickMinimized { get; set; }      // quick voice shows as a small bar

        // Look and placement
        public string Style { get; set; }             // "classic" | "compact" | "line" | "dot"
        public string Edge { get; set; }              // "top" | "bottom" | "left" | "right"
        public double EdgeOffset { get; set; }        // 0..1 along the edge
        public string Monitor { get; set; }           // device name; empty = primary
        public bool ExpandOnHover { get; set; }

        // When it shows and goes away
        public string ShowWhen { get; set; }          // "smart" | "away" | "alerts"
        public string DismissFor { get; set; }        // "return" | "15m" | "1h"
        public int FinishedSeconds { get; set; }
        public bool ShowOverFullscreen { get; set; }

        // System
        public bool StartWithWindows { get; set; }
        public bool ApproveFromNotch { get; set; }
        public bool ShowQuestions { get; set; }       // Claude's end-of-turn questions, with their buttons
        public bool KeepHooksInstalled { get; set; }

        public Settings()
        {
            Shortcut = "copilot";
            CustomModifiers = 2 | 4;   // Ctrl+Shift (Ctrl+Alt+Space is Claude's own quick entry)
            CustomKey = 0x20;          // Space
            VoiceMode = "voice-or-type";
            NewChatLink = "claude://claude.ai/new";
            QuickModel = "haiku";
            QuickSpeak = true;
            QuickVoiceName = "";
            QuickRate = 1;
            QuickEngine = "windows";
            QuickLocalModel = "small";
            QuickAccess = "web";
            QuickHistory = "private";
            QuickTts = "windows";
            QuickNaturalVoice = 1;
            Style = "classic";
            Edge = "top";
            EdgeOffset = 0.5;
            Monitor = "";
            ExpandOnHover = true;
            ShowWhen = "smart";
            DismissFor = "return";
            FinishedSeconds = 12;
            ShowOverFullscreen = false;
            StartWithWindows = true;
            ApproveFromNotch = true;
            ShowQuestions = true;
            KeepHooksInstalled = true;
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch { } // a broken file is replaced by defaults on the next save
            return new Settings();
        }

        // Set for the demo, so dragging its notch around doesn't overwrite your real settings.
        [ScriptIgnore]
        public bool Transient { get; set; }

        public void Save()
        {
            if (Transient) return;
            try
            {
                Directory.CreateDirectory(DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Pretty(new JavaScriptSerializer().Serialize(this)));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static bool Exists { get { return File.Exists(FilePath); } }

        public Settings Clone()
        {
            var js = new JavaScriptSerializer();
            return js.Deserialize<Settings>(js.Serialize(this));
        }

        public void CopyFrom(Settings other)
        {
            foreach (var p in typeof(Settings).GetProperties())
                if (p.CanRead && p.CanWrite) p.SetValue(this, p.GetValue(other, null), null);
            Normalize();
        }

        // Guard against hand-edited values the rest of the app doesn't expect.
        void Normalize()
        {
            Shortcut = OneOf(Shortcut, "copilot", "custom", "off");
            VoiceMode = OneOf(VoiceMode, "voice-or-type", "type", "voice", "quick");
            QuickModel = OneOf(QuickModel, "haiku", "sonnet", "opus");
            if (QuickVoiceName == null) QuickVoiceName = "";
            if (QuickRate < -5 || QuickRate > 5) QuickRate = 1;
            QuickEngine = OneOf(QuickEngine, "windows", "local");
            QuickLocalModel = OneOf(QuickLocalModel, "small", "base");
            QuickAccess = OneOf(QuickAccess, "web", "full");
            QuickHistory = OneOf(QuickHistory, "private", "history");
            QuickTts = OneOf(QuickTts, "windows", "natural");
            if (QuickNaturalVoice < 0 || QuickNaturalVoice > 7) QuickNaturalVoice = 1;
            Style = OneOf(Style, "classic", "compact", "line", "dot");
            Edge = OneOf(Edge, "top", "bottom", "left", "right");
            ShowWhen = OneOf(ShowWhen, "smart", "away", "alerts");
            DismissFor = OneOf(DismissFor, "return", "15m", "1h");
            if (double.IsNaN(EdgeOffset) || EdgeOffset < 0 || EdgeOffset > 1) EdgeOffset = 0.5;
            if (FinishedSeconds < 0 || FinishedSeconds > 3600) FinishedSeconds = 12;
            if (string.IsNullOrEmpty(NewChatLink) || !NewChatLink.StartsWith("claude://", StringComparison.OrdinalIgnoreCase))
                NewChatLink = "claude://claude.ai/new";
            if (Monitor == null) Monitor = "";
        }

        static string OneOf(string value, params string[] allowed)
        {
            foreach (var a in allowed) if (a == value) return value;
            return allowed[0];
        }

        [ScriptIgnore]
        public TimeSpan DismissDuration
        {
            get { return DismissFor == "15m" ? TimeSpan.FromMinutes(15) : DismissFor == "1h" ? TimeSpan.FromHours(1) : TimeSpan.Zero; }
        }
    }
}
