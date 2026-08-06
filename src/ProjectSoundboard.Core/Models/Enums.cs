namespace ProjectSoundboard.Core.Models;

public enum AppTheme
{
    Dark,
    Light,
    System,
    HighContrast
}

public enum LibraryViewMode
{
    Grid,
    Compact,
    List
}

public enum SortMode
{
    Name,
    RecentlyAdded,
    RecentlyPlayed,
    MostPlayed,
    Duration,
    Random
}

/// <summary>What happens when files are dropped onto the window.</summary>
public enum ImportBehavior
{
    /// <summary>Show the import dialog and let the user pick (default).</summary>
    Ask,

    /// <summary>Copy into the main sound library folder.</summary>
    CopyToLibrary,

    /// <summary>Index the files where they already live.</summary>
    IndexInPlace
}

/// <summary>What to do when an imported file name already exists in the destination.</summary>
public enum ConflictAction
{
    Ask,
    Replace,
    KeepBoth,
    Skip
}

/// <summary>How a triggered sound interacts with sounds that are already playing.</summary>
public enum PlaybackMode
{
    /// <summary>Layer on top of whatever is already playing.</summary>
    Overlap,

    /// <summary>Stop everything else first.</summary>
    Solo,

    /// <summary>Retrigger from the start if the same sound is already playing.</summary>
    Restart
}

public enum ColorBlindMode
{
    None,
    Protanopia,
    Deuteranopia,
    Tritanopia
}

public enum HotkeyAction
{
    PlaySound,
    StopAll,
    PauseResume,
    Next,
    Previous,
    Random,
    MuteMicrophone,
    MuteSoundboard,
    TogglePassthrough,
    PushToTalk,
    VolumeUp,
    VolumeDown,
    ShowHideWindow
}
