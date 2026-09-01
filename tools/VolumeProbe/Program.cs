using ProjectSoundboard.Audio;
using ProjectSoundboard.Core.Storage;

namespace VolumeProbe;

/// <summary>
/// Whether the volume the slider shows after a restart is the volume the sound comes out at.
///
/// The report is that it is not: the slider sits where it was left, but the sound is quieter
/// until the slider is moved, at which point it jumps to where it should have been. That can
/// only mean the saved number reached the slider and not the audio, so this walks the two
/// paths — the one taken on startup, and the one taken when the slider moves — and compares
/// what each leaves on the buses.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var settings = new SettingsService();
        settings.Load();

        var audio = settings.Settings.Audio;

        // A level nobody would land on by accident, so a stale default is obvious.
        audio.MasterVolume = 0.6f;
        audio.VirtualMicVolume = 1.0f;
        audio.MonitorVolume = 0.8f;

        var wantedMic = audio.VirtualMicVolume * audio.MasterVolume;
        var wantedMonitor = audio.MonitorVolume * audio.MasterVolume;

        Console.WriteLine($"  saved master volume : {audio.MasterVolume:0.00}");
        Console.WriteLine($"  so the buses want   : mic {wantedMic:0.000}, monitor {wantedMonitor:0.000}");
        Console.WriteLine();

        var devices = new AudioDeviceService();
        using var engine = new AudioEngine(settings, devices);

        // --- the startup path -------------------------------------------------
        engine.ApplyAudioSettings();

        var startupMic = engine.VirtualMicBus.Volume;
        var startupMonitor = engine.MonitorBus.Volume;

        Console.WriteLine($"  after startup       : mic {startupMic:0.000}, monitor {startupMonitor:0.000}");

        // --- the path taken when the slider is dragged -----------------------
        engine.SetMasterVolume(0.6f);

        var draggedMic = engine.VirtualMicBus.Volume;
        var draggedMonitor = engine.MonitorBus.Volume;

        Console.WriteLine($"  after a drag        : mic {draggedMic:0.000}, monitor {draggedMonitor:0.000}");
        Console.WriteLine();

        var failures = 0;

        if (Math.Abs(startupMic - wantedMic) > 0.001f ||
            Math.Abs(startupMonitor - wantedMonitor) > 0.001f)
        {
            Console.WriteLine("FAIL - starting up does not put the saved volume on the buses.");
            failures++;
        }

        if (Math.Abs(startupMic - draggedMic) > 0.001f ||
            Math.Abs(startupMonitor - draggedMonitor) > 0.001f)
        {
            Console.WriteLine("FAIL - moving the slider to the value it already had changes the volume.");
            failures++;
        }

        // --- and what actually comes out ------------------------------------
        //
        // The numbers above are fields. This plays a real sound and reads the level off the
        // bus meter, which is the only thing that answers "is it as loud as it should be".
        var sample = Directory.EnumerateFiles(@"F:\Downloads", "*.mp3").FirstOrDefault();

        if (sample is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  playing {Path.GetFileName(sample)}");

            var atStartup = Measure(engine, sample);

            // Now the drag: the same value the slider already showed.
            engine.SetMasterVolume(0.6f);
            var afterDrag = Measure(engine, sample);

            Console.WriteLine($"  peak as started     : {atStartup:0.0000}");
            Console.WriteLine($"  peak after a drag   : {afterDrag:0.0000}");

            if (atStartup > 0.0001f && Math.Abs(atStartup - afterDrag) / atStartup > 0.05)
            {
                Console.WriteLine("FAIL - moving the slider to where it already was changed the loudness.");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - the volume that is saved is the volume that comes out."
            : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Play a sound for a moment and report the loudest the bus meter saw.</summary>
    private static float Measure(AudioEngine engine, string path)
    {
        var entry = new ProjectSoundboard.Core.Models.SoundEntry { FilePath = path };

        var handle = engine.Play(entry, PlayTarget.Both);
        if (handle is null) { Console.WriteLine("  (nothing played)"); return 0f; }

        var peak = 0f;

        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(50);
            if (engine.MonitorBus.Meter is { } meter) peak = Math.Max(peak, meter.Peak);
        }

        engine.StopAll();
        Thread.Sleep(200);

        return peak;
    }
}
