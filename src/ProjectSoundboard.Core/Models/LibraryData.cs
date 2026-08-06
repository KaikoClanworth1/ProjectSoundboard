namespace ProjectSoundboard.Core.Models;

/// <summary>
/// Everything the library knows, persisted as library.json. This is metadata only —
/// audio files themselves are never stored here.
/// </summary>
public sealed class LibraryData
{
    public int SchemaVersion { get; set; } = 1;

    public List<SoundEntry> Sounds { get; set; } = new();
    public List<SoundGroup> Groups { get; set; } = new();

    /// <summary>Most recent first, sound ids.</summary>
    public List<string> History { get; set; } = new();

    public DateTime LastScanUtc { get; set; }
}
