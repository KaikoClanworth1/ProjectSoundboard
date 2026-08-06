using System.Text.Json.Serialization;

namespace ProjectSoundboard.Core.Models;

/// <summary>
/// A single sound in the library. Everything the user customises lives here;
/// the file on disk is never modified unless the user explicitly asks for it.
/// </summary>
public sealed class SoundEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Absolute path to the audio file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// User supplied name shown in the UI. When null/empty the original file name
    /// (without extension) is displayed instead.
    /// </summary>
    public string? CustomName { get; set; }

    /// <summary>Optional path to a user supplied thumbnail stored in the image cache.</summary>
    public string? ImagePath { get; set; }

    /// <summary>Optional emoji shown instead of / next to the thumbnail.</summary>
    public string? Emoji { get; set; }

    public string? GroupId { get; set; }

    public List<string> Tags { get; set; } = new();

    // ---- per sound playback settings -------------------------------------

    /// <summary>Linear gain multiplier, 0..2 (1.0 = unity).</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Playback rate, 0.25..4.0 (1.0 = normal).</summary>
    public float Speed { get; set; } = 1.0f;

    public bool Loop { get; set; }

    /// <summary>Fade in duration in milliseconds.</summary>
    public int FadeInMs { get; set; }

    /// <summary>Fade out duration in milliseconds.</summary>
    public int FadeOutMs { get; set; }

    /// <summary>Playback start offset in milliseconds.</summary>
    public int TrimStartMs { get; set; }

    /// <summary>Playback end offset in milliseconds. 0 means "to end of file".</summary>
    public int TrimEndMs { get; set; }

    /// <summary>Apply peak normalisation using <see cref="PeakAmplitude"/>.</summary>
    public bool Normalize { get; set; }

    // ---- library state ---------------------------------------------------

    public bool IsFavorite { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    // ---- cached file facts (refreshed when the file changes) --------------

    public long FileSizeBytes { get; set; }
    public long FileTicks { get; set; }
    public double DurationSeconds { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    /// <summary>Measured peak sample (0..1) used for normalisation. 0 = not analysed yet.</summary>
    public float PeakAmplitude { get; set; }

    /// <summary>Set when the file could not be found on the last scan.</summary>
    public bool IsMissing { get; set; }

    /// <summary>Set when the file exists but could not be decoded.</summary>
    public bool IsBroken { get; set; }

    // ---- derived ---------------------------------------------------------

    [JsonIgnore]
    public string OriginalFileName => Path.GetFileName(FilePath);

    [JsonIgnore]
    public string OriginalNameWithoutExtension =>
        Path.GetFileNameWithoutExtension(FilePath) ?? string.Empty;

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(CustomName) ? OriginalNameWithoutExtension : CustomName!;

    [JsonIgnore]
    public bool HasCustomName => !string.IsNullOrWhiteSpace(CustomName);

    [JsonIgnore]
    public string? Directory => Path.GetDirectoryName(FilePath);

    [JsonIgnore]
    public string Extension => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    /// <summary>Effective playback length after trims, in seconds.</summary>
    [JsonIgnore]
    public double EffectiveDurationSeconds
    {
        get
        {
            var end = TrimEndMs > 0 ? TrimEndMs / 1000.0 : DurationSeconds;
            return Math.Max(0, end - TrimStartMs / 1000.0);
        }
    }

    public SoundEntry Clone() => (SoundEntry)MemberwiseClone();
}
