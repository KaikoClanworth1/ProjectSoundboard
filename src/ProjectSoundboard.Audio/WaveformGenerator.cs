using System.Security.Cryptography;
using System.Text;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>Min/max peak pairs per pixel column, ready to draw.</summary>
public sealed class WaveformData
{
    public required float[] Min { get; init; }
    public required float[] Max { get; init; }
    public required double DurationSeconds { get; init; }
    public int Buckets => Min.Length;

    /// <summary>Loudest sample in the file, reusable for normalisation.</summary>
    public float Peak { get; init; }
}

/// <summary>
/// Produces the waveform preview shown in the properties panel and the trim editor.
/// Results are cached on disk so re-opening a sound is instant.
/// </summary>
public static class WaveformGenerator
{
    private const int FileMagic = 0x50535746; // "PSWF"
    public const int DefaultBuckets = 600;

    public static WaveformData? Generate(
        string path, int buckets = DefaultBuckets, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 32, 4000);

        var cacheFile = CachePath(path, buckets);
        var cached = TryReadCache(cacheFile);
        if (cached is not null) return cached;

        try
        {
            // Analysis quality does not need the full rate; 22 kHz mono is plenty for a
            // preview and roughly quarters the decode cost.
            const int analysisRate = 22050;
            using var source = AudioFileFactory.Open(path, analysisRate, 1);

            var duration = source.Duration.TotalSeconds;
            var totalSamples = (long)(duration * analysisRate);
            if (totalSamples <= 0) return null;

            var samplesPerBucket = Math.Max(1, totalSamples / buckets);

            var min = new float[buckets];
            var max = new float[buckets];
            var buffer = new float[8192];

            var bucket = 0;
            var inBucket = 0L;
            var lo = float.MaxValue;
            var hi = float.MinValue;
            var peak = 0f;

            int read;
            while ((read = source.Provider.Read(buffer, 0, buffer.Length)) > 0 && bucket < buckets)
            {
                ct.ThrowIfCancellationRequested();

                for (var i = 0; i < read && bucket < buckets; i++)
                {
                    var v = buffer[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;

                    var abs = Math.Abs(v);
                    if (abs > peak) peak = abs;

                    if (++inBucket < samplesPerBucket) continue;

                    min[bucket] = lo == float.MaxValue ? 0 : lo;
                    max[bucket] = hi == float.MinValue ? 0 : hi;
                    bucket++;
                    inBucket = 0;
                    lo = float.MaxValue;
                    hi = float.MinValue;
                }
            }

            // Flush a partially filled trailing bucket.
            if (bucket < buckets && inBucket > 0)
            {
                min[bucket] = lo == float.MaxValue ? 0 : lo;
                max[bucket] = hi == float.MinValue ? 0 : hi;
                bucket++;
            }

            var data = new WaveformData
            {
                Min = min,
                Max = max,
                DurationSeconds = duration,
                Peak = peak
            };

            TryWriteCache(cacheFile, data);
            return data;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"Waveform generation failed for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public static Task<WaveformData?> GenerateAsync(
        string path, int buckets = DefaultBuckets, CancellationToken ct = default) =>
        Task.Run(() => Generate(path, buckets, ct), ct);

    // ---- disk cache -------------------------------------------------------

    private static string CachePath(string audioPath, int buckets)
    {
        var stamp = 0L;
        var size = 0L;
        try
        {
            var info = new FileInfo(audioPath);
            if (info.Exists) { stamp = info.LastWriteTimeUtc.Ticks; size = info.Length; }
        }
        catch { /* fall through with zeros */ }

        var key = $"{audioPath.ToLowerInvariant()}|{stamp}|{size}|{buckets}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.WaveformCacheDir, hash + ".pswf");
    }

    private static WaveformData? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadInt32() != FileMagic) return null;

            var duration = reader.ReadDouble();
            var peak = reader.ReadSingle();
            var count = reader.ReadInt32();
            if (count is <= 0 or > 8000) return null;

            var min = new float[count];
            var max = new float[count];
            for (var i = 0; i < count; i++) min[i] = reader.ReadSingle();
            for (var i = 0; i < count; i++) max[i] = reader.ReadSingle();

            return new WaveformData { Min = min, Max = max, DurationSeconds = duration, Peak = peak };
        }
        catch
        {
            try { File.Delete(path); } catch { /* ignore */ }
            return null;
        }
    }

    private static void TryWriteCache(string path, WaveformData data)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.WaveformCacheDir);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(FileMagic);
            writer.Write(data.DurationSeconds);
            writer.Write(data.Peak);
            writer.Write(data.Min.Length);
            foreach (var v in data.Min) writer.Write(v);
            foreach (var v in data.Max) writer.Write(v);
        }
        catch (Exception ex)
        {
            Log.Debug($"Waveform cache write failed: {ex.Message}");
        }
    }

    public static void ClearCache()
    {
        try
        {
            if (!Directory.Exists(AppPaths.WaveformCacheDir)) return;
            foreach (var f in Directory.EnumerateFiles(AppPaths.WaveformCacheDir, "*.pswf"))
                File.Delete(f);
        }
        catch (Exception ex) { Log.Debug($"Waveform cache clear failed: {ex.Message}"); }
    }
}
