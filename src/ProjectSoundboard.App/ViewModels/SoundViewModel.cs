using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>
/// UI wrapper around a <see cref="SoundEntry"/>. These are cached per entry and reused
/// across searches so scrolling a large library does not allocate a new object per tile.
/// </summary>
public sealed partial class SoundViewModel : ObservableObject
{
    private readonly ImageCacheService _images;

    public SoundViewModel(SoundEntry entry, ImageCacheService images, string? groupName)
    {
        Entry = entry;
        _images = images;
        _groupName = groupName;
    }

    public SoundEntry Entry { get; }

    public string Id => Entry.Id;
    public string FilePath => Entry.FilePath;
    public string DisplayName => Entry.DisplayName;
    public string OriginalFileName => Entry.OriginalFileName;
    public string? Directory => Entry.Directory;
    public string Extension => Entry.Extension;
    public double DurationSeconds => Entry.DurationSeconds;
    public int Bitrate => Entry.Bitrate;
    public int SampleRate => Entry.SampleRate;
    public int Channels => Entry.Channels;
    public long FileSizeBytes => Entry.FileSizeBytes;
    public int PlayCount => Entry.PlayCount;
    public DateTime? LastPlayedUtc => Entry.LastPlayedUtc;
    public bool IsMissing => Entry.IsMissing;
    public bool IsBroken => Entry.IsBroken;
    public bool HasProblem => Entry.IsMissing || Entry.IsBroken;
    public IReadOnlyList<string> Tags => Entry.Tags;
    public string TagsText => string.Join(", ", Entry.Tags);
    public bool HasTags => Entry.Tags.Count > 0;

    public string? Emoji => Entry.Emoji;
    public bool HasEmoji => !string.IsNullOrWhiteSpace(Entry.Emoji);

    /// <summary>Single character shown on the generated tile when there is no image.</summary>
    public string Initial => ImageStore.InitialFor(Entry.DisplayName);

    /// <summary>Stable auto colour derived from the name, used behind <see cref="Initial"/>.</summary>
    public string AutoColor => ImageStore.AutoColor(Entry.OriginalNameWithoutExtension);

    public BitmapSource? Thumbnail => _images.Get(Entry.ImagePath);
    public bool HasThumbnail => Thumbnail is not null;

    public string ChannelsText => Channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        > 2 => $"{Channels} channels",
        _ => "Unknown"
    };

    public string QualityText => SampleRate > 0
        ? $"{SampleRate / 1000.0:0.#} kHz · {(Bitrate > 0 ? $"{Bitrate} kbps" : Extension)}"
        : Extension;

    [ObservableProperty]
    private string? _groupName;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsFavorite
    {
        get => Entry.IsFavorite;
        set
        {
            if (Entry.IsFavorite == value) return;
            Entry.IsFavorite = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Re-read everything that can change from the properties panel.</summary>
    public void RefreshAll()
    {
        _images.Invalidate(Entry.ImagePath);

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(OriginalFileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Directory));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(AutoColor));
        OnPropertyChanged(nameof(Emoji));
        OnPropertyChanged(nameof(HasEmoji));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(PlayCount));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(ChannelsText));
    }
}
