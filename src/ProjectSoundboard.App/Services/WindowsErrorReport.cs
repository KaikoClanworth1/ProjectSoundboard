using System.Diagnostics;
using System.Text;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Asks Windows what it saw when a run of this app disappeared.
///
/// When the process is killed outright — a stack overflow is the usual reason here — .NET gets
/// no opportunity to write anything down, but Windows Error Reporting does. Its entry names the
/// exception type and the faulting module, which is exactly the missing piece: that is how the
/// layout recursion on large libraries was identified in the first place.
/// </summary>
internal static class WindowsErrorReport
{
    /// <summary>How far either side of the run to look. WER can lag the crash by a few seconds.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    public static string? Describe(CrashReporter.SessionInfo session)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var from = session.StartedUtc.ToLocalTime() - Slack;
            var text = new StringBuilder();

            using var log = new EventLog("Application");

            // Newest first: the crash we want is at the end of the run, not the start.
            for (var i = log.Entries.Count - 1; i >= 0 && text.Length < 4000; i--)
            {
                EventLogEntry entry;
                try { entry = log.Entries[i]; }
                catch { continue; }

                if (entry.TimeGenerated < from) break;
                if (!Mentions(entry.Message)) continue;

                text.AppendLine($"[{entry.TimeGenerated:dd MMM HH:mm:ss}] {entry.Source}");
                text.AppendLine(Condense(entry.Message));
                text.AppendLine();
            }

            return text.Length == 0
                ? "Windows has no record of it. That usually means the process was closed rather " +
                  "than having failed — a forced quit, a sign-out, or the power going."
                : text.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            // Reading the event log needs permissions we may not have. Never a reason to fail.
            return $"(the Windows event log could not be read: {ex.Message})";
        }
    }

    private static bool Mentions(string message) =>
        message.Contains("ProjectSoundboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WER entries are long and mostly boilerplate. Keep the lines that identify the failure.
    /// </summary>
    private static string Condense(string message)
    {
        var keep = new[]
        {
            "Faulting application name", "Faulting module name", "Exception code",
            "Problem signature", "P4:", "P5:", "P7:", "P9:", "Fault offset",
            "System.", "Application:", "Framework Version", "Description:", "Unhandled exception"
        };

        var lines = message.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && keep.Any(k => l.Contains(k, StringComparison.Ordinal)))
            .Take(14);

        var text = string.Join(Environment.NewLine + "  ", lines);
        return text.Length == 0 ? "  " + Truncate(message, 400) : "  " + text;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.Replace("\r\n", " ") : value[..max].Replace("\r\n", " ") + "…";
}
