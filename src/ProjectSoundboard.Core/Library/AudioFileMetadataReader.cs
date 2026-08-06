using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

/// <summary>
/// Reads duration / bitrate / sample rate straight from the file headers via TagLib#.
/// This is header-only, so it stays fast enough to run across tens of thousands of files.
/// </summary>
public static class AudioFileMetadataReader
{
    /// <summary>
    /// Fill the cached file facts on <paramref name="entry"/>. Returns false if the file
    /// could not be parsed, in which case the entry is flagged broken.
    /// </summary>
    public static bool Populate(SoundEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.FilePath);
            if (!info.Exists)
            {
                entry.IsMissing = true;
                return false;
            }

            entry.IsMissing = false;
            entry.FileSizeBytes = info.Length;
            entry.FileTicks = info.LastWriteTimeUtc.Ticks;

            using var tag = TagLib.File.Create(entry.FilePath);
            var props = tag.Properties;
            if (props is null)
            {
                entry.IsBroken = true;
                return false;
            }

            entry.DurationSeconds = props.Duration.TotalSeconds;
            entry.Bitrate = props.AudioBitrate;
            entry.SampleRate = props.AudioSampleRate;
            entry.Channels = props.AudioChannels;
            entry.IsBroken = entry.DurationSeconds <= 0;
            return !entry.IsBroken;
        }
        catch (Exception ex)
        {
            Log.Debug($"Metadata read failed for {entry.FilePath}: {ex.Message}");
            entry.IsBroken = true;
            return false;
        }
    }

    /// <summary>True when the cached facts are stale relative to the file on disk.</summary>
    public static bool NeedsRefresh(SoundEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.FilePath);
            if (!info.Exists) return true;
            return entry.FileTicks != info.LastWriteTimeUtc.Ticks
                || entry.FileSizeBytes != info.Length
                || entry.DurationSeconds <= 0;
        }
        catch
        {
            return true;
        }
    }
}
