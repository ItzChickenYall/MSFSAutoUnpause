using System;
using System.IO;
using System.Text;

namespace MSFSAutoUnpause;

public static class Logger
{
    private static readonly string _logDir;
    private static readonly object _lock = new();

    static Logger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MSFSAutoUnpause", "logs");
        Directory.CreateDirectory(_logDir);
        PruneOldLogs();
    }

    public static string LogDirectory => _logDir;

    // Keeps only today's and yesterday's log files. Runs once at startup — logs
    // accumulate fast during a stuck-pause retry loop, and nothing past a couple of
    // days back has ever been needed for diagnosis.
    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-1);
            foreach (var file in Directory.GetFiles(_logDir, "unpause_*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string datePart = name.Length > "unpause_".Length ? name.Substring("unpause_".Length) : "";
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var fileDate) &&
                    fileDate.Date < cutoff)
                {
                    try { File.Delete(file); } catch { /* skip files in use, etc. */ }
                }
            }
        }
        catch { /* never crash startup over log cleanup */ }
    }

    private static string LogPath =>
        Path.Combine(_logDir, $"unpause_{DateTime.UtcNow:yyyy-MM-dd}.log");

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Diag(string msg)  => Write("DIAG ", msg);

    // Local time first (what the user reads off the clock / Volanta), UTC kept alongside
    // because dev-notes and Windows Event Log cross-referencing are all done in UTC.
    // e.g. "2026-10-25 11:07:08 local (UTC+8) | 03:07:08 UTC [INFO ] ..."
    private static void Write(string level, string msg)
    {
        var    utc    = DateTime.UtcNow;
        var    local  = utc.ToLocalTime();
        var    off    = TimeZoneInfo.Local.GetUtcOffset(utc);
        string offStr = (off < TimeSpan.Zero ? "-" : "+") +
                        (off.Minutes == 0 ? $"{Math.Abs(off.Hours)}" : $"{off:hh\\:mm}");
        string line = $"{local:yyyy-MM-dd HH:mm:ss} local (UTC{offStr}) | {utc:HH:mm:ss} UTC [{level}] {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* never crash if logging fails */ }
        }
    }
}
