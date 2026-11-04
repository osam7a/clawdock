using System;
using System.IO;

namespace Clawdock
{
    // Troubleshooting notes in %LOCALAPPDATA%\Clawdock\Clawdock.log. The file is
    // started over once it passes 256 KB so it can't grow without bound.
    static class Log
    {
        static readonly object gate = new object();

        // Set CLAWDOCK_DEBUG=1 to also record details that can include your session names.
        public static readonly bool Verbose = Environment.GetEnvironmentVariable("CLAWDOCK_DEBUG") == "1";
        public static readonly string FilePath = Path.Combine(Settings.DataDir, "Clawdock.log");

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
