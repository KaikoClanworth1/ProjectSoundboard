using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.Core.Storage;

/// <summary>
/// Saves and restores named routing setups. Knows nothing about audio devices beyond their
/// ids — resolving those to something a person can read is the caller's job, because only
/// the app layer can see the device list.
/// </summary>
public sealed class PresetService
{
    private readonly SettingsService _settings;

    public PresetService(SettingsService settings) => _settings = settings;

    public IReadOnlyList<DevicePreset> Presets => _settings.Settings.Presets;

    public DevicePreset? Active =>
        _settings.Settings.ActivePresetId is { } id
            ? Presets.FirstOrDefault(p => p.Id == id)
            : null;

    /// <summary>
    /// Take a snapshot of how things are wired right now.
    /// <paramref name="deviceName"/> turns a device id into its friendly name.
    /// </summary>
    public DevicePreset Capture(string name, Func<string?, string?> deviceName)
    {
        var audio = _settings.Settings.Audio;
        var mic = _settings.Settings.Microphone;

        return new DevicePreset
        {
            Name = CleanName(name),

            VirtualMicDeviceId = audio.VirtualMicDeviceId,
            MonitorDeviceId = audio.MonitorDeviceId,
            VirtualMicEnabled = audio.VirtualMicEnabled,
            MonitorEnabled = audio.MonitorEnabled,
            VirtualMicVolume = audio.VirtualMicVolume,
            MonitorVolume = audio.MonitorVolume,
            MasterVolume = audio.MasterVolume,

            MicInputDeviceId = mic.InputDeviceId,
            MicPassthroughEnabled = mic.PassthroughEnabled,
            MicMonitorEnabled = mic.MonitorEnabled,
            MicInputGain = mic.InputGain,
            MicOutputGain = mic.OutputGain,
            MicMonitorVolume = mic.MonitorVolume,
            MicPushToTalkEnabled = mic.PushToTalkEnabled,

            VirtualMicDeviceName = deviceName(audio.VirtualMicDeviceId),
            MonitorDeviceName = deviceName(audio.MonitorDeviceId),
            MicInputDeviceName = deviceName(mic.InputDeviceId)
        };
    }

    public DevicePreset Add(string name, Func<string?, string?> deviceName)
    {
        var preset = Capture(name, deviceName);
        _settings.Settings.Presets.Add(preset);
        _settings.Settings.ActivePresetId = preset.Id;
        _settings.Save();
        return preset;
    }

    /// <summary>Point an existing preset at how things are wired now.</summary>
    public void UpdateFromCurrent(DevicePreset preset, Func<string?, string?> deviceName)
    {
        var fresh = Capture(preset.Name, deviceName);

        fresh.Id = preset.Id;
        Copy(fresh, preset);

        _settings.Settings.ActivePresetId = preset.Id;
        _settings.Save();
    }

    /// <summary>
    /// Write a preset back into the live settings. The caller restarts the audio stack —
    /// this only decides what it should restart into.
    /// </summary>
    public void Apply(DevicePreset preset)
    {
        var audio = _settings.Settings.Audio;
        var mic = _settings.Settings.Microphone;

        audio.VirtualMicDeviceId = preset.VirtualMicDeviceId;
        audio.MonitorDeviceId = preset.MonitorDeviceId;
        audio.VirtualMicEnabled = preset.VirtualMicEnabled;
        audio.MonitorEnabled = preset.MonitorEnabled;
        audio.VirtualMicVolume = preset.VirtualMicVolume;
        audio.MonitorVolume = preset.MonitorVolume;
        audio.MasterVolume = preset.MasterVolume;

        mic.InputDeviceId = preset.MicInputDeviceId;
        mic.PassthroughEnabled = preset.MicPassthroughEnabled;
        mic.MonitorEnabled = preset.MicMonitorEnabled;
        mic.InputGain = preset.MicInputGain;
        mic.OutputGain = preset.MicOutputGain;
        mic.MonitorVolume = preset.MicMonitorVolume;
        mic.PushToTalkEnabled = preset.MicPushToTalkEnabled;

        _settings.Settings.ActivePresetId = preset.Id;
        _settings.Save();

        Log.Info($"Preset '{preset.Name}' applied.");
    }

    public void Rename(DevicePreset preset, string name)
    {
        preset.Name = CleanName(name);
        _settings.Save();
    }

    public void Delete(DevicePreset preset)
    {
        _settings.Settings.Presets.Remove(preset);

        if (_settings.Settings.ActivePresetId == preset.Id)
            _settings.Settings.ActivePresetId = null;

        _settings.Save();
    }

    /// <summary>A preset whose devices are all still present can be applied as-is.</summary>
    public bool IsFullyAvailable(DevicePreset preset, Func<string?, bool> deviceExists) =>
        (preset.VirtualMicDeviceId is null || deviceExists(preset.VirtualMicDeviceId))
        && (preset.MonitorDeviceId is null || deviceExists(preset.MonitorDeviceId))
        && (preset.MicInputDeviceId is null || deviceExists(preset.MicInputDeviceId));

    private static string CleanName(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Untitled preset";
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    private static void Copy(DevicePreset from, DevicePreset to)
    {
        to.Name = from.Name;
        to.VirtualMicDeviceId = from.VirtualMicDeviceId;
        to.MonitorDeviceId = from.MonitorDeviceId;
        to.VirtualMicEnabled = from.VirtualMicEnabled;
        to.MonitorEnabled = from.MonitorEnabled;
        to.VirtualMicVolume = from.VirtualMicVolume;
        to.MonitorVolume = from.MonitorVolume;
        to.MasterVolume = from.MasterVolume;
        to.MicInputDeviceId = from.MicInputDeviceId;
        to.MicPassthroughEnabled = from.MicPassthroughEnabled;
        to.MicMonitorEnabled = from.MicMonitorEnabled;
        to.VirtualMicDeviceName = from.VirtualMicDeviceName;
        to.MonitorDeviceName = from.MonitorDeviceName;
        to.MicInputDeviceName = from.MicInputDeviceName;
    }
}
