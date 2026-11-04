using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClaudeNotch
{
    // A natural-sounding voice on this PC: KittenTTS (Apache 2.0, github.com/KittenML/KittenTTS),
    // run by sherpa-onnx (Apache 2.0, github.com/k2-fsa/sherpa-onnx). The Nano model speaks about
    // six times faster than real time on a laptop CPU, so a sentence is ready well before the one
    // before it has been read out.
    //
    // Downloaded once, from settings, into %LOCALAPPDATA%\ClaudeNotch\kitten, and checked against
    // known SHA-256s. The engine build has its C runtime built in and needs only Windows' own DLLs,
    // so it works on any 64-bit x64 PC. The model stays loaded (in this process) after first use.
    static class NaturalVoice
    {
        // In speaker-ID order (sid 0..7): the model's male and female voices alternate.
        public static readonly string[] Voices = { "Jasper", "Bella", "Bruno", "Luna", "Hugo", "Rosie", "Leo", "Kiki" };
        public static string VoiceLabel(int sid) { return Voices[sid] + (sid % 2 == 0 ? " (male)" : " (female)"); }

        const string EngineUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release.tar.bz2";
        const string EngineSha256 = "6dffdc715a4465b989446a6105265d2cb345e7101591a17d35534b6758f6e8df";
        const long EngineBytes = 24805859;
        // 1.x, not 2.x: 2.0 dropped the built-in pronunciation (espeak-ng) that reads any word, names included.
        const string EngineLib = "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release/lib/";
        static readonly string[] EngineFiles = { "sherpa-onnx-c-api.dll", "onnxruntime.dll", "onnxruntime_providers_shared.dll" };

        const string ModelName = "kitten-nano-en-v0_8-fp32"; // fp32: the int8 build is slower on a CPU
        const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + ModelName + ".tar.bz2";
        const string ModelSha256 = "16092117bfe591ddcd58d078e1454603b8e1caea46f85653b2c2efae76bd883e";
        const long ModelBytes = 63815222;

        public static readonly string Folder = Path.Combine(Settings.DataDir, "kitten");
        static string EngineDir { get { return Path.Combine(Folder, "engine"); } }
        static string ModelDir { get { return Path.Combine(Folder, ModelName); } }
        static string Tar { get { return Path.Combine(Environment.SystemDirectory, "tar.exe"); } }

        // The engine is built for x64 (on an ARM PC the Windows voices are used), and unpacking
        // needs the tar.exe that comes with Windows 10 1803 and later.
        public static bool Supported
        {
            get
            {
                return Environment.Is64BitProcess && File.Exists(Tar)
                    && string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "AMD64", StringComparison.OrdinalIgnoreCase);
            }
        }

        static bool EngineInstalled
        {
            get { foreach (string f in EngineFiles) if (!File.Exists(Path.Combine(EngineDir, f))) return false; return true; }
        }

        static bool ModelInstalled
        {
            get
            {
                return File.Exists(Path.Combine(ModelDir, "model.fp32.onnx")) && File.Exists(Path.Combine(ModelDir, "voices.bin"))
                    && File.Exists(Path.Combine(ModelDir, "tokens.txt")) && Directory.Exists(Path.Combine(ModelDir, "espeak-ng-data"));
            }
        }

        public static bool Installed { get { return EngineInstalled && ModelInstalled; } }
        public static long DownloadBytes { get { return (EngineInstalled ? 0 : EngineBytes) + (ModelInstalled ? 0 : ModelBytes); } }

        // Downloads whatever is missing. progress: 0..1. Runs on the calling thread.
        public static void Install(Action<double> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Folder);
            long total = DownloadBytes, done = 0;
            if (!EngineInstalled)
            {
                string archive = Path.Combine(Folder, "engine.tar.bz2");
                LocalSpeech.Download(EngineUrl, archive, EngineSha256, n => progress((done + n) / (double)total), cancelled);
                done += EngineBytes;
                // Only the three DLLs, not the engine's command-line tools.
                var members = new StringBuilder();
                foreach (string f in EngineFiles) members.Append(" \"").Append(EngineLib).Append(f).Append('"');
                string tmp = Fresh(Path.Combine(Folder, "engine.tmp"));
                Untar(archive, tmp, "--strip-components=2" + members);
                Delete(EngineDir);
                Directory.Move(tmp, EngineDir);
                File.Delete(archive);
            }
            if (!ModelInstalled)
            {
                string archive = Path.Combine(Folder, "model.tar.bz2");
                LocalSpeech.Download(ModelUrl, archive, ModelSha256, n => progress((done + n) / (double)total), cancelled);
                string tmp = Fresh(Path.Combine(Folder, "model.tmp"));
                Untar(archive, tmp, "");
                Delete(ModelDir);
                Directory.Move(Path.Combine(tmp, ModelName), ModelDir);
                Delete(tmp);
                File.Delete(archive);
            }
            if (!Installed) throw new IOException("The download was missing some of the voice's files.");
            progress(1);
        }

        static void Untar(string archive, string dir, string members)
        {
            var psi = new ProcessStartInfo(Tar, "-xjf \"" + archive + "\" -C \"" + dir + "\" " + members)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new IOException("Couldn't unpack " + Path.GetFileName(archive) + ": " + err.Trim());
            }
        }

        static string Fresh(string dir) { Delete(dir); Directory.CreateDirectory(dir); return dir; }
        static void Delete(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        // ---- speaking ------------------------------------------------------

        static readonly object gate = new object();
        static IntPtr tts;

        // Says text in voice sid (0..7) at speed (1 = normal): mono samples, -1..1.
        public static float[] Generate(string text, int sid, float speed, out int sampleRate)
        {
            lock (gate)
            {
                Load();
                IntPtr t = Utf8(text);
                try
                {
                    IntPtr audio = SherpaOnnxOfflineTtsGenerate(tts, t, Math.Max(0, Math.Min(Voices.Length - 1, sid)), speed);
                    if (audio == IntPtr.Zero) throw new IOException("The natural voice couldn't say that.");
                    try
                    {
                        var a = (GeneratedAudio)Marshal.PtrToStructure(audio, typeof(GeneratedAudio));
                        var samples = new float[Math.Max(0, a.n)];
                        if (a.n > 0) Marshal.Copy(a.samples, samples, 0, a.n);
                        sampleRate = a.sample_rate;
                        return samples;
                    }
                    finally { SherpaOnnxDestroyOfflineTtsGeneratedAudio(audio); }
                }
                finally { Marshal.FreeHGlobal(t); }
            }
        }

        // The speed for the -5..5 reading speed setting (shared with the Windows voice). Kitten's
        // own pace is slow (about 110 words a minute), so "Normal" is 1.4 times that.
        public static float Speed(int rate)
        {
            return (float)Math.Max(0.9, Math.Min(2.0, 1.4 + rate * 0.1));
        }

        // Cuts the silence the model leaves at each end of a sentence down to a short pause, so
        // sentences follow each other the way people talk.
        public static float[] Trim(float[] samples, int sampleRate)
        {
            const float Quiet = 0.01f;
            int first = 0, last = samples.Length - 1;
            while (first < samples.Length && Math.Abs(samples[first]) < Quiet) first++;
            while (last > first && Math.Abs(samples[last]) < Quiet) last--;
            if (first >= samples.Length) return samples;
            first = Math.Max(0, first - sampleRate * 30 / 1000);                 // keep 30 ms before
            last = Math.Min(samples.Length - 1, last + sampleRate * 80 / 1000);  // and 80 ms after
            var trimmed = new float[last - first + 1];
            Array.Copy(samples, first, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        // Loads the model in the background (about a second), so the first sentence isn't late.
        public static void Preload()
        {
            if (tts != IntPtr.Zero || !Installed) return;
            ThreadPool.QueueUserWorkItem(_ => { try { lock (gate) Load(); } catch (Exception e) { Log.Write("Natural voice: " + e.Message); } });
        }

        static void Load()
        {
            if (tts != IntPtr.Zero) return;
            if (!Installed) throw new FileNotFoundException("The natural voice isn't downloaded.");
            // Loaded by full path; its onnxruntime.dll is found next to it.
            if (LoadLibraryEx(Path.Combine(EngineDir, "sherpa-onnx-c-api.dll"), IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't load the voice engine");
            var config = new TtsConfig();
            config.model.kitten.model = Keep(ModelPath("model.fp32.onnx"));
            config.model.kitten.voices = Keep(ModelPath("voices.bin"));
            config.model.kitten.tokens = Keep(ModelPath("tokens.txt"));
            config.model.kitten.data_dir = Keep(ModelPath("espeak-ng-data"));
            config.model.kitten.length_scale = 1;
            config.model.num_threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            config.model.provider = Keep("cpu");
            config.max_num_sentences = 1;
            config.silence_scale = 0.2f;
            tts = SherpaOnnxCreateOfflineTts(ref config);
            if (tts == IntPtr.Zero) throw new IOException("Couldn't load the natural voice.");
        }

        // The engine may open files with narrow (ANSI) paths, so a user folder with non-English
        // letters could break it; the short 8.3 form of such a path is plain ASCII. Short names
        // aren't always reliable (or turned on), so only when needed, and only if it really resolves.
        static string ModelPath(string name)
        {
            string path = Path.Combine(ModelDir, name);
            bool ascii = true;
            foreach (char c in path) if (c > 127) { ascii = false; break; }
            if (ascii) return path;
            var sb = new StringBuilder(1024);
            uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
            string shortPath = n > 0 && n < sb.Capacity ? sb.ToString() : null;
            return shortPath != null && (File.Exists(shortPath) || Directory.Exists(shortPath)) ? shortPath : path;
        }

        static IntPtr Keep(string s) { return Utf8(s); } // config strings live as long as the voice (the process)

        static IntPtr Utf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }

        // ---- sherpa-onnx C API (c-api.h of v1.13.8; these layouts must match that version) ----

        [StructLayout(LayoutKind.Sequential)] struct Vits { public IntPtr model, lexicon, tokens, data_dir; public float noise_scale, noise_scale_w, length_scale; public IntPtr dict_dir; }
        [StructLayout(LayoutKind.Sequential)] struct Matcha { public IntPtr acoustic_model, vocoder, lexicon, tokens, data_dir; public float noise_scale, length_scale; public IntPtr dict_dir; }
        [StructLayout(LayoutKind.Sequential)] struct Kokoro { public IntPtr model, voices, tokens, data_dir; public float length_scale; public IntPtr dict_dir, lexicon, lang; }
        [StructLayout(LayoutKind.Sequential)] struct Kitten { public IntPtr model, voices, tokens, data_dir; public float length_scale; }
        [StructLayout(LayoutKind.Sequential)] struct Zipvoice { public IntPtr tokens, encoder, decoder, vocoder, data_dir, lexicon; public float feat_scale, t_shift, target_rms, guidance_scale; }
        [StructLayout(LayoutKind.Sequential)] struct Pocket { public IntPtr lm_flow, lm_main, encoder, decoder, text_conditioner, vocab_json, token_scores_json; public int voice_embedding_cache_capacity; }
        [StructLayout(LayoutKind.Sequential)] struct Supertonic { public IntPtr duration_predictor, text_encoder, vector_estimator, vocoder, tts_json, unicode_indexer, voice_style; }

        [StructLayout(LayoutKind.Sequential)]
        struct ModelConfig
        {
            public Vits vits;
            public int num_threads, debug;
            public IntPtr provider;
            public Matcha matcha;
            public Kokoro kokoro;
            public Kitten kitten;
            public Zipvoice zipvoice;
            public Pocket pocket;
            public Supertonic supertonic;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TtsConfig
        {
            public ModelConfig model;
            public IntPtr rule_fsts;
            public int max_num_sentences;
            public IntPtr rule_fars;
            public float silence_scale;
        }

        [StructLayout(LayoutKind.Sequential)] struct GeneratedAudio { public IntPtr samples; public int n, sample_rate; }

        [DllImport("sherpa-onnx-c-api.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SherpaOnnxCreateOfflineTts(ref TtsConfig config);
        [DllImport("sherpa-onnx-c-api.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SherpaOnnxOfflineTtsGenerate(IntPtr tts, IntPtr text, int sid, float speed);
        [DllImport("sherpa-onnx-c-api.dll", CallingConvention = CallingConvention.Cdecl)] static extern void SherpaOnnxDestroyOfflineTtsGeneratedAudio(IntPtr audio);

        const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
    }
}
