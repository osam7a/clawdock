using System;
using System.IO;

namespace Clawdock.Tests
{
    // What has to keep working for an install made under the app's earlier name.
    static class RenameTests
    {
        public static void Quick_voices_history_follows_the_new_folder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "notch-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string old = Path.Combine(dir, "C--Users-me-AppData-Local-ClaudeNotch-voice");
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "chat.jsonl"), "kept");
                string mine = Path.Combine(dir, "C--Code-ClaudeNotch");
                Directory.CreateDirectory(mine);

                Installer.MoveVoiceHistory(dir, "ClaudeNotch");

                Check.Equal("kept", File.ReadAllText(Path.Combine(dir, "C--Users-me-AppData-Local-Clawdock-voice", "chat.jsonl")));
                Check.That(!Directory.Exists(old), "the old folder is gone");
                Check.That(Directory.Exists(mine), "other projects aren't touched");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void History_already_there_is_not_overwritten()
        {
            string dir = Path.Combine(Path.GetTempPath(), "notch-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string old = Path.Combine(dir, "x-ClaudeNotch-voice"), now = Path.Combine(dir, "x-Clawdock-voice");
                Directory.CreateDirectory(old);
                Directory.CreateDirectory(now);
                File.WriteAllText(Path.Combine(now, "new.jsonl"), "new");

                Installer.MoveVoiceHistory(dir, "ClaudeNotch");

                Check.Equal("new", File.ReadAllText(Path.Combine(now, "new.jsonl")));
                Check.That(Directory.Exists(old), "left where it was");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
