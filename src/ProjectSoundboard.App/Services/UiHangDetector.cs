using System.Diagnostics;
using System.Windows.Threading;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Watches whether the window is still answering.
///
/// A frozen application leaves nothing behind. It has not crashed, so no exception is thrown
/// and nothing is caught; Windows records nothing unless the user goes through the "not
/// responding" dialog; and the log simply stops mid-run with no indication that anything is
/// wrong. All that reaches the person using it is an app that has to be ended by hand.
///
/// So the interface thread is asked, from outside, whether it is still there. When it stops
/// answering that gets written down while it is still stuck — the end of the log is then the
/// last thing the app managed before it went quiet, which is the clue worth having.
/// </summary>
internal sealed class UiHangDetector : IDisposable
{
    /// <summary>How often to ask. Cheap: one empty message on the queue.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long without an answer before it counts. Generous on purpose — opening a folder
    /// of thousands of files, or a driver taking its time, can hold the thread for a while
    /// without anything actually being wrong.
    /// </summary>
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(20);

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;

    /// <summary>
    /// When the interface last finished answering, or 0 before it ever has. Counting from
    /// startup instead was wrong: the first report from a real freeze timed it from a reply
    /// that had never happened, so the duration was whatever the app had been running rather
    /// than how long it had been stuck.
    /// </summary>
    private long _lastReplyTicks;

    private bool _reported;
    private bool _disposed;

    public UiHangDetector(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "ui-hang-detector",
            // Below normal: this must never be the reason the interface is slow.
            Priority = ThreadPriority.BelowNormal
        };
    }

    public void Start() => _thread.Start();

    private void Watch()
    {
        while (!_cancel.IsCancellationRequested)
        {
            // Normal priority, not Background. Background sits behind every other kind of
            // work, so a genuinely busy start-up looked identical to a frozen one. At Normal
            // it is answered as soon as the thread is pumping at all, and going unanswered
            // means blocked rather than merely busy.
            _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => Volatile.Write(ref _lastReplyTicks, Stopwatch.GetTimestamp())));

            if (_cancel.Token.WaitHandle.WaitOne(Interval)) return;

            var last = Volatile.Read(ref _lastReplyTicks);

            // Nothing to measure against until the interface has answered once. Start-up is
            // legitimately busy, and timing a freeze from before the app was ever up would
            // report a duration that means nothing.
            if (last == 0) continue;

            var since = Stopwatch.GetElapsedTime(last);

            if (since >= Threshold)
            {
                // Once per episode. A freeze that lasts minutes should not produce a report
                // every two seconds.
                if (!_reported)
                {
                    _reported = true;

                    Log.Error($"The interface has not responded for {since.TotalSeconds:F0} seconds. " +
                              "Writing a report now, while it is still stuck.");

                    CrashReporter.WriteHang(since, UpdateService.CurrentVersion.ToString());
                }
            }
            else if (_reported)
            {
                _reported = false;
                Log.Warn("The interface started responding again.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancel.Cancel();
        _cancel.Dispose();
    }
}
