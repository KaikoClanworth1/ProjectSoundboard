using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectSoundboard.App.Controls;
using ProjectSoundboard.App.ViewModels;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

public partial class LibraryView : UserControl
{
    private MainViewModel? _viewModel;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyViewMode();

        // Ctrl+F has to work wherever focus happens to be on this page, not only in the list.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyViewMode();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ViewMode) or nameof(MainViewModel.TileSize))
            ApplyViewMode();
    }

    /// <summary>
    /// Swap the item template and items panel to match the chosen layout. Doing this in
    /// code rather than with triggers keeps the virtualizing panel from being rebuilt on
    /// every unrelated property change.
    /// </summary>
    private void ApplyViewMode()
    {
        if (_viewModel is null || !IsLoaded) return;

        var mode = _viewModel.ViewMode;

        var templateKey = mode switch
        {
            LibraryViewMode.Grid => "GridTile",
            LibraryViewMode.Compact => "CompactTile",
            _ => "ListRow"
        };

        SoundList.ItemTemplate = (DataTemplate)FindResource(templateKey);

        var panel = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));

        switch (mode)
        {
            case LibraryViewMode.Grid:
            {
                var size = Math.Clamp(_viewModel.TileSize, 96, 220);
                panel.SetValue(VirtualizingWrapPanel.ItemWidthProperty, size);
                // Extra vertical room for the two lines of text under the artwork.
                panel.SetValue(VirtualizingWrapPanel.ItemHeightProperty, size + 46);
                break;
            }

            case LibraryViewMode.Compact:
                panel.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 230d);
                panel.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 54d);
                break;

            default:
                // One row per line: an absurdly wide item width forces a single column.
                panel.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 100000d);
                panel.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 48d);
                break;
        }

        SoundList.ItemsPanel = new ItemsPanelTemplate(panel);
    }

    // -----------------------------------------------------------------------

    private void OnNodeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel is null) return;
        if (e.NewValue is LibraryNode node) _viewModel.SelectedNode = node;
    }

    /// <summary>
    /// A soundboard should fire on a single click — that is the whole point of the tiles.
    /// Selection happens at the same time so the details panel follows along.
    /// </summary>
    private void OnSoundClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.ChangedButton != MouseButton.Left) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not SoundViewModel sound) return;

        _viewModel.SelectedSound = sound;
        _viewModel.PlayCommand.Execute(sound);
    }

    private void OnSoundListKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                if (_viewModel.SelectedSound is { } selected)
                {
                    _viewModel.PlayCommand.Execute(selected);
                    e.Handled = true;
                }
                break;

            case Key.Delete:
                if (_viewModel.SelectedSound is { } toRemove)
                {
                    _viewModel.RemoveFromLibraryCommand.Execute(toRemove);
                    e.Handled = true;
                }
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedNode is not { IsUserGroup: true, Group: not null } node) return;

        var dialog = new TextPromptWindow("Rename group", "Group name", node.Name)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dialog.Value)) return;

        _viewModel.Services.Library.RenameGroup(node.Group, dialog.Value.Trim());
        _viewModel.BuildTree();
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start is not null)
        {
            if (start is T match) return match;
            start = VisualTreeHelper.GetParent(start);
        }
        return null;
    }
}
