using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LoopbackProbe;

/// <summary>
/// Captures whatever Windows is actually playing on the default output device and prints the
/// level every quarter second, stamped with elapsed milliseconds.
///
/// This is the only honest way to answer "does the mute button mute": it measures what comes
/// out of the speakers, not what the code believes it is doing. Run it alongside the app and
/// line the timestamps up with whatever was clicked.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var seconds = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 20.0;
        var wanted = args.Length > 1 ? args[1] : null;

        using var enumerator = new MMDeviceEnumerator();

        // Naming a device matters here: the soundboard feeds two of them at once, and the
        // whole point of the second mute is that they stop carrying different things.
        var device = wanted is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"No active output device matching '{wanted}'.");

        Console.WriteLine($"device: {device.FriendlyName}");
        Console.WriteLine($"window: {seconds:F0}s, level printed every 250 ms");
        Console.WriteLine();

        using var capture = new WasapiLoopbackCapture(device);

        var format = capture.WaveFormat;
        var bytesPerSample = format.BitsPerSample / 8;
        var start = System.Diagnostics.Stopwatch.StartNew();

        var windowPeak = 0f;
        var windowIndex = 0L;

        capture.DataAvailable += (_, e) =>
        {
            // The loopback stream is 32-bit float on every machine that matters here.
            for (var i = 0; i + bytesPerSample <= e.BytesRecorded; i += bytesPerSample)
            {
                var sample = BitConverter.ToSingle(e.Buffer, i);
                var abs = Math.Abs(sample);
                if (abs > windowPeak) windowPeak = abs;
            }

            var elapsed = start.ElapsedMilliseconds;
            while (elapsed >= (windowIndex + 1) * 250)
            {
                windowIndex++;
                var peak = windowPeak;
                windowPeak = 0f;

                var db = peak < 1e-6f ? -120 : 20 * Math.Log10(peak);
                var bar = new string('#', Math.Clamp((int)((db + 60) / 3), 0, 20));

                Console.WriteLine($"{windowIndex * 250,6} ms  peak={peak:F5}  {db,7:F1} dB  {bar}");
            }
        };

        capture.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.StopRecording();

        Console.WriteLine();
        Console.WriteLine("done");
        return 0;
    }
}
