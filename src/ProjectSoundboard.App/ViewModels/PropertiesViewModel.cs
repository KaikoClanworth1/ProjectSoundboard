using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Audio;
using ProjectSoundboard.Audio.Playback;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>
/// The right hand properties panel: everything about one sound, editable in place.
/// Changes are applied to the entry immediately and persisted on the autosave timer.
/// </summary>
public sealed partial class PropertiesViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    private CancellationTokenSource? _waveformCts;
    private PlaybackHandle? _preview;
    private bool _loading;

    public PropertiesViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    [ObservableProperty]
    private SoundViewModel? _sound;

    public bool HasSound => Sound is not null;

    // ---- editable fields --------------------------------------------------

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string? _emoji;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private string? _selectedGroupId;

    [ObservableProperty] private float _volume = 1f;
    [ObservableProperty] private float _speed = 1f;
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private int _fadeInMs;
    [ObservableProperty] private int _fadeOutMs;
    [ObservableProperty] private double _trimStartSeconds;
    [ObservableProperty] private double _trimEndSeconds;
    [ObservableProperty] private bool _normalize;

    // ---- live state -------------------------------------------------------

    [ObservableProperty] private WaveformData? _waveform;
    [ObservableProperty] private bool _isWaveformLoading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _positionText = "0:00";

    public IReadOnlyList<SoundGroup> AvailableGroups => _services.Library.Groups;

    public BitmapSource? Thumbnail => Sound?.Thumbnail;

    // ---- keybind ----------------------------------------------------------

    /// <summary>The keybind on this sound, shown on its own row so it is not hidden away.</summary>
    public string HotkeyText =>
        Sound is null ? "—" : _main.HotkeyTextFor(Sound.Id) ?? "Not set";

    public bool HasHotkey => Sound is not null && _main.HotkeyTextFor(Sound.Id) is not null;

    [RelayCommand]
    private void SetHotkey()
    {
        if (Sound is null) return;

        _main.SetSoundHotkey(Sound);
        RefreshHotkey();
    }

    public void RefreshHotkey()
    {
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(HasHotkey));
    }

    /// <summary>Load a sound into the panel, or clear it when null.</summary>
    public void Load(SoundViewModel? sound)
    {
        StopPreview();

        _loading = true;
        Sound = sound;

        if (sound is null)
        {
            Waveform = null;
            _loading = false;
            OnPropertyChanged(nameof(HasSound));
            return;
        }

        var entry = sound.Entry;

        DisplayName = entry.DisplayName;
        Emoji = entry.Emoji;
        TagsText = string.Join(", ", entry.Tags);
        SelectedGroupId = entry.GroupId;

        Volume = entry.Volume;
        Speed = entry.Speed;
        Loop = entry.Loop;
        FadeInMs = entry.FadeInMs;
        FadeOutMs = entry.FadeOutMs;
        TrimStartSeconds = entry.TrimStartMs / 1000.0;
        TrimEndSeconds = entry.TrimEndMs / 1000.0;
        Normalize = entry.Normalize;

        _loading = false;

        OnPropertyChanged(nameof(HasSound));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(AvailableGroups));
        RefreshHotkey();

        LoadWaveform(entry.FilePath);
    }

    private void LoadWaveform(string path)
    {
        _waveformCts?.Cancel();
        _waveformCts = new CancellationTokenSource();
        var token = _waveformCts.Token;

        Waveform = null;

        if (!_services.Settings.Settings.Appearance.ShowWaveformInPanel) return;
        if (!File.Exists(path)) return;

        IsWaveformLoading = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var data = await WaveformGenerator.GenerateAsync(path, ct: token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Waveform = data;
                    IsWaveformLoading = false;
                });
            }
            catch (OperationCanceledException) { /* superseded by a newer selection */ }
            catch (Exception ex)
            {
                Log.Debug($"Waveform load failed: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(() => IsWaveformLoading = false);
            }
        }, token);
    }

    // ---- property change plumbing ----------------------------------------

    /// <summary>
    /// Apply an edit to the selected sound.
    ///
    /// <paramref name="affectsList"/> should only be true for things that change which
    /// sounds appear or in what order — the name, tags or group. Everything else skips the
    /// library-wide refresh, which for a large library is the difference between a slider
    /// that glides and one that stutters and throws you back to the top of the page.
    /// </summary>
    private void Apply(Action<SoundEntry> mutate, bool affectsList = false, bool refreshTile = true)
    {
        if (_loading || Sound is null) return;

        mutate(Sound.Entry);

        if (affectsList) _services.Library.NotifyChanged();
        else _services.Library.MarkMetadataDirty();

        if (refreshTile) Sound.RefreshAll();
    }

    // ---- edits that change what the list shows -----------------------------

    partial void OnDisplayNameChanged(string value)
    {
        Apply(e => e.CustomName = string.IsNullOrWhiteSpace(value) ||
                                  value == e.OriginalNameWithoutExtension
            ? null
            : value.Trim(), affectsList: true);
    }

    partial void OnTagsTextChanged(string value)
    {
        Apply(e =>
        {
            e.Tags.Clear();
            e.Tags.AddRange(value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }, affectsList: true);
    }

    partial void OnSelectedGroupIdChanged(string? value) =>
        Apply(e => e.GroupId = value, affectsList: true);

    // ---- edits that only affect this sound's tile --------------------------

    partial void OnEmojiChanged(string? value) =>
        Apply(e => e.Emoji = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    // ---- playback settings: invisible to the list, so nothing is rebuilt ----

    /// <summary>
    /// Every currently playing instance of this sound — the panel's own preview and any
    /// normal triggers. Live tweaks have to reach all of them; only touching the preview
    /// meant that adjusting volume while a sound was actually playing did nothing.
    /// </summary>
    private IEnumerable<PlaybackHandle> LiveHandles()
    {
        if (Sound is null) yield break;

        if (_preview is { IsCompleted: false }) yield return _preview;

        foreach (var handle in _services.Engine.Active)
        {
            if (handle.SoundId != Sound.Id || handle.IsCompleted) continue;
            if (ReferenceEquals(handle, _preview)) continue;
            yield return handle;
        }
    }

    partial void OnVolumeChanged(float value)
    {
        Apply(e => e.Volume = value, refreshTile: false);
        foreach (var handle in LiveHandles()) handle.SetVolume(value);
    }

    partial void OnSpeedChanged(float value)
    {
        Apply(e => e.Speed = value, refreshTile: false);
        foreach (var handle in LiveHandles()) handle.SetSpeed(value);
    }

    partial void OnLoopChanged(bool value)
    {
        Apply(e => e.Loop = value, refreshTile: false);

        // Take effect on a sound that is already playing, the same way volume and speed do,
        // and keep the transport bar's loop button showing the same state.
        foreach (var handle in LiveHandles()) handle.SetLoop(value);
        if (!_loading && Sound is not null) _main.NotifyLoopChanged();
    }

    /// <summary>Reflect a loop change made from the transport bar, without writing it back.</summary>
    public void NotifyLoopChanged(bool loop)
    {
        if (Loop == loop) return;

        _loading = true;
        Loop = loop;
        _loading = false;
    }

    partial void OnFadeInMsChanged(int value) =>
        Apply(e => e.FadeInMs = Math.Max(0, value), refreshTile: false);

    partial void OnFadeOutMsChanged(int value) =>
        Apply(e => e.FadeOutMs = Math.Max(0, value), refreshTile: false);

    partial void OnNormalizeChanged(bool value) => Apply(e => e.Normalize = value, refreshTile: false);

    partial void OnTrimStartSecondsChanged(double value) =>
        Apply(e => e.TrimStartMs = (int)Math.Max(0, value * 1000), refreshTile: false);

    partial void OnTrimEndSecondsChanged(double value) =>
        Apply(e => e.TrimEndMs = (int)Math.Max(0, value * 1000), refreshTile: false);

    // ---- commands ---------------------------------------------------------

    [RelayCommand]
    private void Preview()
    {
        if (Sound is null) return;

        StopPreview();
        _preview = _services.Engine.Preview(Sound.Entry);
    }

    [RelayCommand]
    private void PlayEverywhere()
    {
        if (Sound is null) return;
        _main.PlayCommand.Execute(Sound);
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_preview is null) return;
        if (_preview.IsPaused) _preview.Resume();
        else _preview.Pause();
    }

    [RelayCommand]
    private void Restart() => _preview?.Restart();

    [RelayCommand]
    private void StopPreview()
    {
        _preview?.Stop(20);
        _preview = null;
        Progress = 0;
        IsPlaying = false;
        IsPaused = false;
    }

    [RelayCommand]
    private void ResetName()
    {
        if (Sound is null) return;
        DisplayName = Sound.Entry.OriginalNameWithoutExtension;
        Apply(e => e.CustomName = null);
    }

    [RelayCommand]
    private void ChooseImage()
    {
        if (Sound is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose an image for this sound",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;
        SetImageFromFile(dialog.FileName);
    }

    /// <summary>Copy an image file into the thumbnail cache (also used by drag and drop).</summary>
    public void SetImageFromFile(string path)
    {
        if (Sound is null) return;

        if (!ImageStore.IsSupportedImage(path))
        {
            MessageBox.Show("That file type is not a supported image.", "Unsupported image",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _services.Images.Invalidate(Sound.Entry.ImagePath);
        ImageStore.Store(Sound.Entry, path);
        _services.Library.NotifyChanged();

        Sound.RefreshAll();
        OnPropertyChanged(nameof(Thumbnail));
    }

    [RelayCommand]
    private void PasteImage()
    {
        if (Sound is null) return;

        try
        {
            if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is null) return;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));

                using var stream = new MemoryStream();
                encoder.Save(stream);

                _services.Images.Invalidate(Sound.Entry.ImagePath);
                ImageStore.Store(Sound.Entry, stream.ToArray());
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string?>().FirstOrDefault();
                if (file is null || !ImageStore.IsSupportedImage(file)) return;

                _services.Images.Invalidate(Sound.Entry.ImagePath);
                ImageStore.Store(Sound.Entry, file);
            }
            else
            {
                return;
            }

            _services.Library.NotifyChanged();
            Sound.RefreshAll();
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not paste the image: {ex.Message}", "Paste failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveImage()
    {
        if (Sound is null) return;

        _services.Images.Invalidate(Sound.Entry.ImagePath);
        ImageStore.Remove(Sound.Entry);
        _services.Library.NotifyChanged();

        Sound.RefreshAll();
        OnPropertyChanged(nameof(Thumbnail));
    }

    [RelayCommand]
    private void ResetPlaybackSettings()
    {
        if (Sound is null) return;

        _loading = true;
        Volume = 1f;
        Speed = 1f;
        Loop = false;
        FadeInMs = 0;
        FadeOutMs = 0;
        TrimStartSeconds = 0;
        TrimEndSeconds = 0;
        Normalize = false;
        _loading = false;

        Apply(e =>
        {
            e.Volume = 1f;
            e.Speed = 1f;
            e.Loop = false;
            e.FadeInMs = e.FadeOutMs = 0;
            e.TrimStartMs = e.TrimEndMs = 0;
            e.Normalize = false;
        });
    }

    [RelayCommand]
    private void OpenLocation() => _main.OpenFileLocationCommand.Execute(Sound);

    [RelayCommand]
    private void RemoveFromLibrary() => _main.RemoveFromLibraryCommand.Execute(Sound);

    [RelayCommand]
    private void ToggleFavorite() => _main.ToggleFavoriteCommand.Execute(Sound);

    /// <summary>
    /// Rename the actual file on disk. Hidden behind Settings → Advanced because the whole
    /// point of display names is that the file is left alone.
    /// </summary>
    [RelayCommand]
    private void RenameFileOnDisk()
    {
        if (Sound is null) return;

        if (!_services.Settings.Settings.Advanced.AllowFileRenaming)
        {
            MessageBox.Show(
                "Renaming files on disk is turned off.\n\n" +
                "Enable it in Settings → Advanced if you really want the file itself renamed. " +
                "Normally you want the display name instead, which leaves the file untouched.",
                "File renaming disabled", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extension = Path.GetExtension(Sound.FilePath);
        var safe = LibraryService.MakeSafeFileName(DisplayName);

        if (safe.Length == 0)
        {
            MessageBox.Show(
                "That display name has no characters Windows can use in a file name, so the " +
                "file cannot be renamed to it.",
                "Cannot use that name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(safe + extension, Sound.OriginalFileName, StringComparison.Ordinal))
        {
            MessageBox.Show(
                $"The file is already called “{Sound.OriginalFileName}”, so there is nothing " +
                "to rename. Change the display name first if you want the file to match it.",
                "Nothing to change", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Show what the file will genuinely end up called, including any characters that
        // had to be dropped, rather than springing it on them afterwards.
        var note = safe == DisplayName.Trim()
            ? string.Empty
            : "\n\nSome characters are not allowed in file names and have been removed.";

        var confirm = MessageBox.Show(
            $"Rename the file on disk to:\n\n{safe}{extension}{note}\n\n" +
            "This changes the actual file, not just how it is shown here.",
            "Rename file", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var name = safe;

        if (_services.Library.RenameFileOnDisk(Sound.Entry, name, out var error))
        {
            Apply(e => e.CustomName = null);
            Sound.RefreshAll();
        }
        else
        {
            MessageBox.Show($"Could not rename the file: {error}", "Rename failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Set the trim start to the current preview position.</summary>
    [RelayCommand]
    private void SetTrimStartHere()
    {
        if (_preview is null || Sound is null) return;
        TrimStartSeconds = Math.Round(_preview.PositionSeconds, 2);
    }

    [RelayCommand]
    private void SetTrimEndHere()
    {
        if (_preview is null || Sound is null) return;
        TrimEndSeconds = Math.Round(_preview.PositionSeconds, 2);
    }

    [RelayCommand]
    private void ClearTrim()
    {
        TrimStartSeconds = 0;
        TrimEndSeconds = 0;
    }

    /// <summary>
    /// Seeking a compressed stream is not free — the decoder has to re-sync and refill.
    /// A drag produces one of these per mouse move, and honouring every one leaves the
    /// decoder permanently behind, so the audio never actually plays. The playhead still
    /// follows the pointer; only the audio seek is rate limited, with an exact one on release.
    /// </summary>
    private DateTime _lastSeekUtc = DateTime.MinValue;

    private static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(120);

    /// <summary>Called when a scrub drag ends, to land exactly where the user let go.</summary>
    public void EndScrub()
    {
        if (_pendingScrubFraction is not { } fraction) return;

        _pendingScrubFraction = null;
        _lastSeekUtc = DateTime.MinValue;
        SeekTo(fraction);
    }

    private double? _pendingScrubFraction;

    /// <summary>
    /// Called by the waveform control when the user clicks or drags it. Seeks whatever is
    /// currently playing this sound — the preview or a normal trigger — and starts it from
    /// that point if nothing is playing.
    /// </summary>
    public void SeekTo(double fraction, bool isScrubbing = false)
    {
        if (isScrubbing)
        {
            // Always move the playhead so the drag feels attached to the pointer.
            Progress = Math.Clamp(fraction, 0, 1);
            _pendingScrubFraction = fraction;

            if (DateTime.UtcNow - _lastSeekUtc < SeekThrottle) return;
            _pendingScrubFraction = null;
        }

        _lastSeekUtc = DateTime.UtcNow;
        SeekCore(fraction);
    }

    private void SeekCore(double fraction)
    {
        if (Sound is null) return;

        var handle = LiveHandles().FirstOrDefault();

        if (handle is null)
        {
            // Nothing playing: start a preview so the click still does something useful.
            // Reusing any live handle above is what stops a rapid drag spawning a new
            // preview per mouse move.
            _preview = _services.Engine.Preview(Sound.Entry);
            handle = _preview;
            if (handle is null) return;
        }

        handle.Seek(fraction * Sound.DurationSeconds);
        Progress = Math.Clamp(fraction, 0, 1);
    }

    /// <summary>Refresh the transport readouts; called from the shell's 20 Hz timer.</summary>
    public void UpdateLive()
    {
        if (Sound is null) return;

        var handle = _preview
            ?? _services.Engine.Active.FirstOrDefault(h => h.SoundId == Sound.Id);

        if (handle is null || handle.IsCompleted)
        {
            if (IsPlaying)
            {
                IsPlaying = false;
                IsPaused = false;
                Progress = 0;
                PositionText = "0:00";
            }
            if (_preview is not null && _preview.IsCompleted) _preview = null;
            return;
        }

        IsPlaying = true;
        IsPaused = handle.IsPaused;
        Progress = handle.Progress;

        var position = TimeSpan.FromSeconds(handle.PositionSeconds);
        PositionText = $"{position.Minutes}:{position.Seconds:00}";
    }

    public void Dispose()
    {
        _waveformCts?.Cancel();
        StopPreview();
    }
}
