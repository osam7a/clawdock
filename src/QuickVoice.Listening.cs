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

namespace ClaudeNotch
{
    // Quick voice: hearing you, and listening again for a follow-up.
    partial class QuickVoice
    {
        // ---- listening -----------------------------------------------------

        void StartListening()
        {
            if (!VoiceLauncher.HasMicrophone()) { Fail("No microphone found."); return; }

            heardParts.Clear();
            followUp = followUpDue = false;
            Notice = "";
            turnNotes.Clear();
            Heard = Partial = Answer = Status = "";
            held = false; holdDecided = false; stopping = false; voiced = false; transcribing = false;
            listenId++;
            Level = 0;
            MicReady = settings.QuickEngine != "local"; // the built-in engine says when audio is flowing
            ListenStarted = lastSpeech = DateTime.Now;
            State = Phase.Listening;
            ThreadPool.QueueUserWorkItem(_ => { try { EnsureClaude(); } catch (Exception e) { Log.Write("Quick voice: " + e.Message); } }); // warm up while you talk
            PrepareVoice();
            tick.Start();
            Raise();

            // The built-in engine (whisper.cpp) when it's chosen and downloaded.
            listeningLocal = settings.QuickEngine == "local" && LocalSpeech.Installed(settings.QuickLocalModel);
            if (listeningLocal) { listeningModern = false; StartLocal(); return; }

            // Windows' modern dictation when it's allowed (much better), else the old engine.
            listeningModern = !modernBroken && (!privacyBlocked || ModernDictation.OnlineSpeechOn);
            if (!listeningModern) { StartLegacy(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool fresh = false;
                try
                {
                    lock (listenGate)
                    {
                        if (modern == null)
                        {
                            fresh = true;
                            var m = new ModernDictation();
                            m.Hypothesis += t => ui.BeginInvoke(new Action(() => OnPartial(t)));
                            m.Result += t => ui.BeginInvoke(new Action(() => OnPhrase(t)));
                            modern = m;
                        }
                        modern.Start();
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Quick voice: modern speech recognition: 0x" + e.HResult.ToString("X") + " " + e.Message);
                    // A recognizer whose start failed stays unusable, so make a new one next time. Only
                    // a brand-new one failing (for a reason other than the privacy setting) means
                    // modern dictation doesn't work here.
                    lock (listenGate) modern = null;
                    if (e.HResult == ModernDictation.PrivacyNotAccepted) privacyBlocked = true;
                    else if (fresh) modernBroken = true;
                    ui.BeginInvoke(new Action(() => { if (State == Phase.Listening && !stopping) { listeningModern = false; StartLegacy(); } }));
                }
            });
        }

        void StartLocal()
        {
            try
            {
                if (mic == null)
                {
                    mic = new MicRecorder();
                    mic.Level += (level, speech) => ui.BeginInvoke(new Action(() =>
                    {
                        if (State != Phase.Listening) return;
                        Level = level;
                        if (!MicReady) { MicReady = true; Raise(); }
                        if (speech) { voiced = true; lastSpeech = DateTime.Now; }
                    }));
                }
                mic.Start();
            }
            catch (Exception e) { Log.Write("Quick voice: microphone: " + e.Message); Fail("Couldn't use the microphone."); }
        }

        // The recording is done: transcribe it on this PC, then ask.
        void FinishLocal()
        {
            byte[] wav = mic.Stop();
            if (!voiced || MicRecorder.Seconds(wav) < 0.3) { Fail("Didn't catch that."); return; }
            State = Phase.Thinking;
            Status = "Transcribing…";
            transcribing = true;
            Raise();
            int id = listenId;
            string model = settings.QuickLocalModel;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text = null;
                var clock = Stopwatch.StartNew();
                try { text = LocalSpeech.Transcribe(wav, model); }
                catch (Exception e) { Log.Write("Quick voice: transcribing: " + e.Message); }
                if (Log.Verbose) Log.Write("Quick voice: transcribed " + MicRecorder.Seconds(wav).ToString("0.0") + " s in " + clock.ElapsedMilliseconds + " ms");
                ui.BeginInvoke(new Action(() =>
                {
                    if (id != listenId || !transcribing) return; // cut off meanwhile
                    transcribing = false;
                    if (string.IsNullOrEmpty(text)) { Fail(text == null ? "Couldn't transcribe that." : "Didn't catch that."); return; }
                    Heard = text;
                    Ask(text);
                }));
            });
        }

        void StartLegacy()
        {
            try { EnsureRecognizer(); }
            catch (Exception e) { Log.Write("Quick voice: speech recognition: " + e.Message); Fail("Windows speech recognition isn't available."); return; }
            try { recognizer.RecognizeAsync(RecognizeMode.Multiple); }
            catch (InvalidOperationException) { } // still running from last time
            catch (Exception e) { Log.Write("Quick voice: " + e.Message); Fail("Couldn't use the microphone."); }
        }

        void OnPartial(string text)
        {
            if (State != Phase.Listening) return;
            Partial = text;
            lastSpeech = DateTime.Now;
            Raise();
        }

        void OnPhrase(string text)
        {
            if (State != Phase.Listening) return;
            heardParts.Add(text);
            Heard = string.Join(" ", heardParts.ToArray());
            Partial = "";
            lastSpeech = DateTime.Now;
            Raise();
        }

