using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LibrarySort;

/// <summary>What a music file says about itself.</summary>
internal sealed record Tags(
    string? AlbumArtist, string? Artist, string? Album, string? Title,
    int Track, int Disc, int DiscCount, string? Date);

/// <summary>
/// Reads tags with ffprobe.
///
/// The tags are worth far more than the folder names here: a folder called "Nevermind" turns
/// out to be guitar lessons, and a folder called "KPop Demon Hunters (2025)" is a soundtrack
/// whose every track has a different artist. Guessing from paths would file both wrongly.
/// </summary>
internal static class Tags_
{
    public static string? FfProbe { get; set; }

    public static Tags? Read(string path)
    {
        if (FfProbe is null) return null;

        var info = new ProcessStartInfo
        {
            FileName = FfProbe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var argument in new[]
                 { "-v", "quiet", "-print_format", "json", "-show_format", path })
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            if (json.Length == 0) return null;

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("format", out var format) ||
                !format.TryGetProperty("tags", out var tags))
            {
                return null;
            }

            var (track, _) = Pair(Text(tags, "track"));
            var (disc, discs) = Pair(Text(tags, "disc") ?? Text(tags, "discnumber"));

            return new Tags(
                Text(tags, "album_artist") ?? Text(tags, "ALBUM_ARTIST") ?? Text(tags, "albumartist"),
                Text(tags, "artist") ?? Text(tags, "ARTIST"),
                Text(tags, "album") ?? Text(tags, "ALBUM"),
                Text(tags, "title") ?? Text(tags, "TITLE"),
                track, disc, discs,
                Text(tags, "originalyear") ?? Text(tags, "originaldate") ?? Text(tags, "date") ?? Text(tags, "year"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Tags come as "4" or "4/18"; both halves are useful.</summary>
    private static (int Number, int Of) Pair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0);

        var parts = value.Split('/', StringSplitOptions.TrimEntries);

        _ = int.TryParse(parts[0], out var number);
        var of = parts.Length > 1 && int.TryParse(parts[1], out var total) ? total : 0;

        return (number, of);
    }

    /// <summary>Tag names differ in case between formats, so both are tried.</summary>
    private static string? Text(JsonElement tags, string name)
    {
        foreach (var property in tags.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.String) continue;

            var value = property.Value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
