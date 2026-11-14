using System;
using System.IO;

namespace QuickSnip.Services;

/// <summary>
/// 极简文件日志。托盘程序没有控制台，出了问题只能靠它：
/// %APPDATA%\QuickSnip\logs\app.log，写不进去就退到 %TEMP%。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logFile;
    private static bool _resolved;

    public static string? CurrentFile
    {
        get { EnsurePath(); return _logFile; }
    }

    private static void EnsurePath()
    {
        if (_resolved) return;
        _resolved = true;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickSnip", "logs");
            System.IO.Directory.CreateDirectory(dir);
            _logFile = Path.Combine(dir, "app.log");
            return;
        }
        catch
        {
            // 继续往下试
        }

        try
        {
            _logFile = Path.Combine(Path.GetTempPath(), "QuickSnip-app.log");
        }
        catch
        {
            _logFile = null;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                EnsurePath();
                if (_logFile == null) return;

                var info = new FileInfo(_logFile);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    string bak = _logFile + ".1";
                    try { File.Delete(bak); } catch { }
                    try { File.Move(_logFile, bak); } catch { }
                }

                File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响主流程。
        }
    }

    public static void Exception(string context, Exception ex) => Write($"[EX] {context} :: {ex}");
}
