using ProjectSoundboard.Audio;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace RerouteProbe;

/// <summary>
/// What happens to a playing song when the outputs change under it.
///
/// Before, it was one of two wrong things: cut off on a bus whose device changed, or carrying
/// on through the old device on one that did not, until another song was picked. This plays a
/// real song on the real devices, changes the outputs, and checks the song comes out on the new
/// ones from where it had got to — and that a change which moves nothing leaves it alone.
///
/// Needs the app's settings copied beside it, so the devices are the real ones.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A file, or a folder to take the longest song from. Long, so it is still playing
        // after every move this makes.
        var from = args.FirstOrDefault() ?? @"F:\Downloads";

        var sample = File.Exists(from)
            ? from
            : Directory.Exists(from)
                ? Directory.EnumerateFiles(from, "*.mp3")
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault()
                : null;

        if (sample is null) { Console.WriteLine("No sample audio."); return 1; }

        var settings = new SettingsService();
        settings.Load();
        var audio = settings.Settings.Audio;

        using var engine = new AudioEngine(settings, new AudioDeviceService());
        engine.ApplyAudioSettings();

        if (!engine.VirtualMicBus.IsRunning || !engine.MonitorBus.IsRunning)
        {
            Console.WriteLine("Both outputs need to be running; copy the app's settings beside this first.");
            return 1;
        }

        var entry = new SoundEntry { FilePath = sample };
        SoundEntry? Find(string id) => id == entry.Id ? entry : null;

        var failures = 0;

        Console.WriteLine($"  playing {Path.GetFileName(sample)}");
        var original = engine.Play(entry);
        if (original is null) { Console.WriteLine("FAIL - nothing played."); return 1; }

        Thread.Sleep(2500);
        var reached = original.PositionSeconds;
        Console.WriteLine($"  had reached         : {reached:0.00}s");
        Console.WriteLine();

        // ---- a change that moves nothing --------------------------------------
        var levelsOnly = engine.Reroute(() =>
        {
            audio.MonitorVolume = Math.Clamp(audio.MonitorVolume - 0.1f, 0f, 1.5f);
            engine.ApplyAudioSettings();
        }, Find);

        Console.WriteLine($"  levels only         : moved {levelsOnly.Count}, still the same song: {!original.IsCompleted}");
        if (levelsOnly.Count != 0) { Console.WriteLine("FAIL - a change of level restarted the song."); failures++; }
        if (original.IsCompleted) { Console.WriteLine("FAIL - a change of level stopped the song."); failures++; }

        // ---- a real change of outputs -------------------------------------------
        var monitorWas = audio.MonitorEnabled;

        var moved = engine.Reroute(() =>
        {
            audio.MonitorEnabled = !monitorWas;
            engine.ApplyAudioSettings();
        }, Find);

        Thread.Sleep(400);

        var carried = moved.Count == 1 ? moved[0].To.PositionSeconds : 0;
        Console.WriteLine($"  outputs changed     : moved {moved.Count}, now at {carried:0.00}s");

        if (moved.Count != 1)
        {
            Console.WriteLine("FAIL - the playing song was not moved onto the new outputs.");
            failures++;
        }
        else
        {
            // From where it was, not from the start: a little after, never back near zero.
            if (carried < reached - 0.25 || carried > reached + 2.0)
            {
                Console.WriteLine($"FAIL - it should have carried on from about {reached:0.00}s.");
                failures++;
            }

            if (moved[0].To.IsCompleted)
            {
                Console.WriteLine("FAIL - the moved song is not playing.");
                failures++;
            }

            Thread.Sleep(300);

            if (!moved[0].From.IsCompleted)
            {
                Console.WriteLine("FAIL - the old copy is still playing through the old outputs.");
                failures++;
            }

            var copies = engine.Active.Count(h => h.SoundId == entry.Id && !h.IsCompleted);
            Console.WriteLine($"  copies playing      : {copies}");
            if (copies != 1) { Console.WriteLine("FAIL - expected exactly one copy of the song."); failures++; }

            // ---- paused stays paused ------------------------------------------------
            moved[0].To.Pause();
            Thread.Sleep(150);

            var back = engine.Reroute(() =>
            {
                audio.MonitorEnabled = monitorWas;
                engine.ApplyAudioSettings();
            }, Find);

            var stillPaused = back.Count == 1 && back[0].To.IsPaused;
            Console.WriteLine($"  paused, then moved  : moved {back.Count}, still paused: {stillPaused}");
            if (!stillPaused) { Console.WriteLine("FAIL - a paused song started playing when it moved."); failures++; }
        }

        engine.StopAll(0);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - a playing song follows the outputs, from where it had got to."
            : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }
}
