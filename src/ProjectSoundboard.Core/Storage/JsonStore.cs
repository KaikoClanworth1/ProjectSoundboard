using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectSoundboard.Core.Storage;

/// <summary>
/// Small crash-safe JSON reader/writer: writes to a temp file then atomically
/// replaces the target, and keeps a .bak so a corrupt write is always recoverable.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null) return value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read {Path.GetFileName(path)}: {ex.Message}");
            TryRestoreBackup(path);
            try
            {
                if (File.Exists(path))
                {
                    var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
                    if (value is not null) return value;
                }
            }
            catch { /* fall through to defaults */ }
        }

        return fallback();
    }

    /// <summary>
    /// One lock per file being written.
    ///
    /// Two saves of the same file can be asked for at the same moment — a folder watcher
    /// noticing a new file while the app is adding it deliberately, which is exactly what a
    /// download into a watched folder does. Both wrote to the same temp name, the second
    /// found it held by the first, and the exception came out on a timer thread where
    /// nothing was catching it and ended the process.
    /// </summary>
    private static readonly Dictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static object GateFor(string path)
    {
        lock (Gates)
        {
            if (Gates.TryGetValue(path, out var gate)) return gate;
            return Gates[path] = new object();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, Options);

        lock (GateFor(path))
        {
            // A name of its own as well as the lock. The lock settles this application; the
            // name settles everything else that might be holding a file in this folder —
            // another copy of the app on a shared folder, a virus scanner, a backup tool.
            var tmp = $"{path}.{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.tmp";
            var bak = path + ".bak";

            try
            {
                Retry(() => File.WriteAllText(tmp, json));

                Retry(() =>
                {
                    if (File.Exists(path))
                    {
                        // Replace keeps a backup copy and is atomic on NTFS.
                        File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                });
            }
            finally
            {
                // Nothing else will ever look at it, and leaving it would accumulate one
                // per failed save.
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* it will be overwritten next time */ }
            }
        }
    }

    /// <summary>
    /// Try a few times before giving up. A file can be held for a moment by something with
    /// no interest in it — a scanner, an indexer, a sync client — and failing the save
    /// outright because of that would lose the change for no good reason.
    /// </summary>
    private static void Retry(Action action)
    {
        const int attempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }

    private static void TryRestoreBackup(string path)
    {
        var bak = path + ".bak";
        if (!File.Exists(bak)) return;
        try
        {
            File.Copy(bak, path, overwrite: true);
            Log.Info($"Restored {Path.GetFileName(path)} from backup.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Backup restore failed: {ex.Message}");
        }
    }
}
