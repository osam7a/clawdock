using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Media.SpeechRecognition;

namespace Wrenlet
{
    // Windows' modern dictation: the engine behind Win+H voice typing, far more accurate than the
    // old desktop engine in System.Speech. It needs "Online speech recognition" turned on in
    // Windows (Settings → Privacy & security → Speech); without it, quick voice falls back to the
    // old engine.
    //
    // The calls block until Windows answers (this compiler can't await WinRT operations), so call
    // them off the UI thread. Events arrive on a worker thread.
    class ModernDictation
    {
        public const int PrivacyNotAccepted = unchecked((int)0x80045509);

        public event Action<string> Hypothesis; // the words of the phrase being spoken, so far
        public event Action<string> Result;     // a finished phrase

        readonly SpeechRecognizer recognizer;

        public ModernDictation()
        {
            recognizer = new SpeechRecognizer();
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compile = recognizer.CompileConstraintsAsync();
            Wait(compile);
            var status = compile.GetResults().Status;
            if (status != SpeechRecognitionResultStatus.Success) throw new InvalidOperationException("dictation isn't available (" + status + ")");
            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(1); // quick voice decides when you're done
            recognizer.HypothesisGenerated += (s, e) => { if (Hypothesis != null) Hypothesis(e.Hypothesis.Text); };
            recognizer.ContinuousRecognitionSession.ResultGenerated += (s, e) =>
            {
                var r = e.Result;
                if (r.Status != SpeechRecognitionResultStatus.Success || r.Confidence == SpeechRecognitionConfidence.Rejected) return;
                if (!string.IsNullOrWhiteSpace(r.Text) && Result != null) Result(r.Text);
            };
        }

        // "Online speech recognition" is on in Windows' privacy settings.
        public static bool OnlineSpeechOn
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy"))
                        return key != null && Convert.ToInt32(key.GetValue("HasAccepted", 0)) == 1;
                }
                catch { return false; }
            }
        }

        public void Start()
        {
            try { Wait(recognizer.ContinuousRecognitionSession.StartAsync()); }
            catch (InvalidOperationException)
            {
                // Still running from last time: end that and start over.
                Cancel();
                Wait(recognizer.ContinuousRecognitionSession.StartAsync());
            }
        }

        // Finishes the phrase being spoken (its Result comes first), then stops.
        public void Stop()
        {
            try { Wait(recognizer.ContinuousRecognitionSession.StopAsync()); } catch { }
        }

        public void Cancel()
        {
            try { Wait(recognizer.ContinuousRecognitionSession.CancelAsync()); } catch { }
        }

        static void Wait(IAsyncInfo op)
        {
            var clock = Stopwatch.StartNew();
            while (op.Status == AsyncStatus.Started && clock.ElapsedMilliseconds < 10000) Thread.Sleep(15);
            if (op.Status == AsyncStatus.Error) throw op.ErrorCode;
            if (op.Status == AsyncStatus.Started) throw new TimeoutException("Windows speech recognition didn't answer");
        }
    }
}
