using System.IO;
using System.Net.Http;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Finds, and if necessary fetches, the tool that talks to YouTube.
///
/// yt-dlp rather than anything else for the reason it is always chosen: YouTube changes its
/// player regularly and yt-dlp tracks those changes within days, where every alternative
/// spends weeks broken. It is a single self-contained executable, which suits an application
/// that has to stay portable — it lives in the Data folder next to everything else, so
/// copying the app to another machine brings it along.
///
/// It is deliberately not shipped inside the release. It goes out of date quickly, and a
/// bundled copy would be stale the week after each release; fetching it means it is current
/// the first time somebody uses the feature, and updatable without a new build.
/// </summary>
public sealed class YtDlpTool
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    /// <summary>The official Windows build. Named exactly this in every release.</summary>
    private const string AssetName = "yt-dlp.exe";

    public const string ProjectPageUrl = "https://github.com/yt-dlp/yt-dlp";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Where a fetched copy lives: with the rest of the portable data.</summary>
    public static string ManagedPath => Path.Combine(AppPaths.DataRoot, "tools", AssetName);

    public string? LastError { get; private set; }

    /// <summary>
    /// Where yt-dlp is, or null. A copy the user already has is preferred over fetching
    /// another: somebody who keeps it on PATH is keeping it updated themselves.
    /// </summary>
    public static string? Locate()
    {
        if (File.Exists(ManagedPath)) return ManagedPath;

        // Beside the application, for anyone who drops it in themselves.
        var local = Path.Combine(AppPaths.AppDirectory, AssetName);
        if (File.Exists(local)) return local;

        return OnPath();
    }

    private static string? OnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), AssetName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry is not worth failing over */ }
        }

        return null;
    }

    /// <summary>Download the current release into the Data folder. Returns its path, or null.</summary>
    public async Task<string?> FetchAsync(IProgress<string>? status, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            status?.Report("Looking up the latest yt-dlp…");

            using var http = new HttpClient { Timeout = Timeout };

            // GitHub refuses anonymous API calls without one.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectSoundboard");

            var json = await http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            var url = FindAssetUrl(json);

            if (url is null)
            {
                LastError = "The latest yt-dlp release does not list a Windows build.";
                return null;
            }

            status?.Report("Downloading yt-dlp…");

            var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

            // An executable that small is an error page, not a program.
            if (bytes.Length < 1_000_000)
            {
                LastError = "The download was too small to be yt-dlp.";
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ManagedPath)!);

            // Written beside the target and moved into place, so a failure part way through
            // cannot leave a half-written executable that looks installed.
            var temporary = ManagedPath + ".part";
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            File.Move(temporary, ManagedPath, overwrite: true);

            Log.Info($"yt-dlp downloaded to {ManagedPath} ({bytes.Length / 1024 / 1024} MB).");
            return ManagedPath;
        }
        catch (OperationCanceledException)
        {
            LastError = "Cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"Could not fetch yt-dlp: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pull the Windows asset's URL out of the release JSON without deserialising the lot —
    /// a GitHub release document is large and almost all of it is of no interest.
    /// </summary>
    private static string? FindAssetUrl(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)) continue;
            if (!string.Equals(name.GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            return asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
        }

        return null;
    }
}
