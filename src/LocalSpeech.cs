using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeNotch
{
    // Speech recognition on this PC with whisper.cpp (MIT licensed, github.com/ggml-org/whisper.cpp)
    // and an English Whisper model. Nothing leaves the computer.
    //
    // The engine and the model are downloaded once, from settings, into
    // %LOCALAPPDATA%\ClaudeNotch\whisper, and each file is checked against its known SHA-256
    // before it's used. A recording is transcribed after you stop talking (about 1 to 2 seconds
    // for a short question with the small model).
    static class LocalSpeech
    {
        public class Model
        {
            public string Key, Label, File, Sha256;
            public long Bytes;
            public string Url { get { return "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + File; } }
        }

        public static readonly Model[] Models =
        {
            new Model { Key = "small", Label = "Accurate", File = "ggml-small.en-q5_1.bin", Bytes = 190098681, Sha256 = "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30" },
            new Model { Key = "base", Label = "Fast", File = "ggml-base.en-q5_1.bin", Bytes = 59721011, Sha256 = "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f" },
        };

        const string EngineUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        const string EngineSha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
        const long EngineBytes = 8928640;

        public static readonly string Folder = Path.Combine(Settings.DataDir, "whisper");
        static string EngineExe { get { return Path.Combine(Folder, "engine", "Release", "whisper-cli.exe"); } }

        public static Model Find(string key)
        {
            foreach (var m in Models) if (m.Key == key) return m;
            return Models[0];
        }

        public static bool EngineInstalled { get { return File.Exists(EngineExe); } }
        public static bool Installed(string modelKey) { return EngineInstalled && File.Exists(Path.Combine(Folder, Find(modelKey).File)); }

        // How much a download of this model (and the engine, if it's missing) is.
        public static long DownloadBytes(string modelKey)
        {
            long n = File.Exists(Path.Combine(Folder, Find(modelKey).File)) ? 0 : Find(modelKey).Bytes;
            return n + (EngineInstalled ? 0 : EngineBytes);
        }

        // Downloads whatever is missing. progress: 0..1. Runs on the calling thread.
        public static void Install(string modelKey, Action<double> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Folder);
            var model = Find(modelKey);
            long total = DownloadBytes(modelKey), done = 0;
            if (!EngineInstalled)
            {
                string zip = Path.Combine(Folder, "engine.zip");
                Download(EngineUrl, zip, EngineSha256, n => progress((done + n) / (double)total), cancelled);
                done += EngineBytes;
                string dir = Path.Combine(Folder, "engine");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(zip, dir);
                File.Delete(zip);
                if (!EngineInstalled) throw new IOException("The download didn't contain whisper-cli.exe.");
            }
            string file = Path.Combine(Folder, model.File);
            if (!File.Exists(file)) Download(model.Url, file, model.Sha256, n => progress((done + n) / (double)total), cancelled);
            progress(1);
        }

        static void Download(string url, string path, string sha256, Action<long> progress, Func<bool> cancelled)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string part = path + ".part";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ClaudeNotch";
            using (var response = request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(part))
            using (var hash = SHA256.Create())
            {
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled()) { output.Close(); File.Delete(part); throw new OperationCanceledException(); }
                    output.Write(buf, 0, n);
                    hash.TransformBlock(buf, 0, n, null, 0);
                    got += n;
                    progress(got);
                }
                hash.TransformFinalBlock(buf, 0, 0);
                string actual = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                output.Close();
                if (actual != sha256)
                {
                    File.Delete(part);
                    throw new IOException("The download of " + Path.GetFileName(path) + " didn't match its checksum, so it wasn't used.");
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        // What was said in a WAV recording (16 kHz mono), or "" for nothing.
        public static string Transcribe(byte[] wav, string modelKey)
        {
            string file = Path.Combine(Path.GetTempPath(), "claude-notch-" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(file, wav);
            try
            {
                // Whisper always works on a 30-second window; telling it the clip is shorter
                // (50 frames a second, with room to spare) makes short questions several times faster.
                int ctx = (int)Math.Min(1500, Math.Max(512, MicRecorder.Seconds(wav) * 50 + 160));
                int threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1));
                string args = "-m \"" + Path.Combine(Folder, Find(modelKey).File) + "\" -f \"" + file + "\" -l en -nt -np -bs 1 -bo 1 -fa -t " + threads + " -ac " + ctx;
                var psi = new ProcessStartInfo(EngineExe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(EngineExe),
                };
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    string text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new TimeoutException("Transcribing took too long."); }
                    return Clean(text);
                }
            }
            finally { try { File.Delete(file); } catch { } }
        }

        // Whisper marks silence and noise like "[BLANK_AUDIO]" or "(wind blowing)".
        static string Clean(string text)
        {
            text = Regex.Replace(text ?? "", @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }
}
