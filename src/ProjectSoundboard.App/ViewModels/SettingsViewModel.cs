using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProjectSoundboard.Audio;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.ViewModels;

public enum SettingsSection
{
    General,
    Appearance,
    Playback,
    Library,
    Search,
    Performance,
    Notifications,
    Accessibility,
    Backup,
    Advanced
}

/// <summary>A watched folder row in Settings → Library.</summary>
public sealed partial class FolderRowViewModel : ObservableObject
{
    public FolderRowViewModel(LibraryFolder folder, int soundCount)
    {
        Folder = folder;
        _enabled = folder.Enabled;
        _recursive = folder.Recursive;
        _watch = folder.Watch;
        _groupFromSubfolders = folder.GroupFromSubfolders;
        SoundCount = soundCount;
    }

    public LibraryFolder Folder { get; }
    public string Path => Folder.Path;
    public bool IsMainLibrary => Folder.IsMainLibrary;
    public int SoundCount { get; }
    public bool Exists => Directory.Exists(Folder.Path);

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _recursive;
    [ObservableProperty] private bool _watch;
    [ObservableProperty] private bool _groupFromSubfolders;

    public event EventHandler? Changed;

    /// <summary>Raised when a change needs a rescan to take effect, not just a save.</summary>
    public event EventHandler? RescanNeeded;

    partial void OnEnabledChanged(bool value) { Folder.Enabled = value; Changed?.Invoke(this, EventArgs.Empty); }
    partial void OnRecursiveChanged(bool value) { Folder.Recursive = value; Changed?.Invoke(this, EventArgs.Empty); }
    partial void OnWatchChanged(bool value) { Folder.Watch = value; Changed?.Invoke(this, EventArgs.Empty); }

    partial void OnGroupFromSubfoldersChanged(bool value)
    {
        Folder.GroupFromSubfolders = value;
        Changed?.Invoke(this, EventArgs.Empty);
        if (value) RescanNeeded?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Everything under the Settings page, grouped into the categories in the sidebar.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private bool _loading;

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;

        Load();
        RefreshFolders();
        RefreshBackups();
    }

    [ObservableProperty] private SettingsSection _section = SettingsSection.General;
    [ObservableProperty] private string _statusText = string.Empty;

    // ---- General ----------------------------------------------------------

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _confirmOnDelete;

    // ---- Appearance -------------------------------------------------------

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private string _accentColor = "#6C8CFF";
    [ObservableProperty] private double _uiScale = 1.0;
    [ObservableProperty] private bool _enableAnimations;
    [ObservableProperty] private bool _showSoundImages;
    [ObservableProperty] private bool _showWaveformInPanel;

    public IReadOnlyList<string> AccentPresets { get; } = new[]
    {
        "#6C8CFF", "#5865F2", "#1DB954", "#F2711C", "#E0426A", "#9B5DE5", "#00B8D9", "#F4B400"
    };

    // ---- Playback ---------------------------------------------------------

    [ObservableProperty] private PlaybackMode _playbackMode;
    [ObservableProperty] private int _maxSimultaneousSounds;
    [ObservableProperty] private bool _stopOnSecondPress;
    [ObservableProperty] private bool _duckMicrophoneWhilePlaying;
    [ObservableProperty] private float _micDuckAmount;
    [ObservableProperty] private int _globalFadeOutMs;
    [ObservableProperty] private bool _rememberHistory;

    // ---- Library ----------------------------------------------------------

    public ObservableCollection<FolderRowViewModel> Folders { get; } = new();

    [ObservableProperty] private string? _mainLibraryPath;
    [ObservableProperty] private ImportBehavior _importBehavior;
    [ObservableProperty] private ConflictAction _conflictAction;
    [ObservableProperty] private bool _preserveFolderStructureOnImport;
    [ObservableProperty] private bool _autoTagFromFolderName;
    [ObservableProperty] private bool _groupFromSubfoldersByDefault;
    [ObservableProperty] private bool _detectDuplicatesOnImport;
    [ObservableProperty] private bool _watchFolders;
    [ObservableProperty] private bool _scanOnStartup;
    [ObservableProperty] private string _extensionsText = string.Empty;

