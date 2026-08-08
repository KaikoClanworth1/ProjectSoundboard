namespace ProjectSoundboard.Core.Models;

/// <summary>
/// A named routing setup, so switching between the way one voice app is wired and another
/// is one click rather than four dropdowns. VRChat listening to a real microphone and
/// Discord listening to the virtual cable is the case this exists for.
///
/// Deliberately only routing and levels. Presets that quietly changed the EQ or the noise
/// gate as well would be impossible to reason about — those stay where you set them.
/// </summary>
public sealed class DevicePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New preset";

    // ---- outputs ----------------------------------------------------------

    public string? VirtualMicDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public bool VirtualMicEnabled { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;

    public float VirtualMicVolume { get; set; } = 1.0f;
    public float MonitorVolume { get; set; } = 0.8f;
    public float MasterVolume { get; set; } = 1.0f;

    // ---- microphone -------------------------------------------------------

    public string? MicInputDeviceId { get; set; }
    public bool MicPassthroughEnabled { get; set; }
    public bool MicMonitorEnabled { get; set; }

    // ---- remembered names -------------------------------------------------

    /// <summary>
    /// Device names as they were when the preset was made. Ids are opaque and a device that
    /// is unplugged has no name to look up, so without these a preset could not say what it
    /// was pointing at.
    /// </summary>
    public string? VirtualMicDeviceName { get; set; }

    public string? MonitorDeviceName { get; set; }
    public string? MicInputDeviceName { get; set; }

    public DevicePreset Clone() => (DevicePreset)MemberwiseClone();
}
