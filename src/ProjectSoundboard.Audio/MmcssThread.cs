using System.Runtime.InteropServices;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>
/// Tells Windows that a thread is doing audio work, through the Multimedia Class Scheduler.
///
/// Without this the threads that decode and mix are, as far as the scheduler is concerned,
/// ordinary work competing with everything else. That is fine until the machine is busy —
/// saving a large image, compiling, scanning for viruses — at which point they stop being
/// given the CPU often enough to meet the sound card's deadline, and the sound breaks up.
///
/// Registering under "Pro Audio" moves the thread into a scheduling class Windows keeps
/// running through exactly that kind of load. It is what audio software does, and it costs
/// one call per thread.
/// </summary>
internal static class MmcssThread
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    /// <summary>AVRT_PRIORITY_HIGH.</summary>
    private const int PriorityHigh = 2;

    /// <summary>Once per thread; the registration lasts as long as the thread does.</summary>
    [ThreadStatic]
    private static bool _registered;

    /// <summary>
    /// Register the calling thread. Safe to call on every buffer: after the first it is a
    /// single thread-static check, which is the point — the render thread is owned by NAudio
    /// and there is no other moment to reach it.
    /// </summary>
    public static void EnsureRegistered(string task = "Pro Audio", bool high = false)
    {
        if (_registered) return;
        _registered = true;

        if (!OperatingSystem.IsWindows()) return;

        try
        {
            uint index = 0;
            var handle = AvSetMmThreadCharacteristicsW(task, ref index);

            if (handle == IntPtr.Zero)
            {
                Log.Debug($"Could not register this thread as '{task}' audio work: " +
                          $"error {Marshal.GetLastWin32Error()}.");
                return;
            }

            if (high) AvSetMmThreadPriority(handle, PriorityHigh);

            // Deliberately never reverted. The handle is released when the thread ends, and
            // these threads live exactly as long as the work they are doing.
            Log.Debug($"Thread '{Thread.CurrentThread.Name ?? "audio"}' registered as '{task}'.");
        }
        catch (Exception ex)
        {
            // Missing on stripped-down Windows installs. Never a reason to stop playing.
            Log.Debug($"MMCSS unavailable: {ex.Message}");
        }
    }
}
