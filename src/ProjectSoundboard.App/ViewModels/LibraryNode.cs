using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.ViewModels;

public enum LibraryNodeKind
{
    AllSounds,
    Favorites,
    RecentlyPlayed,
    MostPlayed,
    RecentlyAdded,
    Problems,
    Group
}

/// <summary>
/// An entry in the sidebar. Smart nodes (favourites, recents, …) and user created groups
/// share one type so the tree template stays simple.
/// </summary>
public sealed partial class LibraryNode : ObservableObject
{
    public LibraryNode(LibraryNodeKind kind, string name, string glyph, SoundGroup? group = null)
    {
        Kind = kind;
        _name = name;
        Glyph = glyph;
        Group = group;
        _isExpanded = group?.IsExpanded ?? true;
    }

    public LibraryNodeKind Kind { get; }
    public string Glyph { get; }
    public SoundGroup? Group { get; }
    public string? GroupId => Group?.Id;

    public ObservableCollection<LibraryNode> Children { get; } = new();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Groups can be renamed, deleted and dropped onto; smart nodes cannot.</summary>
    public bool IsUserGroup => Kind == LibraryNodeKind.Group;

    public bool HasChildren => Children.Count > 0;

    public string? Color => Group?.Color;

    partial void OnIsExpandedChanged(bool value)
    {
        if (Group is not null) Group.IsExpanded = value;
    }
}
