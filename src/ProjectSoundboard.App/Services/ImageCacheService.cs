using System.IO;
using System.Windows.Media.Imaging;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Decodes thumbnails once, at tile resolution, and hands out frozen bitmaps that can be
/// shared across every view. Decoding at the display size (rather than full resolution)
/// is what keeps a library of thousands of custom images from eating gigabytes.
/// </summary>
public sealed class ImageCacheService
{
    private sealed class Entry
    {
        public required BitmapSource Image { get; init; }
        public required long Bytes { get; init; }
        public required long Stamp { get; init; }
        public DateTime LastUsed { get; set; }
    }

    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _bytes;

    /// <summary>Memory budget in bytes; least recently used images are evicted past it.</summary>
    public long BudgetBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>Longest edge the cached bitmap is decoded to.</summary>
    public int DecodeSize { get; set; } = 256;

    public long UsedBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _cache.Count; } }

    /// <summary>
    /// Get a thumbnail, decoding it if necessary. Returns null when the file is missing
    /// or is not a readable image.
    /// </summary>
    public BitmapSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        long stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch { return null; }

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var hit) && hit.Stamp == stamp)
            {
                hit.LastUsed = DateTime.UtcNow;
                return hit.Image;
            }
        }

        var decoded = Decode(path);
        if (decoded is null) return null;

        var bytes = (long)decoded.PixelWidth * decoded.PixelHeight * 4;

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var stale)) _bytes -= stale.Bytes;

            _cache[path] = new Entry
            {
                Image = decoded,
                Bytes = bytes,
                Stamp = stamp,
                LastUsed = DateTime.UtcNow
            };

            _bytes += bytes;
            EvictIfNeeded();
        }

        return decoded;
    }

    private BitmapSource? Decode(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // OnLoad plus a stream copy means the file is not left locked, so the user can
            // still move or delete the original.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = DecodeSize;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not decode image {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void EvictIfNeeded()
    {
        if (_bytes <= BudgetBytes) return;

        foreach (var key in _cache.OrderBy(kv => kv.Value.LastUsed).Select(kv => kv.Key).ToList())
        {
            if (_bytes <= BudgetBytes * 0.85) break;
            if (!_cache.Remove(key, out var removed)) continue;
            _bytes -= removed.Bytes;
        }
    }

    public void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            if (_cache.Remove(path, out var removed)) _bytes -= removed.Bytes;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _bytes = 0;
        }
    }
}
