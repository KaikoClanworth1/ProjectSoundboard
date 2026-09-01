using ProjectSoundboard.Core.Storage;

namespace PresetProbe;

/// <summary>
/// Whether switching preset really switches the microphone.
///
/// The case this is written for is the one presets exist for: VR listening to a headset with
/// the microphone passed through and turned up, and a desk setup with it out of the mix
/// entirely. Carrying which device but not whether it is used, or not how loud it is, leaves
/// half of that to be set by hand every time — which is the whole thing presets are meant to
/// save.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var settings = new SettingsService();
        settings.Load();

        var presets = new PresetService(settings);
        var audio = settings.Settings.Audio;
        var mic = settings.Settings.Microphone;

        settings.Settings.Presets.Clear();

        // ---- the VR setup ---------------------------------------------------
        audio.VirtualMicVolume = 0.9f;
        audio.MasterVolume = 0.7f;
        mic.InputDeviceId = "headset-mic";
        mic.PassthroughEnabled = true;
        mic.MonitorEnabled = true;
        mic.InputGain = 1.6f;
        mic.OutputGain = 1.1f;
        mic.MonitorVolume = 0.55f;
        mic.PushToTalkEnabled = false;

        var vr = presets.Add("VR", _ => "Headset");

        // ---- the desk setup, with the microphone out of it -------------------
        audio.VirtualMicVolume = 1.0f;
        audio.MasterVolume = 1.0f;
        mic.InputDeviceId = "desk-mic";
        mic.PassthroughEnabled = false;
        mic.MonitorEnabled = false;
        mic.InputGain = 0.8f;
        mic.OutputGain = 1.0f;
        mic.MonitorVolume = 0.2f;
        mic.PushToTalkEnabled = true;

        var desk = presets.Add("Desk", _ => "Desk mic");

        var failures = 0;

        // ---- switch back to VR ----------------------------------------------
        presets.Apply(vr);

        failures += Check("VR: microphone device", mic.InputDeviceId, "headset-mic");
        failures += Check("VR: passed through", mic.PassthroughEnabled, true);
        failures += Check("VR: heard back", mic.MonitorEnabled, true);
        failures += Check("VR: input gain", mic.InputGain, 1.6f);
        failures += Check("VR: output gain", mic.OutputGain, 1.1f);
        failures += Check("VR: monitor volume", mic.MonitorVolume, 0.55f);
        failures += Check("VR: push to talk", mic.PushToTalkEnabled, false);
        failures += Check("VR: master volume", audio.MasterVolume, 0.7f);

        Console.WriteLine();

        // ---- and to the desk --------------------------------------------------
        presets.Apply(desk);

        failures += Check("Desk: microphone device", mic.InputDeviceId, "desk-mic");
        failures += Check("Desk: passed through", mic.PassthroughEnabled, false);
        failures += Check("Desk: input gain", mic.InputGain, 0.8f);
        failures += Check("Desk: monitor volume", mic.MonitorVolume, 0.2f);
        failures += Check("Desk: push to talk", mic.PushToTalkEnabled, true);
        failures += Check("Desk: master volume", audio.MasterVolume, 1.0f);

        // ---- the processing is not a preset's business -------------------------
        Console.WriteLine();

        var gateBefore = mic.GateThresholdDb;
        mic.GateThresholdDb = -33f;
        presets.Apply(vr);

        failures += Check("the noise gate is left alone", mic.GateThresholdDb, -33f);
        mic.GateThresholdDb = gateBefore;

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS - a preset carries the whole setup, microphone and all."
            : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }

    private static int Check<T>(string what, T actual, T expected)
    {
        var ok = EqualEnough(actual, expected);
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what,-30} {actual}");

        if (!ok) Console.WriteLine($"        expected {expected}");
        return ok ? 0 : 1;
    }

    private static bool EqualEnough<T>(T a, T b) =>
        a is float x && b is float y
            ? Math.Abs(x - y) < 0.0001f
            : Equals(a, b);
}
