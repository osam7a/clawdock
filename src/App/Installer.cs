using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace ClaudeNotch
{
    // Claude Notch is a single exe. Run from anywhere, it copies itself to
    // %LOCALAPPDATA%\ClaudeNotch, registers itself to start with Windows (HKCU Run key),
    // installs the Claude Code hooks, and starts the installed copy. A running older copy
    // is asked to quit first, so running a new build is also how you update.
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ClaudeNotch";

        public static readonly string InstalledExe = Path.Combine(Settings.DataDir, "ClaudeNotch.exe");

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
                CopyWithRetry(CurrentExe, InstalledExe);
                RemoveOldStartupEntries();
                SetStartWithWindows(settings.StartWithWindows);
                if (settings.KeepHooksInstalled) HookInstaller.Install(InstalledExe);
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Settings.DataDir });
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Install failed: " + e.Message);
                return false;
            }
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Notch.lnk"); }
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
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "Claude Notch" });
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
            try { if (Directory.Exists(LegacyDir)) Directory.Delete(LegacyDir, true); } catch { }
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

        // ---- for a while the app was called Wrenlet ------------------------

        static string LegacyDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wrenlet"); } }

        // Moves that install's settings and downloads here and removes its startup entries. Its
        // exe stays for two weeks: sessions already open still run its hook until they restart,
        // and with no notch on its pipe it exits quietly. Its hooks are replaced when these are installed.
        public static void MigrateLegacy()
        {
            try
            {
                if (!Directory.Exists(LegacyDir)) return;
                string marker = Path.Combine(LegacyDir, "moved-to-claudenotch");
                if (File.Exists(marker))
                {
                    if (DateTime.Now - File.GetLastWriteTime(marker) > TimeSpan.FromDays(14)) Directory.Delete(LegacyDir, true);
                    return;
                }
                foreach (var p in Process.GetProcessesByName("Wrenlet"))
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                Directory.CreateDirectory(Settings.DataDir);
                try { File.Delete(Path.Combine(Settings.DataDir, "moved-to-wrenlet")); } catch { }
                foreach (string entry in Directory.GetFileSystemEntries(LegacyDir))
                {
                    string name = Path.GetFileName(entry);
                    string target = Path.Combine(Settings.DataDir, name);
                    if (name == "Wrenlet.exe" || name == "Wrenlet.log" || File.Exists(target) || Directory.Exists(target)) continue;
                    MoveWithRetry(entry, target);
                }
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    if (key.GetValue("Wrenlet") != null) key.DeleteValue("Wrenlet");
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wrenlet.lnk")); } catch { }
                File.WriteAllText(marker, "");
                Log.Write("Moved the Wrenlet install here.");
            }
            catch (Exception e) { Log.Write("Couldn't move the Wrenlet install: " + e.Message); }
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
            foreach (var p in Process.GetProcessesByName("ClaudeNotch"))
                if (p.Id != me)
                    try { if (!p.WaitForExit(1000)) p.Kill(); } catch { }
        }

        static void CopyWithRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= 10) throw; Thread.Sleep(300); }
            }
        }
    }
}