    // ---- Search -----------------------------------------------------------

    [ObservableProperty] private bool _fuzzyMatching;
    [ObservableProperty] private bool _searchFileName;
    [ObservableProperty] private bool _searchDisplayName;
    [ObservableProperty] private bool _searchTags;
    [ObservableProperty] private bool _searchGroup;
    [ObservableProperty] private bool _rememberRecentSearches;

    // ---- Performance ------------------------------------------------------

    [ObservableProperty] private bool _lazyLoading;
    [ObservableProperty] private bool _cacheImages;
    [ObservableProperty] private bool _preloadFrequentSounds;
    [ObservableProperty] private int _preloadCount;
    [ObservableProperty] private int _scanThreads;
    [ObservableProperty] private bool _backgroundIndexing;
    [ObservableProperty] private int _imageCacheMb;
    [ObservableProperty] private string _cacheStatsText = string.Empty;

    // ---- Notifications ----------------------------------------------------

    [ObservableProperty] private bool _showToasts;
    [ObservableProperty] private bool _notifyOnMissingFiles;
    [ObservableProperty] private bool _notifyOnImport;
    [ObservableProperty] private bool _notifyOnDeviceChange;

    // ---- Accessibility ----------------------------------------------------

    [ObservableProperty] private bool _highContrast;
    [ObservableProperty] private bool _largeText;
    [ObservableProperty] private double _textScale;
    [ObservableProperty] private bool _screenReaderHints;
    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private ColorBlindMode _colorBlindMode;
    [ObservableProperty] private bool _alwaysShowFocusOutline;

    // ---- Advanced / developer ---------------------------------------------

    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private bool _allowFileRenaming;
    [ObservableProperty] private int _metadataAutosaveSeconds;
    [ObservableProperty] private string _diagnosticsText = string.Empty;

    // ---- Backup -----------------------------------------------------------

    public ObservableCollection<FileInfo> Backups { get; } = new();

    // ---- library health ---------------------------------------------------

    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private int _brokenCount;
    [ObservableProperty] private int _duplicateGroupCount;
    [ObservableProperty] private string _healthText = string.Empty;

    public IReadOnlyList<AppTheme> Themes { get; } = new[]
        { AppTheme.Dark, AppTheme.Light, AppTheme.System, AppTheme.HighContrast };

    public IReadOnlyList<PlaybackMode> PlaybackModes { get; } = Enum.GetValues<PlaybackMode>();
    public IReadOnlyList<ImportBehavior> ImportBehaviors { get; } = Enum.GetValues<ImportBehavior>();
    public IReadOnlyList<ConflictAction> ConflictActions { get; } = Enum.GetValues<ConflictAction>();
    public IReadOnlyList<ColorBlindMode> ColorBlindModes { get; } = Enum.GetValues<ColorBlindMode>();

    // -----------------------------------------------------------------------

