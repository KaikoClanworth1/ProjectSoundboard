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
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--save") return ConcurrentSave();

        return CrashReport();
    }

    /// <summary>
    /// Saves the same file from several threads at once, which is what happens when a folder
    /// watcher notices a file at the same moment the app is adding it deliberately —
    /// downloading into a watched folder does exactly that.
    ///
    /// Both saves used the same temp name, the second found it held by the first, and the
    /// exception surfaced on a timer thread where nothing caught it and ended the process.
    /// </summary>
    private static int ConcurrentSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "psb-save-probe");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, "library.json");
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        const int threads = 8;
        const int rounds = 40;

        using (var ready = new Barrier(threads))
        {
            var workers = Enumerable.Range(0, threads).Select(n => new Thread(() =>
            {
                // All in at once, so they genuinely collide rather than queue politely.
                ready.SignalAndWait();

                for (var i = 0; i < rounds; i++)
                {
                    try
                    {
                        JsonStore.Save(path, new { Writer = n, Round = i, Sounds = new int[64] });
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }
            })).ToList();

            foreach (var w in workers) w.Start();
            foreach (var w in workers) w.Join();
        }

        Console.WriteLine($"{threads} threads x {rounds} saves of the same file");
        Console.WriteLine($"  failures : {failures.Count}");

        foreach (var ex in failures.Take(3)) Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");

        var readable = false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            readable = document.RootElement.TryGetProperty("Writer", out _);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    the file did not survive: {ex.Message}");
        }

        Console.WriteLine($"  result readable afterwards : {readable}");

        // Nothing left behind: a temp file per failed save would accumulate.
        var strays = Directory.GetFiles(root, "*.tmp").Length;
        Console.WriteLine($"  stray temp files : {strays}");

        try { Directory.Delete(root, true); } catch { /* best effort */ }

        var ok = failures.IsEmpty && readable && strays == 0;
        Console.WriteLine(ok
            ? "PASS - concurrent saves do not collide, and the file survives."
            : "FAIL - saving the same file from two places still breaks.");

        return ok ? 0 : 1;
    }

    private static int CrashReport()
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
