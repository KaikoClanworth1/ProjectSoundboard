namespace ProjectSoundboard.Core.Models;

/// <summary>A folder the library indexes. Sounds are discovered, never imported one by one.</summary>
public sealed class LibraryFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Path { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    /// <summary>Keep a FileSystemWatcher on this folder and sync changes live.</summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// True for the single folder that "Import to Main Sound Library" copies into.
    /// </summary>
    public bool IsMainLibrary { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Group automatically assigned to sounds discovered under this folder.</summary>
    public string? DefaultGroupId { get; set; }
}
