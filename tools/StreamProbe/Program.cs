using NAudio.Wave;
using ProjectSoundboard.Audio.Playback;

namespace StreamProbe;

/// <summary>
/// Plays the streaming path for a few seconds and reports whether audio actually came out.
///
/// The voice is constructed on an STA thread on purpose: that is what the UI thread is, and
/// creating a Media Foundation reader there and then reading it from the decoder thread is
/// what silently killed playback a fraction of a second after it started.
/// </summary>
internal static class Program
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const double Seconds = 4.0;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: StreamProbe <file> [file ...]");
            return 2;
        }

        Console.WriteLine($"apartment: {Thread.CurrentThread.GetApartmentState()}");

        // Control: reproduce the old arrangement (reader opened here, read from a background
        // thread) so a pass below means the fix worked rather than the probe being blind.
        if (args[0] == "--legacy")
        {
            Console.WriteLine("mode: LEGACY (open on STA, read on background thread)");
            Console.WriteLine();

            foreach (var path in args.Skip(1)) LegacyProbe(path);
            return 0;
        }

        Console.WriteLine();

        var failures = 0;

        foreach (var path in args)
        {
            try
            {
                if (!Probe(path)) failures++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {Path.GetFileName(path)}  ({ex.GetType().Name}: {ex.Message})");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static bool Probe(string path)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        var settings = new VoiceSettings { Volume = 1f };

        using var voice = new StreamingVoice(path, settings, format);

        // 10 ms at a time, paced to real time. Draining flat out would empty the ring far
        // faster than the decoder can fill it and report starvation that no real playback
        // would ever see.
        const int frames = SampleRate / 100;
        var buffer = new float[frames * Channels];

        var blocks = (int)(Seconds * 100);
        var peak = 0f;
        long nonSilent = 0;
        long produced = 0;
        var endedEarly = false;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var b = 0; b < blocks; b++)
        {
            var read = voice.Read(buffer, 0, buffer.Length);

            if (read < buffer.Length)
            {
                endedEarly = true;
                produced += read;
                Measure(buffer, read, ref peak, ref nonSilent);
                break;
            }

            produced += read;
            Measure(buffer, read, ref peak, ref nonSilent);

            var due = TimeSpan.FromMilliseconds((b + 1) * 10);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }

        var producedSeconds = produced / (double)(SampleRate * Channels);
        var audibleRatio = produced == 0 ? 0 : nonSilent / (double)produced;

        // A working stream fills the whole window, has real signal in it, and is not mostly
        // silence papered over the top of a dead decoder.
        var ok = !endedEarly && peak > 0.001f && audibleRatio > 0.5;

        Console.WriteLine(
            $"{(ok ? "PASS" : "FAIL")}  {Path.GetFileName(path),-58}  " +
            $"played={producedSeconds:F2}s peak={peak:F4} audible={audibleRatio:P0} " +
            $"starvations={voice.Starvations}{(endedEarly ? " ENDED-EARLY" : "")}");

        return ok;
    }

    /// <summary>
    /// What the shipped code used to do: open the decoder on the caller's STA thread, then
    /// read it from a background thread.
    /// </summary>
    private static void LegacyProbe(string path)
    {
        using var source = ProjectSoundboard.Audio.AudioFileFactory.Open(path, SampleRate, Channels);

        // Priming on this thread works, which is exactly why the sound started before dying.
        var primer = new float[SampleRate * Channels / 4];
        var primed = source.Provider.Read(primer, 0, primer.Length);

        string? error = null;
        var readOnOtherThread = 0;

        var worker = new Thread(() =>
        {
            var buffer = new float[4096 * Channels];
            try { readOnOtherThread = source.Provider.Read(buffer, 0, buffer.Length); }
            catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; }
        });

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        Console.WriteLine(
            $"{Path.GetFileName(path),-58}  primed={primed} background-read={readOnOtherThread}");

        if (error is not null) Console.WriteLine($"    -> {error}");
    }

    private static void Measure(float[] buffer, int count, ref float peak, ref long nonSilent)
    {
        for (var i = 0; i < count; i++)
        {
            var abs = Math.Abs(buffer[i]);
            if (abs > peak) peak = abs;
            if (abs > 0.0001f) nonSilent++;
        }
    }
}
