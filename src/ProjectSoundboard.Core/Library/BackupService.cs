using System.IO.Compression;
using System.Text.Json;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

/// <summary>A single portable file containing everything except the audio itself.</summary>
public sealed class BackupBundle
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Product { get; set; } = AppPaths.ProductName;
    public AppSettings? Settings { get; set; }
    public LibraryData? Library { get; set; }
}

public sealed class BackupService
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;

    public BackupService(SettingsService settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
    }

    // ---- full bundle ------------------------------------------------------

    /// <summary>
    /// Write a .psbackup archive: metadata JSON plus every custom thumbnail, so the
    /// backup restores identically on another machine.
    /// </summary>
    public void ExportBundle(string destinationPath, bool includeImages = true)
    {
        var bundle = new BackupBundle
        {
            Settings = _settings.Settings,
            Library = _library.Snapshot()
        };

        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        var entry = archive.CreateEntry("bundle.json", CompressionLevel.Optimal);
        using (var stream = entry.Open())
        {
            JsonSerializer.Serialize(stream, bundle, JsonStore.Options);
        }

        if (includeImages && Directory.Exists(AppPaths.ImageCacheDir))
        {
            foreach (var image in Directory.EnumerateFiles(AppPaths.ImageCacheDir))
            {
                try
                {
                    archive.CreateEntryFromFile(image, "images/" + Path.GetFileName(image),
                        CompressionLevel.Fastest);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not add {image} to backup: {ex.Message}");
                }
            }
        }

        Log.Info($"Backup written to {destinationPath}.");
    }

    /// <summary>Restore a .psbackup archive. The current data is snapshotted first.</summary>
    public void ImportBundle(string sourcePath, bool restoreSettings = true, bool restoreLibrary = true)
    {
        CreateAutomaticBackup("before-restore");

        using var archive = ZipFile.OpenRead(sourcePath);

        var manifest = archive.GetEntry("bundle.json")
            ?? throw new InvalidDataException("This file is not a Project Soundboard backup.");

        BackupBundle? bundle;
        using (var stream = manifest.Open())
        {
            bundle = JsonSerializer.Deserialize<BackupBundle>(stream, JsonStore.Options);
        }

        if (bundle is null) throw new InvalidDataException("The backup could not be read.");

        Directory.CreateDirectory(AppPaths.ImageCacheDir);
        foreach (var e in archive.Entries.Where(e => e.FullName.StartsWith("images/", StringComparison.Ordinal)))
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            try { e.ExtractToFile(Path.Combine(AppPaths.ImageCacheDir, e.Name), overwrite: true); }
            catch (Exception ex) { Log.Warn($"Could not restore image {e.Name}: {ex.Message}"); }
        }

        if (restoreSettings && bundle.Settings is not null)
        {
            JsonStore.Save(AppPaths.SettingsFile, bundle.Settings);
            _settings.Load();
        }

        if (restoreLibrary && bundle.Library is not null)
        {
            _library.ReplaceAll(bundle.Library);
        }

        Log.Info($"Backup restored from {sourcePath}.");
    }

    /// <summary>Timestamped copy kept in the app data folder; the 20 newest are retained.</summary>
    public string CreateAutomaticBackup(string reason)
    {
        Directory.CreateDirectory(AppPaths.BackupDir);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.psbackup";
        var path = Path.Combine(AppPaths.BackupDir, name);

        try
        {
            ExportBundle(path, includeImages: false);
            PruneBackups();
        }
        catch (Exception ex)
        {
            Log.Warn($"Automatic backup failed: {ex.Message}");
        }

        return path;
    }

    private static void PruneBackups()
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.BackupDir)
                .GetFiles("*.psbackup")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(20);

            foreach (var f in files) f.Delete();
        }
        catch { /* best effort */ }
    }

    public IReadOnlyList<FileInfo> ListBackups()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BackupDir);
            return new DirectoryInfo(AppPaths.BackupDir)
                .GetFiles("*.psbackup")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToArray();
        }
        catch { return Array.Empty<FileInfo>(); }
    }

    // ---- targeted exports -------------------------------------------------

    public void ExportSettings(string path) => JsonStore.Save(path, _settings.Settings);

    public void ImportSettings(string path)
    {
        var loaded = JsonStore.Load<AppSettings>(path, () => throw new InvalidDataException("Not a settings file."));
        JsonStore.Save(AppPaths.SettingsFile, loaded);
        _settings.Load();
    }

    public void ExportGroups(string path) => JsonStore.Save(path, _library.Groups.ToList());

    /// <summary>
    /// Export display names as CSV keyed by file path, so they can be inspected or
    /// edited in a spreadsheet and brought back later.
    /// </summary>
    public void ExportDisplayNames(string path)
    {
        var lines = new List<string> { "FilePath,DisplayName" };
        lines.AddRange(_library.Sounds
            .Where(s => s.HasCustomName)
            .Select(s => $"{Csv(s.FilePath)},{Csv(s.DisplayName)}"));

        File.WriteAllLines(path, lines);
    }

    public int ImportDisplayNames(string path)
    {
        var applied = 0;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = SplitCsv(line);
            if (parts.Count < 2) continue;

            var entry = _library.GetByPath(parts[0]);
            if (entry is null) continue;

            entry.CustomName = parts[1];
            applied++;
        }

        if (applied > 0) _library.NotifyChanged();
        return applied;
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }
}
