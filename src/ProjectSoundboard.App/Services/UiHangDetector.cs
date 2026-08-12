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

    /// <summary>When the interface last finished answering. Written by the UI thread.</summary>
    private long _lastReplyTicks = Stopwatch.GetTimestamp();

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
            // Background priority, so it queues behind everything the app actually wants to
            // do. That is the point: it is answered once the interface is genuinely free.
            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => Volatile.Write(ref _lastReplyTicks, Stopwatch.GetTimestamp())));

            if (_cancel.Token.WaitHandle.WaitOne(Interval)) return;

            var since = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReplyTicks));

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
