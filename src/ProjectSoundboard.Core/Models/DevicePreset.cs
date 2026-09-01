namespace ProjectSoundboard.Core.Models;

/// <summary>
/// A named routing setup, so switching between the way one voice app is wired and another
/// is one click rather than four dropdowns. VRChat listening to a real microphone and
/// Discord listening to the virtual cable is the case this exists for.
///
/// Routing and levels, for the outputs and for the microphone alike. Which microphone, whether
/// it is passed through, how loud it is and whether it waits for a key are all part of how one
/// app is wired and another is not, and leaving them out meant switching preset still left the
/// microphone to be set by hand.
///
/// Not the EQ, the noise gate, or the rest of the processing. Those are how the voice is made
/// to sound rather than where it goes, they are set once and left, and a preset that quietly
/// changed them as well would be impossible to reason about.
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

    /// <summary>
    /// How loud the microphone is in each setup. A headset held to the face in VR is not at
    /// the same level as a desk microphone spoken across, so carrying the device without the
    /// level got half the job done and left the other half to be set by hand every time.
    /// </summary>
    public float MicInputGain { get; set; } = 1.0f;

    public float MicOutputGain { get; set; } = 1.0f;
    public float MicMonitorVolume { get; set; } = 0.4f;

    /// <summary>
    /// Whether the microphone only opens while the key is held. One app doing the talking
    /// over an open mic and another wanting push to talk is the same reason presets exist.
    /// </summary>
    public bool MicPushToTalkEnabled { get; set; }

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
