using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.Core.Storage;

/// <summary>Loads, saves and hands out the single <see cref="AppSettings"/> instance.</summary>
public sealed class SettingsService
{
    private readonly Lock _gate = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private bool _dirty;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>Raised after settings are saved so services can re-read what they need.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        AppPaths.EnsureCreated();
        Settings = JsonStore.Load(AppPaths.SettingsFile, CreateDefaults);
        Log.Verbose = Settings.Advanced.VerboseLogging;

        if (!string.IsNullOrWhiteSpace(Settings.Advanced.CustomDataPath))
        {
            AppPaths.SetDataRoot(Settings.Advanced.CustomDataPath);
            AppPaths.EnsureCreated();
        }

        Log.Info($"Settings loaded (setup completed: {Settings.SetupCompleted}).");
    }

    public void Save()
    {
        lock (_gate)
        {
            JsonStore.Save(AppPaths.SettingsFile, Settings);
            _lastSaveUtc = DateTime.UtcNow;
            _dirty = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mark settings dirty; <see cref="FlushIfDue"/> writes them out on a timer.</summary>
    public void MarkDirty() => _dirty = true;

    public void FlushIfDue()
    {
        if (!_dirty) return;
        var interval = Math.Max(3, Settings.Advanced.MetadataAutosaveSeconds);
        if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < interval) return;
        Save();
    }

    /// <summary>Notify listeners without writing to disk (used for live audio tweaks).</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static AppSettings CreateDefaults()
    {
        var s = new AppSettings();
        s.Hotkeys.AddRange(new[]
        {
            new HotkeyBinding { Action = HotkeyAction.StopAll,           VirtualKey = 0x1B, Modifiers = HotkeyModifiers.Control }, // Ctrl+Esc
            new HotkeyBinding { Action = HotkeyAction.MuteMicrophone,    VirtualKey = 0x7B, Modifiers = HotkeyModifiers.None },    // F12
            new HotkeyBinding { Action = HotkeyAction.MuteSoundboard,    VirtualKey = 0x7A, Modifiers = HotkeyModifiers.None },    // F11
            new HotkeyBinding { Action = HotkeyAction.TogglePassthrough, VirtualKey = 0x79, Modifiers = HotkeyModifiers.None },    // F10
        });
        return s;
    }
}
