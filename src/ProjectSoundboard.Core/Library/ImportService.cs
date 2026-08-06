using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

/// <summary>One file queued for import, with the folder layout it should keep.</summary>
public sealed class ImportItem
{
    public required string SourcePath { get; init; }

    /// <summary>Path relative to the dropped folder, used when preserving structure.</summary>
    public string RelativePath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }
}

public sealed class ImportPlan
{
    public List<ImportItem> Items { get; } = new();

    /// <summary>Files skipped because the extension is not supported.</summary>
    public int UnsupportedCount { get; set; }

    /// <summary>Files already indexed in the library at their current location.</summary>
    public List<string> AlreadyInLibrary { get; } = new();

    /// <summary>Items that look identical to something already in the library.</summary>
    public List<ImportItem> LikelyDuplicates { get; } = new();

    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public bool HasFolders { get; set; }
    public bool IsEmpty => Items.Count == 0;
}

public sealed class ImportConflict
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public long SourceSize { get; init; }
    public long DestinationSize { get; init; }
}

public sealed class ImportResult
{
    public List<SoundEntry> Added { get; } = new();
    public int Copied { get; set; }
    public int Indexed { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public int Renamed { get; set; }
    public List<string> Errors { get; } = new();
}

public sealed class ImportProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public string? CurrentFile { get; init; }
}

