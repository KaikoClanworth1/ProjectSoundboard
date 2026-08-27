namespace ProjectSoundboard.Core.Models;

/// <summary>
/// A user defined, optionally nested category. Groups are purely virtual —
/// they never move files on disk.
/// </summary>
public sealed class SoundGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Group";

    /// <summary>Parent group id, or null for a root level group.</summary>
    public string? ParentId { get; set; }

    /// <summary>
    /// The folder on disk this group stands for, when it has one.
    ///
    /// Groups began as a purely organisational idea with nothing behind them, which is fine
    /// until something has to be *put* somewhere: downloading into a group had to guess the
    /// destination from where its existing sounds happened to live, and a group with nothing
    /// in it yet gave nothing to guess from. A group made in the sidebar now gets a real
    /// folder, and anything added to it lands there.
    ///
    /// Null for groups made before this existed, and for any group not backed by a folder.
    /// Everything still works without one; the destination is simply guessed as it was.
    /// </summary>
    public string? FolderPath { get; set; }

    /// <summary>Accent colour as #RRGGBB. Null = auto colour from the name.</summary>
    public string? Color { get; set; }

    /// <summary>Optional emoji shown next to the group name.</summary>
    public string? Emoji { get; set; }

    public int SortOrder { get; set; }

    public bool IsExpanded { get; set; } = true;
}
