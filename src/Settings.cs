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
        public string VoiceMode { get; set; }         // "voice-or-type" | "type" | "voice"
        public string PromptTarget { get; set; }      // "desktop" | "cli"
        public string CliFolder { get; set; }
        public string NewChatLink { get; set; }

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
        public bool KeepHooksInstalled { get; set; }

        public Settings()
        {
            Shortcut = "copilot";
            CustomModifiers = 2 | 1;   // Ctrl+Alt
            CustomKey = 0x20;          // Space
            VoiceMode = "voice-or-type";
            PromptTarget = "desktop";
            CliFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            NewChatLink = "claude://claude.ai/new";
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
            VoiceMode = OneOf(VoiceMode, "voice-or-type", "type", "voice");
            PromptTarget = OneOf(PromptTarget, "desktop", "cli");
            Style = OneOf(Style, "classic", "compact", "line", "dot");
            Edge = OneOf(Edge, "top", "bottom", "left", "right");
            ShowWhen = OneOf(ShowWhen, "smart", "away", "alerts");
            DismissFor = OneOf(DismissFor, "return", "15m", "1h");
            if (double.IsNaN(EdgeOffset) || EdgeOffset < 0 || EdgeOffset > 1) EdgeOffset = 0.5;
            if (FinishedSeconds < 0 || FinishedSeconds > 3600) FinishedSeconds = 12;
            if (string.IsNullOrEmpty(CliFolder)) CliFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
