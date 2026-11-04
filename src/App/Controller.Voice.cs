using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // What quick voice asks for: messaging sessions and starting new projects.
    partial class Controller
    {
        // ---- quick voice and your other sessions ---------------------------

        List<SessionBrief.Entry> LiveSessions()
        {
            var list = new List<SessionBrief.Entry>();
            foreach (var s in sessions)
            {
                if (QuickVoice.IsVoiceFolder(s.Cwd)) continue;
                var request = pending.Find(p => p.SessionId == s.Id);
                var question = questions.Find(q => q.SessionId == s.Id);
                list.Add(new SessionBrief.Entry
                {
                    Id = s.Id, Name = s.Name, Cwd = s.Cwd, Desktop = s.Desktop, Busy = s.Busy, Elapsed = s.Elapsed, Updated = s.Updated,
                    Waiting = request != null ? "permission for " + request.Detail : question != null ? "an answer to: " + question.Ask : null,
                });
            }
            return list;
        }

        // Quick voice wants to say something to one of your sessions. That can interrupt it, so
        // nothing goes out until you press Allow, and you see the exact message first.
        void OnSessionMessageWanted(string id, string message)
        {
            var s = sessions.Find(x => x.Id == id && !QuickVoice.IsVoiceFolder(x.Cwd));
            if (s == null || message == "")
            {
                quick.AddNote("Couldn't find that session, so nothing was sent");
                quick.TellNext("Your message wasn't sent: that session isn't running.");
                return;
            }
            string sid = s.Id, name = s.DisplayName;
            var request = new HookServer.Pending
            {
                SessionId = "", Tool = "sending a message", Cwd = QuickVoice.Folder,
                Detail = "To " + name + (s.Busy ? " (working)" : "") + ": " + message, FullDetail = "To " + name + (s.Busy ? " (working)" : "") + ":\n" + message,
            };
            request.Decided = allow =>
            {
                if (allow) DeliverMessage(sid, name, message);
                else quick.TellNext("The user didn't allow your message to " + name + ", so it wasn't sent.");
            };
            pending.Add(request);
            Refresh();
        }

        void DeliverMessage(string id, string name, string message)
        {
            var s = sessions.Find(x => x.Id == id);
            if (s == null)
            {
                quick.AddNote("Not sent: " + name + " has ended");
                quick.TellNext("Your message wasn't sent: " + name + " has ended.");
                return;
            }
            // Only into the chat it was meant for: unless that chat is confirmed open, you paste it.
            bool typeable = s.Desktop && !string.IsNullOrEmpty(s.Name) && sessions.FindAll(x => x.Name == s.Name).Count == 1;
            OpenSession(s);
            if (!typeable) { OnClipboard(name, message); return; }
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool open = false;
                for (int waited = 0; !open && waited < 4000; waited += 200)
                {
                    open = watcher.SessionTitle == s.Name;
                    if (!open) Thread.Sleep(200);
                }
                ui.BeginInvoke(new Action(() =>
                {
                    if (!open) { OnClipboard(name, message); return; }
                    PromptSender.ToDesktop(message, null);
                    quick.AddNote("Sent to " + name);
                    quick.TellNext("Your message to " + name + " was sent. Its reply will show up in the session snapshot.");
                }));
            });
        }

        // Quick voice wants to start a new project. Nothing is made until you press Allow.
        void OnNewProjectWanted(string wanted, string model, string effort, string first)
        {
            model = model.ToLowerInvariant();
            effort = effort.ToLowerInvariant();
            if (Array.IndexOf(ProjectStarter.Models, model) < 0) model = "";
            if (Array.IndexOf(ProjectStarter.Efforts, effort) < 0) effort = "";
            string name = ProjectStarter.CleanName(wanted);
            if (name == "")
            {
                quick.AddNote("That name can't be a folder, so nothing was started");
                quick.TellNext("The project wasn't started: \"" + wanted + "\" can't be used as a folder name.");
                return;
            }
            string folder = System.IO.Path.Combine(settings.ProjectsFolder, name);
            if (!ProjectStarter.IsFree(folder))
            {
                quick.AddNote(name + " already exists, so nothing was started");
                quick.TellNext("The project wasn't started: " + folder + " already exists and isn't empty. Ask the user for another name.");
                return;
            }
            bool auto = settings.NewProjectAuto;
            var request = new HookServer.Pending
            {
                SessionId = "", Tool = "starting a new project", Cwd = QuickVoice.Folder,
                Detail = name + (first == "" ? "" : ": " + first.Replace("\r", " ").Replace("\n", " ")),
                FullDetail = "Project: " + name + "\n\nFolder: " + folder + "\n\nPermission mode: " + (auto ? "Auto" : "the Claude app's default")
                    + "\n\nModel: " + (model == "" ? "your default" : model == "fable" ? "fable (uses usage credits)" : model)
                    + "\n\nEffort: " + (effort == "" ? "the Claude app's default" : effort)
                    + "\n\nFirst message:\n" + (first == "" ? "(none)" : first),
            };
            request.Decided = allow =>
            {
                if (allow) StartProject(name, folder, model, effort, first, auto);
                else quick.TellNext("The user didn't allow the new project " + name + ", so it wasn't started.");
            };
            pending.Add(request);
            Refresh();
        }

        void StartProject(string name, string folder, string model, string effort, string first, bool auto)
        {
            quick.AddNote("Starting " + name + "…");
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string id = ProjectStarter.Create(folder, name, model);
                if (id == null)
                {
                    ui.BeginInvoke(new Action(() =>
                    {
                        quick.AddNote("Couldn't start " + name);
                        quick.TellNext("The project " + name + " couldn't be started. Say so, and that the reason is in Claude Notch's log.");
                    }));
                    return;
                }
                VoiceLauncher.OpenLink("claude://resume?session=" + id);
                // Only into the new chat: unless it's confirmed open, the mode stays and you paste.
                bool open = false;
                for (int waited = 0; !open && waited < 15000; waited += 200)
                {
                    open = watcher.SessionTitle == name;
                    if (!open) Thread.Sleep(200);
                }
                bool autoOn = open && auto && ProjectStarter.SetAutoMode();
                ui.BeginInvoke(new Action(() =>
                {
                    quick.AddNote("Made " + folder);
                    if (auto && !autoOn) quick.AddNote("Couldn't turn on Auto mode: choose it under the prompt box");
                    if (!open && effort != "") quick.AddNote("Couldn't set the effort: use /effort " + effort + " in the chat");
                    if (!open && first != "") { OnClipboard(name, first); return; }
                    if (!open) { quick.TellNext("The project " + name + " was started and should be opening in the Claude app."); return; }
                    // The app only takes the effort as a command in the chat, so it goes first.
                    var messages = new List<string>();
                    if (effort != "") messages.Add("/effort " + effort);
                    if (first != "") messages.Add(first);
                    if (messages.Count > 0) PromptSender.ToDesktopInOrder(messages);
                    if (first != "") quick.AddNote("Sent the first message to " + name);
                    quick.TellNext("The project " + name + " was started in " + folder + " and opened in the Claude app" + (first != "" ? ", with the first message sent." : "."));
                }));
            });
        }

        void OnClipboard(string name, string message)
        {
            try { System.Windows.Clipboard.SetText(message); } catch { }
            quick.AddNote("On your clipboard: paste it into " + name);
            quick.TellNext("Your message to " + name + " couldn't be typed for the user. It's on their clipboard for them to paste, so it hasn't been sent yet.");
        }
    }
}