        void EnsureRecognizer()
        {
            if (recognizer != null) return;
            RecognizerInfo info = null;
            foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (r.Culture.Equals(CultureInfo.CurrentUICulture)) { info = r; break; }
                if (info == null || r.Culture.Name == "en-US") info = r;
            }
            if (info == null) throw new InvalidOperationException("no recognizer installed");
            var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechHypothesized += (s, e) => { string t = e.Result.Text; ui.BeginInvoke(new Action(() => OnPartial(t))); };
            engine.SpeechRecognized += (s, e) => { string t = e.Result.Text; ui.BeginInvoke(new Action(() => OnPhrase(t))); };
            engine.SpeechDetected += (s, e) => ui.BeginInvoke(new Action(() => { lastSpeech = DateTime.Now; }));
            engine.RecognizeCompleted += (s, e) => ui.BeginInvoke(new Action(OnRecognizeCompleted));
            recognizer = engine;
        }

        void OnTick()
        {
            var now = DateTime.Now;
            if (State == Phase.Listening && !stopping)
            {
                bool any = heardParts.Count > 0 || Partial != "" || voiced;
                if (!holdDecided && now - ListenStarted >= HoldThreshold) { holdDecided = true; held = Hotkeys.Held; }
                if (held && !Hotkeys.Held) FinishListening();              // let go of the key
                else if (!held && holdDecided && any && now - lastSpeech > EndSilence) FinishListening();
                else if (!any && followUp && now - ListenStarted > FollowUpWait) CancelFollowUp();
                else if (!any && now - ListenStarted > NothingHeard && !Hotkeys.Held) FinishListening();
                else if (now - ListenStarted > MaxListen) FinishListening();
            }
            if (player != null && settings.QuickTts != "windows" && (Speaking || player.Playing))
            {
                bool talking = NaturalSpeaking;
                if (talking != Speaking)
                {
                    Speaking = talking;
                    if (!talking && State == Phase.Done) FinishedAt = now; // linger from the last word
                    Raise();
                }
            }
            // The answer ended on a question and it's been read: listen for the reply.
            if (State == Phase.Done && followUpDue && !Speaking && now - FinishedAt > FollowUpPause) { followUpDue = false; StartFollowUp(); return; }
            if (State == Phase.Done && KeepOpen != null && KeepOpen()) FinishedAt = now;
            // Minimized, the bar stays until you close it (or the conversation times out).
            if (State == Phase.Done && Persist != null && Persist() && now - lastUsed < IdleExit) FinishedAt = now;
            if (State == Phase.Done && !Speaking && now - FinishedAt > Linger) { SaveTurn(false); State = Phase.Idle; Raise(); }
            if (State == Phase.Error && now - FinishedAt > TimeSpan.FromSeconds(8)) { State = Typing ? Phase.Done : Phase.Idle; Status = Typing ? "" : Status; Raise(); }
            if (State == Phase.Idle) tick.Stop();
        }

        // ---- follow-ups ----------------------------------------------------

        // Claude asked something: the microphone opens by itself, and if you don't answer it
        // closes again and the answer stays on screen as it was.
        static readonly TimeSpan FollowUpPause = TimeSpan.FromMilliseconds(350); // so it doesn't hear the voice's last word
        static readonly TimeSpan FollowUpWait = TimeSpan.FromSeconds(7);
        bool followUpDue, followUp;
        string followUpHeard = "", followUpAnswer = "";

        // Listening for the reply to a question, rather than because you pressed the shortcut.
        public bool FollowingUp { get { return State == Phase.Listening && followUp; } }

        void StartFollowUp()
        {
            followUpHeard = Heard;
            followUpAnswer = Answer;
            SaveTurn(false);
            StartListening();
            followUp = State == Phase.Listening;
            if (followUp) { Status = "Go ahead, I'm listening"; Raise(); }
        }

        // No reply (or Esc): back to the answer as it was.
        public void CancelFollowUp()
        {
            if (!FollowingUp) return;
            stopping = true;
            StopRecognizer();
            followUp = false;
            if (history.Count > 0) { turnNotes = history[history.Count - 1].Notes; history.RemoveAt(history.Count - 1); }
            Heard = followUpHeard;
            Answer = followUpAnswer;
            Partial = Status = "";
            State = Phase.Done;
            FinishedAt = DateTime.Now;
            tick.Start();
            Raise();
        }

        void FinishListening()
        {
            if (stopping) return;
            if (FollowingUp && heardParts.Count == 0 && Partial == "" && !voiced) { CancelFollowUp(); return; }
            stopping = true;
            if (listeningLocal) { FinishLocal(); return; }
            if (listeningModern)
            {
                // Stopping finishes the phrase being spoken; its result is queued before this.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (listenGate) { if (modern != null) modern.Stop(); }
                    ui.BeginInvoke(new Action(OnRecognizeCompleted));
                });
                return;
            }
            // Stopping lets the words being recognized finish; RecognizeCompleted follows.
            try { recognizer.RecognizeAsyncStop(); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (s, e) => { t.Stop(); if (State == Phase.Listening) { StopRecognizer(); OnRecognizeCompleted(); } };
            t.Start();
        }

        void OnRecognizeCompleted()
        {
            if (State != Phase.Listening || !stopping) return;
            string text = string.Join(" ", heardParts.ToArray()).Trim();
            if (text == "") text = Partial.Trim();
            Partial = "";
            if (text == "") { Fail("Didn't catch that."); return; }
            Heard = text;
            Ask(text);
        }

        void StopRecognizer()
        {
            if (listeningLocal && mic != null) { try { mic.Stop(); } catch { } }
            try { if (recognizer != null) recognizer.RecognizeAsyncCancel(); } catch { }
            if (modern != null) ThreadPool.QueueUserWorkItem(_ => { lock (listenGate) { if (modern != null) modern.Cancel(); } });
        }
    }
}
