using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // What clicks and shortcuts do.
    partial class Controller
    {
        // ---- actions -------------------------------------------------------

        void OpenPrimary()
        {
            // During quick voice a click is about the conversation: the small bar opens up again.
            if (quick.Active && pending.Count == 0 && shownKind != "alert")
            {
                if (settings.QuickMinimized) ToggleQuickMinimized();
                return;
            }
            var s = Primary();
            if (s != null) OpenSession(s);
            else watcher.RestoreClaude();
        }

        void OpenSession(SessionInfo s)
        {
            if (!s.Desktop && s.Host != IntPtr.Zero)
            {
                if (!TerminalFocus.Focus(s.Host)) tray.Balloon("Couldn't bring that terminal to the front.");
                return;
            }
            if (!s.Desktop && s.Host == IntPtr.Zero && s.Pid > 0)
            {
                tray.Balloon("Couldn't find the window for " + s.DisplayName + ".");
                return;
            }
            watcher.RestoreClaude();
            if (!string.IsNullOrEmpty(s.Name) && s.Name != watcher.SessionTitle) SelectInSidebar(s.Name);
        }

        // Best effort: click the session's entry in Claude's sidebar. Sidebar entries are plain
        // buttons named after the session, so a session called e.g. "Settings" could match an
        // action button: only click when exactly one candidate exists, never in a toolbar.
        static void SelectInSidebar(string name)
        {
            IntPtr h = Native.FindClaudeWindow();
            if (h == IntPtr.Zero) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var win = AutomationElement.FromHandle(h);
                    var named = new PropertyCondition(AutomationElement.NameProperty, name);
                    var walker = TreeWalker.ControlViewWalker;
                    var candidates = new List<AutomationElement>();
                    foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, named))
                    {
                        var type = e.Current.ControlType;
                        if (type != ControlType.Button && type != ControlType.ListItem && type != ControlType.TreeItem) continue;
                        var parent = walker.GetParent(e);
                        if (parent != null && (parent.Current.ControlType == ControlType.ToolBar || parent.Current.ControlType == ControlType.MenuBar)) continue;
                        candidates.Add(e);
                    }
                    if (candidates.Count != 1) return;
                    object pattern;
                    if (candidates[0].TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
                    else if (candidates[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select();
                }
                catch { }
            });
        }

        void Dismiss()
        {
            // A request from one of your chats is up, over quick voice: × closes only that.
            if (shownKind == "alert" && quick.Active && (pending.Count == 0 || !QuickVoice.IsVoiceFolder(pending[0].Cwd)))
            {
                CloseShownRequest();
                Refresh();
                return;
            }
            if (quick.Active)
            {
                // Stop quick voice, and say no to anything it was waiting to do.
                foreach (var p in pending.ToArray())
                    if (QuickVoice.IsVoiceFolder(p.Cwd)) Decline(p, "Not allowed");
                quick.Dismiss();
                return;
            }
            if (watcher.InVoice) watcher.EndVoice();
            ReleasePending(null);
            alerts.Clear();
            questions.Clear(); // they're still in Claude; the notch just stops asking
            var d = settings.DismissDuration;
            dismissedUntil = d == TimeSpan.Zero ? DateTime.MaxValue : DateTime.Now + d;
            Refresh();
        }

        string shownKind = "";

        // The request on the notch, and only that one, so any others waiting behind it stay:
        // to Claude's own prompt (quick voice's is a no, noted in its chat).
        void CloseShownRequest()
        {
            if (pending.Count > 0) HandBack(pending[0]);
            else if (alerts.Count > 0) alerts.RemoveAt(alerts.Count - 1);
        }

        // Esc closes what's up on the notch: the quick voice panel (as its ×), or a permission
        // request or question (nothing allowed or denied, and the notch isn't hidden for later).
        // On the minimized bar it only stops the reading.
        void OnEscape()
        {
            if (shownKind == "quick")
            {
                if (quick.FollowingUp) quick.CancelFollowUp();
                else if (quick.Typing) Dismiss();
                else if (settings.QuickMinimized && !quick.InTurn) quick.Hush();
                else Dismiss();
            }
            else if (shownKind == "alert") CloseShownRequest();
            else if (shownKind == "question" && questions.Count > 0) questions.RemoveAt(questions.Count - 1);
            Refresh();
        }

        // Twice in a row: the text box instead of the microphone the first press opened.
        void OnShortcutTwice()
        {
            if (settings.VoiceMode == "quick") quick.OpenTyping();
        }

        void OnShortcut()
        {
            if (settings.VoiceMode == "quick")
            {
                // This cuts off the question that's waiting, so say so before its turn moves up.
                if (quick.State == QuickVoice.Phase.Thinking || quick.State == QuickVoice.Phase.Answering)
                    foreach (var p in pending.ToArray())
                        if (QuickVoice.IsVoiceFolder(p.Cwd)) Decline(p, "Stopped before you answered");
                quick.Press();
                return;
            }
            if (watcher.InVoice) return; // already talking
            string mode = settings.VoiceMode;
            // Typing goes to Claude's own quick entry (Ctrl+Alt+Space).
            if (mode == "type" || (mode == "voice-or-type" && !VoiceLauncher.HasMicrophone())) PromptSender.OpenQuickEntry(settings.NewChatLink);
            else VoiceLauncher.StartAsync(settings.NewChatLink);
        }
    }
}
