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
    /// <summary>Playback window. Must exceed the buffer under test or nothing can run dry.</summary>
    private static double Seconds = 4.0;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: StreamProbe <file> [file ...]");
            return 2;
        }

        if (args[0].StartsWith("--secs=", StringComparison.Ordinal))
        {
            Seconds = double.Parse(args[0]["--secs=".Length..]);
            args = args.Skip(1).ToArray();
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

        if (args[0] == "--rapid")
        {
            Console.WriteLine("mode: RAPID (how long starting a sound blocks the caller)");
            Console.WriteLine();

            RapidProbe(args.Skip(1).ToArray());
            return 0;
        }

        if (args[0] == "--loopclock")
        {
            Console.WriteLine("mode: LOOP CLOCK (reported position must return to 0 each pass)");
            Console.WriteLine();

            foreach (var path in args.Skip(1)) LoopClockProbe(path);
            return 0;
        }

        if (args[0] == "--loop")
        {
            Console.WriteLine("mode: LOOP (SetLoop while playing, what the loop button drives)");
            Console.WriteLine();

            foreach (var path in args.Skip(1)) LoopProbe(path);
            return 0;
        }

        if (args[0] == "--mute")
        {
            Console.WriteLine("mode: MUTE (ExternalGain, what the mute button drives)");
            Console.WriteLine();

            foreach (var path in args.Skip(1)) MuteProbe(path);
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
    /// Starts one sound after another the way pressing Next repeatedly does, and times how
    /// long each start holds up the caller.
    ///
    /// That caller is the interface thread in the real app, so any time spent here is time
    /// the window is frozen. Two voices are made per sound, one per output, exactly as the
    /// engine does when the two outputs are different devices.
    /// </summary>
    private static void RapidProbe(string[] paths)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        var live = new List<StreamingVoice>();
        var worst = TimeSpan.Zero;
        var total = TimeSpan.Zero;

        try
        {
            for (var round = 0; round < paths.Length; round++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // Both outputs, like the engine.
                live.Add(new StreamingVoice(paths[round], new VoiceSettings { Volume = 1f }, format));
                live.Add(new StreamingVoice(paths[round], new VoiceSettings { Volume = 1f }, format));

                clock.Stop();
                total += clock.Elapsed;
                if (clock.Elapsed > worst) worst = clock.Elapsed;

                Console.WriteLine($"  start {round + 1,2}: blocked the caller for " +
                                  $"{clock.Elapsed.TotalMilliseconds,7:F0} ms   " +
                                  $"({Path.GetFileName(paths[round])})");

                // Roughly the pace of somebody clicking Next.
                Thread.Sleep(400);
            }
        }
        finally
        {
            foreach (var voice in live) voice.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"  worst: {worst.TotalMilliseconds:F0} ms, total frozen: {total.TotalMilliseconds:F0} ms");

        // A quarter second of frozen window per press is already bad; a second is a hang.
        Console.WriteLine(worst < TimeSpan.FromMilliseconds(250)
            ? "PASS — starting a sound does not hold up the caller."
            : "FAIL — starting a sound freezes the caller for too long.");
    }

    /// <summary>
    /// Loops a one-second region and watches the position the transport bar reads. It has to
    /// sawtooth back to zero on each pass, not keep climbing past the length of the sound.
    /// </summary>
    private static void LoopClockProbe(string path)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        var settings = new VoiceSettings { Volume = 1f, TrimEndMs = 1000, Loop = true };

        using var voice = new StreamingVoice(path, settings, format);

        const int frames = SampleRate / 100;
        var buffer = new float[frames * Channels];
        var positions = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var b = 0; b < 320; b++)   // 3.2 s, so at least three passes
        {
            voice.Read(buffer, 0, buffer.Length);
            if (b % 25 == 0) positions.Add(voice.PositionSeconds);

            var due = TimeSpan.FromMilliseconds((b + 1) * 10);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }

        var max = positions.Max();
        var wraps = positions.Zip(positions.Skip(1), (a, c) => c < a - 0.1).Count(x => x);

        // Two things have to hold: it never reads past the one-second region, and it visibly
        // goes backwards at least twice in 3.2 s.
        var ok = max <= 1.1 && wraps >= 2;

        Console.WriteLine(
            $"{(ok ? "PASS" : "FAIL")}  {Path.GetFileName(path),-46}  " +
            $"max={max:F2}s wraps={wraps}");
        Console.WriteLine("        " + string.Join(" ", positions.Select(p => $"{p:F2}")));
    }

    /// <summary>
    /// Trims the sound to its first second, then checks that it stops there when looping is
    /// off, and keeps going past it when looping is switched on mid-playback.
    /// </summary>
    private static void LoopProbe(string path)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        static VoiceSettings OneSecond() =>
            new() { Volume = 1f, TrimStartMs = 0, TrimEndMs = 1000 };

        // Control: without looping it has to end at the trim point.
        using (var plain = new StreamingVoice(path, OneSecond(), format))
        {
            var (endedAt, _) = RunUntilEnd(plain, 2.0, null);
            Console.WriteLine(
                $"{(endedAt is > 0.5 and < 1.5 ? "PASS" : "FAIL")}  loop off  " +
                $"{Path.GetFileName(path),-46}  ended at {endedAt:F2}s (expected ~1.00s)");
        }

        // Switch looping on part way through: it must run straight past the trim point.
        using (var looped = new StreamingVoice(path, OneSecond(), format))
        {
            var (endedAt, peak) = RunUntilEnd(looped, 2.5, 0.5);
            var ok = endedAt < 0 && peak > 0.001f;
            Console.WriteLine(
                $"{(ok ? "PASS" : "FAIL")}  loop on   {Path.GetFileName(path),-46}  " +
                $"{(endedAt < 0 ? "still playing at 2.50s" : $"ended at {endedAt:F2}s")}, peak={peak:F4}");
        }
    }

    /// <summary>
    /// Play at real-time pace. Returns when the voice ends (with the time it ended) or the
    /// window expires (-1). <paramref name="enableLoopAt"/> switches looping on part way.
    /// </summary>
    private static (double EndedAt, float Peak) RunUntilEnd(
        StreamingVoice voice, double seconds, double? enableLoopAt)
    {
        const int frames = SampleRate / 100;
        var buffer = new float[frames * Channels];
        var blocks = (int)(seconds * 100);
        var peak = 0f;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var b = 0; b < blocks; b++)
        {
            var now = b / 100.0;
            if (enableLoopAt is { } at && now >= at) voice.SetLoop(true);

            var read = voice.Read(buffer, 0, buffer.Length);
            var block = Peak(buffer, read);

            // Only count signal from after the loop was switched on, so a loud opening
            // second cannot stand in for audio that is no longer being produced.
            if (enableLoopAt is null || now > 1.2) { if (block > peak) peak = block; }

            if (read < buffer.Length) return (now, peak);

            var due = TimeSpan.FromMilliseconds((b + 1) * 10);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }

        return (-1, peak);
    }

    /// <summary>
    /// The soundboard mute button ends up setting ExternalGain to 0 on every live voice.
    /// This checks that a voice actually goes silent when it does.
    /// </summary>
    private static void MuteProbe(string path)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        using var voice = new StreamingVoice(path, new VoiceSettings { Volume = 1f }, format);

        // Paced to real time. Draining flat out empties the ring faster than the decoder
        // fills it, and the silence that follows looks exactly like a successful mute.
        var before = ReadPhase(voice, 10);

        voice.ExternalGain = 0f;
        var muted = ReadPhase(voice, 10);

        voice.ExternalGain = 1f;
        var after = ReadPhase(voice, 10);

        var ok = before > 0.001f && muted < 0.0001f && after > 0.001f;

        Console.WriteLine(
            $"{(ok ? "PASS" : "FAIL")}  {Path.GetFileName(path),-50}  " +
            $"before={before:F4} muted={muted:F6} unmuted={after:F4}");
    }

    /// <summary>Read <paramref name="blocks"/> × 10 ms at real-time pace, loudest sample wins.</summary>
    private static float ReadPhase(StreamingVoice voice, int blocks)
    {
        const int frames = SampleRate / 100;
        var buffer = new float[frames * Channels];
        var peak = 0f;

        for (var i = 0; i < blocks; i++)
        {
            var read = voice.Read(buffer, 0, buffer.Length);
            var block = Peak(buffer, read);
            if (block > peak) peak = block;
            Thread.Sleep(10);
        }

        return peak;
    }

    private static float Peak(float[] buffer, int count)
    {
        var peak = 0f;
        for (var i = 0; i < count; i++)
        {
            var abs = Math.Abs(buffer[i]);
            if (abs > peak) peak = abs;
        }
        return peak;
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
