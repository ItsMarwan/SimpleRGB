using System.Collections.Concurrent;
using System.Text;

namespace SimpleRGB.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()[..4]}] {Source}: {Message}";
}

/// <summary>
/// Small thread-safe logger. Writes to %APPDATA%\SimpleRGB\logs\simplergb.log on a background
/// thread (so a slow disk never stalls a device thread) and raises <see cref="EntryLogged"/>.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly BlockingCollection<LogEntry> Queue = new(new ConcurrentQueue<LogEntry>(), 10_000);
    private static readonly Thread Writer;
    private static readonly ConcurrentQueue<LogEntry> Recent = new();

    public static event Action<LogEntry>? EntryLogged;
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static bool WriteToFile { get; set; } = true;

    static Log()
    {
        Writer = new Thread(WriterLoop) { IsBackground = true, Name = "SimpleRGB log writer" };
        Writer.Start();
    }

    public static string LogFile => Path.Combine(AppPaths.LogDir, "simplergb.log");

    public static IReadOnlyList<LogEntry> RecentEntries => Recent.ToArray();

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Write(LogLevel.Warning, source, message);
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message);
    public static void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string source, string message)
    {
        if (level < MinimumLevel) return;
        var entry = new LogEntry(DateTime.Now, level, source, message);
        Recent.Enqueue(entry);
        while (Recent.Count > 500 && Recent.TryDequeue(out _)) { }
        try { EntryLogged?.Invoke(entry); } catch { /* a listener must never break logging */ }
        Queue.TryAdd(entry);
    }

    /// <summary>Blocks until queued entries are written (used on exit and by the CLI probe).</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Queue.Count > 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
    }

    private static void WriterLoop()
    {
        foreach (var entry in Queue.GetConsumingEnumerable())
        {
            if (!WriteToFile) continue;
            try
            {
                var file = LogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Copy(file, file + ".old", overwrite: true);
                    File.WriteAllText(file, string.Empty);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"{entry.Time:yyyy-MM-dd} {entry}");
                // Batch whatever else is already waiting into the same write.
                while (sb.Length < 64 * 1024 && Queue.TryTake(out var more))
                    sb.AppendLine($"{more.Time:yyyy-MM-dd} {more}");
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the app (e.g. disk full / file locked).
            }
        }
    }
}
