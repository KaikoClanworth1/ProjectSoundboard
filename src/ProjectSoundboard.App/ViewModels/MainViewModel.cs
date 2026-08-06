using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Audio;
using ProjectSoundboard.Audio.Playback;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.ViewModels;

public enum NavPage
{
    Library,
    Audio,
    Microphone,
    Hotkeys,
    Settings
}

/// <summary>
/// The shell view model: navigation, library browsing and search, the transport bar, and
/// everything the status strip reports.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Dictionary<string, SoundViewModel> _viewModelCache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _meterTimer;
    private readonly Random _random = new();

    private CancellationTokenSource? _scanCts;
    private bool _suppressSearch;
    private bool _disposed;

    public MainViewModel(AppServices services)
    {
        _services = services;

        Audio = new AudioSettingsViewModel(services);
        Microphone = new MicrophoneViewModel(services);
        Hotkeys = new HotkeysViewModel(services);
        Settings = new SettingsViewModel(services, this);
        Properties = new PropertiesViewModel(services, this);

        _viewMode = services.Settings.Settings.Appearance.ViewMode;
        _tileSize = services.Settings.Settings.Appearance.GridTileSize;
        _showProperties = services.Settings.Settings.Appearance.ShowPropertiesPanel;
        _masterVolume = services.Settings.Settings.Audio.MasterVolume;

        // Typing should feel instant but not re-filter 20,000 items on every keystroke.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RefreshResults();
            RequestScrollToTop();
        };

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateLiveState();
        _meterTimer.Start();

        services.Library.LibraryChanged += OnLibraryChanged;
        services.Library.ScanProgressChanged += OnScanProgress;
        services.Engine.PlaybackChanged += OnPlaybackChanged;
        services.Engine.PlaybackFailed += OnPlaybackFailed;
        services.Devices.DevicesChanged += OnDevicesChanged;

        RefreshCableBranding();
        BuildTree();
        RefreshResults();

        _ = InitialScanAsync();
    }

    public AudioSettingsViewModel Audio { get; }
    public MicrophoneViewModel Microphone { get; }
    public HotkeysViewModel Hotkeys { get; }
    public SettingsViewModel Settings { get; }
    public PropertiesViewModel Properties { get; }

    public AppServices Services => _services;

    /// <summary>Shown next to the title, so a bug report always carries a version with it.</summary>
    public string VersionText => "v" + UpdateService.CurrentVersion.ToString(3);

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private NavPage _currentPage = NavPage.Library;

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse<NavPage>(page, true, out var target)) CurrentPage = target;
    }

    // -----------------------------------------------------------------------
    // Library browsing
    // -----------------------------------------------------------------------

    public ObservableCollection<LibraryNode> Nodes { get; } = new();
    public ObservableCollection<SoundViewModel> Results { get; } = new();
    public ObservableCollection<string> RecentSearches { get; } = new();

    public IReadOnlyList<SortMode> SortModes { get; } = Enum.GetValues<SortMode>();

    /// <summary>The recent-search chips only earn their space once there is history to show.</summary>
    public bool ShowRecentSearches =>
        _services.Settings.Settings.Search.RememberRecentSearches
        && RecentSearches.Count > 0
        && string.IsNullOrEmpty(SearchText);

    public bool ShowEmptyState => ResultCount == 0;

    public string EmptyStateTitle => IsEmptyLibrary
        ? "Your library is empty"
        : !string.IsNullOrWhiteSpace(SearchText)
            ? "Nothing matched that search"
            : "Nothing here yet";

    public string EmptyStateMessage => IsEmptyLibrary
        ? "Point Project Soundboard at a folder of audio files, or just drag files onto this window."
        : !string.IsNullOrWhiteSpace(SearchText)
            ? "Try a shorter search, or check the spelling. Fuzzy matching is on by default."
            : "This group has no sounds in it. Drag sounds onto it in the sidebar to move them here.";

    [ObservableProperty]
    private LibraryNode? _selectedNode;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SoundViewModel? _selectedSound;

    [ObservableProperty]
    private LibraryViewMode _viewMode;

    [ObservableProperty]
    private double _tileSize;

    [ObservableProperty]
    private SortMode _sortMode = SortMode.Name;

    [ObservableProperty]
    private bool _showProperties;

    [ObservableProperty]
    private string _breadcrumb = "All sounds";

    [ObservableProperty]
    private int _resultCount;

    [ObservableProperty]
    private bool _isEmptyLibrary;

    /// <summary>
    /// Raised only when the *query* changes, so the list should genuinely start from the
    /// top. Editing a sound refreshes the same list in place and must keep your position.
    /// </summary>
    public event EventHandler? ScrollToTopRequested;

    private void RequestScrollToTop() => ScrollToTopRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedNodeChanged(LibraryNode? value)
    {
        Breadcrumb = BuildBreadcrumb(value);
        RefreshResults();
        RequestScrollToTop();
    }

    partial void OnSortModeChanged(SortMode value)
    {
        RefreshResults();
        RequestScrollToTop();
    }

    partial void OnSelectedSoundChanged(SoundViewModel? oldValue, SoundViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Properties.Load(newValue);
    }

    partial void OnViewModeChanged(LibraryViewMode value)
    {
        _services.Settings.Settings.Appearance.ViewMode = value;
        _services.Settings.MarkDirty();
    }

    partial void OnTileSizeChanged(double value)
    {
        _services.Settings.Settings.Appearance.GridTileSize = value;
        _services.Settings.MarkDirty();
    }

    partial void OnShowPropertiesChanged(bool value)
    {
        _services.Settings.Settings.Appearance.ShowPropertiesPanel = value;
        _services.Settings.MarkDirty();
    }

    private string BuildBreadcrumb(LibraryNode? node)
    {
        if (node is null) return "All sounds";
        if (node.Kind != LibraryNodeKind.Group) return node.Name;

        var parts = new List<string>();
        var group = node.Group;

        while (group is not null)
        {
            parts.Insert(0, group.Name);
            group = group.ParentId is null ? null : _services.Library.GetGroup(group.ParentId);
        }

        parts.Insert(0, "Library");
        return string.Join("  ›  ", parts);
    }

    /// <summary>Rebuild the sidebar from the current groups.</summary>
    public void BuildTree()
    {
        var previousId = SelectedNode?.GroupId;
        var previousKind = SelectedNode?.Kind ?? LibraryNodeKind.AllSounds;

        Nodes.Clear();

        Nodes.Add(new LibraryNode(LibraryNodeKind.AllSounds, "All sounds", ""));
        Nodes.Add(new LibraryNode(LibraryNodeKind.Favorites, "Favorites", ""));
        Nodes.Add(new LibraryNode(LibraryNodeKind.RecentlyPlayed, "Recently played", ""));
        Nodes.Add(new LibraryNode(LibraryNodeKind.MostPlayed, "Most played", ""));
        Nodes.Add(new LibraryNode(LibraryNodeKind.RecentlyAdded, "Recently added", ""));

        var groups = _services.Library.Groups;
        var byParent = groups.GroupBy(g => g.ParentId ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.SortOrder).ToList());

        void AddChildren(LibraryNode parent, string parentId)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;

            foreach (var group in children)
            {
                var node = new LibraryNode(LibraryNodeKind.Group, group.Name, "", group);
                parent.Children.Add(node);
                AddChildren(node, group.Id);
            }
        }

        if (byParent.TryGetValue(string.Empty, out var roots))
        {
            foreach (var group in roots)
            {
                var node = new LibraryNode(LibraryNodeKind.Group, group.Name, "", group);
                AddChildren(node, group.Id);
                Nodes.Add(node);
            }
        }

        var problems = _services.Library.FindMissing().Count + _services.Library.FindBroken().Count;
        if (problems > 0)
        {
            Nodes.Add(new LibraryNode(LibraryNodeKind.Problems, "Needs attention", "")
            {
                Count = problems
            });
        }

        UpdateNodeCounts();

        SelectedNode = previousId is not null
            ? FindNode(Nodes, n => n.GroupId == previousId) ?? Nodes[0]
            : FindNode(Nodes, n => n.Kind == previousKind) ?? Nodes[0];
    }

    private static LibraryNode? FindNode(IEnumerable<LibraryNode> nodes, Func<LibraryNode, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (predicate(node)) return node;
            var hit = FindNode(node.Children, predicate);
            if (hit is not null) return hit;
        }
        return null;
    }

    private void UpdateNodeCounts()
    {
        var sounds = _services.Library.Sounds;

        foreach (var node in Flatten(Nodes))
        {
            node.Count = node.Kind switch
            {
                LibraryNodeKind.AllSounds => sounds.Count,
                LibraryNodeKind.Favorites => sounds.Count(s => s.IsFavorite),
                LibraryNodeKind.RecentlyPlayed => sounds.Count(s => s.LastPlayedUtc is not null),
                LibraryNodeKind.MostPlayed => sounds.Count(s => s.PlayCount > 0),
                LibraryNodeKind.RecentlyAdded => Math.Min(sounds.Count, 100),
                LibraryNodeKind.Problems => sounds.Count(s => s.IsMissing || s.IsBroken),
                LibraryNodeKind.Group when node.GroupId is not null =>
                    sounds.Count(s => s.GroupId is not null &&
                                      _services.Library.GetGroupTree(node.GroupId).Contains(s.GroupId)),
                _ => 0
            };
        }
    }

    private static IEnumerable<LibraryNode> Flatten(IEnumerable<LibraryNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>
    /// Raised either side of a results rebuild. The view uses them to hold the scroll
    /// position: rebuilding empties the collection first, and an empty list has no extent
    /// to scroll within, so the offset would otherwise be clamped away to zero.
    /// </summary>
    public event EventHandler? ResultsRefreshing;

    public event EventHandler? ResultsRefreshed;

    /// <summary>Re-run the current filter and repopulate the results list.</summary>
    public void RefreshResults()
    {
        ResultsRefreshing?.Invoke(this, EventArgs.Empty);

        var node = SelectedNode;

        var query = new SearchQuery
        {
            Text = SearchText,
            Sort = SortMode,
            GroupId = node?.Kind == LibraryNodeKind.Group ? node.GroupId : null,
            FavoritesOnly = node?.Kind == LibraryNodeKind.Favorites,
            MissingOnly = node?.Kind == LibraryNodeKind.Problems
        };

        if (node?.Kind == LibraryNodeKind.RecentlyPlayed) query.Sort = SortMode.RecentlyPlayed;
        else if (node?.Kind == LibraryNodeKind.MostPlayed) query.Sort = SortMode.MostPlayed;
        else if (node?.Kind == LibraryNodeKind.RecentlyAdded) query.Sort = SortMode.RecentlyAdded;

        var matches = _services.Search.Execute(query);

        if (node?.Kind == LibraryNodeKind.RecentlyPlayed)
            matches = matches.Where(s => s.LastPlayedUtc is not null).ToArray();
        else if (node?.Kind == LibraryNodeKind.MostPlayed)
            matches = matches.Where(s => s.PlayCount > 0).ToArray();
        else if (node?.Kind == LibraryNodeKind.RecentlyAdded)
            matches = matches.Take(100).ToArray();

        var previousSelection = SelectedSound?.Id;

        Results.Clear();
        foreach (var entry in matches) Results.Add(GetOrCreate(entry));

        ResultCount = Results.Count;
        IsEmptyLibrary = _services.Library.Count == 0;

        if (previousSelection is not null)
        {
            var restored = Results.FirstOrDefault(r => r.Id == previousSelection);
            if (restored is not null) SelectedSound = restored;
        }

        SyncPlayingState();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _services.Search.RememberSearch(SearchText);
            SyncRecentSearches();
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowRecentSearches));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));

        ResultsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private void SyncRecentSearches()
    {
        RecentSearches.Clear();
        foreach (var s in _services.Settings.Settings.Search.RecentSearches) RecentSearches.Add(s);
        OnPropertyChanged(nameof(ShowRecentSearches));
    }

    private SoundViewModel GetOrCreate(SoundEntry entry)
    {
        if (_viewModelCache.TryGetValue(entry.Id, out var existing))
        {
            existing.GroupName = _services.Library.GetGroup(entry.GroupId)?.Name;
            return existing;
        }

        var vm = new SoundViewModel(entry, _services.Images,
            _services.Library.GetGroup(entry.GroupId)?.Name);

        _viewModelCache[entry.Id] = vm;
        return vm;
    }

    /// <summary>Look up the wrapper for an entry, creating it if the tile is off screen.</summary>
    public SoundViewModel? FindViewModel(string soundId)
    {
        if (_viewModelCache.TryGetValue(soundId, out var vm)) return vm;
        var entry = _services.Library.GetById(soundId);
        return entry is null ? null : GetOrCreate(entry);
    }

    // -----------------------------------------------------------------------
    // Scanning
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanStatus;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    private async Task InitialScanAsync()
    {
        if (!_services.Settings.Settings.Library.ScanOnStartup) return;
        if (_services.Settings.Settings.Library.Folders.Count == 0) return;

        await RescanAsync();

        _services.Library.StartWatching();
        await _services.Engine.PreloadAsync(_services.Library.Sounds);
    }

    [RelayCommand]
    public async Task RescanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        try
        {
            await _services.Library.ScanAsync(_scanCts.Token);
        }
        finally
        {
            IsScanning = false;
            ScanStatus = null;
        }
    }

    private void OnScanProgress(object? sender, ScanProgress e)
    {
        Dispatch(() =>
        {
            if (e.IsComplete)
            {
                ScanStatus = null;
                StatusMessage = $"{_services.Library.Count:N0} sounds  ·  " +
                                $"{e.Added} added, {e.Removed} removed";
                return;
            }

            ScanStatus = e.CurrentFolder is not null
                ? $"Scanning {Path.GetFileName(e.CurrentFolder.TrimEnd('\\'))}…"
                : $"Indexing {e.Processed:N0} of {e.Discovered:N0}…";
        });
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            BuildTree();
            RefreshResults();
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            Audio.RefreshDevices();
            Microphone.RefreshDevices();
            RefreshCableBranding();

            if (_services.Settings.Settings.Notifications.NotifyOnDeviceChange)
                StatusMessage = "Audio devices changed.";
        });
    }

    // -----------------------------------------------------------------------
    // Playback
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private float _masterVolume;

    [ObservableProperty]
    private bool _soundboardMuted;

    [ObservableProperty]
    private int _playingCount;

    partial void OnMasterVolumeChanged(float value)
    {
        _services.Engine.SetMasterVolume(value);
        _services.Settings.MarkDirty();
    }

    /// <summary>
    /// "Only one sound at a time" — a plain toggle over the playback mode, so the common
    /// choice does not need a trip into a settings dropdown. Settings owns the underlying
    /// value; this is a view onto it, which keeps the two screens from disagreeing.
    /// </summary>
    public bool AutoStopPrevious
    {
        get => Settings.PlaybackMode == PlaybackMode.Solo;
        set => Settings.PlaybackMode = value ? PlaybackMode.Solo : PlaybackMode.Overlap;
    }

    /// <summary>Called by the settings page when the playback mode changes there.</summary>
    public void NotifyPlaybackModeChanged() => OnPropertyChanged(nameof(AutoStopPrevious));

    [RelayCommand]
    private void ToggleAutoStopPrevious() => AutoStopPrevious = !AutoStopPrevious;

    partial void OnSoundboardMutedChanged(bool value) => _services.Engine.SetSoundboardMuted(value);

    [RelayCommand]
    public void Play(SoundViewModel? sound)
    {
        if (sound is null) return;

        if (sound.IsMissing)
        {
            StatusMessage = $"'{sound.DisplayName}' is missing from disk.";
            return;
        }

        var handle = _services.Engine.Play(sound.Entry);
        if (handle is null) return;

        _services.Library.RecordPlayed(sound.Entry);
        sound.RefreshAll();
        SyncPlayingState();
    }

    [RelayCommand]
    private void StopSound(SoundViewModel? sound)
    {
        if (sound is null) return;
        _services.Engine.StopSound(sound.Id);
    }

    [RelayCommand]
    private void StopAll() => _services.Engine.StopAll();

    [RelayCommand]
    private void TogglePause() => _services.Engine.TogglePauseAll();

    [RelayCommand]
    private void ToggleSoundboardMute() => SoundboardMuted = !SoundboardMuted;

    [RelayCommand]
    private void PlayRandom()
    {
        var pool = Results.Where(r => !r.HasProblem).ToList();
        if (pool.Count == 0) return;
        Play(pool[_random.Next(pool.Count)]);
    }

    [RelayCommand]
    private void PlayNext() => Step(1);

    [RelayCommand]
    private void PlayPrevious() => Step(-1);

    private void Step(int direction)
    {
        if (Results.Count == 0) return;

        var index = SelectedSound is null ? -1 : Results.IndexOf(SelectedSound);
        index = ((index + direction) % Results.Count + Results.Count) % Results.Count;

        SelectedSound = Results[index];
        Play(SelectedSound);
    }

    private void OnPlaybackChanged(object? sender, EventArgs e) => Dispatch(SyncPlayingState);

    private void OnPlaybackFailed(object? sender, string message) =>
        Dispatch(() => StatusMessage = message);

    private void SyncPlayingState()
    {
        var active = _services.Engine.Active;
        PlayingCount = active.Count;

        var playing = active.Select(h => h.SoundId).ToHashSet(StringComparer.Ordinal);

        foreach (var vm in Results)
        {
            var isPlaying = playing.Contains(vm.Id);
            if (vm.IsPlaying != isPlaying) vm.IsPlaying = isPlaying;
            if (!isPlaying && vm.Progress != 0) vm.Progress = 0;
        }
    }

    // -----------------------------------------------------------------------
    // Live status (meters, progress)
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private double _virtualMicPeak;

    [ObservableProperty]
    private double _virtualMicRms;

    [ObservableProperty]
    private double _monitorPeak;

    [ObservableProperty]
    private double _micInputPeak;

    [ObservableProperty]
    private double _micOutputPeak;

    [ObservableProperty]
    private bool _isTalking;

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private string _virtualMicDeviceText = "Not configured";

    /// <summary>Real Windows device name, shown on hover so the alias never hides anything.</summary>
    [ObservableProperty]
    private string? _virtualMicTooltip;

    [ObservableProperty]
    private bool _virtualMicHealthy;

    /// <summary>
    /// Cached so the 20 Hz status refresh does not re-enumerate every audio endpoint.
    /// Refreshed when devices change or the user picks a different output.
    /// </summary>
    private string? _cableOutputName;

    private void RefreshCableBranding()
    {
        var cable = VirtualCable.Detect(_services.Devices,
            _services.Settings.Settings.Audio.VirtualMicDeviceId);

        _cableOutputName = cable?.Output.Name;
    }

    private void UpdateLiveState()
    {
        if (_disposed) return;

        var engine = _services.Engine;

        VirtualMicPeak = engine.VirtualMicBus.Meter?.Peak ?? 0;
        VirtualMicRms = engine.VirtualMicBus.Meter?.Rms ?? 0;
        MonitorPeak = engine.MonitorBus.Meter?.Peak ?? 0;

        MicInputPeak = _services.Microphone.InputMeter.Peak;
        MicOutputPeak = _services.Microphone.OutputMeter.Peak;
        IsTalking = _services.Microphone.IsRunning && _services.Microphone.IsTalking;

        VirtualMicHealthy = engine.VirtualMicBus.IsRunning;

        // Show our own label for the cable, but keep the real name one hover away.
        var realName = engine.VirtualMicBus.DeviceName;
        var isCable = realName is not null && realName == _cableOutputName;

        VirtualMicDeviceText = isCable
            ? VirtualCable.OutputAlias
            : realName ?? "Not configured";

        VirtualMicTooltip = isCable ? $"Windows device: {realName}" : realName;
        LatencyText = engine.VirtualMicBus.IsRunning || engine.MonitorBus.IsRunning
            ? $"{engine.EstimatedLatencyMs} ms"
            : "—";

        if (PlayingCount > 0)
        {
            foreach (var handle in engine.Active)
            {
                var vm = _viewModelCache.TryGetValue(handle.SoundId, out var found) ? found : null;
                if (vm is null) continue;
                vm.Progress = handle.Progress;
                vm.IsPaused = handle.IsPaused;
            }
        }

        Properties.UpdateLive();
        Audio.UpdateLive();
        Microphone.UpdateLive();

        _services.Settings.FlushIfDue();
        _services.Library.SaveIfDirty();
    }

    // -----------------------------------------------------------------------
    // Sound actions
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void ToggleFavorite(SoundViewModel? sound)
    {
        if (sound is null) return;
        sound.IsFavorite = !sound.IsFavorite;
        _services.Library.NotifyChanged();
    }

    [RelayCommand]
    private void OpenFileLocation(SoundViewModel? sound)
    {
        if (sound is null) return;

        try
        {
            if (File.Exists(sound.FilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{sound.FilePath}\"")
                {
                    UseShellExecute = true
                });
            }
            else if (sound.Directory is not null && Directory.Exists(sound.Directory))
            {
                Process.Start(new ProcessStartInfo(sound.Directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveFromLibrary(SoundViewModel? sound)
    {
        if (sound is null) return;

        if (_services.Settings.Settings.General.ConfirmOnDelete)
        {
            var result = MessageBox.Show(
                $"Remove '{sound.DisplayName}' from the library?\n\n" +
                "The file stays on your disk — only the library entry, custom name, image " +
                "and settings are removed.",
                "Remove from library", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (result != MessageBoxResult.OK) return;
        }

        _services.Library.RemoveFromLibrary(new[] { sound.Entry });
        _viewModelCache.Remove(sound.Id);
        if (SelectedSound == sound) SelectedSound = null;
    }

    /// <summary>
    /// Same import flow as drag and drop, reached from a file picker. Dragging is faster but
    /// it is not discoverable, and it is awkward with a screen reader.
    /// </summary>
    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var extensions = string.Join(";", _services.Settings.Settings.Library.Extensions
            .Select(e => "*" + e));

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose sound files to import",
            Multiselect = true,
            Filter = $"Audio files|{extensions}|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var owner = Application.Current.MainWindow;
        if (owner is null) return;

        await Views.ImportFlow.RunAsync(owner, _services, dialog.FileNames, this);
    }

    /// <summary>Create a hotkey bound to this sound and jump to the hotkeys page to capture it.</summary>
    [RelayCommand]
    private void AssignHotkey(SoundViewModel? sound)
    {
        if (sound is null) return;

        var row = Hotkeys.AddForSound(sound);
        CurrentPage = NavPage.Hotkeys;
        Hotkeys.BeginCaptureCommand.Execute(row);
    }

    [RelayCommand]
    private void CreateGroup()
    {
        var parentId = SelectedNode?.Kind == LibraryNodeKind.Group ? SelectedNode.GroupId : null;
        var group = _services.Library.CreateGroup("New group", parentId);

        BuildTree();
        SelectedNode = FindNode(Nodes, n => n.GroupId == group.Id);
    }

    [RelayCommand]
    private void DeleteGroup(LibraryNode? node)
    {
        if (node?.Group is null) return;

        var result = MessageBox.Show(
            $"Delete the group '{node.Name}'?\n\n" +
            "Sounds inside it stay in your library and move up to the parent group.",
            "Delete group", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (result != MessageBoxResult.OK) return;

        _services.Library.DeleteGroup(node.Group);
        BuildTree();
    }

    /// <summary>Pick a group from a list and move the sound into it.</summary>
    [RelayCommand]
    private void MoveToGroup(SoundViewModel? sound)
    {
        if (sound is null) return;

        var groups = _services.Library.Groups;
        if (groups.Count == 0)
        {
            StatusMessage = "There are no groups yet — make one with the + button in the sidebar.";
            return;
        }

        var dialog = new Views.GroupPickerWindow(groups, sound.Entry.GroupId)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true) return;

        AssignToGroup(new[] { sound }, dialog.SelectedGroupId);
    }

    /// <summary>Take a sound back out of its group without removing it from the library.</summary>
    [RelayCommand]
    private void RemoveFromGroup(SoundViewModel? sound)
    {
        if (sound is null) return;

        if (sound.Entry.GroupId is null)
        {
            StatusMessage = $"'{sound.DisplayName}' is not in a group.";
            return;
        }

        AssignToGroup(new[] { sound }, null);
    }

    /// <summary>Move sounds into a group — used by drag and drop in the sidebar.</summary>
    public void AssignToGroup(IEnumerable<SoundViewModel> sounds, string? groupId)
    {
        var entries = sounds.Select(s => s.Entry).ToList();
        if (entries.Count == 0) return;

        _services.Library.AssignGroup(entries, groupId);
        StatusMessage = groupId is null
            ? $"{entries.Count} sound(s) removed from their group."
            : $"{entries.Count} sound(s) moved to '{_services.Library.GetGroup(groupId)?.Name}'.";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _suppressSearch = true;
        SearchText = string.Empty;
        _suppressSearch = false;
        RefreshResults();
    }

    [RelayCommand]
    private void UseRecentSearch(string? text)
    {
        if (text is null) return;
        SearchText = text;
    }

    [RelayCommand]
    private void CycleViewMode()
    {
        ViewMode = ViewMode switch
        {
            LibraryViewMode.Grid => LibraryViewMode.Compact,
            LibraryViewMode.Compact => LibraryViewMode.List,
            _ => LibraryViewMode.Grid
        };
    }

    [RelayCommand]
    private void TogglePropertiesPanel() => ShowProperties = !ShowProperties;

    // -----------------------------------------------------------------------
    // Global hotkeys
    // -----------------------------------------------------------------------

    public void HandleHotkey(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case HotkeyAction.PlaySound when binding.SoundId is not null:
            {
                var vm = FindViewModel(binding.SoundId);
                if (vm is not null) Play(vm);
                break;
            }

            case HotkeyAction.StopAll: _services.Engine.StopAll(); break;
            case HotkeyAction.PauseResume: _services.Engine.TogglePauseAll(); break;
            case HotkeyAction.Next: PlayNext(); break;
            case HotkeyAction.Previous: PlayPrevious(); break;
            case HotkeyAction.Random: PlayRandom(); break;
            case HotkeyAction.MuteSoundboard: ToggleSoundboardMute(); break;

            case HotkeyAction.MuteMicrophone:
                Microphone.Muted = !Microphone.Muted;
                break;

            case HotkeyAction.TogglePassthrough:
                Microphone.TogglePassthroughCommand.Execute(null);
                break;

            case HotkeyAction.VolumeUp:
                MasterVolume = Math.Clamp(MasterVolume + 0.05f, 0f, 1f);
                break;

            case HotkeyAction.VolumeDown:
                MasterVolume = Math.Clamp(MasterVolume - 0.05f, 0f, 1f);
                break;

            case HotkeyAction.ShowHideWindow:
                ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public event EventHandler? ToggleWindowRequested;

    public void HandlePushToTalk(bool held) => _services.Microphone.PushToTalkHeld = held;

    // -----------------------------------------------------------------------

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _meterTimer.Stop();
        _searchDebounce.Stop();
        _scanCts?.Cancel();

        _services.Library.LibraryChanged -= OnLibraryChanged;
        _services.Library.ScanProgressChanged -= OnScanProgress;
        _services.Engine.PlaybackChanged -= OnPlaybackChanged;
        _services.Engine.PlaybackFailed -= OnPlaybackFailed;
        _services.Devices.DevicesChanged -= OnDevicesChanged;

        Properties.Dispose();
    }
}
