using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Clawdock
{
    // Keeps the installed copy up to date from the project's GitHub releases. A new exe is downloaded
    // and checked in the background, then started when nothing is waiting on you. It replaces
    // this copy the way running a newer exe by hand does (see Installer).
    class Updater
    {
        public const string Repo = "osam7a/clawdock";
        const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
        const string AssetName = "Clawdock.exe";
        const long MostBytes = 64 * 1024 * 1024;
        static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2), CheckEvery = TimeSpan.FromHours(6);

        public class Release { public string Tag, Url, Sha256; }

        public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;
        static string Folder { get { return Path.Combine(Settings.DataDir, "update"); } }

        readonly Settings settings;
        readonly Func<bool, TimeSpan, bool> calm; // (you asked for it, how long it has waited) -> a good moment?
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        DateTime nextCheck = DateTime.Now + FirstCheckAfter, readySince;
        string readyExe, readyTag;
        bool working, asked, cleaned;

        public string Status { get; private set; }
        public event Action Changed;

        public Updater(Settings settings, Func<bool, TimeSpan, bool> calm)
        {
            this.settings = settings;
            this.calm = calm;
            Status = "Version " + Short(Current);
            timer.Tick += (s, e) => Tick();
        }

        public static string Short(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        // Only the installed copy updates itself: a test build stays what it is.
        public void Start() { if (Installer.IsInstalledCopy) timer.Start(); }

        public void CheckNow()
        {
            if (!Installer.IsInstalledCopy) { Say("Only an installed copy can update itself."); return; }
            asked = true;
            if (readyExe != null) TryInstall();
            else Check();
        }

        void Tick()
        {
            if (!cleaned && !working && readyExe == null) { cleaned = true; Installer.CleanUpAfterUpdate(); }
            if (readyExe != null) { if (settings.AutoUpdate || asked) TryInstall(); return; }
            if (settings.AutoUpdate && DateTime.Now >= nextCheck) Check();
        }

        void Check()
        {
            if (working) return;
            working = true;
            nextCheck = DateTime.Now + CheckEvery;
            Say("Checking for updates…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result = null, exe = null, tag = null;
                try
                {
                    var release = ParseRelease(Fetch(LatestUrl));
                    // One that had to be undone is only tried again if you ask for it.
                    if (release == null || !IsNewer(release.Tag, Current) || (!asked && IsSkipped(release.Tag, Installer.SkippedVersion))) result = "Version " + Short(Current) + " is the latest.";
                    else
                    {
                        exe = Download(release);
                        tag = release.Tag;
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Update check failed: " + e.Message);
                    result = "Couldn't check for updates. Version " + Short(Current) + ".";
                }
                ui.BeginInvoke(new Action(() =>
                {
                    working = false;
                    if (exe != null)
                    {
                        readyExe = exe; readyTag = tag; readySince = DateTime.Now;
                        Say(tag.TrimStart('v') + " is ready and installs when nothing's waiting on you.");
                        if (settings.AutoUpdate || asked) TryInstall();
                    }
                    else { asked = false; Say(result); }
                }));
            });
        }

        void TryInstall()
        {
            if (readyExe == null || !calm(asked, DateTime.Now - readySince)) return;
            string exe = readyExe;
            readyExe = null;
            try
            {
                Log.Write("Updating to " + readyTag);
                Say("Updating to " + readyTag.TrimStart('v') + "…");
                // It stops this copy, puts itself in place and starts from there.
                Process.Start(new ProcessStartInfo(exe, "--update") { UseShellExecute = false, WorkingDirectory = Settings.DataDir });
            }
            catch (Exception e)
            {
                Log.Write("Couldn't start the update: " + e.Message);
                Say("Couldn't install the update. See the log.");
            }
        }

        void Say(string status)
        {
            Status = status;
            if (Changed != null) Changed();
        }

        // ---- the release ----------------------------------------------------

        // The latest release, or null if it has nothing this app can install.
        public static Release ParseRelease(string json)
        {
            var d = Json.ParseObject(json);
            if (d == null || Json.Str(d, "tag_name") == "") return null;
            object v;
            if (d.TryGetValue("draft", out v) && v is bool && (bool)v) return null;
            if (d.TryGetValue("prerelease", out v) && v is bool && (bool)v) return null;
            var assets = d.TryGetValue("assets", out v) ? v as IEnumerable : null;
            if (assets == null) return null;
            foreach (object o in assets)
            {
                var a = o as Dictionary<string, object>;
                if (a == null || Json.Str(a, "name") != AssetName) continue;
                string url = Json.Str(a, "browser_download_url");
                if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
                string digest = Json.Str(a, "digest");
                return new Release { Tag = Json.Str(d, "tag_name"), Url = url, Sha256 = digest.StartsWith("sha256:") ? digest.Substring(7) : "" };
            }
            return null;
        }

        // Numbers only, so "v0.4.0" is newer than 0.3.0.0. Anything unreadable isn't newer.
        public static bool IsNewer(string tag, Version current)
        {
            Version v = VersionOf(tag);
            if (v == null) return false;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        }

        internal static bool IsSkipped(string tag, string skipped)
        {
            Version v = VersionOf(tag);
            return v != null && skipped != "" && Short(v) == skipped;
        }

        static Version VersionOf(string tag)
        {
            Version v;
            return Version.TryParse((tag ?? "").TrimStart('v', 'V'), out v) ? v : null;
        }

        // ---- downloading ----------------------------------------------------

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "Clawdock/" + Short(Current);
            r.Accept = "application/vnd.github+json, application/octet-stream";
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        static string Fetch(string url)
        {
            using (var response = Request(url).GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        static string Download(Release release)
        {
            Directory.CreateDirectory(Folder);
            string part = Path.Combine(Folder, AssetName + ".part");
            string exe = Path.Combine(Folder, AssetName);
            try
            {
                using (var response = Request(release.Url).GetResponse())
                {
                    if (response.ResponseUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("the download wasn't over HTTPS");
                    using (var from = response.GetResponseStream())
                    using (var to = File.Create(part))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int n;
                        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += n;
                            if (total > MostBytes) throw new InvalidDataException("the download is too large");
                            to.Write(buffer, 0, n);
                        }
                    }
                }
                Verify(part, release);
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(part, exe);
                return exe;
            }
            finally { try { File.Delete(part); } catch { } }
        }

        // Must be what the release says it is, and really this app at that version.
        public static void Verify(string path, Release release)
        {
            if (release.Sha256 == "") throw new InvalidDataException("the release doesn't give a checksum");
            if (!string.Equals(Sha256Of(path), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download doesn't match the release's checksum");
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(path); }
            catch (Exception e) { throw new InvalidDataException("the download isn't this app: " + e.Message); }
            if (name.Name != "Clawdock") throw new InvalidDataException("the download is " + name.Name);
            Version want = VersionOf(release.Tag);
            if (want == null || name.Version.Major != want.Major || name.Version.Minor != want.Minor || name.Version.Build != Math.Max(0, want.Build))
                throw new InvalidDataException("the download is version " + name.Version + ", not " + release.Tag);
        }

        internal static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }
    }
}
