using System.Text;
using System.Text.Json;

namespace ProjectSoundboard.Core.Storage;

/// <summary>Whether a report came from a caught exception or from a run that simply vanished.</summary>
public enum CrashKind
{
    /// <summary>An exception reached the top of the stack and we were still alive to write it down.</summary>
    Exception,

    /// <summary>
    /// The previous run never shut down. Nothing was caught, because nothing could be — a
    /// stack overflow, an access violation or the process being killed all end this way.
    /// </summary>
    UncleanShutdown
}

/// <summary>One crash report on disk.</summary>
public sealed record CrashReport(string Path, DateTime WhenUtc, string Title)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string When => WhenUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
}

/// <summary>
/// Writes a self-contained account of a crash: what happened, in which version, on what, and
/// what the app was doing at the time.
///
/// The important half is <see cref="UncleanShutdown"/>. The failures that have been hardest to
/// pin down here — the layout recursion on large libraries — kill the process outright, and
/// .NET cannot catch or log them. So instead of trying, a marker file is written while the app
/// runs and removed on a clean exit. Finding it at startup means the last run died, and the
/// log it left behind is the evidence.
/// </summary>
public static class CrashReporter
{
    private const int RecentLogLines = 300;

    /// <summary>Extra context supplied by the app layer — devices, library size, and so on.</summary>
    public static Func<string>? DescribeEnvironment { get; set; }

    /// <summary>
    /// Anything the app layer can find out about the previous crash after the fact, such as
    /// what Windows itself recorded. Given the marker written at the start of that run.
    /// </summary>
    public static Func<SessionInfo, string?>? DescribePostMortem { get; set; }

    public sealed record SessionInfo(int ProcessId, string Version, DateTime StartedUtc, string? LogFile);

    // -----------------------------------------------------------------------
    // Session marker
    // -----------------------------------------------------------------------

