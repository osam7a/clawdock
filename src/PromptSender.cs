using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace ClaudeNotch
{
    // Puts text into the Claude app's prompt box (the chat or Code session that is open, or a new
    // chat if the open view has no prompt box) and sends it, and opens Claude's quick entry.
    static class PromptSender
    {
        // Reports a problem on the UI thread (shown under the notch).
        public static event Action<string> Failed;

        // Claude's own quick entry box: the app's Ctrl+Alt+Space. If Claude isn't running, it's
        // started on a new chat instead (its shortcut only works while it runs).
        public static void OpenQuickEntry(string newChatLink)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!DesktopAppRunning())
                    {
                        VoiceLauncher.OpenLink(newChatLink);
                        return;
                    }
                    // The shortcut that got us here may still be held (the Copilot key holds Win and
                    // Shift); with them down this would be a different combination.
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 1500 && ModifiersDown()) Thread.Sleep(20);
                    Native.PressKeys(Native.VK_CONTROL, Native.VK_MENU, Native.VK_SPACE);
                }
                catch (Exception e) { Log.Write("Quick entry: " + e.Message); }
            });
        }

        // The Claude app is running, even with its window closed to the tray (its shortcut still
        // works then). Claude Code's CLI is a claude.exe too, so tell them apart by where they live.
        static bool DesktopAppRunning()
        {
            if (Native.FindClaudeWindow() != IntPtr.Zero) return true;
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (path.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf(@"\.local\bin", StringComparison.OrdinalIgnoreCase) < 0) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        static bool ModifiersDown()
        {
            foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) // Shift, Ctrl, Alt, left and right Win
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }

        public static void ToDesktop(string text, string newChatLink)
        {
            var t = new Thread(() =>
            {
                try { if (!SendToDesktop(text, newChatLink)) Fail("Couldn't find Claude's prompt box. Your text is on the clipboard."); }
                catch (Exception e) { Log.Write("Prompt: " + e.Message); Fail("Couldn't send to Claude."); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // the clipboard needs STA
            t.Start();
        }

        // Several messages to the open chat, one after another. Stops at the first that can't be sent.
        public static void ToDesktopInOrder(IList<string> messages)
        {
            var t = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < messages.Count; i++)
                    {
                        if (i > 0) Thread.Sleep(1500);
                        if (!SendToDesktop(messages[i], null)) { Fail("Couldn't find Claude's prompt box. Your text is on the clipboard."); return; }
                    }
                }
                catch (Exception e) { Log.Write("Prompt: " + e.Message); Fail("Couldn't send to Claude."); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // the clipboard needs STA
            t.Start();
        }

        static bool SendToDesktop(string text, string newChatLink)
        {
            IntPtr hwnd = Native.FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                if (newChatLink == null) { SetClipboard(text); return false; } // only this chat: never a new one
                VoiceLauncher.OpenLink(newChatLink);
                hwnd = VoiceLauncher.WaitForClaudeWindow(15000);
                if (hwnd == IntPtr.Zero) { SetClipboard(text); return false; }
            }

            var win = AutomationElement.FromHandle(hwnd);
            Native.ForceForeground(hwnd);
            var box = FindPromptBox(win, 400);
            if (box == null)
            {
                if (newChatLink == null) { SetClipboard(text); return false; } // only this chat: never a new one
                VoiceLauncher.OpenLink(newChatLink);
                box = FindPromptBox(win, 10000);
            }
            if (box == null) { SetClipboard(text); return false; }

            Native.ForceForeground(hwnd);
            if (!Native.WaitForForeground(hwnd, 2000)) { SetClipboard(text); return false; }
            try { box.SetFocus(); } catch { }
            Thread.Sleep(80);

            // Paste rather than type, so long and multi-line prompts arrive intact. Keys are only
            // sent while Claude is in front, so they can never land in another app.
            var previous = GetClipboard();
            SetClipboard(text);
            if (Native.GetForegroundWindow() != hwnd) return false;
            Native.PressKeys(Native.VK_CONTROL, Native.VK_V);
            Thread.Sleep(150);
            if (Native.GetForegroundWindow() != hwnd) return false;
            Native.PressKeys(Native.VK_RETURN);
            Thread.Sleep(400);
            if (previous != null) SetClipboard(previous);
            return true;
        }

        // Claude's prompt box is a ProseMirror editor, exposed as an Edit control.
        static AutomationElement FindPromptBox(AutomationElement win, int timeoutMs)
        {
            var edits = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var clock = Stopwatch.StartNew();
            do
            {
                try
                {
                    AutomationElement fallback = null;
                    foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, edits))
                    {
                        var c = e.Current;
                        if (!c.IsKeyboardFocusable || !c.IsEnabled || c.IsOffscreen) continue;
                        if ((c.ClassName ?? "").IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0) return e;
                        string n = (c.Name ?? "").ToLowerInvariant();
                        if (fallback == null && (n.Contains("prompt") || n.Contains("message") || n.Contains("reply"))) fallback = e;
                    }
                    if (fallback != null) return fallback;
                }
                catch { }
                Thread.Sleep(150);
            } while (clock.ElapsedMilliseconds < timeoutMs);
            return null;
        }

        // A copy of everything on the clipboard (text, images, copied files, ...), so it can be
        // put back after pasting. Formats that can't be read are skipped.
        static DataObject GetClipboard()
        {
            try
            {
                var current = Clipboard.GetDataObject();
                if (current == null) return null;
                var copy = new DataObject();
                int kept = 0;
                foreach (string format in current.GetFormats(false))
                {
                    try
                    {
                        object value = current.GetData(format, false);
                        if (value == null) continue;
                        copy.SetData(format, value, false);
                        kept++;
                    }
                    catch { }
                }
                return kept > 0 ? copy : null;
            }
            catch { return null; }
        }

        static void SetClipboard(string text)
        {
            var data = new DataObject();
            data.SetText(text);
            SetClipboard(data);
        }

        // Marked so Windows leaves it out of clipboard history (Win+V) and cloud clipboard sync.
        static void SetClipboard(DataObject data)
        {
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetDataObject(data, true); return; } catch { Thread.Sleep(50); } // another app may hold it briefly
            }
        }

        static void Fail(string message)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() => { if (Failed != null) Failed(message); }));
        }
    }
}
