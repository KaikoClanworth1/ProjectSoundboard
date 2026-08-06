using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string Title { get; init; }
    public required string Notes { get; init; }
    public required string DownloadUrl { get; init; }
    public required string ReleasePageUrl { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads it, and swaps it in.
///
/// The user's data is never touched: everything personal lives in the Data folder, and the
/// swap explicitly excludes it. Updates are opt-in — the app asks, it never replaces itself
/// behind your back.
/// </summary>
public sealed class UpdateService
{
    // Change these if the repository moves. While the owner is left unset every check is a
    // silent no-op rather than an error the user has to look at.
    public const string RepositoryOwner = "KaikoClanworth1";
    public const string RepositoryName = "ProjectSoundboard";

    private const string UnconfiguredOwner = "REPLACE_WITH_GITHUB_USERNAME";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settings;

    public UpdateService(SettingsService settings) => _settings = settings;

    public static bool IsConfigured => RepositoryOwner != UnconfiguredOwner;

    public static string ReleasesPageUrl =>
        $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    public string? LastError { get; private set; }

    /// <summary>The version currently running.</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    // ---- checking ---------------------------------------------------------

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>
    /// Ask GitHub for the latest release. Returns null when we are up to date, the release
    /// was skipped, the repository is not configured yet, or the network is unavailable —
    /// none of which are worth interrupting the user for.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(bool ignoreSkipped = false, CancellationToken ct = default)
    {
        LastError = null;

        if (!IsConfigured)
        {
            Log.Debug("Update check skipped: no repository configured.");
            return null;
        }

        try
        {
            using var http = CreateClient(CheckTimeout);

            var url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
            var release = await http.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);

            _settings.Settings.General.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.MarkDirty();

            if (release is null || release.Draft || release.Prerelease) return null;
            if (!TryParseVersion(release.TagName, out var version)) return null;

            if (version <= CurrentVersion)
            {
                Log.Info($"Up to date ({CurrentVersion}); latest release is {version}.");
                return null;
            }

            if (!ignoreSkipped &&
                string.Equals(_settings.Settings.General.SkippedUpdateVersion, version.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Update {version} is available but was skipped by the user.");
                return null;
            }

            if (!ignoreSkipped && IsSnoozed(version))
            {
                Log.Info($"Update {version} is available but was deferred until " +
                         $"{_settings.Settings.General.SnoozedUntilUtc:HH:mm}.");
                return null;
            }

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

            if (asset?.DownloadUrl is null)
            {
                LastError = "The release has no downloadable .zip attached.";
                Log.Warn(LastError);
                return null;
            }

            Log.Info($"Update available: {version} (running {CurrentVersion}).");

            return new UpdateInfo
            {
                Version = version,
                TagName = release.TagName ?? version.ToString(),
                Title = string.IsNullOrWhiteSpace(release.Name) ? $"Version {version}" : release.Name!,
                Notes = release.Body ?? string.Empty,
                DownloadUrl = asset.DownloadUrl,
                ReleasePageUrl = release.HtmlUrl ?? ReleasesPageUrl,
                SizeBytes = asset.Size,
                PublishedAt = release.PublishedAt
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Being offline is normal and should never surface as an error dialog.
            LastError = ex.Message;
            Log.Debug($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// How close together two startup checks may be. This exists only to stop a restart
    /// loop hammering the API — a single check per launch is nothing against GitHub's
    /// sixty-an-hour allowance, and a longer gap just makes new releases look invisible.
    /// </summary>
    private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long "Later" holds off asking about that same version again.</summary>
    private static readonly TimeSpan SnoozeDuration = TimeSpan.FromHours(4);

    public bool ShouldCheckOnStartup()
    {
        if (!IsConfigured) return false;
        if (!_settings.Settings.General.CheckForUpdates) return false;

        var last = _settings.Settings.General.LastUpdateCheckUtc;
        return last is null || DateTime.UtcNow - last.Value >= MinimumCheckInterval;
    }

    /// <summary>True while the user has told us "Later" about this particular version.</summary>
    private bool IsSnoozed(Version version)
    {
        var general = _settings.Settings.General;

        if (general.SnoozedUntilUtc is not { } until || DateTime.UtcNow >= until) return false;

        return string.Equals(general.SnoozedUpdateVersion, version.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hold off on this version for a few hours after the user defers it.</summary>
    public void SnoozeVersion(Version version)
    {
        _settings.Settings.General.SnoozedUpdateVersion = version.ToString();
        _settings.Settings.General.SnoozedUntilUtc = DateTime.UtcNow + SnoozeDuration;
        _settings.Save();

        Log.Info($"Update {version} deferred for {SnoozeDuration.TotalHours:0} hours.");
    }

    // ---- downloading ------------------------------------------------------

    /// <summary>
    /// Download and unpack the release into the staging folder. Returns the folder holding
    /// the new files, or null on failure.
    /// </summary>
    public async Task<string?> DownloadAsync(
        UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            // Start from empty so a failed or superseded download cannot pile up.
            CleanUpStaging();
            Directory.CreateDirectory(AppPaths.UpdateStagingDir);

            var zipPath = Path.Combine(AppPaths.UpdateStagingDir, $"{update.TagName}.zip");
            var extractPath = Path.Combine(AppPaths.UpdateStagingDir, update.TagName);

            using (var http = CreateClient(DownloadTimeout))
            using (var response = await http
                       .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? update.SizeBytes;

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(zipPath);

                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    if (total > 0) progress?.Report((double)received / total);
                }
            }

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractPath);

            // Releases are often zipped with a single top level folder; step into it so the
            // copy lands files where the executable actually lives.
            var root = FindPayloadRoot(extractPath);

            if (!Directory.EnumerateFiles(root, "*.exe").Any())
            {
                LastError = "The downloaded update did not contain an executable.";
                Log.Warn(LastError);
                return null;
            }

            try { File.Delete(zipPath); } catch { /* leave it; harmless */ }

            Log.Info($"Update {update.Version} staged at {root}.");
            return root;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Update download failed", ex);
            return null;
        }
    }

    private static string FindPayloadRoot(string extractPath)
    {
        if (Directory.EnumerateFiles(extractPath).Any()) return extractPath;

        var directories = Directory.GetDirectories(extractPath);
        return directories.Length == 1 ? directories[0] : extractPath;
    }

    // ---- applying ---------------------------------------------------------

    /// <summary>True when the application folder can actually be written to.</summary>
    public static bool CanApplyInPlace()
    {
        try
        {
            var probe = Path.Combine(AppPaths.AppDirectory, ".update-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hand over to a small script that waits for this process to exit, copies the new files
    /// in, and relaunches. Windows will not let a running executable overwrite itself, so the
    /// swap has to happen from outside the process.
    /// </summary>
    public bool ApplyAndRestart(string stagedRoot)
    {
        try
        {
            var appDir = AppPaths.AppDirectory;
            var exePath = Environment.ProcessPath;

            if (exePath is null)
            {
                LastError = "Could not work out where the application is installed.";
                return false;
            }

            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"ProjectSoundboard-update-{Guid.NewGuid():N}.ps1");

            // /XD Data keeps settings, artwork, waveforms, backups and logs untouched.
            // Nothing is deleted: robocopy without /PURGE only adds and overwrites.
            // $$ raw string: interpolation uses {{ }}, so PowerShell's own braces stay literal.
            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                $target = {{Quote(appDir)}}
                $source = {{Quote(stagedRoot)}}
                $exe    = {{Quote(exePath)}}

                # Wait for Project Soundboard to close before touching its files.
                $deadline = (Get-Date).AddSeconds(30)
                while ((Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 200
                }
                Start-Sleep -Milliseconds 600

                robocopy $source $target /E /XD "{{AppPaths.PortableFolderName}}" /R:3 /W:1 /NFL /NDL /NJH /NJS /NP

                Start-Process -FilePath $exe
                Remove-Item -LiteralPath {{Quote(scriptPath)}} -Force
                """;

            File.WriteAllText(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Log.Info("Update handed off to the swap script; shutting down.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Could not start the update swap", ex);
            return false;
        }
    }

    /// <summary>
    /// Delete anything left in the staging folder.
    ///
    /// Applying an update unpacks the new build into Data\updates and then copies it over
    /// the application. Nothing ever removed that copy afterwards, so every update left
    /// another few megabytes of a previous version sitting there for good. By the time the
    /// updated app is running, the copy has already happened and the staged files are dead
    /// weight.
    /// </summary>
    public static void CleanUpStaging()
    {
        try
        {
            var staging = AppPaths.UpdateStagingDir;
            if (!Directory.Exists(staging)) return;

            var freed = 0L;

            foreach (var directory in Directory.EnumerateDirectories(staging))
            {
                freed += DirectorySize(directory);
                Directory.Delete(directory, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(staging))
            {
                freed += new FileInfo(file).Length;
                File.Delete(file);
            }

            if (freed > 0) Log.Info($"Cleared {freed / 1024 / 1024} MB of staged update files.");
        }
        catch (Exception ex)
        {
            // A locked file just means we try again next launch.
            Log.Debug($"Could not clear staged updates: {ex.Message}");
        }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public void SkipVersion(Version version)
    {
        _settings.Settings.General.SkippedUpdateVersion = version.ToString();
        _settings.Save();
        Log.Info($"Update {version} skipped.");
    }

    public static void OpenReleasesPage(string? url = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url ?? ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the releases page: {ex.Message}");
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ProjectSoundboard/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Accepts tags like "v1.2.3", "1.2.3", "release-1.2" and "v2".</summary>
    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        // Drop any prefix ("v", "release-") and any suffix ("-beta", "+build").
        var digits = new string(tag.SkipWhile(c => !char.IsDigit(c)).ToArray());
        var cut = digits.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) digits = digits[..cut];
        if (digits.Length == 0) return false;

        // Version needs at least major.minor, so a bare "2" has to be padded.
        if (!digits.Contains('.')) digits += ".0";

        if (!Version.TryParse(digits, out var parsed)) return false;

        version = parsed;
        return true;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
