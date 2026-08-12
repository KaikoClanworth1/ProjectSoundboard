using System.Diagnostics;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Guarantees that closing the window closes the application.
///
/// Shutting down means stopping WASAPI devices, and a stop can block for as long as the
/// driver wants to take — a virtual cable whose other end went away, a wireless headset that
/// dropped, a device removed mid-run. Any of those leaves the process alive with no window,
/// which looks exactly like the app being stuck in the background and ends in Task Manager.
///
/// So shutdown is given a deadline. Overrunning it is written to the log, with the step that
/// was still going, before the process is ended by hand.
/// </summary>
internal static class ShutdownWatchdog
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    private static int _started;
    private static volatile string _step = "closing";

    /// <summary>What is being shut down at the moment, named in the log if we run out of time.</summary>
    public static void Reached(string step) => _step = step;

    public static void Start()
    {
        // Closing can be entered more than once; only the first arms it.
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        var thread = new Thread(Watch)
        {
            // Background, so a shutdown that finishes properly takes this with it.
            IsBackground = true,
            Name = "shutdown-watchdog"
        };

        thread.Start();
    }

    private static void Watch()
    {
        var clock = Stopwatch.StartNew();
        Thread.Sleep(Deadline);

        // Still here, so something is not letting go.
        Log.Error($"Shutdown was still at '{_step}' after {clock.Elapsed.TotalSeconds:F0} seconds. " +
                  "Ending the process rather than leaving it running with no window. This is " +
                  "usually an audio device that will not stop — see the log above for which one.");

        Log.Shutdown();

        // Deliberate, so the next start must not read it as a crash.
        try { CrashReporter.EndSession(); } catch { /* going down regardless */ }

        Environment.Exit(0);
    }
}
