using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

public enum DeviceKind
{
    Output,
    Input
}

public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>True when the name matches a known virtual audio cable driver.</summary>
    public bool IsVirtualCable { get; init; }

    /// <summary>Native mix format sample rate, useful for the latency estimate.</summary>
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Enumerates WASAPI endpoints and watches for devices being plugged in or removed.
/// </summary>
public sealed class AudioDeviceService : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;

    /// <summary>
    /// Name fragments used to spot a virtual microphone cable. The soundboard needs one of
    /// these as its output so that Discord/VRChat can pick it up as a microphone.
    /// </summary>
    private static readonly string[] VirtualCableHints =
    {
        "CABLE Input", "CABLE-A Input", "CABLE-B Input", "CABLE-C Input", "CABLE-D Input",
        "VB-Audio", "VB-Cable", "Voicemeeter Input", "Voicemeeter Aux Input",
        "Voicemeeter VAIO3 Input", "Virtual Audio Cable", "VoiceMeeter", "Line 1 (Virtual Audio Cable)",
        "Virtual Cable", "Soundboard Virtual"
    };

    private static readonly string[] VirtualCaptureHints =
    {
        "CABLE Output", "VB-Audio Virtual Cable", "Voicemeeter Out", "Virtual Audio Cable"
    };

    /// <summary>Raised when Windows reports a device added, removed or defaulted.</summary>
    public event EventHandler? DevicesChanged;

    public AudioDeviceService()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Device notifications unavailable: {ex.Message}");
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(DeviceKind kind)
    {
        var flow = kind == DeviceKind.Output ? DataFlow.Render : DataFlow.Capture;
        var result = new List<AudioDeviceInfo>();

        string? defaultId = null;
        try
        {
            var def = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = def.ID;
        }
        catch { /* no device at all */ }

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    var name = device.FriendlyName;
                    var hints = kind == DeviceKind.Output ? VirtualCableHints : VirtualCaptureHints;

                    result.Add(new AudioDeviceInfo
                    {
                        Id = device.ID,
                        Name = name,
                        Kind = kind,
                        IsDefault = device.ID == defaultId,
                        IsVirtualCable = hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)),
                        SampleRate = device.AudioClient.MixFormat.SampleRate,
                        Channels = device.AudioClient.MixFormat.Channels
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug($"Skipping endpoint: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Device enumeration failed", ex);
        }

        return result
            .OrderByDescending(d => d.IsVirtualCable)
            .ThenByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Resolve a saved device id, falling back to the system default.</summary>
    public MMDevice? Resolve(string? deviceId, DeviceKind kind)
    {
        var flow = kind == DeviceKind.Output ? DataFlow.Render : DataFlow.Capture;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try { return _enumerator.GetDevice(deviceId); }
            catch (Exception ex) { Log.Warn($"Device {deviceId} unavailable: {ex.Message}"); }
        }

        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch (Exception ex)
        {
            Log.Error($"No default {kind} device available", ex);
            return null;
        }
    }

    /// <summary>Best guess at the virtual cable to use as the soundboard microphone.</summary>
    public AudioDeviceInfo? DetectVirtualMicrophone() =>
        GetDevices(DeviceKind.Output).FirstOrDefault(d => d.IsVirtualCable);

    /// <summary>True when any recognised virtual cable driver is installed.</summary>
    public bool HasVirtualCable() => DetectVirtualMicrophone() is not null;

    /// <summary>The capture endpoint that pairs with a virtual cable output, if present.</summary>
    public AudioDeviceInfo? DetectVirtualCaptureDevice() =>
        GetDevices(DeviceKind.Input).FirstOrDefault(d => d.IsVirtualCable);

    // ---- IMMNotificationClient -------------------------------------------

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
    public void OnDeviceAdded(string pwstrDeviceId) => Raise();
    public void OnDeviceRemoved(string deviceId) => Raise();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Raise();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private void Raise()
    {
        try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Debug($"DevicesChanged handler threw: {ex.Message}"); }
    }

    public void Dispose()
    {
        try
        {
            if (_registered) _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch { /* ignore */ }
        _enumerator.Dispose();
    }
}
