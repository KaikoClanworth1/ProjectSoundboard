using System.Collections.Concurrent;
using System.Text;

namespace ProjectSoundboard.Core.Storage;

/// <summary>
/// Dependency free logger. Writes are queued and flushed on a background thread so
/// logging never blocks the audio or UI threads.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly Lock Gate = new();
    private static Thread? _worker;
    private static string? _file;
    private static volatile bool _started;

    public static bool Verbose { get; set; }

    /// <summary>
    /// The file being written to, so a crash report can point at the log that belongs to the
    /// run that died rather than guessing from the date.
    /// </summary>
    public static string? CurrentFile => _file;

    /// <summary>Ring buffer of the most recent lines, surfaced in Settings → Developer.</summary>
    public static IReadOnlyCollection<string> Recent
    {
        get { lock (Gate) return RecentLines.ToArray(); }
    }

    private static readonly Queue<string> RecentLines = new();
    private const int RecentCapacity = 500;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            _file = Path.Combine(AppPaths.LogDir, $"soundboard-{DateTime.Now:yyyy-MM-dd}.log");
            PruneOldLogs();
        }
        catch { _file = null; }

        _worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "log-writer",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message} :: {ex}");
    public static void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (Gate)
        {
            RecentLines.Enqueue(line);
            while (RecentLines.Count > RecentCapacity) RecentLines.Dequeue();
        }

        if (_started && !Queue.IsAddingCompleted)
        {
            try { Queue.Add(line); } catch { /* shutting down */ }
        }
    }

    private static void Pump()
    {
        var buffer = new StringBuilder();
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            buffer.Clear();
            buffer.AppendLine(line);

            // Drain anything else that piled up so we write in batches.
            while (Queue.TryTake(out var more)) buffer.AppendLine(more);

            if (_file is null) continue;
            try { File.AppendAllText(_file, buffer.ToString()); }
            catch { /* logging must never throw */ }
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, "soundboard-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { /* best effort */ }
    }

    public static void Shutdown()
    {
        if (!_started) return;
        try
        {
            Queue.CompleteAdding();
            _worker?.Join(500);
        }
        catch { /* ignore */ }
    }
}
