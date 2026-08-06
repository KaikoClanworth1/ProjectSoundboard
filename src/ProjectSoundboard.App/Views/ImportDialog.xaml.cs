using System.Windows;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

/// <summary>
/// The "where should these go?" step of a drag and drop import. Deliberately opinionated:
/// copying into the main library is pre-selected because it is what stops sounds vanishing
/// later when a Downloads folder gets cleaned out.
/// </summary>
public partial class ImportDialog : Window
{
    public ImportDialog(ImportPlan plan, AppSettings settings, string mainLibraryPath)
    {
        InitializeComponent();

        var fileWord = plan.Items.Count == 1 ? "file" : "files";
        SummaryText.Text = $"{plan.Items.Count:N0} audio {fileWord} ready to import";

        var details = new List<string>();
        if (plan.TotalBytes > 0) details.Add(FormatSize(plan.TotalBytes));
        if (plan.HasFolders) details.Add("folders were scanned recursively");
        if (plan.UnsupportedCount > 0) details.Add($"{plan.UnsupportedCount} unsupported file(s) skipped");
        if (plan.AlreadyInLibrary.Count > 0)
            details.Add($"{plan.AlreadyInLibrary.Count} already in your library");

        DetailText.Text = string.Join("  ·  ", details);

        if (plan.LikelyDuplicates.Count > 0)
        {
            DuplicateText.Visibility = Visibility.Visible;
            DuplicateText.Text =
                $"{plan.LikelyDuplicates.Count} of these look like duplicates of sounds you already have.";
        }

        LibraryPathText.Text = $"Copies to: {mainLibraryPath}";

        PreserveStructure.IsChecked = settings.Library.PreserveFolderStructureOnImport;
        AutoTag.IsChecked = settings.Library.AutoTagFromFolderName;
        DetectDuplicates.IsChecked = settings.Library.DetectDuplicatesOnImport;

        // Respect a previously remembered preference by pre-selecting it.
        if (settings.Library.ImportBehavior == ImportBehavior.IndexInPlace)
            OptionIndex.IsChecked = true;
        else
            OptionCopy.IsChecked = true;
    }

    public ImportBehavior Behavior =>
        OptionIndex.IsChecked == true ? ImportBehavior.IndexInPlace : ImportBehavior.CopyToLibrary;

    public bool RememberMyChoice => RememberChoice.IsChecked == true;
    public bool PreserveFolderStructure => PreserveStructure.IsChecked == true;
    public bool AutoTagFromFolder => AutoTag.IsChecked == true;
    public bool DetectDuplicateFiles => DetectDuplicates.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

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
