namespace Clawdock.Tests
{
    static class DefaultsTests
    {
        public static void A_new_install_uses_quick_voice_with_whisper_and_bruno()
        {
            var s = new Settings();
            Check.Equal("quick", s.VoiceMode);
            Check.Equal("local", s.QuickEngine);
            Check.Equal("base", s.QuickLocalModel);
            Check.Equal("natural", s.QuickTts);
            Check.Equal("Bruno", NaturalVoice.Voices[s.QuickNaturalVoice]);
        }

        public static void The_fast_model_is_the_60_MB_one()
        {
            Check.Equal(60, (int)System.Math.Round(LocalSpeech.Find("base").Bytes / 1000000.0));
        }

        public static void Only_a_new_install_downloads_by_itself()
        {
            Check.That(!new Settings().FetchVoices, "not unless it's the first run");
        }
    }
}