    private void Load()
    {
        _loading = true;
        var s = _services.Settings.Settings;

        StartWithWindows = s.General.StartWithWindows;
        StartMinimized = s.General.StartMinimized;
        MinimizeToTray = s.General.MinimizeToTray;
        CloseToTray = s.General.CloseToTray;
        CheckForUpdates = s.General.CheckForUpdates;
        ConfirmOnDelete = s.General.ConfirmOnDelete;

        Theme = s.Appearance.Theme;
        AccentColor = s.Appearance.AccentColor;
        UiScale = s.Appearance.UiScale;
        EnableAnimations = s.Appearance.EnableAnimations;
        ShowSoundImages = s.Appearance.ShowSoundImages;
        ShowWaveformInPanel = s.Appearance.ShowWaveformInPanel;

        PlaybackMode = s.Playback.Mode;
        MaxSimultaneousSounds = s.Playback.MaxSimultaneousSounds;
        StopOnSecondPress = s.Playback.StopOnSecondPress;
        DuckMicrophoneWhilePlaying = s.Playback.DuckMicrophoneWhilePlaying;
        MicDuckAmount = s.Playback.MicDuckAmount;
        GlobalFadeOutMs = s.Playback.GlobalFadeOutMs;
        RememberHistory = s.Playback.RememberHistory;

        MainLibraryPath = s.Library.MainLibraryPath;
        ImportBehavior = s.Library.ImportBehavior;
        ConflictAction = s.Library.ConflictAction;
        PreserveFolderStructureOnImport = s.Library.PreserveFolderStructureOnImport;
        AutoTagFromFolderName = s.Library.AutoTagFromFolderName;
        GroupFromSubfoldersByDefault = s.Library.GroupFromSubfoldersByDefault;
        DetectDuplicatesOnImport = s.Library.DetectDuplicatesOnImport;
        WatchFolders = s.Library.WatchFolders;
        ScanOnStartup = s.Library.ScanOnStartup;
        ExtensionsText = string.Join(", ", s.Library.Extensions);

        FuzzyMatching = s.Search.FuzzyMatching;
        SearchFileName = s.Search.SearchFileName;
        SearchDisplayName = s.Search.SearchDisplayName;
        SearchTags = s.Search.SearchTags;
        SearchGroup = s.Search.SearchGroup;
        RememberRecentSearches = s.Search.RememberRecentSearches;

        LazyLoading = s.Performance.LazyLoading;
        CacheImages = s.Performance.CacheImages;
        PreloadFrequentSounds = s.Performance.PreloadFrequentSounds;
        PreloadCount = s.Performance.PreloadCount;
        ScanThreads = s.Performance.ScanThreads;
        BackgroundIndexing = s.Performance.BackgroundIndexing;
        ImageCacheMb = s.Performance.ImageCacheMb;

        ShowToasts = s.Notifications.ShowToasts;
        NotifyOnMissingFiles = s.Notifications.NotifyOnMissingFiles;
        NotifyOnImport = s.Notifications.NotifyOnImport;
        NotifyOnDeviceChange = s.Notifications.NotifyOnDeviceChange;

        HighContrast = s.Accessibility.HighContrast;
        LargeText = s.Accessibility.LargeText;
        TextScale = s.Accessibility.TextScale;
        ScreenReaderHints = s.Accessibility.ScreenReaderHints;
        ReducedMotion = s.Accessibility.ReducedMotion;
        ColorBlindMode = s.Accessibility.ColorBlindMode;
        AlwaysShowFocusOutline = s.Accessibility.AlwaysShowFocusOutline;

        VerboseLogging = s.Advanced.VerboseLogging;
        DeveloperMode = s.Advanced.DeveloperMode;
        AllowFileRenaming = s.Advanced.AllowFileRenaming;
        MetadataAutosaveSeconds = s.Advanced.MetadataAutosaveSeconds;

        _loading = false;
    }

