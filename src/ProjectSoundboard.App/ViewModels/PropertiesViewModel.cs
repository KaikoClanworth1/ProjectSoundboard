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

    private void Apply(Action<SoundEntry> mutate)
    {
        if (_loading || Sound is null) return;

        mutate(Sound.Entry);
        _services.Library.NotifyChanged();
        Sound.RefreshAll();
    }

    partial void OnDisplayNameChanged(string value)
    {
        Apply(e => e.CustomName = string.IsNullOrWhiteSpace(value) ||
                                  value == e.OriginalNameWithoutExtension
            ? null
            : value.Trim());
    }

    partial void OnEmojiChanged(string? value) =>
        Apply(e => e.Emoji = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnTagsTextChanged(string value)
    {
        Apply(e =>
        {
            e.Tags.Clear();
            e.Tags.AddRange(value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        });
    }

    partial void OnSelectedGroupIdChanged(string? value) => Apply(e => e.GroupId = value);

    partial void OnVolumeChanged(float value)
    {
        Apply(e => e.Volume = value);
        _preview?.SetVolume(value);
    }

    partial void OnSpeedChanged(float value)
    {
        Apply(e => e.Speed = value);
        _preview?.SetSpeed(value);
    }

    partial void OnLoopChanged(bool value) => Apply(e => e.Loop = value);
    partial void OnFadeInMsChanged(int value) => Apply(e => e.FadeInMs = Math.Max(0, value));
    partial void OnFadeOutMsChanged(int value) => Apply(e => e.FadeOutMs = Math.Max(0, value));
    partial void OnNormalizeChanged(bool value) => Apply(e => e.Normalize = value);

    partial void OnTrimStartSecondsChanged(double value) =>
        Apply(e => e.TrimStartMs = (int)Math.Max(0, value * 1000));

    partial void OnTrimEndSecondsChanged(double value) =>
        Apply(e => e.TrimEndMs = (int)Math.Max(0, value * 1000));

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

        var name = DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var confirm = MessageBox.Show(
            $"Rename the file on disk to:\n\n{name}{Path.GetExtension(Sound.FilePath)}\n\n" +
            "This changes the actual file, not just how it is shown here.",
            "Rename file", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

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
    /// Called by the waveform control when the user clicks it. Seeks whatever is currently
    /// playing this sound — the preview or a normal trigger — and starts it from that point
    /// if nothing is playing, which is what clicking a waveform is expected to do.
    /// </summary>
    public void SeekTo(double fraction)
    {
        if (Sound is null) return;

        var handle = _preview
                     ?? _services.Engine.Active.FirstOrDefault(h => h.SoundId == Sound.Id);

        if (handle is null || handle.IsCompleted)
        {
            // Nothing playing: start a preview so the click still does something useful.
            _preview = _services.Engine.Preview(Sound.Entry);
            handle = _preview;
            if (handle is null) return;
        }

        var seconds = fraction * Sound.DurationSeconds;

        foreach (var voice in handle.Voices)
        {
            switch (voice)
            {
                case CachedVoice cached: cached.Seek(seconds); break;
                case StreamingVoice streaming: streaming.Seek(seconds); break;
            }
        }

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
