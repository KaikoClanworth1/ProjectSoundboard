using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

/// <summary>
/// Owns the thumbnail cache. Images the user picks are copied in, so moving or deleting
/// the original never leaves a broken tile.
/// </summary>
public static class ImageStore
{
    public static readonly string[] SupportedExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static bool IsSupportedImage(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Copy an image into the cache and return the cached path.</summary>
    public static string? Store(SoundEntry entry, string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;

            Directory.CreateDirectory(AppPaths.ImageCacheDir);
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext)) ext = ".png";

            var dest = Path.Combine(AppPaths.ImageCacheDir, $"{entry.Id}{ext}");

            Remove(entry);
            File.Copy(sourcePath, dest, overwrite: true);
            entry.ImagePath = dest;
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not store image for {entry.DisplayName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Write raw image bytes (used for clipboard paste) into the cache.</summary>
    public static string? Store(SoundEntry entry, byte[] data, string extension = ".png")
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ImageCacheDir);
            var dest = Path.Combine(AppPaths.ImageCacheDir, $"{entry.Id}{extension}");

            Remove(entry);
            File.WriteAllBytes(dest, data);
            entry.ImagePath = dest;
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not store pasted image for {entry.DisplayName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Delete the cached thumbnail and clear the reference.</summary>
    public static void Remove(SoundEntry entry)
    {
        try
        {
            foreach (var ext in SupportedExtensions)
            {
                var candidate = Path.Combine(AppPaths.ImageCacheDir, $"{entry.Id}{ext}");
                if (File.Exists(candidate)) File.Delete(candidate);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not delete cached image: {ex.Message}");
        }

        entry.ImagePath = null;
    }

    /// <summary>Remove cached images that no longer belong to any sound.</summary>
    public static int PruneOrphans(IEnumerable<SoundEntry> sounds)
    {
        var live = new HashSet<string>(sounds.Select(s => s.Id), StringComparer.Ordinal);
        var removed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.ImageCacheDir))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (live.Contains(id)) continue;
                File.Delete(file);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Image prune failed: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// Deterministic accent colour for the generated letter tile, so a sound always
    /// looks the same between sessions. Returns #RRGGBB.
    /// </summary>
    public static string AutoColor(string seed)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in seed)
            {
                hash ^= char.ToLowerInvariant(c);
                hash *= 16777619;
            }

            // Fixed saturation/lightness keeps every generated tile legible against
            // both the dark and light backgrounds.
            var hue = hash % 360;
            return HslToHex(hue, 0.55, 0.55);
        }
    }

    public static string InitialFor(string displayName)
    {
        foreach (var c in displayName)
        {
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        }
        return "?";
    }

    private static string HslToHex(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return $"#{To255(r + m):X2}{To255(g + m):X2}{To255(b + m):X2}";
    }

    private static int To255(double v) => (int)Math.Clamp(Math.Round(v * 255), 0, 255);
}