    private void Save()
    {
        if (_loading) return;

        var s = _services.Settings.Settings;

        s.General.StartWithWindows = StartWithWindows;
        s.General.StartMinimized = StartMinimized;
        s.General.MinimizeToTray = MinimizeToTray;
        s.General.CloseToTray = CloseToTray;
        s.General.CheckForUpdates = CheckForUpdates;
        s.General.ConfirmOnDelete = ConfirmOnDelete;

        s.Appearance.Theme = Theme;
        s.Appearance.AccentColor = AccentColor;
        s.Appearance.UiScale = UiScale;
        s.Appearance.EnableAnimations = EnableAnimations;
        s.Appearance.ShowSoundImages = ShowSoundImages;
        s.Appearance.ShowWaveformInPanel = ShowWaveformInPanel;

        s.Playback.Mode = PlaybackMode;
        s.Playback.MaxSimultaneousSounds = MaxSimultaneousSounds;
        s.Playback.StopOnSecondPress = StopOnSecondPress;
        s.Playback.DuckMicrophoneWhilePlaying = DuckMicrophoneWhilePlaying;
        s.Playback.MicDuckAmount = MicDuckAmount;
        s.Playback.GlobalFadeOutMs = GlobalFadeOutMs;
        s.Playback.RememberHistory = RememberHistory;

        s.Library.MainLibraryPath = MainLibraryPath;
        s.Library.ImportBehavior = ImportBehavior;
        s.Library.ConflictAction = ConflictAction;
        s.Library.PreserveFolderStructureOnImport = PreserveFolderStructureOnImport;
        s.Library.AutoTagFromFolderName = AutoTagFromFolderName;
        s.Library.GroupFromSubfoldersByDefault = GroupFromSubfoldersByDefault;
        s.Library.DetectDuplicatesOnImport = DetectDuplicatesOnImport;
        s.Library.WatchFolders = WatchFolders;
        s.Library.ScanOnStartup = ScanOnStartup;

        s.Search.FuzzyMatching = FuzzyMatching;
        s.Search.SearchFileName = SearchFileName;
        s.Search.SearchDisplayName = SearchDisplayName;
        s.Search.SearchTags = SearchTags;
        s.Search.SearchGroup = SearchGroup;
        s.Search.RememberRecentSearches = RememberRecentSearches;

        s.Performance.LazyLoading = LazyLoading;
        s.Performance.CacheImages = CacheImages;
        s.Performance.PreloadFrequentSounds = PreloadFrequentSounds;
        s.Performance.PreloadCount = PreloadCount;
        s.Performance.ScanThreads = Math.Max(1, ScanThreads);
        s.Performance.BackgroundIndexing = BackgroundIndexing;
        s.Performance.ImageCacheMb = Math.Max(16, ImageCacheMb);

        s.Notifications.ShowToasts = ShowToasts;
        s.Notifications.NotifyOnMissingFiles = NotifyOnMissingFiles;
        s.Notifications.NotifyOnImport = NotifyOnImport;
        s.Notifications.NotifyOnDeviceChange = NotifyOnDeviceChange;

        s.Accessibility.HighContrast = HighContrast;
        s.Accessibility.LargeText = LargeText;
        s.Accessibility.TextScale = TextScale;
        s.Accessibility.ScreenReaderHints = ScreenReaderHints;
        s.Accessibility.ReducedMotion = ReducedMotion;
        s.Accessibility.ColorBlindMode = ColorBlindMode;
        s.Accessibility.AlwaysShowFocusOutline = AlwaysShowFocusOutline;

        s.Advanced.VerboseLogging = VerboseLogging;
        s.Advanced.DeveloperMode = DeveloperMode;
        s.Advanced.AllowFileRenaming = AllowFileRenaming;
        s.Advanced.MetadataAutosaveSeconds = Math.Max(3, MetadataAutosaveSeconds);

        Log.Verbose = VerboseLogging;
        _services.Images.BudgetBytes = s.Performance.ImageCacheMb * 1024L * 1024L;

        _services.Settings.Save();
    }

    // ---- reactions --------------------------------------------------------

