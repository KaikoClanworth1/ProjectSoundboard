using System.Windows;
using System.Windows.Input;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

public partial class GroupPickerWindow : Window
{
    public GroupPickerWindow(IReadOnlyList<SoundGroup> groups, string? currentGroupId)
    {
        InitializeComponent();

        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = groups.FirstOrDefault(g => g.Id == currentGroupId) ?? groups.FirstOrDefault();

        SubtitleText.Text = currentGroupId is null
            ? "Choose where this sound should live."
            : "Choose a different group, or take it out of groups entirely.";

        Loaded += (_, _) => GroupList.Focus();
    }

    /// <summary>Null means "no group".</summary>
    public string? SelectedGroupId { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not SoundGroup group) return;

        SelectedGroupId = group.Id;
        DialogResult = true;
    }

    private void OnNoGroup(object sender, RoutedEventArgs e)
    {
        SelectedGroupId = null;
        DialogResult = true;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnAccept(sender, e);

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
