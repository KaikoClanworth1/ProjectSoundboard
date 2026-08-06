using System.Windows;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.App.ViewModels;
using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Views;

/// <summary>
/// Orchestrates a drag and drop import end to end: build the plan, ask where the files
/// should go, resolve name clashes, then hand the result to the library.
/// </summary>
public static class ImportFlow
{
    public static async Task RunAsync(
        Window owner, AppServices services, IReadOnlyList<string> droppedPaths, MainViewModel main)
    {
        var plan = services.Import.BuildPlan(droppedPaths);

        if (plan.IsEmpty)
        {
            var message = plan.AlreadyInLibrary.Count > 0
                ? $"Those {plan.AlreadyInLibrary.Count} file(s) are already in your library."
                : plan.UnsupportedCount > 0
                    ? "None of those files are audio formats Project Soundboard can play."
                    : "Nothing there to import.";

            main.StatusMessage = message;
            return;
        }

        var settings = services.Settings.Settings;
        var libraryPath = services.Import.EnsureMainLibraryPath();

        var behavior = settings.Library.ImportBehavior;
        var preserveStructure = settings.Library.PreserveFolderStructureOnImport;

        // Ask unless the user has already told us what they always want.
        if (behavior == ImportBehavior.Ask)
        {
            var dialog = new ImportDialog(plan, settings, libraryPath) { Owner = owner };
            if (dialog.ShowDialog() != true) return;

            behavior = dialog.Behavior;
            preserveStructure = dialog.PreserveFolderStructure;

            settings.Library.PreserveFolderStructureOnImport = dialog.PreserveFolderStructure;
            settings.Library.AutoTagFromFolderName = dialog.AutoTagFromFolder;
            settings.Library.DetectDuplicatesOnImport = dialog.DetectDuplicateFiles;

            if (dialog.RememberMyChoice) settings.Library.ImportBehavior = behavior;

            services.Settings.Save();
        }

        main.StatusMessage = $"Importing {plan.Items.Count:N0} sound(s)…";

        var progress = new Progress<ImportProgress>(p =>
        {
            main.StatusMessage = p.CurrentFile is null
                ? $"Importing… {p.Completed:N0} of {p.Total:N0}"
                : $"Importing {p.CurrentFile} ({p.Completed + 1:N0} of {p.Total:N0})";
        });

        var cancelled = false;

        var result = await services.Import.ExecuteAsync(
            plan,
            behavior,
            preserveStructure,
            resolveConflict: conflict =>
            {
                // The import runs on a background task; the dialog has to come back here.
                return owner.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new ConflictDialog(conflict) { Owner = owner };

                    if (dialog.ShowDialog() != true)
                    {
                        cancelled = true;
                        return (ConflictAction.Skip, true);
                    }

                    return (dialog.Action, dialog.ApplyToAllConflicts);
                }).Task;
            },
            progress: progress);

        if (cancelled)
        {
            main.StatusMessage = "Import cancelled.";
            return;
        }

        // Newly copied files land in a watched folder, so pick them up right away.
        if (behavior == ImportBehavior.CopyToLibrary) services.Library.StartWatching();

        var summary = behavior == ImportBehavior.IndexInPlace
            ? $"Added {result.Indexed:N0} sound(s) from their current location."
            : BuildCopySummary(result);

        main.StatusMessage = summary;
        main.BuildTree();
        main.RefreshResults();

        Log.Info(summary);

        if (result.Errors.Count > 0)
        {
            MessageBox.Show(
                $"{result.Errors.Count} file(s) could not be imported:\n\n" +
                string.Join("\n", result.Errors.Take(10)) +
                (result.Errors.Count > 10 ? $"\n…and {result.Errors.Count - 10} more." : string.Empty),
                "Some files were skipped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildCopySummary(ImportResult result)
    {
        var parts = new List<string> { $"Imported {result.Copied:N0} sound(s)" };

        if (result.Renamed > 0) parts.Add($"{result.Renamed} renamed to avoid clashes");
        if (result.Replaced > 0) parts.Add($"{result.Replaced} replaced");
        if (result.Skipped > 0) parts.Add($"{result.Skipped} skipped");
        if (result.Errors.Count > 0) parts.Add($"{result.Errors.Count} failed");

        return string.Join(" · ", parts) + ".";
    }
}