    partial void OnThemeChanged(AppTheme value) { Save(); _services.Theme.Apply(); }
    partial void OnAccentColorChanged(string value) { Save(); _services.Theme.Apply(); }
    partial void OnHighContrastChanged(bool value) { Save(); _services.Theme.Apply(); }
    partial void OnColorBlindModeChanged(ColorBlindMode value) { Save(); _services.Theme.Apply(); }
    partial void OnUiScaleChanged(double value) { Save(); ScaleChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnLargeTextChanged(bool value) { Save(); ScaleChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnTextScaleChanged(double value) { Save(); ScaleChanged?.Invoke(this, EventArgs.Empty); }

    partial void OnStartWithWindowsChanged(bool value)
    {
        Save();
        ApplyRunAtStartup(value);
    }

    partial void OnWatchFoldersChanged(bool value)
    {
        Save();
        if (value) _services.Library.StartWatching();
        else _services.Library.StopWatching();
    }

    partial void OnShowSoundImagesChanged(bool value) { Save(); _main.RefreshResults(); }
    partial void OnFuzzyMatchingChanged(bool value) { Save(); _main.RefreshResults(); }
    partial void OnSearchFileNameChanged(bool value) { Save(); _main.RefreshResults(); }
    partial void OnSearchDisplayNameChanged(bool value) { Save(); _main.RefreshResults(); }
    partial void OnSearchTagsChanged(bool value) { Save(); _main.RefreshResults(); }
    partial void OnSearchGroupChanged(bool value) { Save(); _main.RefreshResults(); }

    partial void OnStartMinimizedChanged(bool value) => Save();
    partial void OnMinimizeToTrayChanged(bool value) => Save();
    partial void OnCloseToTrayChanged(bool value) => Save();
    partial void OnCheckForUpdatesChanged(bool value) => Save();
    partial void OnConfirmOnDeleteChanged(bool value) => Save();
    partial void OnEnableAnimationsChanged(bool value) => Save();
    partial void OnShowWaveformInPanelChanged(bool value) => Save();
    partial void OnPlaybackModeChanged(PlaybackMode value)
    {
        Save();
        OnPropertyChanged(nameof(AutoStopPrevious));
        _main.NotifyPlaybackModeChanged();
    }

    /// <summary>Plain-language view over the playback mode; see MainViewModel for the rationale.</summary>
    public bool AutoStopPrevious
    {
        // The property and the enum share a name, so the enum needs qualifying here.
        get => PlaybackMode == Core.Models.PlaybackMode.Solo;
        set => PlaybackMode = value
            ? Core.Models.PlaybackMode.Solo
            : Core.Models.PlaybackMode.Overlap;
    }
    partial void OnMaxSimultaneousSoundsChanged(int value) => Save();
    partial void OnStopOnSecondPressChanged(bool value) => Save();
    partial void OnDuckMicrophoneWhilePlayingChanged(bool value) => Save();
    partial void OnMicDuckAmountChanged(float value) => Save();
    partial void OnGlobalFadeOutMsChanged(int value) => Save();
    partial void OnRememberHistoryChanged(bool value) => Save();
    partial void OnImportBehaviorChanged(ImportBehavior value) => Save();
    partial void OnConflictActionChanged(ConflictAction value) => Save();
    partial void OnPreserveFolderStructureOnImportChanged(bool value) => Save();
    partial void OnAutoTagFromFolderNameChanged(bool value) => Save();
    partial void OnGroupFromSubfoldersByDefaultChanged(bool value) => Save();
    partial void OnDetectDuplicatesOnImportChanged(bool value) => Save();
    partial void OnScanOnStartupChanged(bool value) => Save();
    partial void OnRememberRecentSearchesChanged(bool value) => Save();
    partial void OnLazyLoadingChanged(bool value) => Save();
    partial void OnCacheImagesChanged(bool value) => Save();
    partial void OnPreloadFrequentSoundsChanged(bool value) => Save();
    partial void OnPreloadCountChanged(int value) => Save();
    partial void OnScanThreadsChanged(int value) => Save();
    partial void OnBackgroundIndexingChanged(bool value) => Save();
    partial void OnImageCacheMbChanged(int value) => Save();
    partial void OnShowToastsChanged(bool value) => Save();
    partial void OnNotifyOnMissingFilesChanged(bool value) => Save();
    partial void OnNotifyOnImportChanged(bool value) => Save();
    partial void OnNotifyOnDeviceChangeChanged(bool value) => Save();
    partial void OnScreenReaderHintsChanged(bool value) => Save();
    partial void OnReducedMotionChanged(bool value) => Save();
    partial void OnAlwaysShowFocusOutlineChanged(bool value) => Save();
    partial void OnVerboseLoggingChanged(bool value) => Save();
    partial void OnDeveloperModeChanged(bool value) => Save();
    partial void OnAllowFileRenamingChanged(bool value) => Save();
    partial void OnMetadataAutosaveSecondsChanged(int value) => Save();

    /// <summary>Raised when the window needs to re-apply its zoom transform.</summary>
    public event EventHandler? ScaleChanged;

    // ---- updates and storage location -------------------------------------

    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _isCheckingForUpdates;

    public string AppVersionText => $"Version {UpdateService.CurrentVersion.ToString(3)}";

    public bool IsPortable => AppPaths.IsPortable;

    public string DataLocationText => AppPaths.IsPortable
        ? $"Portable — everything is kept in {AppPaths.DataRoot}, so this folder can be moved " +
          "or copied to a USB stick and it will take your settings with it."
        : $"This install location is read-only, so settings are kept in {AppPaths.DataRoot} instead.";

    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        if (IsCheckingForUpdates) return;

        if (!UpdateService.IsConfigured)
        {
            UpdateStatus = "Update checking is not configured in this build.";
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatus = "Checking GitHub…";

        try
        {
            // ignoreSkipped: an explicit check should surface a version they skipped before.
            var update = await _services.Updates.CheckAsync(ignoreSkipped: true);

            if (update is null)
            {
                UpdateStatus = _services.Updates.LastError is null
                    ? $"You are up to date ({UpdateService.CurrentVersion.ToString(3)})."
                    : $"Could not check for updates: {_services.Updates.LastError}";
                return;
            }

            UpdateStatus = $"Version {update.Version.ToString(3)} is available.";

            var dialog = new Views.UpdateDialog(_services.Updates, update)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private static void OpenReleasesPage() => UpdateService.OpenReleasesPage();

    [RelayCommand]
    private void SetAccent(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex)) AccentColor = hex;
    }

    // ---- commands: library folders ----------------------------------------

    public void RefreshFolders()
    {
        Folders.Clear();

        var sounds = _services.Library.Sounds;

        foreach (var folder in _services.Settings.Settings.Library.Folders)
        {
            var count = sounds.Count(s =>
                s.FilePath.StartsWith(folder.Path, StringComparison.OrdinalIgnoreCase));

            var row = new FolderRowViewModel(folder, count);
            row.Changed += (_, _) =>
            {
                _services.Settings.Save();
                _services.Library.StartWatching();
            };
            row.RescanNeeded += async (_, _) =>
            {
                StatusText = "Building groups from subfolders…";
                await _main.RescanAsync();
                _main.BuildTree();
                StatusText = "Groups created from subfolder names.";
            };

            Folders.Add(row);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of sounds" };
        if (dialog.ShowDialog() != true) return;

        AddFolderPath(dialog.FolderName);
        await _main.RescanAsync();
        RefreshFolders();
    }

    /// <summary>Register a folder with the library, ignoring duplicates.</summary>
    public bool AddFolderPath(string path)
    {
        var folders = _services.Settings.Settings.Library.Folders;

        if (folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "That folder is already in your library.";
            return false;
        }

        folders.Add(new LibraryFolder
        {
            Path = path,
            Recursive = true,
            Watch = true,
            GroupFromSubfolders = _services.Settings.Settings.Library.GroupFromSubfoldersByDefault
        });

        _services.Settings.Save();
        StatusText = $"Added '{path}'.";
        return true;
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(FolderRowViewModel? row)
    {
        if (row is null) return;

        var confirm = MessageBox.Show(
            $"Stop watching this folder?\n\n{row.Path}\n\n" +
            $"Its {row.SoundCount:N0} sound(s) leave your library, along with their display " +
            "names, artwork and tags. Nothing on disk is touched — the audio files stay " +
            "exactly where they are, and you can add the folder back at any time.",
            "Remove folder", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var folders = _services.Settings.Settings.Library.Folders;
        folders.Remove(row.Folder);
        _services.Settings.Save();

        // The scan will not do this for us: it only treats a sound as gone if it still sits
        // under a watched folder, so removing one used to leave its sounds stranded.
        var removed = _services.Library.RemoveSoundsUnder(
            row.Path, folders.Select(f => f.Path));

        await _main.RescanAsync();
        _main.BuildTree();
        RefreshFolders();

        StatusText = removed > 0
            ? $"Removed '{row.Path}' and its {removed:N0} sound(s)."
            : $"Removed '{row.Path}'.";
    }

    [RelayCommand]
    private void ChooseMainLibrary()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where imported sounds should be copied",
            InitialDirectory = MainLibraryPath ?? AppPaths.DefaultMainLibrary
        };

        if (dialog.ShowDialog() != true) return;

        MainLibraryPath = dialog.FolderName;
        Save();
        _services.Import.EnsureMainLibraryPath();
        RefreshFolders();
        StatusText = $"Main sound library set to '{MainLibraryPath}'.";
    }

    [RelayCommand]
    private void OpenMainLibrary()
    {
        var path = _services.Import.EnsureMainLibraryPath();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void ApplyExtensions()
    {
        var extensions = ExtensionsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (extensions.Count == 0)
        {
            StatusText = "Keep at least one file type.";
            return;
        }

        _services.Settings.Settings.Library.Extensions = extensions;
        _services.Settings.Save();
        StatusText = "File types updated — rescan to pick up newly supported files.";
    }

    // ---- commands: library health -----------------------------------------

    [RelayCommand]
    private void CheckLibraryHealth()
    {
        var missing = _services.Library.FindMissing();
        var broken = _services.Library.FindBroken();
        var duplicates = _services.Library.FindDuplicates();

        MissingCount = missing.Count;
        BrokenCount = broken.Count;
        DuplicateGroupCount = duplicates.Count;

        HealthText = MissingCount == 0 && BrokenCount == 0 && DuplicateGroupCount == 0
            ? "Everything checks out — no missing, broken or duplicate sounds."
            : $"{MissingCount} missing · {BrokenCount} unreadable · " +
              $"{DuplicateGroupCount} duplicate group(s) ({duplicates.Sum(g => g.Count - 1)} extra copies)";
    }

    [RelayCommand]
    private void RepairMissing()
    {
        var repaired = _services.Library.RepairMissing();
        CheckLibraryHealth();
        StatusText = repaired == 0
            ? "No missing files came back. Check the drive or folder is connected."
            : $"Recovered {repaired} sound(s).";
    }

    [RelayCommand]
    private void RemoveMissing()
    {
        var missing = _services.Library.FindMissing();
        if (missing.Count == 0) { StatusText = "Nothing to remove."; return; }

        var confirm = MessageBox.Show(
            $"Remove {missing.Count} missing sound(s) from the library?\n\n" +
            "This only clears library entries whose files no longer exist.",
            "Remove missing", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _services.Library.RemoveFromLibrary(missing);
        CheckLibraryHealth();
        StatusText = $"Removed {missing.Count} missing sound(s).";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _services.Library.ClearHistory();
        StatusText = "Play history cleared.";
    }

    // ---- commands: caches -------------------------------------------------

    [RelayCommand]
    private void RefreshCacheStats()
    {
        var cache = _services.Engine.Cache;
        CacheStatsText =
            $"Audio cache: {cache.Count} sounds, {cache.UsedBytes / 1024 / 1024} MB  ·  " +
            $"Images: {_services.Images.Count} cached, {_services.Images.UsedBytes / 1024 / 1024} MB";
    }

    [RelayCommand]
    private void ClearCaches()
    {
        _services.Engine.Cache.Clear();
        _services.Images.Clear();
        WaveformGenerator.ClearCache();
        RefreshCacheStats();
        StatusText = "Caches cleared.";
    }

    [RelayCommand]
    private void PruneOrphanImages()
    {
        var removed = ImageStore.PruneOrphans(_services.Library.Sounds);
        StatusText = $"Removed {removed} unused thumbnail(s).";
    }

    // ---- commands: backup -------------------------------------------------

    public void RefreshBackups()
    {
        Backups.Clear();
        foreach (var file in _services.Backup.ListBackups()) Backups.Add(file);
    }

    [RelayCommand]
    private void ExportBackup()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export a full backup",
            Filter = "Project Soundboard backup|*.psbackup",
            FileName = $"soundboard-{DateTime.Now:yyyy-MM-dd}.psbackup"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            _services.Backup.ExportBundle(dialog.FileName);
            StatusText = $"Backup written to {dialog.FileName}.";
            RefreshBackups();
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportBackup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore from a backup",
            Filter = "Project Soundboard backup|*.psbackup|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;
        RestoreFrom(dialog.FileName);
    }

    [RelayCommand]
    private void RestoreBackup(FileInfo? file)
    {
        if (file is null) return;
        RestoreFrom(file.FullName);
    }

    private void RestoreFrom(string path)
    {
        var confirm = MessageBox.Show(
            "Restoring replaces your current settings and library metadata.\n\n" +
            "A safety copy of what you have now is taken first. Continue?",
            "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _services.Backup.ImportBundle(path);
            Load();
            RefreshFolders();
            RefreshBackups();
            _services.Theme.Apply();
            _services.Engine.ApplyAudioSettings();
            _main.BuildTree();
            _main.RefreshResults();
            StatusText = "Backup restored.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportSettings() => ExportTo("Settings|*.json", "settings.json",
        path => _services.Backup.ExportSettings(path));

    [RelayCommand]
    private void ExportGroups() => ExportTo("Groups|*.json", "groups.json",
        path => _services.Backup.ExportGroups(path));

    [RelayCommand]
    private void ExportDisplayNames() => ExportTo("CSV|*.csv", "display-names.csv",
        path => _services.Backup.ExportDisplayNames(path));

    private void ExportTo(string filter, string defaultName, Action<string> write)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName };
        if (dialog.ShowDialog() != true) return;

        try
        {
            write(dialog.FileName);
            StatusText = $"Exported to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportDisplayNames()
    {
        var dialog = new OpenFileDialog { Filter = "CSV|*.csv|All files|*.*" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var applied = _services.Backup.ImportDisplayNames(dialog.FileName);
            _main.RefreshResults();
            StatusText = $"Applied {applied} display name(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    // ---- commands: advanced / developer -----------------------------------

    [RelayCommand]
    private static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private static void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void RefreshDiagnostics()
    {
        var engine = _services.Engine;
        var process = Process.GetCurrentProcess();

        DiagnosticsText = string.Join(Environment.NewLine, new[]
        {
            $"Version            : {typeof(SettingsViewModel).Assembly.GetName().Version}",
            $"Data folder        : {AppPaths.DataRoot}",
            $"Sounds indexed     : {_services.Library.Count:N0}",
            $"Groups             : {_services.Library.Groups.Count}",
            $"Virtual mic bus    : {(engine.VirtualMicBus.IsRunning ? engine.VirtualMicBus.DeviceName : "stopped")}",
            $"Monitor bus        : {(engine.MonitorBus.IsRunning ? engine.MonitorBus.DeviceName : "stopped")}",
            $"Buffer / latency   : {engine.VirtualMicBus.LatencyMs} ms / ~{engine.EstimatedLatencyMs} ms",
            $"Mic passthrough    : {(_services.Microphone.IsRunning ? _services.Microphone.DeviceName : "off")}",
            $"Voices playing     : {engine.Active.Count}",
            $"Audio cache        : {engine.Cache.Count} sounds, {engine.Cache.UsedBytes / 1024 / 1024} MB",
            $"Image cache        : {_services.Images.Count} images, {_services.Images.UsedBytes / 1024 / 1024} MB",
            $"Working set        : {process.WorkingSet64 / 1024 / 1024} MB",
            $"Threads            : {process.Threads.Count}",
            $"Hotkey conflicts   : {_services.Hotkeys.Conflicts.Count}"
        });
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(DiagnosticsText);
            StatusText = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void RunSetupWizard()
    {
        var wizard = new Views.SetupWizardWindow { Owner = Application.Current.MainWindow };
        if (wizard.ShowDialog() != true) return;

        Load();
        RefreshFolders();
        _services.Theme.Apply();
        _services.StartAudio();
        _main.BuildTree();
        _main.RefreshResults();
    }

    [RelayCommand]
    private void ResetAllSettings()
    {
        var confirm = MessageBox.Show(
            "Reset every setting back to its default?\n\n" +
            "Your sounds, groups, custom names and images are not touched — only preferences.",
            "Reset settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _services.Backup.CreateAutomaticBackup("before-reset");

        var s = _services.Settings.Settings;
        var keepFolders = s.Library.Folders.ToList();
        var keepMain = s.Library.MainLibraryPath;

        s.General = new GeneralSettings();
        s.Appearance = new AppearanceSettings();
        s.Playback = new PlaybackSettings();
        s.Search = new SearchSettings();
        s.Performance = new PerformanceSettings();
        s.Notifications = new NotificationSettings();
        s.Accessibility = new AccessibilitySettings();
        s.Advanced = new AdvancedSettings();
        s.Library = new LibrarySettings { Folders = keepFolders, MainLibraryPath = keepMain };

        _services.Settings.Save();

        Load();
        _services.Theme.Apply();
        StatusText = "Settings reset. A backup was saved first.";
    }

    private void ApplyRunAtStartup(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "ProjectSoundboard";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update the run-at-startup entry: {ex.Message}");
            StatusText = "Could not update the Windows startup entry.";
        }
    }
}
