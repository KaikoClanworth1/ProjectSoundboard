using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>One row in the hotkeys table.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly HotkeysViewModel _owner;

    public HotkeyRowViewModel(HotkeyBinding binding, HotkeysViewModel owner, string? soundName)
    {
        Binding = binding;
        _owner = owner;
        _soundName = soundName;
        _enabled = binding.Enabled;
        _action = binding.Action;
    }

    public HotkeyBinding Binding { get; }

    [ObservableProperty] private HotkeyAction _action;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _soundName;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _hasConflict;

    public string KeyText => IsCapturing ? "Press a key…" : Binding.ToString();

    public bool RequiresSound => Action == HotkeyAction.PlaySound;

    public string ActionText => Action switch
    {
        HotkeyAction.PlaySound => SoundName is null ? "Play sound (none chosen)" : $"Play “{SoundName}”",
        HotkeyAction.StopAll => "Stop all sounds",
        HotkeyAction.PauseResume => "Pause / resume",
        HotkeyAction.Next => "Next sound",
        HotkeyAction.Previous => "Previous sound",
        HotkeyAction.Random => "Play a random sound",
        HotkeyAction.MuteMicrophone => "Mute microphone",
        HotkeyAction.MuteSoundboard => "Mute soundboard",
        HotkeyAction.MuteVirtualMic => "Mute everything to voice chat",
        HotkeyAction.TogglePassthrough => "Toggle mic passthrough",
        HotkeyAction.PushToTalk => "Push to talk (hold)",
        HotkeyAction.VolumeUp => "Volume up",
        HotkeyAction.VolumeDown => "Volume down",
        HotkeyAction.ShowHideWindow => "Show / hide window",
        _ => Action.ToString()
    };

    partial void OnActionChanged(HotkeyAction value)
    {
        Binding.Action = value;
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(RequiresSound));
        _owner.Persist();
    }

    partial void OnEnabledChanged(bool value)
    {
        Binding.Enabled = value;
        _owner.Persist();
    }

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(KeyText));

    public void SetKey(int virtualKey, HotkeyModifiers modifiers)
    {
        Binding.VirtualKey = virtualKey;
        Binding.Modifiers = modifiers;
        IsCapturing = false;

        OnPropertyChanged(nameof(KeyText));
        _owner.Persist();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(ActionText));
    }
}

/// <summary>Hotkeys page: assign, capture and validate global shortcuts.</summary>
public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly AppServices _services;

    public HotkeysViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = new();

    public IReadOnlyList<HotkeyAction> Actions { get; } =
        Enum.GetValues<HotkeyAction>().ToArray();

    [ObservableProperty] private HotkeyRowViewModel? _capturingRow;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private int _conflictCount;

    public void Reload()
    {
        Rows.Clear();

        foreach (var binding in _services.Settings.Settings.Hotkeys)
        {
            var soundName = binding.SoundId is null
                ? null
                : _services.Library.GetById(binding.SoundId)?.DisplayName;

            Rows.Add(new HotkeyRowViewModel(binding, this, soundName));
        }

        RefreshConflicts();
    }

    private void RefreshConflicts()
    {
        var conflicts = _services.Hotkeys.Conflicts
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in Rows) row.HasConflict = conflicts.Contains(row.Binding.Id);

        ConflictCount = conflicts.Count;
        StatusText = ConflictCount == 0
            ? string.Empty
            : $"{ConflictCount} hotkey(s) are already used by another application and were not registered.";
    }

    /// <summary>Write the bindings back and re-register them with Windows.</summary>
    public void Persist()
    {
        _services.Settings.Save();
        _services.Hotkeys.RegisterAll();
        RefreshConflicts();
    }

    [RelayCommand]
    private void AddHotkey()
    {
        var binding = new HotkeyBinding { Action = HotkeyAction.StopAll };
        _services.Settings.Settings.Hotkeys.Add(binding);

        var row = new HotkeyRowViewModel(binding, this, null);
        Rows.Add(row);

        Persist();
        BeginCapture(row);
    }

    /// <summary>Bind a key directly to one sound — the common case for a soundboard.</summary>
    public HotkeyRowViewModel AddForSound(SoundViewModel sound)
    {
        var binding = new HotkeyBinding
        {
            Action = HotkeyAction.PlaySound,
            SoundId = sound.Id
        };

        _services.Settings.Settings.Hotkeys.Add(binding);

        var row = new HotkeyRowViewModel(binding, this, sound.DisplayName);
        Rows.Add(row);

        Persist();
        return row;
    }

    [RelayCommand]
    private void RemoveHotkey(HotkeyRowViewModel? row)
    {
        if (row is null) return;

        _services.Settings.Settings.Hotkeys.Remove(row.Binding);
        Rows.Remove(row);

        if (CapturingRow == row) CapturingRow = null;
        Persist();
    }

    [RelayCommand]
    private void BeginCapture(HotkeyRowViewModel? row)
    {
        if (row is null) return;

        if (CapturingRow is not null) CapturingRow.IsCapturing = false;

        CapturingRow = row;
        row.IsCapturing = true;
        StatusText = "Press the key combination you want to use, or Esc to cancel.";
    }

    [RelayCommand]
    private void CancelCapture()
    {
        if (CapturingRow is not null) CapturingRow.IsCapturing = false;
        CapturingRow = null;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void ClearKey(HotkeyRowViewModel? row)
    {
        row?.SetKey(0, HotkeyModifiers.None);
    }

    /// <summary>
    /// Called by the view's key handler while a row is capturing. Returns true when the
    /// key was consumed.
    /// </summary>
    public bool HandleCapturedKey(int virtualKey, HotkeyModifiers modifiers)
    {
        var row = CapturingRow;
        if (row is null) return false;

        // Esc cancels; a bare modifier is not a usable hotkey on its own.
        if (virtualKey == 0x1B && modifiers == HotkeyModifiers.None)
        {
            CancelCapture();
            return true;
        }

        if (IsModifierKey(virtualKey)) return true;

        var duplicate = Rows.FirstOrDefault(r =>
            r != row && r.Binding.VirtualKey == virtualKey && r.Binding.Modifiers == modifiers);

        if (duplicate is not null)
        {
            StatusText = $"That combination is already used for “{duplicate.ActionText}”.";
            return true;
        }

        if (!_services.Hotkeys.IsAvailable(virtualKey, modifiers))
        {
            StatusText = "Another application already owns that combination. Try a different one.";
            return true;
        }

        row.SetKey(virtualKey, modifiers);
        CapturingRow = null;
        StatusText = $"Assigned {row.Binding}.";
        return true;
    }

    private static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
}
