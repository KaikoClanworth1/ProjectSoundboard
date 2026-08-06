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

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var bak = path + ".bak";

        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
        {
            // Replace keeps a backup copy and is atomic on NTFS.
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
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
