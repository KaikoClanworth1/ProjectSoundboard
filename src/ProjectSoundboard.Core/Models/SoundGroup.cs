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

    /// <summary>Accent colour as #RRGGBB. Null = auto colour from the name.</summary>
    public string? Color { get; set; }

    /// <summary>Optional emoji shown next to the group name.</summary>
    public string? Emoji { get; set; }

    public int SortOrder { get; set; }

    public bool IsExpanded { get; set; } = true;
}
