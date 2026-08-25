using System.Collections.Concurrent;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

public sealed class ScanProgress
{
    public int Discovered { get; init; }
    public int Processed { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public string? CurrentFolder { get; init; }
    public bool IsComplete { get; init; }
}

/// <summary>
/// Owns the library: folder scanning, live folder watching, and all metadata mutations.
/// All public members are safe to call from any thread; change notifications are raised
/// on a background thread and marshalled by the UI layer.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Lock _gate = new();

    private LibraryData _data = new();

    /// <summary>Fast path lookup, keyed by normalised absolute path.</summary>
    private readonly Dictionary<string, SoundEntry> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SoundEntry> _byId = new(StringComparer.Ordinal);

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new();
    private System.Threading.Timer? _debounceTimer;
    private bool _dirty;
    private int _scanning;

    public LibraryService(SettingsService settings) => _settings = settings;

    /// <summary>Raised whenever the set of sounds or groups changed in a way the UI must reflect.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised repeatedly during a scan.</summary>
    public event EventHandler<ScanProgress>? ScanProgressChanged;

    public bool IsScanning => Volatile.Read(ref _scanning) != 0;

    // -----------------------------------------------------------------------
    // Snapshots
    // -----------------------------------------------------------------------

    public IReadOnlyList<SoundEntry> Sounds
    {
        get { lock (_gate) return _data.Sounds.ToArray(); }
    }

    public IReadOnlyList<SoundGroup> Groups
    {
        get { lock (_gate) return _data.Groups.ToArray(); }
    }

    public int Count
    {
        get { lock (_gate) return _data.Sounds.Count; }
    }

