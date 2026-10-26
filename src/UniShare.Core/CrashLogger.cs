using System;
using System.IO;

namespace UniShare.Core
{
    /// <summary>
    /// 未处理异常落盘记录（%ProgramData%\UniShare\logs\crash.log），便于用户反馈问题时定位
    /// </summary>
    public static class CrashLogger
    {
        private static readonly object s_lock = new object();
        private const long MaxLogBytes = 2 * 1024 * 1024;

        public static string GetLogPath()
        {
            return Path.Combine(ConfigManager.GetDataDirectory(), "logs", "crash.log");
        }

        public static void Write(string context, Exception? ex)
        {
            try
            {
                lock (s_lock)
                {
                    string path = GetLogPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                    {
                        File.Move(path, path + ".old", overwrite: true);
                    }
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