    /// <summary>
    /// Note that a run has begun. Returns the previous session when it never finished, so the
    /// caller can turn it into a report.
    /// </summary>
    public static SessionInfo? BeginSession(string version, string? logFile)
    {
        SessionInfo? previous = null;

        try
        {
            if (File.Exists(AppPaths.SessionMarker))
            {
                var text = File.ReadAllText(AppPaths.SessionMarker);
                previous = JsonSerializer.Deserialize<SessionInfo>(text);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read the previous session marker: {ex.Message}");
        }

        try
        {
            var session = new SessionInfo(Environment.ProcessId, version, DateTime.UtcNow, logFile);
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(AppPaths.SessionMarker, JsonSerializer.Serialize(session));
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not write the session marker: {ex.Message}");
        }

        return previous;
    }

    /// <summary>Shutting down properly. Removing the marker is what says "this was not a crash".</summary>
    public static void EndSession()
    {
        try
        {
            if (File.Exists(AppPaths.SessionMarker)) File.Delete(AppPaths.SessionMarker);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not clear the session marker: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Writing reports
    // -----------------------------------------------------------------------

    public static string? WriteException(string source, Exception exception, string version)
    {
        var body = new StringBuilder();

        body.AppendLine($"What happened : {exception.GetType().Name} in {source}");
        body.AppendLine($"Message       : {exception.Message}");
        body.AppendLine();
        body.AppendLine("--- exception ---------------------------------------------------------");
        AppendException(body, exception);

        return Write(CrashKind.Exception, $"{exception.GetType().Name} in {source}", version, body.ToString());
    }

    /// <summary>
    /// Turn a run that never came back into a report. There is no exception to quote, so the
    /// value is entirely in the timing and the log the run left behind.
    /// </summary>
    public static string? WriteUncleanShutdown(SessionInfo previous)
    {
        var body = new StringBuilder();

        body.AppendLine("What happened : the previous run ended without shutting down.");
        body.AppendLine();
        body.AppendLine("This is written after the fact, on the next start, because whatever ended");
        body.AppendLine("that run gave .NET no chance to record anything. A stack overflow, an");
        body.AppendLine("access violation, the process being killed, or the machine losing power");
        body.AppendLine("all look like this. The log from that run, below, is the evidence.");
        body.AppendLine();
        body.AppendLine($"Previous run  : version {previous.Version}, process {previous.ProcessId}");
        body.AppendLine($"Started       : {previous.StartedUtc.ToLocalTime():dd MMM yyyy, HH:mm:ss}");
        body.AppendLine($"Ran for       : {Describe(DateTime.UtcNow - previous.StartedUtc)}");

        var postMortem = Safely(() => DescribePostMortem?.Invoke(previous));
        if (!string.IsNullOrWhiteSpace(postMortem))
        {
            body.AppendLine();
            body.AppendLine("--- what Windows recorded ---------------------------------------------");
            body.AppendLine(postMortem);
        }

        if (previous.LogFile is not null && File.Exists(previous.LogFile))
        {
            body.AppendLine();
            body.AppendLine($"--- end of that run's log ({Path.GetFileName(previous.LogFile)}) -------------");
            body.AppendLine(Tail(previous.LogFile, RecentLogLines));
        }

        return Write(CrashKind.UncleanShutdown, "Previous run ended without shutting down",
                     previous.Version, body.ToString(), includeRecentLog: false);
    }

    private static string? Write(CrashKind kind, string title, string version, string body,
                                 bool includeRecentLog = true)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CrashDir);

            var now = DateTime.Now;
            var path = Path.Combine(AppPaths.CrashDir, $"crash-{now:yyyy-MM-dd-HHmmss}.txt");

            var text = new StringBuilder();
            text.AppendLine("Project Soundboard — crash report");
            text.AppendLine("=======================================================================");
            text.AppendLine($"When          : {now:dd MMM yyyy, HH:mm:ss} ({TimeZoneInfo.Local.StandardName})");
            text.AppendLine($"Kind          : {kind}");
            text.AppendLine($"Version       : {version}");
            text.AppendLine($"OS            : {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
            text.AppendLine($".NET          : {Environment.Version}");
            text.AppendLine($"Processors    : {Environment.ProcessorCount}");
            text.AppendLine($"Data folder   : {AppPaths.DataRoot}");
            text.AppendLine();
            text.AppendLine(body);

            var environment = Safely(() => DescribeEnvironment?.Invoke());
            if (!string.IsNullOrWhiteSpace(environment))
            {
                // For an unclean shutdown this is the machine as it is now, not as it was
                // when the run died — the same setup, but say so rather than imply otherwise.
                text.AppendLine(kind == CrashKind.UncleanShutdown
                    ? "--- setup (this machine, read after the fact) -------------------------"
                    : "--- setup -------------------------------------------------------------");
                text.AppendLine(environment);
                text.AppendLine();
            }

            if (includeRecentLog)
            {
                text.AppendLine("--- what the app was doing --------------------------------------------");
                foreach (var line in Log.Recent.TakeLast(RecentLogLines)) text.AppendLine(line);
            }

            // With a byte-order mark: these get opened in Notepad and pasted into chat
            // windows, and without one the dashes and quotes come out as mojibake.
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            Log.Error($"Crash report written: {path}");

            Prune();
            return path;
        }
        catch (Exception ex)
        {
            // A failure in here must never become the thing that takes the app down.
            try { Log.Error($"Could not write a crash report: {ex.Message}"); } catch { }
            return null;
        }
    }

    private static void AppendException(StringBuilder text, Exception exception, int depth = 0)
    {
        var indent = new string(' ', depth * 2);

        text.AppendLine($"{indent}{exception.GetType().FullName}: {exception.Message}");

        if (exception.StackTrace is { } stack)
        {
            foreach (var line in stack.Split('\n')) text.AppendLine($"{indent}{line.TrimEnd()}");
        }

        // Aggregates hide the real cause one level down, so walk everything.
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                text.AppendLine($"{indent}--- inner ---");
                AppendException(text, inner, depth + 1);
            }
        }
        else if (exception.InnerException is { } single)
        {
            text.AppendLine($"{indent}--- caused by ---");
            AppendException(text, single, depth + 1);
        }
    }

    // -----------------------------------------------------------------------
    // Reading and tidying
    // -----------------------------------------------------------------------

    public static IReadOnlyList<CrashReport> List()
    {
        try
        {
            if (!Directory.Exists(AppPaths.CrashDir)) return Array.Empty<CrashReport>();

            return Directory.EnumerateFiles(AppPaths.CrashDir, "crash-*.txt")
                .Select(path => new CrashReport(path, File.GetLastWriteTimeUtc(path), TitleOf(path)))
                .OrderByDescending(r => r.WhenUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not list crash reports: {ex.Message}");
            return Array.Empty<CrashReport>();
        }
    }

    public static void DeleteAll()
    {
        try
        {
            if (!Directory.Exists(AppPaths.CrashDir)) return;
            foreach (var file in Directory.EnumerateFiles(AppPaths.CrashDir, "crash-*.txt"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not clear crash reports: {ex.Message}");
        }
    }

    /// <summary>Keep the most recent 20. They are small, but they should not pile up forever.</summary>
    private static void Prune()
    {
        try
        {
            var stale = List().Skip(20).ToList();
            foreach (var report in stale) File.Delete(report.Path);
        }
        catch { /* best effort */ }
    }

    private static string TitleOf(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(20))
            {
                if (line.StartsWith("What happened", StringComparison.Ordinal))
                    return line.Split(':', 2).Last().Trim();
            }
        }
        catch { /* fall through */ }

        return "Crash report";
    }

    private static string Tail(string path, int lines)
    {
        try
        {
            // Copied first: the file may still be held open by the logger.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var ring = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                ring.Enqueue(line);
                while (ring.Count > lines) ring.Dequeue();
            }

            return string.Join(Environment.NewLine, ring);
        }
        catch (Exception ex)
        {
            return $"(could not be read: {ex.Message})";
        }
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:F0} seconds"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:F0} minutes"
        : $"{span.TotalHours:F1} hours";

    private static string? Safely(Func<string?>? f)
    {
        try { return f?.Invoke(); }
        catch (Exception ex) { return $"(could not be collected: {ex.Message})"; }
    }
}
