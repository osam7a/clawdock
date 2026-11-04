using System;
using System.Threading;
using System.Windows.Threading;

namespace Clawdock
{
    // A new install gets quick voice's speech recognition and natural voice without a trip to the
    // settings. Until they arrive, Windows' own speech and voices are used. Each file is checked
    // against its known SHA-256 (see LocalSpeech and NaturalVoice). If a download fails, it tries
    // again the next time the app starts.
    static class VoiceSetup
    {
        public static void Start(Settings settings, Action<bool> done)
        {
            var ui = Dispatcher.CurrentDispatcher;
            string engine = settings.QuickEngine, model = settings.QuickLocalModel, tts = settings.QuickTts;
            if (settings.VoiceMode != "quick") { done(true); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = true;
                try
                {
                    if (engine == "local" && !LocalSpeech.Installed(model)) LocalSpeech.Install(model, f => { }, () => false);
                    if (tts == "natural" && NaturalVoice.Supported && !NaturalVoice.Installed) NaturalVoice.Install(f => { }, () => false);
                }
                catch (Exception e)
                {
                    ok = false;
                    Log.Write("Couldn't download quick voice's speech or voice: " + e.Message);
                }
                ui.BeginInvoke(new Action(() => done(ok)));
            });
        }
    }
}
