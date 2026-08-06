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