    public SoundEntry? GetById(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var e) ? e : null;
    }

    public SoundEntry? GetByPath(string path)
    {
        lock (_gate) return _byPath.TryGetValue(Normalize(path), out var e) ? e : null;
    }

    public IReadOnlyList<SoundEntry> GetHistory(int limit)
    {
        lock (_gate)
        {
            return _data.History
                .Take(limit)
                .Select(id => _byId.TryGetValue(id, out var e) ? e : null)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToArray();
        }
    }

    // -----------------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------------

    public void Load()
    {
        AppPaths.EnsureCreated();
        var data = JsonStore.Load(AppPaths.LibraryFile, () => new LibraryData());

        lock (_gate)
        {
            _data = data;
            RebuildIndexes();
        }

        Log.Info($"Library loaded: {data.Sounds.Count} sounds, {data.Groups.Count} groups.");
    }

    public void Save()
    {
        LibraryData snapshot;
        lock (_gate)
        {
            _dirty = false;
            snapshot = new LibraryData
            {
                SchemaVersion = _data.SchemaVersion,
                Sounds = _data.Sounds.ToList(),
                Groups = _data.Groups.ToList(),
                History = _data.History.ToList(),
                LastScanUtc = _data.LastScanUtc
            };
        }

        JsonStore.Save(AppPaths.LibraryFile, snapshot);
    }

    public void SaveIfDirty()
    {
        bool dirty;
        lock (_gate) dirty = _dirty;
        if (dirty) Save();
    }

    private void MarkDirty()
    {
        lock (_gate) _dirty = true;
    }

    private void RebuildIndexes()
    {
        _byPath.Clear();
        _byId.Clear();
        foreach (var s in _data.Sounds)
        {
            _byPath[Normalize(s.FilePath)] = s;
            _byId[s.Id] = s;
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    // -----------------------------------------------------------------------
    // Scanning
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walk every enabled library folder and reconcile the index with what is on disk.
    /// Safe to call repeatedly; concurrent calls are collapsed into one.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _scanning, 1) != 0) return;

        try
        {
            var folders = _settings.Settings.Library.Folders
                .Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path))
                .ToList();

            var extensions = new HashSet<string>(
                _settings.Settings.Library.Extensions.Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            var found = new ConcurrentDictionary<string, LibraryFolder>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                var options = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, _settings.Settings.Performance.ScanThreads)
                };

                Parallel.ForEach(folders, options, folder =>
                {
                    if (!Directory.Exists(folder.Path))
                    {
                        Log.Warn($"Library folder missing: {folder.Path}");
                        return;
                    }

                    Report(new ScanProgress { CurrentFolder = folder.Path, Discovered = found.Count });

                    foreach (var file in EnumerateAudioFiles(folder.Path, folder.Recursive, extensions, ct))
                    {
                        found[Normalize(file)] = folder;
                    }
                });
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Groups have to exist before entries can point at them, and creating them from
            // the parallel loop below would race, so resolve them all up front.
            var groupByPath = BuildSubfolderGroups(found);

            // --- work out what changed -------------------------------------
            List<string> newPaths;
            List<SoundEntry> gone;
            List<SoundEntry> stale;

            lock (_gate)
            {
                newPaths = found.Keys.Where(p => !_byPath.ContainsKey(p)).ToList();

                // A sound disappears only if it lived under a folder we just scanned.
                var roots = folders.Select(f => Normalize(f.Path)).ToList();
                gone = _data.Sounds
                    .Where(s => !found.ContainsKey(Normalize(s.FilePath))
                                && roots.Any(r => IsUnder(s.FilePath, r)))
                    .ToList();

                stale = _data.Sounds
                    .Where(s => found.ContainsKey(Normalize(s.FilePath))
                                && AudioFileMetadataReader.NeedsRefresh(s))
                    .ToList();
            }

            // --- create entries for new files (metadata read in parallel) ---
            var created = new ConcurrentBag<SoundEntry>();
            var processed = 0;

            await Task.Run(() =>
            {
                var options = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, _settings.Settings.Performance.ScanThreads)
                };

                Parallel.ForEach(newPaths, options, path =>
                {
                    var folder = found[path];
                    var entry = new SoundEntry
                    {
                        FilePath = path,
                        GroupId = groupByPath.TryGetValue(path, out var derived)
                            ? derived
                            : folder.DefaultGroupId
                    };

                    AudioFileMetadataReader.Populate(entry);

                    if (_settings.Settings.Library.AutoTagFromFolderName)
                    {
                        var tag = DeriveFolderTag(path, folder.Path);
                        if (tag is not null) entry.Tags.Add(tag);
                    }

                    created.Add(entry);

                    var n = Interlocked.Increment(ref processed);
                    if (n % 64 == 0)
                    {
                        Report(new ScanProgress
                        {
                            Discovered = newPaths.Count,
                            Processed = n,
                            Added = n
                        });
                    }
                });

                Parallel.ForEach(stale, options, entry => AudioFileMetadataReader.Populate(entry));
            }, ct).ConfigureAwait(false);

            // --- commit -----------------------------------------------------
            lock (_gate)
            {
                foreach (var entry in created)
                {
                    var key = Normalize(entry.FilePath);
                    if (_byPath.ContainsKey(key)) continue;
                    _data.Sounds.Add(entry);
                    _byPath[key] = entry;
                    _byId[entry.Id] = entry;
                }

                // Sounds that were already known but have no group yet: file them under the
                // subfolder they live in. Anything the user placed by hand is left alone.
                foreach (var (path, groupId) in groupByPath)
                {
                    if (!_byPath.TryGetValue(path, out var existing)) continue;
                    if (existing.GroupId is not null) continue;

                    existing.GroupId = groupId;
                    _dirty = true;
                }

                foreach (var entry in gone)
                {
                    // Keep customised entries around and flag them so the user can repair
                    // or remove them deliberately; drop untouched ones silently.
                    if (HasUserData(entry))
                    {
                        entry.IsMissing = true;
                    }
                    else
                    {
                        _data.Sounds.Remove(entry);
                        _byPath.Remove(Normalize(entry.FilePath));
                        _byId.Remove(entry.Id);
                    }
                }

                _data.LastScanUtc = DateTime.UtcNow;
                _dirty = true;
            }

            Save();

            Report(new ScanProgress
            {
                Discovered = found.Count,
                Processed = newPaths.Count,
                Added = created.Count,
                Removed = gone.Count,
                IsComplete = true
            });

            Log.Info($"Scan complete: {found.Count} files on disk, {created.Count} added, {gone.Count} removed/missing.");
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Scan cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    private void Report(ScanProgress progress) => ScanProgressChanged?.Invoke(this, progress);

    /// <summary>
    /// Work out which group each discovered file belongs in, creating the groups as needed,
    /// for folders that have "make groups from subfolders" turned on.
    ///
    /// Only the first level counts: <c>Root/OST/Series/track.mp3</c> lands in "OST", not in a
    /// nested "Series". Files sitting directly in the root are left ungrouped.
    /// </summary>
    private Dictionary<string, string> BuildSubfolderGroups(
        IReadOnlyDictionary<string, LibraryFolder> found)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!found.Values.Any(f => f.GroupFromSubfolders)) return result;

        lock (_gate)
        {
            var byName = _data.Groups
                .Where(g => g.ParentId is null)
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, folder) in found)
            {
                if (!folder.GroupFromSubfolders) continue;

                var name = FirstLevelFolderName(path, folder.Path);
                if (name is null) continue;

                if (!byName.TryGetValue(name, out var id))
                {
                    var group = new SoundGroup { Name = name, SortOrder = _data.Groups.Count };
                    _data.Groups.Add(group);
                    byName[name] = id = group.Id;
                    _dirty = true;
                }

                result[path] = id;
            }
        }

        return result;
    }

    /// <summary>The first folder below <paramref name="rootPath"/>, or null if the file sits in it.</summary>
    private static string? FirstLevelFolderName(string filePath, string rootPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null) return null;

            var relative = Path.GetRelativePath(rootPath, dir);
            if (relative is "." or "") return null;
            if (relative.StartsWith("..", StringComparison.Ordinal)) return null;

            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .FirstOrDefault(p => p.Length > 0);

            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drop every sound that lives under <paramref name="folderPath"/> and is not also
    /// covered by one of the folders still in the library.
    ///
    /// The scan cannot do this on its own: it only considers a sound missing if it sits
    /// under a folder that is still being watched, which is deliberate so a disconnected
    /// drive does not wipe entries — but it also meant removing a folder left its sounds
    /// behind forever.
    /// </summary>
    public int RemoveSoundsUnder(string folderPath, IEnumerable<string> remainingFolders)
    {
        var root = Normalize(folderPath);
        var keep = remainingFolders.Select(Normalize).ToList();

        List<SoundEntry> doomed;

        lock (_gate)
        {
            doomed = _data.Sounds
                .Where(s => IsUnder(s.FilePath, root) || Normalize(s.FilePath).Equals(root, StringComparison.OrdinalIgnoreCase))
                .Where(s => !keep.Any(k => IsUnder(s.FilePath, k)))
                .ToList();
        }

        if (doomed.Count == 0) return 0;

        RemoveFromLibrary(doomed);
        Log.Info($"Removed {doomed.Count} sound(s) that lived under {folderPath}.");
        return doomed.Count;
    }

    private static bool HasUserData(SoundEntry e) =>
        e.HasCustomName || e.IsFavorite || e.PlayCount > 0
        || e.Tags.Count > 0 || e.ImagePath is not null || e.GroupId is not null;

    private static string? DeriveFolderTag(string filePath, string rootPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null) return null;
            if (string.Equals(Normalize(dir), Normalize(rootPath), StringComparison.OrdinalIgnoreCase))
                return null;
            return new DirectoryInfo(dir).Name;
        }
        catch { return null; }
    }

    private static bool IsUnder(string path, string root)
    {
        var p = Normalize(path);
        return p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recursive enumeration that survives permission errors and reparse-point loops,
    /// which the built-in EnumerateFiles recursion does not.
    /// </summary>
    public static IEnumerable<string> EnumerateAudioFiles(
        string root, bool recursive, HashSet<string> extensions, CancellationToken ct = default)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex)
            {
                Log.Debug($"Skipping {dir}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                if (extensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }

            if (!recursive) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (info.Name.StartsWith('.')) continue;
                }
                catch { continue; }

                stack.Push(sub);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Folder watching
    // -----------------------------------------------------------------------

    public void StartWatching()
    {
        StopWatching();
        if (!_settings.Settings.Library.WatchFolders) return;

        var extensions = new HashSet<string>(
            _settings.Settings.Library.Extensions, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _settings.Settings.Library.Folders.Where(f => f.Enabled && f.Watch))
        {
            if (!Directory.Exists(folder.Path)) continue;

            try
            {
                var w = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = folder.Recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024
                };

                void Queue(string path)
                {
                    if (!extensions.Contains(Path.GetExtension(path))) return;
                    _pendingPaths[path] = 0;
                    ScheduleDebounce();
                }

                w.Created += (_, e) => Queue(e.FullPath);
                w.Deleted += (_, e) => Queue(e.FullPath);
                w.Changed += (_, e) => Queue(e.FullPath);
                w.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                w.Error += (_, e) => Log.Warn($"Watcher error on {folder.Path}: {e.GetException().Message}");

                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not watch {folder.Path}: {ex.Message}");
            }
        }

        Log.Info($"Watching {_watchers.Count} folder(s).");
    }

    public void StopWatching()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* ignore */ }
        }
        _watchers.Clear();
    }

    /// <summary>
    /// File systems fire bursts of events (a copy can produce a dozen). Collapse them
    /// into one pass that runs 750 ms after things go quiet.
    /// </summary>
    private void ScheduleDebounce()
    {
        // Guarded, because this runs on a pool thread where an escaping exception ends the
        // process rather than being caught anywhere. Saving the library failed here — the
        // folder watcher and a deliberate add both writing at once — and took the whole app
        // down with it. Noticing a file on disk is not worth anybody's session.
        _debounceTimer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                ApplyPending();
            }
            catch (Exception ex)
            {
                Log.Error("Applying watched folder changes failed", ex);
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _debounceTimer.Change(750, Timeout.Infinite);
    }

    private void ApplyPending()
    {
        var paths = _pendingPaths.Keys.ToList();
        foreach (var p in paths) _pendingPaths.TryRemove(p, out _);
        if (paths.Count == 0) return;

        var added = 0;
        var removed = 0;

        foreach (var path in paths)
        {
            try
            {
                var exists = File.Exists(path);
                var key = Normalize(path);

                lock (_gate)
                {
                    var known = _byPath.TryGetValue(key, out var entry);

                    if (exists && !known)
                    {
                        var e = new SoundEntry { FilePath = path };
                        AudioFileMetadataReader.Populate(e);
                        _data.Sounds.Add(e);
                        _byPath[key] = e;
                        _byId[e.Id] = e;
                        added++;
                    }
                    else if (exists && known)
                    {
                        if (AudioFileMetadataReader.NeedsRefresh(entry!))
                            AudioFileMetadataReader.Populate(entry!);
                    }
                    else if (!exists && known)
                    {
                        if (HasUserData(entry!))
                        {
                            entry!.IsMissing = true;
                        }
                        else
                        {
                            _data.Sounds.Remove(entry!);
                            _byPath.Remove(key);
                            _byId.Remove(entry!.Id);
                        }
                        removed++;
                    }

                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Watcher update failed for {path}: {ex.Message}");
            }
        }

        if (added > 0 || removed > 0)
        {
            Log.Info($"Folder watch: +{added} / -{removed}.");
            Save();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // -----------------------------------------------------------------------
    // Mutations
    // -----------------------------------------------------------------------

    /// <summary>Index files that already exist on disk without copying them anywhere.</summary>
    public IReadOnlyList<SoundEntry> AddFiles(IEnumerable<string> paths, string? groupId = null)
    {
        var added = new List<SoundEntry>();

        foreach (var path in paths)
        {
            var key = Normalize(path);
            lock (_gate)
            {
                if (_byPath.ContainsKey(key)) continue;
            }

            var entry = new SoundEntry { FilePath = path, GroupId = groupId };
            AudioFileMetadataReader.Populate(entry);

            lock (_gate)
            {
                if (_byPath.ContainsKey(key)) continue;
                _data.Sounds.Add(entry);
                _byPath[key] = entry;
                _byId[entry.Id] = entry;
                _dirty = true;
            }

            added.Add(entry);
        }

        if (added.Count > 0)
        {
            Save();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>Remove from the library only — the file on disk is untouched.</summary>
    public void RemoveFromLibrary(IEnumerable<SoundEntry> entries)
    {
        var any = false;
        lock (_gate)
        {
            foreach (var e in entries)
            {
                if (!_byId.Remove(e.Id)) continue;
                _byPath.Remove(Normalize(e.FilePath));
                _data.Sounds.Remove(e);
                _data.History.RemoveAll(h => h == e.Id);
                any = true;
            }
            if (any) _dirty = true;
        }

        if (any)
        {
            Save();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Rename the underlying file. Only reachable when the user explicitly opts in —
    /// display names never touch the file system.
    /// </summary>
    /// <summary>
    /// Turn a display name into something Windows will actually accept as a file name.
    /// Song titles routinely contain characters a file name cannot ("Who? : Live"), and the
    /// raw Win32 error for those is impenetrable.
    /// </summary>
    public static string MakeSafeFileName(string proposed)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(proposed.Length);

        foreach (var c in proposed)
        {
            builder.Append(invalid.Contains(c) ? ' ' : c);
        }

        // Collapse the gaps left behind, and drop trailing dots and spaces, which Windows
        // silently strips and then fails to find.
        var cleaned = string.Join(' ',
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');

        // Device names are reserved whatever the extension.
        string[] reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        if (reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) cleaned = "_" + cleaned;

        // Leave room for the extension and the folder.
        return cleaned.Length > 200 ? cleaned[..200].TrimEnd('.', ' ') : cleaned;
    }

    public bool RenameFileOnDisk(SoundEntry entry, string newFileNameWithoutExtension, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(entry.FilePath);
            if (dir is null) { error = "Could not resolve the folder."; return false; }

            if (!File.Exists(entry.FilePath))
            {
                error = "The original file is no longer there.";
                return false;
            }

            var safe = MakeSafeFileName(newFileNameWithoutExtension);
            if (safe.Length == 0)
            {
                error = "That name has no characters Windows can use in a file name.";
                return false;
            }

            var ext = Path.GetExtension(entry.FilePath);
            var target = Path.Combine(dir, safe + ext);

            // Already called that: nothing to do, and certainly not an error.
            if (string.Equals(target, entry.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                // Unless only the capitalisation differs, which is a real rename on a
                // case-insensitive file system and needs a bounce through a temp name.
                if (string.Equals(target, entry.FilePath, StringComparison.Ordinal)) return true;

                var interim = Path.Combine(dir, $"{safe}.{Guid.NewGuid():N}{ext}");
                File.Move(entry.FilePath, interim);
                File.Move(interim, target);

                lock (_gate)
                {
                    _byPath.Remove(Normalize(entry.FilePath));
                    entry.FilePath = target;
                    _byPath[Normalize(target)] = entry;
                    _dirty = true;
                }

                Save();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (File.Exists(target)) { error = "A file with that name already exists."; return false; }

            File.Move(entry.FilePath, target);

            lock (_gate)
            {
                _byPath.Remove(Normalize(entry.FilePath));
                entry.FilePath = target;
                _byPath[Normalize(target)] = entry;
                _dirty = true;
            }

            Save();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void RecordPlayed(SoundEntry entry)
    {
        lock (_gate)
        {
            entry.PlayCount++;
            entry.LastPlayedUtc = DateTime.UtcNow;

            if (_settings.Settings.Playback.RememberHistory)
            {
                _data.History.RemoveAll(h => h == entry.Id);
                _data.History.Insert(0, entry.Id);
                var limit = Math.Max(1, _settings.Settings.Playback.HistoryLimit);
                if (_data.History.Count > limit)
                    _data.History.RemoveRange(limit, _data.History.Count - limit);
            }

            _dirty = true;
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _data.History.Clear();
            _dirty = true;
        }
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged()
    {
        MarkDirty();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Record that a sound's own settings changed, without telling the UI to rebuild its
    /// list. Volume, speed, fades and the like affect nothing about which sounds are shown
    /// or in what order, and a slider drag fires this on every mouse move — re-filtering
    /// the whole library each time is what made those sliders crawl.
    /// </summary>
    public void MarkMetadataDirty() => MarkDirty();

    // ---- groups -----------------------------------------------------------

    public SoundGroup CreateGroup(string name, string? parentId = null)
    {
        var group = new SoundGroup { Name = name, ParentId = parentId };
        lock (_gate)
        {
            group.SortOrder = _data.Groups.Count;
            _data.Groups.Add(group);
            _dirty = true;
        }
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return group;
    }

    public void RenameGroup(SoundGroup group, string name)
    {
        group.Name = name;
        MarkDirty();
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Delete a group. Child groups are re-parented to the deleted group's parent and
    /// member sounds fall back to "Ungrouped" — nothing is removed from the library.
    /// </summary>
    public void DeleteGroup(SoundGroup group)
    {
        lock (_gate)
        {
            foreach (var child in _data.Groups.Where(g => g.ParentId == group.Id))
                child.ParentId = group.ParentId;

            foreach (var s in _data.Sounds.Where(s => s.GroupId == group.Id))
                s.GroupId = group.ParentId;

            _data.Groups.Remove(group);
            _dirty = true;
        }
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public SoundGroup? GetGroup(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _data.Groups.FirstOrDefault(g => g.Id == id);
    }

    public void AssignGroup(IEnumerable<SoundEntry> entries, string? groupId)
    {
        foreach (var e in entries) e.GroupId = groupId;
        MarkDirty();
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>All descendant group ids of <paramref name="groupId"/>, including itself.</summary>
    public HashSet<string> GetGroupTree(string groupId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { groupId };
        lock (_gate)
        {
            bool grew;
            do
            {
                grew = false;
                foreach (var g in _data.Groups)
                {
                    if (g.ParentId is not null && result.Contains(g.ParentId) && result.Add(g.Id))
                        grew = true;
                }
            } while (grew);
        }
        return result;
    }

    // ---- diagnostics ------------------------------------------------------

    public IReadOnlyList<SoundEntry> FindMissing()
    {
        lock (_gate) return _data.Sounds.Where(s => s.IsMissing).ToArray();
    }

    public IReadOnlyList<SoundEntry> FindBroken()
    {
        lock (_gate) return _data.Sounds.Where(s => s.IsBroken && !s.IsMissing).ToArray();
    }

    /// <summary>
    /// Groups of entries that look like the same audio: identical file size and duration.
    /// Cheap enough to run over a huge library, and accurate in practice for duplicates
    /// produced by copying files around.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SoundEntry>> FindDuplicates()
    {
        lock (_gate)
        {
            return _data.Sounds
                .Where(s => !s.IsMissing && s.FileSizeBytes > 0 && s.DurationSeconds > 0)
                .GroupBy(s => (s.FileSizeBytes, Math.Round(s.DurationSeconds, 2)))
                .Where(g => g.Count() > 1)
                .Select(g => (IReadOnlyList<SoundEntry>)g.ToArray())
                .ToArray();
        }
    }

    /// <summary>
    /// Re-check missing entries and clear the flag for any file that came back
    /// (a reconnected drive, a restored folder).
    /// </summary>
    public int RepairMissing()
    {
        var repaired = 0;
        lock (_gate)
        {
            foreach (var s in _data.Sounds.Where(s => s.IsMissing).ToList())
            {
                if (!File.Exists(s.FilePath)) continue;
                s.IsMissing = false;
                AudioFileMetadataReader.Populate(s);
                repaired++;
            }
            if (repaired > 0) _dirty = true;
        }

        if (repaired > 0)
        {
            Save();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        return repaired;
    }

    /// <summary>Replace the whole dataset (used by backup restore).</summary>
    public void ReplaceAll(LibraryData data)
    {
        lock (_gate)
        {
            _data = data;
            RebuildIndexes();
            _dirty = true;
        }
        Save();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public LibraryData Snapshot()
    {
        lock (_gate)
        {
            return new LibraryData
            {
                SchemaVersion = _data.SchemaVersion,
                Sounds = _data.Sounds.Select(s => s.Clone()).ToList(),
                Groups = _data.Groups.ToList(),
                History = _data.History.ToList(),
                LastScanUtc = _data.LastScanUtc
            };
        }
    }

    public void Dispose()
    {
        StopWatching();
        _debounceTimer?.Dispose();
        SaveIfDirty();
    }
}
