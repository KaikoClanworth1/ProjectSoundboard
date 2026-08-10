namespace ProjectSoundboard.Core.Storage;

/// <summary>
/// Central place for every path the application writes to.
///
/// Project Soundboard is portable by default: settings, thumbnails, waveforms, backups and
/// logs all live in a <c>Data</c> folder next to the executable, so the whole thing can be
/// copied to a USB stick or synced between machines. Your actual sound files are never moved
/// — the library only ever stores paths to them.
///
/// When the install location is read-only (Program Files, for instance) it falls back to
/// %APPDATA% automatically, because a portable layout that cannot be written to is worse
/// than a roaming one.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "Project Soundboard";

    /// <summary>Sub-folder next to the executable that holds everything user specific.</summary>
    public const string PortableFolderName = "Data";

    private static string? _overrideRoot;
    private static string? _resolvedRoot;

    /// <summary>True when data is being kept alongside the executable.</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>Folder the executable lives in.</summary>
    public static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    /// <summary>The roaming location used before portable mode, and the read-only fallback.</summary>
    public static string RoamingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);

    public static string DataRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_overrideRoot)) return _overrideRoot!;
            return _resolvedRoot ??= Resolve();
        }
    }

    /// <summary>Redirect all data to a custom folder (Settings → Advanced).</summary>
    public static void SetDataRoot(string? path) => _overrideRoot = path;

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LibraryFile => Path.Combine(DataRoot, "library.json");
    public static string ImageCacheDir => Path.Combine(DataRoot, "images");
    public static string WaveformCacheDir => Path.Combine(DataRoot, "waveforms");
    public static string BackupDir => Path.Combine(DataRoot, "backups");
    public static string LogDir => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// Crash reports. Kept apart from the rolling logs so they are not pruned with them and
    /// so there is one folder to point somebody at.
    /// </summary>
    public static string CrashDir => Path.Combine(DataRoot, "crashes");

    /// <summary>
    /// Written while the app is running and removed on a clean exit. If it is still here at
    /// startup, the previous run died without shutting down.
    /// </summary>
    public static string SessionMarker => Path.Combine(DataRoot, "session.running");

    /// <summary>Where staged updates are unpacked before being applied.</summary>
    public static string UpdateStagingDir => Path.Combine(DataRoot, "updates");

    /// <summary>Default suggestion for the main sound library: Music\Project Soundboard.</summary>
    public static string DefaultMainLibrary => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ProductName);

    private static string Resolve()
    {
        var portable = Path.Combine(AppDirectory, PortableFolderName);

        if (CanWriteTo(portable))
        {
            IsPortable = true;
            MigrateFromRoamingIfNeeded(portable);
            return portable;
        }

        IsPortable = false;
        return RoamingRoot;
    }

    /// <summary>Probe by actually creating and deleting a file — permissions alone lie.</summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".write-test");
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
    /// Bring across an existing roaming profile the first time we run portable, so nobody
    /// loses their library, custom names or artwork when they upgrade.
    ///
    /// This copies rather than moves: if the portable folder later gets wiped (a clean
    /// rebuild, say), the original is still sitting in %APPDATA%.
    /// </summary>
    private static void MigrateFromRoamingIfNeeded(string portableRoot)
    {
        try
        {
            var roaming = RoamingRoot;
            if (!Directory.Exists(roaming)) return;

            // Only migrate into a folder that has no settings of its own.
            if (File.Exists(Path.Combine(portableRoot, "settings.json"))) return;
            if (!File.Exists(Path.Combine(roaming, "settings.json"))) return;

            foreach (var directory in Directory.EnumerateDirectories(roaming, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directory.Replace(roaming, portableRoot));
            }

            foreach (var file in Directory.EnumerateFiles(roaming, "*", SearchOption.AllDirectories))
            {
                // Logs are noise and updates are disposable; everything else comes along.
                var relative = Path.GetRelativePath(roaming, file);
                if (relative.StartsWith("logs", StringComparison.OrdinalIgnoreCase)) continue;
                if (relative.StartsWith("updates", StringComparison.OrdinalIgnoreCase)) continue;

                File.Copy(file, Path.Combine(portableRoot, relative), overwrite: false);
            }

            File.WriteAllText(
                Path.Combine(portableRoot, "migrated-from-appdata.txt"),
                $"Copied from {roaming} on {DateTime.Now:yyyy-MM-dd HH:mm}.{Environment.NewLine}" +
                "The original is still there and can be deleted once you are happy.");
        }
        catch
        {
            // A failed migration must never stop the app from starting; the worst case is
            // that the user starts with an empty library and re-adds their folders.
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ImageCacheDir);
        Directory.CreateDirectory(WaveformCacheDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CrashDir);
    }
}
