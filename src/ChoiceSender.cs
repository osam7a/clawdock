using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeNotch
{
    // A question Claude asked at the end of a turn in the desktop app (the "next step" box:
    // a question plus one or two buttons), as reported by the wrap_up hook event.
    class Question
    {
        public string SessionId, Ask, Cwd;
        public DateTime At;
        public List<string> Labels = new List<string>();  // what the buttons say
        public List<string> Actions = new List<string>(); // app action ("commit", ...) or null for a reply
    }

    // Answers a question by pressing the same button in Claude's window, so the result is
    // exactly what clicking it there does. Claude's window can stay minimized.
    //   1. Look for a button with that label next to the question's text.
    //   2. If the session isn't the one on screen, select it in the sidebar and look again.
    //   3. A reply button only sends its label as your answer, so if the button can't be
    //      found, type the label into that session instead. An app action (commit, create a
    //      PR, ...) is never guessed at: Claude is opened on the session so you can press it.
    static class ChoiceSender
    {
        public static event Action<string> Failed;

        static readonly Dictionary<string, string> ActionNames = new Dictionary<string, string>
        {
            { "commit", "Commit" }, { "create_pr", "Create PR" }, { "merge", "Merge" }, { "fix_ci", "Fix CI" },
            { "address_comments", "Address comments" }, { "resolve_conflicts", "Resolve conflicts" },
            { "verify", "Verify" }, { "archive", "Archive" }, { "new_session", "New session" },
        };

        // The button text for a wrap_up action: its own label for a reply, a readable name for an app action.
        public static string LabelFor(string label, string action)
        {
            string name;
            if (!string.IsNullOrEmpty(action) && ActionNames.TryGetValue(action, out name)) return name;
            if (!string.IsNullOrEmpty(label)) return label;
            return string.IsNullOrEmpty(action) ? "OK" : action.Replace('_', ' ');
        }

        public static void Answer(Question q, int index, string newChatLink, Action openSession, Action selectSession)
        {
            string label = q.Labels[index];
            bool isReply = string.IsNullOrEmpty(q.Actions[index]);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IntPtr h = Native.FindClaudeWindow();
                    if (h != IntPtr.Zero)
                    {
                        var win = AutomationElement.FromHandle(h);
                        if (Press(win, q.Ask, label, isReply)) return;
                        if (selectSession != null)
                        {
                            selectSession();
                            var clock = Stopwatch.StartNew();
                            while (clock.ElapsedMilliseconds < 3000)
                            {
                                Thread.Sleep(250);
                                if (Press(win, q.Ask, label, isReply)) return;
                            }
                        }
                    }
                    if (isReply)
                    {
                        // Same as the button: the label goes to the session as your reply.
                        Dispatch(openSession);
                        Thread.Sleep(800);
                        PromptSender.ToDesktop(label, newChatLink);
                    }
                    else
                    {
                        Dispatch(openSession);
                        Fail("Press \"" + label + "\" in Claude: it couldn't be pressed from the notch.");
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Choice: " + e.Message);
                    Fail("Couldn't answer from the notch. Open Claude to answer.");
                }
            });
        }

        // Finds the button near the question's text. Only falls back to the whole window for a
        // reply, and then only if exactly one button has that label.
        static bool Press(AutomationElement win, string ask, string label, bool isReply)
        {
            var buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            AutomationElement target = null;

            // The question's text can also appear in the chat history, so widen the scope one
            // level at a time around every copy: the box's own buttons are found first.
            var scopes = new List<AutomationElement>();
            foreach (AutomationElement t in win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, ask)))
                scopes.Add(t);
            var walker = TreeWalker.ControlViewWalker;
            for (int i = 0; i < 5 && target == null; i++)
            {
                for (int k = 0; k < scopes.Count && target == null; k++)
                {
                    if (scopes[k] == null) continue;
                    scopes[k] = walker.GetParent(scopes[k]);
                    if (scopes[k] == null) continue;
                    foreach (AutomationElement b in scopes[k].FindAll(TreeScope.Descendants, buttons))
                        if (Matches(b.Current.Name, label)) { target = b; break; }
                }
            }
            if (target == null && isReply)
            {
                var all = win.FindAll(TreeScope.Descendants, new AndCondition(buttons, new PropertyCondition(AutomationElement.NameProperty, label)));
                if (all.Count == 1) target = all[0];
            }
            if (target == null || !target.Current.IsEnabled) return false;

            object pattern;
            if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
            ((InvokePattern)pattern).Invoke();
            return true;
        }

        static bool Matches(string name, string label)
        {
            return name != null && string.Equals(name.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        static void Dispatch(Action a)
        {
            var app = System.Windows.Application.Current;
            if (a != null && app != null) app.Dispatcher.Invoke(a);
        }

        static void Fail(string message)
        {
            var app = System.Windows.Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(new Action(() => { if (Failed != null) Failed(message); }));
        }
    }
}
