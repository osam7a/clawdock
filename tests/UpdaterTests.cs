using System;
using System.IO;

namespace Clawdock.Tests
{
    // Nothing here touches the network or the installed copy.
    static class UpdaterTests
    {
        const string Url = "https://github.com/osam7a/clawdock/releases/download/v9.9.9/Clawdock.exe";

        static string ReleaseJson(string url = Url, string digest = "sha256:abc123", string flags = "")
        {
            return "{'tag_name':'v9.9.9'," + flags + "'assets':[{'name':'notes.txt','browser_download_url':'x'},{'name':'Clawdock.exe','browser_download_url':'" + url + "','digest':'" + digest + "'}]}";
        }

        static Updater.Release Parse(string json) { return Updater.ParseRelease(json.Replace('\'', '"')); }

        public static void Only_a_higher_version_is_newer()
        {
            var now = new Version(0, 3, 0, 0);
            Check.That(Updater.IsNewer("v0.4.0", now), "0.4.0");
            Check.That(Updater.IsNewer("0.3.1", now), "0.3.1");
            Check.That(Updater.IsNewer("v1.0", now), "1.0");
            Check.That(!Updater.IsNewer("v0.3.0", now), "the same version");
            Check.That(!Updater.IsNewer("v0.2.9", now), "an older version");
            Check.That(!Updater.IsNewer("latest", now), "not a version");
            Check.That(!Updater.IsNewer(null, now), "nothing");
        }

        public static void A_release_gives_its_exe_and_checksum()
        {
            var r = Parse(ReleaseJson());
            Check.Equal("v9.9.9", r.Tag);
            Check.Equal(Url, r.Url);
            Check.Equal("abc123", r.Sha256);
            Check.Equal("", Parse(ReleaseJson(digest: "")).Sha256);
        }

        public static void Other_releases_are_skipped()
        {
            Check.Equal(null, Parse(ReleaseJson(flags: "'draft':true,")));
            Check.Equal(null, Parse(ReleaseJson(flags: "'prerelease':true,")));
            Check.Equal(null, Parse("{'tag_name':'v9.9.9','assets':[]}"));
            Check.Equal(null, Parse("{'message':'Not Found'}"));
        }

        public static void A_download_from_anywhere_else_is_refused()
        {
            Check.Equal(null, Parse(ReleaseJson("https://example.com/Clawdock.exe")));
            Check.Equal(null, Parse(ReleaseJson("http://github.com/osam7a/clawdock/releases/download/v9.9.9/Clawdock.exe")));
            Check.Equal(null, Parse(ReleaseJson("https://github.com/someone/else/releases/download/v9.9.9/Clawdock.exe")));
        }

        static void Refused(string path, Updater.Release release, string why)
        {
            try { Updater.Verify(path, release); }
            catch (InvalidDataException e) { Check.Has(e.Message, why); return; }
            throw new Exception("Should have been refused: " + why);
        }

        public static void A_bad_download_is_refused()
        {
            string dir = Path.Combine(Path.GetTempPath(), "notch-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string junk = Path.Combine(dir, "junk.exe");
                File.WriteAllText(junk, "not a program");
                Refused(junk, new Updater.Release { Tag = "v9.9.9", Sha256 = "" }, "isn't this app");
                Refused(junk, new Updater.Release { Tag = "v9.9.9", Sha256 = "00" }, "checksum");

                // A real .NET program, but not this app.
                string other = typeof(UpdaterTests).Assembly.Location;
                Refused(other, new Updater.Release { Tag = "v9.9.9", Sha256 = "" }, "Clawdock.Tests");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void A_file_in_use_can_still_be_replaced()
        {
            string dir = Path.Combine(Path.GetTempPath(), "notch-test-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string from = Path.Combine(dir, "new.exe"), to = Path.Combine(dir, "app.exe");
                File.WriteAllText(from, "new");
                File.WriteAllText(to, "old");
                File.WriteAllText(to + ".free", "");

                // Like a running exe: can't be written, but can be renamed.
                using (new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    Installer.ReplaceFile(from, to);
                Check.Equal("new", File.ReadAllText(to));
                Check.Equal(1, Directory.GetFiles(dir, "app.exe.old-*").Length, "the old one, moved aside");

                Installer.ReplaceFile(from, to + ".free");
                Check.Equal("new", File.ReadAllText(to + ".free"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
