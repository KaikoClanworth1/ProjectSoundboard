namespace ProjectSoundboard.Core.Models;

/// <summary>Root settings object, persisted as settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Bumped when a default changes in a way existing installs should pick up.
    /// See <c>SettingsService.Migrate</c>.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool SetupCompleted { get; set; }

    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public MicrophoneSettings Microphone { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public AccessibilitySettings Accessibility { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    public List<HotkeyBinding> Hotkeys { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Hide the window entirely when minimised, leaving only the tray icon. Off by default:
    /// minimising should put the app on the taskbar like any other program, and vanishing
    /// from it is surprising unless you asked for it.
    /// </summary>
    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool ConfirmOnDelete { get; set; } = true;
    public string Language { get; set; } = "en";

    /// <summary>A release the user chose to skip; they are not asked about it again.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Throttles the startup check so it runs at most once a day.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
}

public sealed class AppearanceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#6C8CFF";
    public double UiScale { get; set; } = 1.0;
    public bool EnableAnimations { get; set; } = true;
    public bool EnableAcrylic { get; set; } = true;
    public bool ShowSoundImages { get; set; } = true;
    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Grid;
    public double GridTileSize { get; set; } = 132;
    public bool ShowPropertiesPanel { get; set; } = true;
    public bool ShowWaveformInPanel { get; set; } = true;
}

public sealed class AudioSettings
{
    /// <summary>
    /// Device id of the virtual cable input that voice apps listen to.
    /// Null until configured — the setup wizard tries to detect it.
    /// </summary>
    public string? VirtualMicDeviceId { get; set; }

    /// <summary>Speakers/headphones so the user can hear what they trigger.</summary>
    public string? MonitorDeviceId { get; set; }

    public bool VirtualMicEnabled { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;

    public float VirtualMicVolume { get; set; } = 1.0f;
    public float MonitorVolume { get; set; } = 0.8f;
    public float MasterVolume { get; set; } = 1.0f;

    /// <summary>Exclusive-mode WASAPI + smallest safe buffer.</summary>
    public bool LowLatencyMode { get; set; } = true;

    /// <summary>Requested output latency in milliseconds.</summary>
    public int BufferSizeMs { get; set; } = 30;

    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;

    public bool LimiterEnabled { get; set; } = true;
    public float LimiterThresholdDb { get; set; } = -1.0f;

    public bool CompressorEnabled { get; set; }
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 3f;
    public float CompressorAttackMs { get; set; } = 10f;
    public float CompressorReleaseMs { get; set; } = 120f;
    public float CompressorMakeupDb { get; set; }

    public bool EqEnabled { get; set; }
    /// <summary>Gain in dB for the 5 fixed bands: 80 / 300 / 1k / 4k / 12k Hz.</summary>
    public float[] EqBandsDb { get; set; } = new float[5];
}

public sealed class MicrophoneSettings
{
    public string? InputDeviceId { get; set; }

    /// <summary>Mix the microphone into the virtual mic so one device carries both.</summary>
    public bool PassthroughEnabled { get; set; }

    /// <summary>Also send the mic to the monitor device so the user can hear themselves.</summary>
    public bool MonitorEnabled { get; set; }

    public float InputGain { get; set; } = 1.0f;
    public float OutputGain { get; set; } = 1.0f;
    public float MonitorVolume { get; set; } = 0.4f;
    public float BoostDb { get; set; }
    public bool Muted { get; set; }
    public bool ForceMono { get; set; } = true;

    public bool NoiseGateEnabled { get; set; } = true;
    public float GateThresholdDb { get; set; } = -45f;
    public float GateAttackMs { get; set; } = 5f;
    public float GateHoldMs { get; set; } = 120f;
    public float GateReleaseMs { get; set; } = 180f;

    public bool CompressorEnabled { get; set; }
    public float CompressorThresholdDb { get; set; } = -20f;
    public float CompressorRatio { get; set; } = 4f;

    public bool LimiterEnabled { get; set; } = true;
    public float LimiterThresholdDb { get; set; } = -1.5f;

    public bool NoiseSuppressionEnabled { get; set; }
    public bool EchoCancellationEnabled { get; set; }

    /// <summary>Only pass audio while the push-to-talk hotkey is held.</summary>
    public bool PushToTalkEnabled { get; set; }
}

public sealed class PlaybackSettings
{
    public PlaybackMode Mode { get; set; } = PlaybackMode.Overlap;
    public int MaxSimultaneousSounds { get; set; } = 16;
    public bool StopOnSecondPress { get; set; }
    public bool DuckMicrophoneWhilePlaying { get; set; }
    public float MicDuckAmount { get; set; } = 0.4f;
    public int GlobalFadeOutMs { get; set; } = 40;
    public bool RememberHistory { get; set; } = true;
    public int HistoryLimit { get; set; } = 200;
}

public sealed class LibrarySettings
{
    public List<LibraryFolder> Folders { get; set; } = new();

    /// <summary>Where "Import to Main Sound Library" copies files.</summary>
    public string? MainLibraryPath { get; set; }

    public ImportBehavior ImportBehavior { get; set; } = ImportBehavior.Ask;
    public ConflictAction ConflictAction { get; set; } = ConflictAction.Ask;
    public bool PreserveFolderStructureOnImport { get; set; } = true;
    public bool AutoTagFromFolderName { get; set; } = true;
    public bool DetectDuplicatesOnImport { get; set; } = true;
    public bool WatchFolders { get; set; } = true;
    public bool ScanOnStartup { get; set; } = true;

    public List<string> Extensions { get; set; } =
        new() { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".aiff" };
}

public sealed class SearchSettings
{
    public bool FuzzyMatching { get; set; } = true;
    public bool SearchFileName { get; set; } = true;
    public bool SearchDisplayName { get; set; } = true;
    public bool SearchTags { get; set; } = true;
    public bool SearchGroup { get; set; } = true;
    public bool RememberRecentSearches { get; set; } = true;
    public int RecentSearchLimit { get; set; } = 12;
    public List<string> RecentSearches { get; set; } = new();
}

public sealed class PerformanceSettings
{
    public bool LazyLoading { get; set; } = true;
    public bool CacheImages { get; set; } = true;
    public bool PreloadFrequentSounds { get; set; } = true;
    public int PreloadCount { get; set; } = 24;
    public int ScanThreads { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
    public bool BackgroundIndexing { get; set; } = true;
    public bool HardwareAcceleration { get; set; } = true;
    public int ImageCacheMb { get; set; } = 128;
}

public sealed class NotificationSettings
{
    public bool ShowToasts { get; set; } = true;
    public bool NotifyOnMissingFiles { get; set; } = true;
    public bool NotifyOnImport { get; set; } = true;
    public bool NotifyOnDeviceChange { get; set; } = true;
    public bool PlaySoundOnError { get; set; }
}

public sealed class AccessibilitySettings
{
    public bool HighContrast { get; set; }
    public bool LargeText { get; set; }
    public double TextScale { get; set; } = 1.0;
    public bool ScreenReaderHints { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool KeyboardNavigationHints { get; set; } = true;
    public ColorBlindMode ColorBlindMode { get; set; } = ColorBlindMode.None;
    public bool AlwaysShowFocusOutline { get; set; }
}

public sealed class AdvancedSettings
{
    public bool VerboseLogging { get; set; }
    public bool DeveloperMode { get; set; }
    public bool ShowAudioDebugOverlay { get; set; }
    public bool AllowFileRenaming { get; set; }
    public int MetadataAutosaveSeconds { get; set; } = 20;
    public string? CustomDataPath { get; set; }
}
