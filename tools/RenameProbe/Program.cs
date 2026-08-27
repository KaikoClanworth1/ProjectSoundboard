using ProjectSoundboard.Core.Library;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace RenameProbe;

/// <summary>
/// What a rescan does after the files have been renamed underneath it.
///
/// Renaming looks like two events at once — a path gone and a path arrived — and taken at
/// face value it leaves the library holding both: the old one listed as missing, with the
/// hotkeys and the artwork still attached to it, and the same sound added again beside it
/// as a stranger. This checks that a rename is recognised for what it is, and, just as
/// importantly, that a file which really was deleted is still reported as gone.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var source = args.FirstOrDefault(a => !a.StartsWith("--"));

        var samples = source is not null
            ? new[] { source }
            : Directory.EnumerateFiles(@"F:\Downloads", "*.mp3").Take(3).ToArray();

        if (samples.Length == 0)
        {
            Console.WriteLine("No sample audio to work with.");
            return 1;
        }

        var folder = Path.Combine(Path.GetTempPath(), "psb-rename-probe");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);

        var copied = new List<string>();

        foreach (var sample in samples)
        {
            var to = Path.Combine(folder, Path.GetFileName(sample));
            File.Copy(sample, to, true);
            copied.Add(to);
        }

        Console.WriteLine($"working folder: {folder}");
        Console.WriteLine($"sounds        : {copied.Count}");
        Console.WriteLine();

        var settings = new SettingsService();
        settings.Load();

        settings.Settings.Library.Folders.Clear();
        settings.Settings.Library.Folders.Add(new LibraryFolder { Path = folder, Recursive = true, Watch = false });

        var library = new LibraryService(settings);
        library.Load();

        // Start from nothing, so a stale library file cannot make this pass or fail on its own.
        library.RemoveFromLibrary(library.Sounds.ToList());

        await library.ScanAsync();

        var failures = 0;

        Console.WriteLine($"first scan    : {library.Count} sound(s), {library.FindMissing().Count} missing");
        if (library.Count != copied.Count) { Console.WriteLine("FAIL - the first scan did not find them all."); failures++; }

        // Something to lose: a name somebody typed, and a group they filed it under.
        var first = library.GetByPath(copied[0]);
        if (first is null) { Console.WriteLine("FAIL - could not find the first sound."); return 1; }

        var id = first.Id;
        first.CustomName = "The Name I Gave It";
        first.GroupId = "some-group";

        Console.WriteLine();
        Console.WriteLine("renaming it on disk, the way tidying a library does…");

        var renamed = Path.Combine(folder, "A Tidier Name.mp3");
        File.Move(copied[0], renamed);

        await library.ScanAsync();

        Console.WriteLine();
        Console.WriteLine($"after rename  : {library.Count} sound(s), {library.FindMissing().Count} missing");

        if (library.Count != copied.Count)
        {
            Console.WriteLine($"FAIL - expected {copied.Count} sound(s); the old one was kept as well.");
            failures++;
        }

        if (library.FindMissing().Count != 0)
        {
            Console.WriteLine("FAIL - the renamed file is still listed as missing.");
            failures++;
        }

        var after = library.GetById(id);

        if (after is null)
        {
            Console.WriteLine("FAIL - the sound lost its identity; anything pointing at it is broken.");
            failures++;
        }
        else
        {
            var followed = string.Equals(after.FilePath, renamed, StringComparison.OrdinalIgnoreCase);
            var kept = after.CustomName == "The Name I Gave It" && after.GroupId == "some-group";

            Console.WriteLine($"  path followed the rename : {followed}");
            Console.WriteLine($"  name and group kept      : {kept}");

            if (!followed) { Console.WriteLine("FAIL - the entry still points at the old path."); failures++; }
            if (!kept) { Console.WriteLine("FAIL - the settings on it were lost."); failures++; }
        }

        // The mess an older scan already left behind: the renamed file was added as a second
        // sound, and the original is still sitting there marked missing. Nothing new appears
        // on this scan, so it can only be cleared by noticing the file is already here.
        Console.WriteLine();
        Console.WriteLine("clearing a leftover from before this was understood…");

        if (copied.Count > 2)
        {
            var present = library.GetByPath(copied[2])!;

            var phantom = library.AddFiles([copied[2]]).FirstOrDefault();
            if (phantom is null || ReferenceEquals(phantom, present))
            {
                // AddFiles will not add the same path twice, so build the leftover by hand:
                // point a copy of the entry at a name that is no longer there.
                var ghost = Path.Combine(folder, "The Name It Used To Have.mp3");
                File.Copy(copied[2], ghost);

                var ghostEntry = library.AddFiles([ghost]).Single();
                ghostEntry.CustomName = "Something I Typed";

                File.Delete(ghost);   // now it is a missing entry for a file that is here twice over
            }

            await library.ScanAsync();

            var leftovers = library.FindMissing().Count;
            Console.WriteLine($"  leftovers now            : {leftovers}");
            Console.WriteLine($"  its name carried over    : {present.CustomName == "Something I Typed"}");

            if (leftovers != 0)
            {
                Console.WriteLine("FAIL - the leftover is still listed as missing.");
                failures++;
            }

            if (present.CustomName != "Something I Typed")
            {
                Console.WriteLine("FAIL - what was typed on the old entry was thrown away.");
                failures++;
            }
        }

        // The other half: a file that really has gone must still be reported gone.
        Console.WriteLine();
        Console.WriteLine("deleting one for real…");

        if (copied.Count > 1)
        {
            var doomed = library.GetByPath(copied[1]);
            doomed!.GroupId = "some-group";

            File.Delete(copied[1]);
            await library.ScanAsync();

            var missing = library.FindMissing().Count;
            Console.WriteLine($"  reported missing         : {missing}");

            if (missing != 1)
            {
                Console.WriteLine("FAIL - a genuinely deleted file should still be reported.");
                failures++;
            }
        }

        try { Directory.Delete(folder, true); } catch { /* leaving a temp folder is not a failure */ }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - a rename is followed, a deletion is still reported."
            : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }
}
