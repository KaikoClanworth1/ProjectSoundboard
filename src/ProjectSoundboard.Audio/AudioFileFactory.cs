using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>A decoded audio file, already converted to the engine's mix format.</summary>
public sealed class AudioSource : IDisposable
{
    public required ISampleProvider Provider { get; init; }
    public required WaveStream Stream { get; init; }
    public TimeSpan Duration => Stream.TotalTime;

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { /* already gone */ }
    }
}

/// <summary>
/// Opens audio files of every supported format and normalises them to one mix format,
/// so the rest of the engine only ever deals with 32-bit float at a fixed rate.
/// </summary>
public static class AudioFileFactory
{
    /// <summary>
    /// Open <paramref name="path"/> and convert to <paramref name="sampleRate"/> /
    /// <paramref name="channels"/>. Throws if the file cannot be decoded.
    /// </summary>
    public static AudioSource Open(string path, int sampleRate, int channels)
    {
        var stream = OpenReader(path);

        try
        {
            ISampleProvider provider = stream.ToSampleProvider();

            if (provider.WaveFormat.Channels != channels)
                provider = MatchChannels(provider, channels);

            if (provider.WaveFormat.SampleRate != sampleRate)
            {
                // WDL's resampler is high quality and cheap enough to run per voice.
                provider = new WdlResamplingSampleProvider(provider, sampleRate);

                if (provider.WaveFormat.Channels != channels)
                    provider = MatchChannels(provider, channels);
            }

            return new AudioSource { Provider = provider, Stream = stream };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static ISampleProvider MatchChannels(ISampleProvider provider, int channels)
    {
        if (provider.WaveFormat.Channels == channels) return provider;

        if (provider.WaveFormat.Channels == 1 && channels == 2)
            return new MonoToStereoSampleProvider(provider);

        if (provider.WaveFormat.Channels == 2 && channels == 1)
            return new StereoToMonoSampleProvider(provider);

        // Surround or other exotic layouts: fold to mono then fan back out.
        var mono = new MultiplexingSampleProvider(new[] { provider }, 1);
        return channels == 1 ? mono : new MonoToStereoSampleProvider(mono);
    }

    /// <summary>
    /// Pick a decoder by extension, with Media Foundation as the general purpose fallback.
    /// Media Foundation on Windows 10+ covers MP3, WAV, M4A, AAC, WMA and FLAC.
    /// </summary>
    private static WaveStream OpenReader(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found.", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            return ext switch
            {
                // Media Foundation only handles Opus inside WebM/Matroska, so bare .opus
                // needs its own decoder.
                ".opus" => new OpusFileReader(path),
                ".ogg" => new VorbisWaveReader(path),
                ".wav" or ".aiff" or ".aif" => new AudioFileReader(path),
                _ => new MediaFoundationReader(path)
            };
        }
        catch (Exception first)
        {
            Log.Debug($"Primary decoder failed for {Path.GetFileName(path)}: {first.Message}");

            // Container and extension disagree more often than you would hope
            // (an .ogg holding Opus, an .m4a that is really a .mp3, …).
            foreach (var fallback in Fallbacks(ext))
            {
                try { return fallback(path); }
                catch { /* try the next one */ }
            }

            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' could not be decoded. {first.Message}", first);
        }
    }

    private static IEnumerable<Func<string, WaveStream>> Fallbacks(string ext)
    {
        // An .ogg container can hold Opus just as easily as Vorbis, and downloaded audio is
        // routinely misnamed, so every decoder gets a turn regardless of the extension.
        if (ext != ".ogg") yield return p => new VorbisWaveReader(p);
        if (ext != ".opus") yield return p => new OpusFileReader(p);
        yield return p => new MediaFoundationReader(p);
        yield return p => new AudioFileReader(p);
    }

    /// <summary>Read the peak sample of a file, used for volume normalisation.</summary>
    public static float MeasurePeak(string path, int sampleRate, int channels, CancellationToken ct = default)
    {
        try
        {
            using var source = Open(path, sampleRate, channels);
            var buffer = new float[sampleRate * channels / 4];
            var peak = 0f;
            int read;

            while ((read = source.Provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                {
                    var abs = Math.Abs(buffer[i]);
                    if (abs > peak) peak = abs;
                }
            }

            return peak;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"Peak analysis failed for {path}: {ex.Message}");
            return 0f;
        }
    }
}
