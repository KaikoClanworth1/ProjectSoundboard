using System.Diagnostics;
using ProjectSoundboard.Core.Storage;

namespace ReportProbe;

/// <summary>
/// Checks that a report about a frozen app still gets written when the app is frozen.
///
/// Describing the setup reads the library and the playing sounds, both behind locks. If the
/// stuck thread is holding one of those, asking for it on the reporting thread waits forever
/// — and the report meant to explain the freeze never appears. Which is exactly what a user
/// saw: unresponsive, and no report to show for it.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "psb-report-probe");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        AppPaths.SetDataRoot(root);
        Log.Start();
        Log.Info("A line of context, so the report has something to carry.");

        // The situation being tested: collecting context never returns.
        CrashReporter.DescribeEnvironment = () =>
        {
            Thread.Sleep(Timeout.Infinite);
            return "never reached";
        };

        var clock = Stopwatch.StartNew();
        var path = CrashReporter.WriteHang(TimeSpan.FromSeconds(12), "probe");
        clock.Stop();

        Console.WriteLine($"took   : {clock.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"written: {(path is null ? "(nothing)" : Path.GetFileName(path))}");

        var failures = 0;

        if (path is null || !File.Exists(path))
        {
            Console.WriteLine("FAIL — no report was written at all.");
            failures++;
        }
        else
        {
            var text = File.ReadAllText(path);

            if (!text.Contains("could not be read"))
            {
                Console.WriteLine("FAIL — the report does not say the context was unavailable.");
                failures++;
            }

            if (!text.Contains("something to carry"))
            {
                Console.WriteLine("FAIL — the report is missing the log it should carry.");
                failures++;
            }
        }

        if (clock.Elapsed > TimeSpan.FromSeconds(10))
        {
            Console.WriteLine("FAIL — it waited far too long on the blocked collector.");
            failures++;
        }

        Console.WriteLine(failures == 0
            ? "PASS — a frozen app still produces a report, and it says why the setup is missing."
            : $"{failures} check(s) failed.");

        Log.Shutdown();
        return failures == 0 ? 0 : 1;
    }
}
