using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Clawdock
{
    // Clawdock is a single exe. Run from anywhere, it copies itself to
    // %LOCALAPPDATA%\Clawdock, registers itself to start with Windows (HKCU Run key),
    // installs the Claude Code hooks, and starts the installed copy. A running older copy
    // is asked to quit first, so running a new build is also how you update.
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "Clawdock";

        public static readonly string InstalledExe = Path.Combine(Settings.DataDir, "Clawdock.exe");

        public static string CurrentExe
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        // Copies this exe into place and starts it. Returns false if the copy failed.
        public static bool InstallAndLaunch(Settings settings)
        {
            try
            {
                StopRunningCopy();
                Directory.CreateDirectory(Settings.DataDir);
                ReplaceFile(CurrentExe, InstalledExe);
                RemoveOldStartupEntries();
                SetStartWithWindows(settings.StartWithWindows);
                if (settings.KeepHooksInstalled) HookInstaller.Install(InstalledExe);
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Settings.DataDir });
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Install failed: " + e.Message);
                // The running copy was already stopped, so bring it back.
                try { if (!IsInstalledCopy && File.Exists(InstalledExe)) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Settings.DataDir }); }
                catch (Exception e2) { Log.Write("Couldn't restart: " + e2.Message); }
                return false;
            }
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clawdock.lnk"); }
        }

        // A Start menu entry, so the app can be found and opened (it opens the settings when
        // it's already running). Runs at each start of the installed copy.
        public static void EnsureShortcut()
        {
            try
            {
                if (File.Exists(ShortcutPath)) return;
                var type = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(type);
                object link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                var lt = link.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { InstalledExe });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { Settings.DataDir });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "Clawdock" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception e) { Log.Write("Couldn't add the Start menu entry: " + e.Message); }
        }

        // `--uninstall` from the command line: stop the running copy, then remove everything.
        public static void Uninstall()
        {
            StopRunningCopy();
            RemoveEverything();
        }

        // Removes the hooks and the startup entry, then deletes the install folder once this
        // process has exited (a running exe can't delete itself).
        public static void RemoveEverything()
        {
            HookInstaller.Uninstall();
            SetStartWithWindows(false);
            RemoveOldStartupEntries();
            try { File.Delete(ShortcutPath); } catch { }
            foreach (string old in new[] { "ClaudeNotch", "Wrenlet" })
                try { if (Directory.Exists(LegacyDir(old))) Directory.Delete(LegacyDir(old), true); } catch { }
            try
            {
                string dir = Settings.DataDir;
                if (Directory.Exists(dir))
                {
                    // Waits for this process (which may be running from that folder) to exit first.
                    string script = "Wait-Process -Id " + Process.GetCurrentProcess().Id + " -ErrorAction SilentlyContinue; Start-Sleep 1; " +
                        "Remove-Item -LiteralPath '" + dir.Replace("'", "''") + "' -Recurse -Force -ErrorAction SilentlyContinue";
                    Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                }
            }
            catch { }
        }

        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue(RunValue, "\"" + InstalledExe + "\"");
                    else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue);
                }
            }
            catch (Exception e) { Log.Write("Could not change the startup entry: " + e.Message); }
        }

        // ---- the app's earlier names: Claude Notch, and before that Wrenlet ---

        static string LegacyDir(string name) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name); }

        public static void MigrateLegacy()
        {
            MigrateFrom("ClaudeNotch", "Claude Notch.lnk");
            MigrateFrom("Wrenlet", "Wrenlet.lnk");
        }

        // Moves that install's settings and downloads here and removes its startup entries. Its
        // exe stays for two weeks: sessions already open still run its hook until they restart,
        // and with no notch on its pipe it exits quietly. Its hooks are replaced when these are installed.
        static void MigrateFrom(string oldName, string shortcut)
        {
            string dir = LegacyDir(oldName);
            try
            {
                if (!Directory.Exists(dir)) return;
                string marker = Path.Combine(dir, "moved-to-clawdock");
                string markerBefore = Path.Combine(dir, "moved-to-claudenotch");
                if (File.Exists(markerBefore) && !File.Exists(marker)) marker = markerBefore;
                if (File.Exists(marker))
                {
                    if (DateTime.Now - File.GetLastWriteTime(marker) > TimeSpan.FromDays(14)) Directory.Delete(dir, true);
                    return;
                }
                foreach (var p in Process.GetProcessesByName(oldName))
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                Directory.CreateDirectory(Settings.DataDir);
                try { File.Delete(Path.Combine(Settings.DataDir, "moved-to-wrenlet")); } catch { }
                foreach (string entry in Directory.GetFileSystemEntries(dir))
                {
                    string name = Path.GetFileName(entry);
                    string target = Path.Combine(Settings.DataDir, name);
                    if (name == oldName + ".exe" || name == oldName + ".log" || name == "update" || name.StartsWith(oldName + ".exe.old-") || File.Exists(target) || Directory.Exists(target)) continue;
                    MoveWithRetry(entry, target);
                }
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    if (key.GetValue(oldName) != null) key.DeleteValue(oldName);
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), shortcut)); } catch { }
                MoveVoiceHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"), oldName);
                File.WriteAllText(marker, "");
                Log.Write("Moved the " + oldName + " install here.");
            }
            catch (Exception e) { Log.Write("Couldn't move the " + oldName + " install: " + e.Message); }
        }

        // Claude Code files a conversation under its folder's path, and quick voice's folder just
        // moved. Its history moves too, so its saved chat can still be resumed. Only this app's own.
        internal static void MoveVoiceHistory(string projects, string oldName)
        {
            string ending = "-" + oldName + "-voice";
            if (!Directory.Exists(projects)) return;
            foreach (string dir in Directory.GetDirectories(projects, "*" + ending))
            {
                string target = dir.Substring(0, dir.Length - ending.Length) + "-Clawdock-voice";
                try { if (!Directory.Exists(target)) MoveWithRetry(dir, target); }
                catch (Exception e) { Log.Write("Couldn't move quick voice's history: " + e.Message); }
            }
        }

        static void MoveWithRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                    return;
                }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { if (i >= 20) throw; Thread.Sleep(250); }
            }
        }

        // The startup-folder shortcuts the old PowerShell installer made.
        static void RemoveOldStartupEntries()
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (string name in new[] { "ClaudeNotch.lnk", "CopilotToClaude.lnk" })
                try { File.Delete(Path.Combine(startup, name)); } catch { }
        }

        // Asks a running copy to quit over the pipe; if it doesn't, ends it.
        static void StopRunningCopy()
        {
            if (!HookServer.IsRunning()) return;
            HookServer.SendCommand("quit");
            for (int i = 0; i < 30 && HookServer.IsRunning(); i++) Thread.Sleep(100);
            if (!HookServer.IsRunning()) return;

            // Still there: end it. This can also end a hook that is waiting on a permission
            // answer; Claude Code then just shows its own prompt.
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("Clawdock"))
                if (p.Id != me)
                    try { if (!p.WaitForExit(1000)) p.Kill(); } catch { }
        }

        // Copies over the target. A hook from an open session may be running the old exe, which can't
        // be overwritten but can be renamed, so after a few tries it moves out of the way.
        internal static void ReplaceFile(string from, string to)
        {
            for (int i = 0; i < 5; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
            string aside = to + ".old-" + DateTime.Now.Ticks;
            File.Move(to, aside);
            try { File.Copy(from, to, true); }
            catch { File.Move(aside, to); throw; }
        }

        // The downloaded update and any old exe moved aside. Whatever is still in use goes next time.
        public static void CleanUpAfterUpdate()
        {
            try
            {
                string update = Path.Combine(Settings.DataDir, "update");
                if (Directory.Exists(update)) Directory.Delete(update, true);
            }
            catch { }
            try
            {
                foreach (string old in Directory.GetFiles(Settings.DataDir, Path.GetFileName(InstalledExe) + ".old-*"))
                    try { File.Delete(old); } catch { }
            }
            catch { }
        }
    }
}
