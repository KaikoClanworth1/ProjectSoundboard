using System.IO;
using System.Windows;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

public partial class ConflictDialog : Window
{
    public ConflictDialog(ImportConflict conflict)
    {
        InitializeComponent();

        FileNameText.Text =
            $"“{Path.GetFileName(conflict.DestinationPath)}” already exists in your sound library.";

        ExistingSizeText.Text = FormatSize(conflict.DestinationSize);
        NewSizeText.Text = FormatSize(conflict.SourceSize);
    }

    public ConflictAction Action { get; private set; } = ConflictAction.Skip;

    public bool ApplyToAllConflicts => ApplyToAll.IsChecked == true;

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Choose(ConflictAction.KeepBoth);
    private void OnReplace(object sender, RoutedEventArgs e) => Choose(ConflictAction.Replace);
    private void OnSkip(object sender, RoutedEventArgs e) => Choose(ConflictAction.Skip);

    private void Choose(ConflictAction action)
    {
        Action = action;
        this.Answer(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => this.Answer(false);

    private static string FormatSize(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} B" : $"{value:0.#} {units[unit]}";
    }
}
