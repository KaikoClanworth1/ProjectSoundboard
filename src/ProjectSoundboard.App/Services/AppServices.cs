using ProjectSoundboard.Audio;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Composition root. A desktop app with one of each service does not need a container;
/// what it does need is one obvious place where construction order is written down.
/// </summary>
public sealed class AppServices : IDisposable
{
    private static AppServices? _instance;

    /// <summary>The single instance, created by <see cref="App.OnStartup"/>.</summary>
    public static AppServices Current =>
        _instance ?? throw new InvalidOperationException("Services have not been initialised yet.");

    public static bool IsInitialised => _instance is not null;

    public SettingsService Settings { get; }
    public PresetService Presets { get; }
    public LibraryService Library { get; }
    public SearchService Search { get; }
    public ImportService Import { get; }
    public BackupService Backup { get; }

    public AudioDeviceService Devices { get; }
    public AudioEngine Engine { get; }
    public MicPassthrough Microphone { get; }

    public ThemeService Theme { get; }
    public HotkeyService Hotkeys { get; }
    public ImageCacheService Images { get; }
    public UpdateService Updates { get; }

    private AppServices()
    {
        AppPaths.EnsureCreated();
        Log.Start();

        Settings = new SettingsService();
        Settings.Load();

        Presets = new PresetService(Settings);

        Library = new LibraryService(Settings);
        Library.Load();

        Search = new SearchService(Settings, Library);
        Import = new ImportService(Settings, Library);
        Backup = new BackupService(Settings, Library);

        Devices = new AudioDeviceService();
        Engine = new AudioEngine(Settings, Devices);
        Microphone = new MicPassthrough(Settings, Devices, Engine);

        Theme = new ThemeService(Settings);
        Hotkeys = new HotkeyService(Settings);
        Updates = new UpdateService(Settings);

        // If we just came back from an update, the staged copy has served its purpose.
        UpdateService.CleanUpStaging();
        Images = new ImageCacheService
        {
            BudgetBytes = Math.Max(32, Settings.Settings.Performance.ImageCacheMb) * 1024L * 1024L
        };
    }

    public static AppServices Initialise() => _instance ??= new AppServices();

    /// <summary>
    /// The setup, for a crash report. Devices and library size are what usually differ
    /// between a machine where something happens and one where it does not, and asking for
    /// them after the fact never gets a complete answer.
    /// </summary>
    public string DescribeForCrashReport()
    {
        var text = new System.Text.StringBuilder();

        try
        {
            var audio = Settings.Settings.Audio;
            var mic = Settings.Settings.Microphone;

            text.AppendLine($"Sounds        : {Library.Sounds.Count} in {Library.Groups.Count} group(s), " +
                            $"{Settings.Settings.Library.Folders.Count} folder(s)");
            text.AppendLine($"Virtual mic   : {Describe(Engine.VirtualMicBus.DeviceName, Engine.VirtualMicBus.IsRunning, audio.VirtualMicEnabled)}");
            text.AppendLine($"Monitor       : {Describe(Engine.MonitorBus.DeviceName, Engine.MonitorBus.IsRunning, audio.MonitorEnabled)}");
            text.AppendLine($"Microphone    : {Describe(Microphone.DeviceName, Microphone.IsRunning, mic.PassthroughEnabled)}");
            text.AppendLine($"Format        : {audio.SampleRate} Hz, {audio.Channels} ch, " +
                            $"{audio.BufferSizeMs} ms buffer, low latency {audio.LowLatencyMode}");
            text.AppendLine($"Playing       : {Engine.Active.Count} sound(s)");
            text.AppendLine($"View          : {Settings.Settings.Appearance.ViewMode}, " +
                            $"theme {Settings.Settings.Appearance.Theme}");
            text.AppendLine($"Hotkeys       : {Settings.Settings.Hotkeys.Count} bound, " +
                            $"{Hotkeys.Conflicts.Count} in conflict, {Hotkeys.Disabled.Count} switched off");

            if (Engine.VirtualMicBus.LastError is { } e1) text.AppendLine($"Virtual mic error : {e1}");
            if (Engine.MonitorBus.LastError is { } e2) text.AppendLine($"Monitor error     : {e2}");
            if (Microphone.LastError is { } e3) text.AppendLine($"Microphone error  : {e3}");
        }
        catch (Exception ex)
        {
            text.AppendLine($"(could not be collected in full: {ex.Message})");
        }

        return text.ToString().TrimEnd();

        static string Describe(string? device, bool running, bool enabled) =>
            !enabled ? "switched off"
            : device is null ? "no device"
            : $"{device}{(running ? "" : " (not running)")}";
    }

    /// <summary>
    /// Bring the audio stack up from the current settings. Called at startup and whenever
    /// the user changes a device.
    /// </summary>
    public void StartAudio()
    {
        Engine.ApplyAudioSettings();

        if (Settings.Settings.Microphone.PassthroughEnabled)
            Microphone.Start();
    }

    public void Dispose()
    {
        try { Hotkeys.Dispose(); } catch { /* ignore */ }
        try { Microphone.Dispose(); } catch { /* ignore */ }
        try { Engine.Dispose(); } catch { /* ignore */ }
        try { Devices.Dispose(); } catch { /* ignore */ }

        try
        {
            Library.SaveIfDirty();
            Library.Dispose();
            Settings.Save();
        }
        catch (Exception ex) { Log.Error("Shutdown save failed", ex); }

        Log.Info("Project Soundboard closed.");
        Log.Shutdown();

        _instance = null;
    }
}
