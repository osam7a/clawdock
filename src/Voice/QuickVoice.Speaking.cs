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
    // Quick voice: reading answers aloud.
    partial class QuickVoice
    {
        // ---- speaking ------------------------------------------------------

        static readonly Regex SentenceEnd = new Regex(@"[.!?…](?=\s)|\n", RegexOptions.Compiled);

        bool sourcesReached;

        // Passes the newly arrived speakable text (no card) on to be read aloud.
        void Feed()
        {
            string clean = WithoutCard(Answer);
            lock (unspoken)
            {
                if (clean.Length <= fed) return;
                unspoken.Append(clean.Substring(fed));
                fed = clean.Length;
            }
        }

        // Stops reading this answer aloud (Esc on the minimized bar); the text still arrives.
        public void Hush()
        {
            hushed = true;
            followUpDue = false;
            StopSpeaking();
            Raise();
        }
        bool hushed;

        void SpeakFinishedSentences(bool all)
        {
            if (!settings.QuickSpeak || hushed) return;
            string chunk;
            lock (unspoken)
            {
                string s = unspoken.ToString();
                int cut = all ? s.Length : -1;
                if (!all) { var m = SentenceEnd.Match(s); Match last = null; while (m.Success) { last = m; m = m.NextMatch(); } if (last != null) cut = last.Index + last.Length; }
                if (cut <= 0) return;
                chunk = s.Substring(0, cut);
                unspoken.Remove(0, cut);
                if (sourcesReached) return;
                // Web search answers end with a list of sources: skip it, it's links.
                var src = SourcesStart.Match(chunk);
                if (src.Success) { chunk = chunk.Substring(0, src.Index); sourcesReached = true; }
            }
            chunk = Speakable(chunk);
            if (chunk == "") return;
            if (UseCloud || UseNatural) { SayNatural(chunk); return; }
            try
            {
                if (voice == null)
                {
                    voice = new SpeechSynthesizer();
                    voice.SetOutputToDefaultAudioDevice();
                    voice.SpeakStarted += (s, e) => ui.BeginInvoke(new Action(() => { Speaking = true; Raise(); }));
                    voice.StateChanged += (s, e) => ui.BeginInvoke(new Action(() =>
                    {
                        bool now = voice.State == SynthesizerState.Speaking;
                        if (now == Speaking) return;
                        Speaking = now;
                        if (!now && State == Phase.Done) FinishedAt = DateTime.Now; // linger from the last word
                        Raise();
                    }));
                }
                if (!string.IsNullOrEmpty(settings.QuickVoiceName)) { try { voice.SelectVoice(settings.QuickVoiceName); } catch { } }
                voice.Rate = settings.QuickRate;
                voice.SpeakAsync(chunk);
                Speaking = true;
            }
            catch (Exception e) { Log.Write("Quick voice: speech: " + e.Message); }
        }

        void StopSpeaking()
        {
            lock (unspoken) unspoken.Length = 0;
            try { if (voice != null) voice.SpeakAsyncCancelAll(); } catch { }
            lock (toSay) toSay.Clear();
            if (player != null) player.Stop();
            Speaking = false;
        }

        // ---- the natural voice (KittenTTS) ---------------------------------

        AudioPlayer player;
        Thread sayThread;
        readonly Queue<string> toSay = new Queue<string>();
        readonly AutoResetEvent sayWake = new AutoResetEvent(false);
        volatile bool synthesizing, naturalBroken;

        // The natural voice reads when it's picked, or in place of ElevenLabs while that's not working.
        bool UseNatural
        {
            get { return (settings.QuickTts == "natural" || (settings.QuickTts == "elevenlabs" && !UseCloud)) && !naturalBroken && NaturalVoice.Supported && NaturalVoice.Installed; }
        }

        // ---- ElevenLabs ----------------------------------------------------

        // After a failure (offline, a bad key, out of credits) ElevenLabs rests for a few minutes,
        // and the natural or Windows voice reads instead.
        DateTime cloudBrokenUntil = DateTime.MinValue;
        string sealedKey, cloudKey;
        public static string LastCloudError = "";

        bool UseCloud { get { return settings.QuickTts == "elevenlabs" && settings.ElevenLabsKey != "" && DateTime.Now >= cloudBrokenUntil; } }

        string CloudKey
        {
            get
            {
                if (sealedKey != settings.ElevenLabsKey) { sealedKey = settings.ElevenLabsKey; cloudKey = ElevenLabs.Unseal(sealedKey); }
                return cloudKey;
            }
        }

        // Sentences are made into audio one at a time on their own thread, and each plays as soon
        // as it's ready (the next one is made while it plays).
        void SayNatural(string text)
        {
            if (player == null) player = new AudioPlayer();
            if (sayThread == null) { sayThread = new Thread(SayLoop) { IsBackground = true, Name = "Natural voice" }; sayThread.Start(); }
            lock (toSay) toSay.Enqueue(text);
            sayWake.Set();
            if (!Speaking) { Speaking = true; Raise(); }
        }

        void SayLoop()
        {
            string previous = "";
            int lastGeneration = -1;
            while (true)
            {
                string text = null;
                int generation;
                lock (toSay)
                {
                    if (toSay.Count > 0) { text = toSay.Dequeue(); synthesizing = true; }
                    generation = player.Generation;
                }
                if (text == null) { sayWake.WaitOne(); continue; }
                if (generation != lastGeneration) { previous = ""; lastGeneration = generation; } // cut off: a new answer
                bool cloud = UseCloud;
                try
                {
                    int rate;
                    float[] samples;
                    if (cloud) samples = ElevenLabs.Generate(CloudKey, settings.ElevenLabsVoice, text, previous, ElevenLabs.Speed(settings.QuickRate), out rate);
                    else samples = NaturalVoice.Generate(text, settings.QuickNaturalVoice, NaturalVoice.Speed(settings.QuickRate), out rate);
                    player.Enqueue(NaturalVoice.Trim(samples, rate), rate, generation); // dropped if you cut it off meanwhile
                    previous = text;
                    if (cloud) LastCloudError = "";
                }
                catch (Exception e)
                {
                    // ElevenLabs: rest it for a few minutes. The natural voice: something's wrong
                    // with the download or this PC, so the Windows voice reads from now on.
                    if (cloud) { Log.Write("ElevenLabs: " + e.Message); LastCloudError = e.Message; cloudBrokenUntil = DateTime.Now.AddMinutes(5); }
                    else { Log.Write("Natural voice: " + e.Message); naturalBroken = true; }
                    string rest = text;
                    ui.BeginInvoke(new Action(() => { lock (unspoken) unspoken.Insert(0, rest + " "); SpeakFinishedSentences(false); }));
                }
                finally { synthesizing = false; }
            }
        }

        // A changed key or voice gets another try right away.
        public void SettingsChanged()
        {
            cloudBrokenUntil = DateTime.MinValue;
        }

        bool NaturalSpeaking
        {
            get { lock (toSay) { if (toSay.Count > 0) return true; } return synthesizing || (player != null && player.Playing); }
        }

        // How loud the answer being read is right now, 0..1; -1 when it can't be measured (the Windows voice).
        public double OutputLevel
        {
            get { return player != null && (player.Playing || UseNatural || UseCloud) ? player.Level : Speaking ? -1 : 0; }
        }

        // Loads the natural voice ahead of the first answer.
        public void PrepareVoice()
        {
            if (settings.QuickSpeak && UseNatural) NaturalVoice.Preload();
        }

        // Markdown and links read badly aloud, even though the instructions ask for none.
        public static string Speakable(string s)
        {
            s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [text](link) -> text
            s = Regex.Replace(s, @"https?://\S+", "");
            s = Regex.Replace(s, @"```[\s\S]*?```", " ");
            s = Regex.Replace(s, @"(?m)^\s*\|.*$", " ");               // a table doesn't read well
            s = Regex.Replace(s, @"(?m)^\s*(-{3,}|\*{3,}|_{3,})\s*$", " ");
            s = Regex.Replace(s, @"(?m)^\s*(#+|[-*+•>]|\d+[.)])\s+", "");
            s = Regex.Replace(s, @"(?<![A-Za-z0-9_])_(?=\S)(.+?)(?<=\S)_(?![A-Za-z0-9_])", "$1");
            s = s.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "").Replace("*", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string[] InstalledVoices()
        {
            try
            {
                using (var s = new SpeechSynthesizer())
                {
                    var names = new List<string>();
                    foreach (var v in s.GetInstalledVoices()) if (v.Enabled) names.Add(v.VoiceInfo.Name);
                    return names.ToArray();
                }
            }
            catch { return new string[0]; }
        }

        void Raise()
        {
            if (Changed != null) Changed();
        }
    }
}