/// <summary>
/// Turns dropped files and folders into library entries, either by copying them into the
/// main sound library or by indexing them where they already live.
/// </summary>
public sealed class ImportService
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;

    public ImportService(SettingsService settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
    }

    /// <summary>Expand dropped paths (files and/or folders) into a concrete import plan.</summary>
    public ImportPlan BuildPlan(IEnumerable<string> droppedPaths)
    {
        var plan = new ImportPlan();
        var extensions = new HashSet<string>(
            _settings.Settings.Library.Extensions, StringComparer.OrdinalIgnoreCase);

        foreach (var path in droppedPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    plan.HasFolders = true;
                    var root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var folderName = Path.GetFileName(root);

                    foreach (var file in LibraryService.EnumerateAudioFiles(root, true, extensions))
                    {
                        var relDir = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root);
                        var rel = relDir == "." ? folderName : Path.Combine(folderName, relDir);
                        AddItem(plan, file, rel);
                    }
                }
                else if (File.Exists(path))
                {
                    if (!extensions.Contains(Path.GetExtension(path)))
                    {
                        plan.UnsupportedCount++;
                        continue;
                    }
                    AddItem(plan, path, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not inspect dropped path {path}: {ex.Message}");
            }
        }

        if (_settings.Settings.Library.DetectDuplicatesOnImport)
            DetectDuplicates(plan);

        return plan;
    }

    private void AddItem(ImportPlan plan, string file, string relativeDir)
    {
        var info = new FileInfo(file);

        if (_library.GetByPath(file) is not null)
        {
            plan.AlreadyInLibrary.Add(file);
            return;
        }

        plan.Items.Add(new ImportItem
        {
            SourcePath = file,
            RelativePath = relativeDir,
            SizeBytes = info.Exists ? info.Length : 0
        });
    }

    private void DetectDuplicates(ImportPlan plan)
    {
        var existing = _library.Sounds
            .Where(s => s.FileSizeBytes > 0)
            .GroupBy(s => s.FileSizeBytes)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in plan.Items)
        {
            if (!existing.TryGetValue(item.SizeBytes, out var candidates)) continue;

            var name = Path.GetFileNameWithoutExtension(item.SourcePath);
            if (candidates.Any(c => string.Equals(c.OriginalNameWithoutExtension, name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                plan.LikelyDuplicates.Add(item);
            }
        }
    }

    /// <summary>
    /// Run the import. <paramref name="resolveConflict"/> is invoked only when a destination
    /// file already exists and the configured action is <see cref="ConflictAction.Ask"/>;
    /// returning an action with applyToAll suppresses further prompts.
    /// </summary>
    public async Task<ImportResult> ExecuteAsync(
        ImportPlan plan,
        ImportBehavior behavior,
        bool preserveStructure,
        Func<ImportConflict, Task<(ConflictAction Action, bool ApplyToAll)>>? resolveConflict = null,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ImportResult();

        if (behavior == ImportBehavior.IndexInPlace)
        {
            var added = _library.AddFiles(plan.Items.Select(i => i.SourcePath));
            result.Added.AddRange(added);
            result.Indexed = added.Count;
            Log.Info($"Imported {added.Count} sound(s) in place.");
            return result;
        }

        var libraryRoot = EnsureMainLibraryPath();
        var copied = new List<string>();
        var standing = _settings.Settings.Library.ConflictAction;
        var done = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new ImportProgress
            {
                Completed = done,
                Total = plan.Items.Count,
                CurrentFile = Path.GetFileName(item.SourcePath)
            });

            try
            {
                var destDir = libraryRoot;
                if (preserveStructure && !string.IsNullOrEmpty(item.RelativePath))
                    destDir = Path.Combine(libraryRoot, SanitizeRelative(item.RelativePath));

                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, Path.GetFileName(item.SourcePath));

                if (File.Exists(dest))
                {
                    var action = standing;

                    if (action == ConflictAction.Ask)
                    {
                        if (resolveConflict is null)
                        {
                            action = ConflictAction.KeepBoth;
                        }
                        else
                        {
                            var conflict = new ImportConflict
                            {
                                SourcePath = item.SourcePath,
                                DestinationPath = dest,
                                SourceSize = item.SizeBytes,
                                DestinationSize = new FileInfo(dest).Length
                            };

                            var (chosen, applyToAll) = await resolveConflict(conflict).ConfigureAwait(false);
                            action = chosen;
                            if (applyToAll) standing = chosen;
                        }
                    }

                    switch (action)
                    {
                        case ConflictAction.Skip:
                            result.Skipped++;
                            done++;
                            continue;

                        case ConflictAction.KeepBoth:
                            dest = MakeUniquePath(dest);
                            result.Renamed++;
                            break;

                        case ConflictAction.Replace:
                            result.Replaced++;
                            break;
                    }
                }

                // Same file dropped onto itself — nothing to copy.
                if (!string.Equals(Path.GetFullPath(item.SourcePath), Path.GetFullPath(dest),
                        StringComparison.OrdinalIgnoreCase))
                {
                    await CopyAsync(item.SourcePath, dest, ct).ConfigureAwait(false);
                }

                copied.Add(dest);
                result.Copied++;
            }
            catch (Exception ex)
            {
                var message = $"{Path.GetFileName(item.SourcePath)}: {ex.Message}";
                result.Errors.Add(message);
                Log.Warn($"Import failed for {message}");
            }

            done++;
        }

        progress?.Report(new ImportProgress
        {
            Completed = done,
            Total = plan.Items.Count
        });

        var entries = _library.AddFiles(copied);
        result.Added.AddRange(entries);

        Log.Info($"Import complete: {result.Copied} copied, {result.Skipped} skipped, " +
                 $"{result.Replaced} replaced, {result.Renamed} renamed, {result.Errors.Count} errors.");

        return result;
    }

    /// <summary>
    /// Resolve (and create) the main sound library folder, making sure it is also a
    /// watched library folder so copied files show up immediately.
    /// </summary>
    public string EnsureMainLibraryPath()
    {
        var lib = _settings.Settings.Library;

        if (string.IsNullOrWhiteSpace(lib.MainLibraryPath))
            lib.MainLibraryPath = AppPaths.DefaultMainLibrary;

        Directory.CreateDirectory(lib.MainLibraryPath!);

        var registered = lib.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, lib.MainLibraryPath, StringComparison.OrdinalIgnoreCase));

        if (registered is null)
        {
            lib.Folders.Add(new LibraryFolder
            {
                Path = lib.MainLibraryPath!,
                Recursive = true,
                Watch = true,
                IsMainLibrary = true
            });
            _settings.Save();
        }
        else if (!registered.IsMainLibrary)
        {
            foreach (var f in lib.Folders) f.IsMainLibrary = false;
            registered.IsMainLibrary = true;
            _settings.Save();
        }

        return lib.MainLibraryPath!;
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        const int bufferSize = 1 << 20; // 1 MB — large files copy in a handful of passes.
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        await src.CopyToAsync(dst, bufferSize, ct).ConfigureAwait(false);
    }

    /// <summary>Append " (2)", " (3)", … until the path is free.</summary>
    public static string MakeUniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static string SanitizeRelative(string relative)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p is not ("." or ".." or ""))
            .Select(p => new string(p.Select(c => invalid.Contains(c) ? '_' : c).ToArray()));
        return Path.Combine(parts.ToArray());
    }
}
