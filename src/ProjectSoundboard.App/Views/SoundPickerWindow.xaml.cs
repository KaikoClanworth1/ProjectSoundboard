using System.Windows;
using System.Windows.Controls;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

/// <summary>
/// Picks one sound out of the library. Used when a keybind is created from the hotkeys page,
/// where there is no sound in hand yet — the other way round starts from the sound itself.
/// </summary>
public partial class SoundPickerWindow : Window
{
    /// <summary>Enough to browse, few enough that the list stays instant while typing.</summary>
    private const int MaxShown = 300;

    private readonly AppServices _services;

    public SoundPickerWindow(AppServices services)
    {
        InitializeComponent();

        _services = services;
        Refresh(string.Empty);

        Loaded += (_, _) => SearchBox.Focus();
    }

    public SoundEntry? Selected => SoundList.SelectedItem as SoundEntry;

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh(SearchBox.Text);

    private void Refresh(string query)
    {
        var matches = _services.Search.Execute(new SearchQuery { Text = query });

        SoundList.ItemsSource = matches.Take(MaxShown).ToList();
        if (SoundList.Items.Count > 0) SoundList.SelectedIndex = 0;

        CountText.Text = matches.Count > MaxShown
            ? $"Showing the first {MaxShown} of {matches.Count} — keep typing to narrow it down."
            : $"{matches.Count} sound{(matches.Count == 1 ? "" : "s")}";

        AcceptButton.IsEnabled = SoundList.Items.Count > 0;
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected is not null) this.Answer(true);
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Selected is not null) this.Answer(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => this.Answer(false);
}
