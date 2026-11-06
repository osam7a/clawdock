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
    // Quick voice: the typed chat, and moving a chat into the Claude app.
    partial class QuickVoice
    {
        // ---- typing ---------------------------------------------------------

        // The text box is open: the same conversation, typed, and not read aloud.
        public bool Typing { get; private set; }
        bool typedTurn;

        public void OpenTyping()
        {
            CutOffForNewTurn();
            shrunk = false;
            Typing = true;
            lastUsed = DateTime.Now;
            State = Phase.Done;
            FinishedAt = DateTime.Now;
            tick.Start();
            Raise();
        }

        public void SendTyped(string text)
        {
            text = (text ?? "").Trim();
            if (text == "") return;
            CutOffForNewTurn();
            Heard = text;
            typedTurn = true;
            Ask(text);
        }

        // Whatever is going on (listening, reading, an answer coming in) stops, and the turn on
        // screen moves up into the history.
        void CutOffForNewTurn()
        {
            followUpDue = followUp = false;
            if (State == Phase.Listening) { stopping = true; StopRecognizer(); }
            StopSpeaking();
            bool cut = (State == Phase.Thinking && !transcribing) || State == Phase.Answering;
            if (cut) Interrupt();
            SaveTurn(cut);
            transcribing = false;
            turnNotes.Clear();
            Notice = "";
            Heard = Partial = Answer = Status = "";
        }

        // ---- opening the chat in Claude ---------------------------------------

        public string Notice { get; private set; } // about opening it, under the chat
        string savedId;  // a private chat saved to Claude, and how many turns it had then
        int savedTurns;

        // VOICE_CHATS is already in your history. A private chat isn't saved anywhere, so asking
        // for it here saves what's on the notch as a new session, by telling Claude Code to keep
        // it. Either way the Claude app opens the session (claude://resume imports it).
        public void OpenInClaude()
        {
            if (settings.QuickHistory == "history")
            {
                string id = HistorySession();
                if (id != null) VoiceLauncher.OpenLink("claude://resume?session=" + id);
                else { Notice = "VOICE_CHATS has nothing in it yet."; Raise(); }
                return;
            }
            var turns = new List<QuickTurn>(history);
            if (Heard != "" && State != Phase.Listening) turns.Add(Current);
            if (turns.Count == 0) { Notice = "Nothing to open yet."; Raise(); return; }
            if (savedId != null && savedTurns == turns.Count) { VoiceLauncher.OpenLink("claude://resume?session=" + savedId); return; }

            var text = new StringBuilder("This is a conversation I had with you through Clawdock's quick voice. Keep it as the record: I may ask about it or continue it here. Reply only with: Saved.\n\n");
            foreach (var t in turns)
            {
                text.Append("Me: ").Append(t.Heard).Append("\n\n");
                foreach (string note in t.Notes) text.Append("(").Append(note).Append(")\n\n");
                if (t.Text != "") text.Append("Claude: ").Append(t.Text).Append("\n\n");
            }
            string count = DateTime.Now.ToString("MMM d, h:mm tt");
            int total = turns.Count;
            Notice = "Saving this chat to Claude…";
            Raise();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string id = SaveChat(text.ToString(), "Quick voice, " + count);
                ui.BeginInvoke(new Action(() =>
                {
                    if (id == null) { Notice = "Couldn't save this chat to Claude."; Raise(); return; }
                    savedId = id;
                    savedTurns = total;
                    Notice = "";
                    Raise();
                    VoiceLauncher.OpenLink("claude://resume?session=" + id);
                }));
            });
        }

        // A short run of Claude Code with nothing but the conversation. Returns its session id.
        string SaveChat(string text, string name)
        {
            try
            {
                string exe = FindClaude();
                if (exe == null) return null;
                Directory.CreateDirectory(Folder);
                string id = Guid.NewGuid().ToString();
                var args = new List<string> { "-p", "--session-id", id, "-n", name, "--model", "haiku", "--tools", "", "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands" };
                var psi = new ProcessStartInfo(exe, JoinArgs(args))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Write("Quick voice: saving the chat: " + e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return null; }
                    return p.ExitCode == 0 ? id : null;
                }
            }
            catch (Exception e) { Log.Write("Quick voice: saving the chat: " + e.Message); return null; }
        }

        // Forgets this private conversation: the chat on the notch and what Claude remembers of it.
        // The panel closes, and the next question starts a new one.
        public void ClearChat()
        {
            followUpDue = followUp = false;
            Typing = false;
            if (State == Phase.Listening) { stopping = true; StopRecognizer(); }
            StopSpeaking();
            KillClaude();
            ignoreTurns = 0;
            transcribing = false;
            savedId = null;
            Notice = "";
            history.Clear();
            turnNotes.Clear();
            Heard = Partial = Answer = Status = "";
            State = Phase.Idle;
            tick.Stop();
            Raise();
        }
    }
}
