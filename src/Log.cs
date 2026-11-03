using System;
using System.IO;

namespace ClaudeNotch
{
    // Troubleshooting notes in %LOCALAPPDATA%\ClaudeNotch\ClaudeNotch.log. The file is
    // started over once it passes 256 KB so it can't grow without bound.
    static class Log
    {
        static readonly object gate = new object();
        public static readonly string FilePath = Path.Combine(Settings.DataDir, "ClaudeNotch.log");

        public static void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Settings.DataDir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 256 * 1024) info.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
                }
                catch { }
            }
        }
    }
}
