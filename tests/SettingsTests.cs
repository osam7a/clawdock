using System;

namespace Clawdock.Tests
{
    // Never Load or Save here: those use your real settings file.
    static class SettingsTests
    {
        static Settings Fixed(Action<Settings> change)
        {
            var bad = new Settings();
            change(bad);
            var s = new Settings();
            s.CopyFrom(bad); // corrects the values as it copies
            return s;
        }

        public static void Defaults_are_already_valid()
        {
            var d = new Settings();
            var s = Fixed(x => { });
            foreach (var p in typeof(Settings).GetProperties())
                if (p.CanRead && p.CanWrite) Check.Equal(p.GetValue(d, null), p.GetValue(s, null), p.Name);
        }

        public static void Unknown_choices_fall_back()
        {
            var s = Fixed(x => { x.Style = "huge"; x.Edge = "middle"; x.QuickAccess = "everything"; x.Shortcut = null; x.QuickTts = ""; });
            Check.Equal("classic", s.Style);
            Check.Equal("top", s.Edge);
            Check.Equal("web", s.QuickAccess);
            Check.Equal("copilot", s.Shortcut);
            Check.Equal("windows", s.QuickTts);
        }

        public static void Good_choices_are_kept()
        {
            var s = Fixed(x => { x.Style = "dot"; x.Edge = "left"; x.QuickAccess = "auto"; x.QuickRate = -5; x.FinishedSeconds = 0; });
            Check.Equal("dot", s.Style);
            Check.Equal("left", s.Edge);
            Check.Equal("auto", s.QuickAccess);
            Check.Equal(-5, s.QuickRate);
            Check.Equal(0, s.FinishedSeconds);
        }

        public static void Numbers_out_of_range_fall_back()
        {
            var s = Fixed(x => { x.QuickRate = 99; x.EdgeOffset = 7; x.FinishedSeconds = -1; x.QuickNaturalVoice = 8; });
            Check.Equal(1, s.QuickRate);
            Check.Near(0.5, s.EdgeOffset);
            Check.Equal(12, s.FinishedSeconds);
            Check.Equal(1, s.QuickNaturalVoice);
            Check.Near(0.5, Fixed(x => x.EdgeOffset = double.NaN).EdgeOffset);
        }

        public static void New_chat_link_must_open_claude()
        {
            Check.Equal("claude://claude.ai/new", Fixed(x => x.NewChatLink = "https://evil.example").NewChatLink);
            Check.Equal("claude://claude.ai/new", Fixed(x => x.NewChatLink = null).NewChatLink);
            Check.Equal("claude://x", Fixed(x => x.NewChatLink = "claude://x").NewChatLink);
        }

        public static void Projects_folder_must_be_a_full_path()
        {
            Check.Equal(ProjectStarter.DefaultRoot, Fixed(x => x.ProjectsFolder = "").ProjectsFolder);
            Check.Equal(ProjectStarter.DefaultRoot, Fixed(x => x.ProjectsFolder = "relative\\path").ProjectsFolder);
            Check.Equal(@"D:\Code", Fixed(x => x.ProjectsFolder = @"D:\Code").ProjectsFolder);
        }

        public static void Text_settings_are_never_null()
        {
            var s = Fixed(x => { x.QuickVoiceName = null; x.ElevenLabsKey = null; x.ElevenLabsVoice = null; x.Monitor = null; });
            Check.Equal("", s.QuickVoiceName);
            Check.Equal("", s.ElevenLabsKey);
            Check.Equal("", s.ElevenLabsVoice);
            Check.Equal("", s.Monitor);
        }

        public static void Clone_copies_but_is_separate()
        {
            var s = new Settings { Style = "dot", ProjectsFolder = @"D:\Code" };
            var c = s.Clone();
            Check.Equal("dot", c.Style);
            Check.Equal(@"D:\Code", c.ProjectsFolder);
            c.Style = "line";
            Check.Equal("dot", s.Style);
        }

        public static void Demo_flag_is_not_saved()
        {
            Check.That(!new Settings { Transient = true }.Clone().Transient, "Transient stays out of the file");
        }

        public static void Dismiss_duration()
        {
            Check.Equal(TimeSpan.Zero, new Settings { DismissFor = "return" }.DismissDuration);
            Check.Equal(TimeSpan.FromMinutes(15), new Settings { DismissFor = "15m" }.DismissDuration);
            Check.Equal(TimeSpan.FromHours(1), new Settings { DismissFor = "1h" }.DismissDuration);
        }
    }
}
