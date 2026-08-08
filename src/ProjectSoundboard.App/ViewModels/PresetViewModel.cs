using CommunityToolkit.Mvvm.ComponentModel;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>One saved routing setup, as the audio page shows it.</summary>
public sealed partial class PresetViewModel : ObservableObject
{
    public PresetViewModel(DevicePreset preset, bool isActive, string? unavailable)
    {
        Preset = preset;
        _isActive = isActive;
        _unavailableText = unavailable;
    }

    public DevicePreset Preset { get; }

    public string Name => Preset.Name;

    [ObservableProperty] private bool _isActive;

    /// <summary>Set when a device this preset points at is not plugged in.</summary>
    [ObservableProperty] private string? _unavailableText;

    public bool IsUnavailable => UnavailableText is not null;

    /// <summary>
    /// The routing in one line, so a preset can be told apart without opening it. Named
    /// after the devices rather than their ids, which are meaningless to read.
    /// </summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();

            parts.Add(Preset.VirtualMicEnabled
                ? $"Voice chat: {Short(Preset.VirtualMicDeviceName)}"
                : "Voice chat: off");

            parts.Add(Preset.MonitorEnabled
                ? $"You hear: {Short(Preset.MonitorDeviceName)}"
                : "You hear: off");

            if (Preset.MicPassthroughEnabled)
                parts.Add($"Mic: {Short(Preset.MicInputDeviceName)}");

            return string.Join("   ·   ", parts);
        }
    }

    private static string Short(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "system default";
        return name.Length > 34 ? name[..33] + "…" : name;
    }
}
