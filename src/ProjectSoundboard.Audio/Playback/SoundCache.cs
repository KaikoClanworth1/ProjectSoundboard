using System.Collections.Concurrent;
using NAudio.Wave;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>A fully decoded sound held in memory, ready to play with zero disk latency.</summary>
public sealed class CachedSound
{
    public required string Path { get; init; }

    /// <summary>Interleaved 32-bit float samples at the engine mix format.</summary>
    public required float[] Data { get; init; }

    public required int SampleRate { get; init; }
    public required int Channels { get; init; }

    public long Frames => Data.LongLength / Channels;
    public double DurationSeconds => (double)Frames / SampleRate;
    public long Bytes => Data.LongLength * sizeof(float);

    /// <summary>Loudest sample in the file, used for normalisation.</summary>
    public float Peak { get; init; }

    internal long LastUsedTicks;
}

/// <summary>
/// Decodes sounds once and keeps them in RAM under a byte budget, evicting least recently
/// used entries. This is what makes a soundboard feel instant: triggering a cached sound
/// costs a memory read, not a file open plus decode.
/// </summary>
public sealed class SoundCache
{
    private readonly ConcurrentDictionary<string, CachedSound> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<CachedSound?>> _loading =
        new(StringComparer.OrdinalIgnoreCase);

    private long _bytes;

    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    /// <summary>Memory budget in bytes. Sounds beyond it are streamed from disk instead.</summary>
    public long BudgetBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Files longer than this are streamed rather than cached.</summary>
    public double MaxCacheableSeconds { get; set; } = 90;

    public long UsedBytes => Interlocked.Read(ref _bytes);
    public int Count => _cache.Count;

    public void Configure(int sampleRate, int channels)
    {
        if (SampleRate == sampleRate && Channels == channels) return;
        SampleRate = sampleRate;
        Channels = channels;
        Clear(); // Everything cached is in the old format.
    }

    public CachedSound? TryGet(string path)
    {
        if (!_cache.TryGetValue(path, out var sound)) return null;
        sound.LastUsedTicks = DateTime.UtcNow.Ticks;
        return sound;
    }

    /// <summary>
    /// Decode and cache <paramref name="path"/>. Returns null when the file is too long to
    /// cache or could not be decoded — the caller should stream it instead.
    /// Concurrent requests for the same path decode only once.
    /// </summary>
    public CachedSound? GetOrLoad(string path)
    {
        var existing = TryGet(path);
        if (existing is not null) return existing;

        var lazy = _loading.GetOrAdd(path, p => new Lazy<CachedSound?>(
            () => Decode(p), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var sound = lazy.Value;
            if (sound is null) return null;

            if (_cache.TryAdd(path, sound))
            {
                Interlocked.Add(ref _bytes, sound.Bytes);
                EvictIfNeeded();
            }

            sound.LastUsedTicks = DateTime.UtcNow.Ticks;
            return sound;
        }
        finally
        {
            _loading.TryRemove(path, out _);
        }
    }

    private CachedSound? Decode(string path)
    {
        try
        {
            using var source = AudioFileFactory.Open(path, SampleRate, Channels);

            if (source.Duration.TotalSeconds > MaxCacheableSeconds)
            {
                Log.Debug($"{Path.GetFileName(path)} is {source.Duration.TotalSeconds:F0}s — streaming instead of caching.");
                return null;
            }

            var estimatedSamples = (int)(source.Duration.TotalSeconds * SampleRate * Channels) + SampleRate;
            var buffer = new List<float>(Math.Max(1024, estimatedSamples));
            var chunk = new float[SampleRate * Channels / 2];

            var peak = 0f;
            int read;
            while ((read = source.Provider.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var abs = Math.Abs(chunk[i]);
                    if (abs > peak) peak = abs;
                }
                buffer.AddRange(chunk.AsSpan(0, read));
            }

            if (buffer.Count == 0) return null;

            return new CachedSound
            {
                Path = path,
                Data = buffer.ToArray(),
                SampleRate = SampleRate,
                Channels = Channels,
                Peak = peak
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not cache {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Warm the cache in the background (used for favourites and frequent sounds).</summary>
    public Task PreloadAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            foreach (var path in paths)
            {
                if (ct.IsCancellationRequested) return;
                if (UsedBytes >= BudgetBytes) return;
                if (!File.Exists(path)) continue;
                GetOrLoad(path);
            }
        }, ct);
    }

    private void EvictIfNeeded()
    {
        if (UsedBytes <= BudgetBytes) return;

        var victims = _cache.Values
            .OrderBy(s => s.LastUsedTicks)
            .ToList();

        foreach (var victim in victims)
        {
            if (UsedBytes <= BudgetBytes * 0.9) break;
            if (!_cache.TryRemove(victim.Path, out var removed)) continue;
            Interlocked.Add(ref _bytes, -removed.Bytes);
        }

        Log.Debug($"Sound cache trimmed to {UsedBytes / 1024 / 1024} MB ({_cache.Count} sounds).");
    }

    public void Remove(string path)
    {
        if (_cache.TryRemove(path, out var removed))
            Interlocked.Add(ref _bytes, -removed.Bytes);
    }

    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }

    public WaveFormat Format => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
}
