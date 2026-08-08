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

    /// <summary>Everything, unfiltered. <see cref="Rows"/> is what the page shows.</summary>
    private readonly List<HotkeyRowViewModel> _all = new();

    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = new();

    public IReadOnlyList<HotkeyAction> Actions { get; } =
        Enum.GetValues<HotkeyAction>().ToArray();

    [ObservableProperty] private HotkeyRowViewModel? _capturingRow;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private int _conflictCount;

    /// <summary>
    /// Filters the list. With a keybind per sound this page becomes the place you go to
    /// find out what is bound to what, and scrolling a few hundred rows is no way to do it.
    /// </summary>
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>How many of the bindings are per-sound, shown as a count on the page.</summary>
    public int SoundBindingCount => _all.Count(r => r.Action == HotkeyAction.PlaySound);

    public int TotalCount => _all.Count;

    public string SummaryText =>
        Rows.Count == _all.Count
            ? $"{_all.Count} keybind{(_all.Count == 1 ? "" : "s")}, {SoundBindingCount} on sounds"
            : $"{Rows.Count} of {_all.Count} keybinds";

    public void Reload()
    {
        _all.Clear();

        foreach (var binding in _services.Settings.Settings.Hotkeys)
        {
            var soundName = binding.SoundId is null
                ? null
                : _services.Library.GetById(binding.SoundId)?.DisplayName;

            _all.Add(new HotkeyRowViewModel(binding, this, soundName));
        }

        ApplyFilter();
        RefreshConflicts();
    }

    /// <summary>Match on what is written on the row: the action, the sound and the keys.</summary>
    private void ApplyFilter()
    {
        var query = SearchText?.Trim();

        Rows.Clear();

        foreach (var row in _all)
        {
            if (!string.IsNullOrEmpty(query) && !Matches(row, query)) continue;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SoundBindingCount));
    }

    private static bool Matches(HotkeyRowViewModel row, string query) =>
        row.ActionText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (row.SoundName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || row.KeyText.Contains(query, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void RefreshConflicts()
    {
        var conflicts = _services.Hotkeys.Conflicts
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in _all) row.HasConflict = conflicts.Contains(row.Binding.Id);

        ConflictCount = conflicts.Count;

        // A key that was taken from the whole machine is a bigger deal than a clash, so it
        // gets said first and in full.
        var disabled = _services.Hotkeys.Disabled;
        DisabledCount = disabled.Count;

        if (disabled.Count > 0)
        {
            var names = string.Join(", ", disabled.Select(d => d.ToString()));
            DisabledText =
                $"Switched off: {names}. A key with no Ctrl, Alt or Win is taken from every " +
                "other application while Project Soundboard is open — that is why it seemed " +
                "to stop working elsewhere. Give it a modifier and switch it back on.";
        }
        else
        {
            DisabledText = string.Empty;
        }

        StatusText = ConflictCount == 0
            ? string.Empty
            : $"{ConflictCount} hotkey(s) are already used by another application and were not registered.";
    }

    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private string _disabledText = string.Empty;

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
        _all.Add(row);

        // A new row must not land behind the current filter, or it looks like nothing happened.
        SearchText = string.Empty;
        ApplyFilter();

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
        _all.Add(row);
        ApplyFilter();

        Persist();
        return row;
    }

    /// <summary>
    /// Pick a sound, then a key. The other direction — starting from the sound — is the
    /// context menu; this is here for when you are already looking at the list.
    /// </summary>
    [RelayCommand]
    private void AddSoundHotkey()
    {
        var picker = new Views.SoundPickerWindow(_services)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (picker.ShowDialog() != true || picker.Selected is not { } entry) return;

        if (AssignTo(entry.Id, entry.DisplayName)) SearchText = entry.DisplayName;
    }

    /// <summary>
    /// Ask for a key and put it on a sound, replacing whatever that sound already had.
    /// Shared with the sound's own context menu so both routes behave identically — the old
    /// one added a second binding every time instead of editing the one already there.
    /// </summary>
    public bool AssignTo(string soundId, string displayName)
    {
        var existing = _services.Settings.Settings.Hotkeys.FirstOrDefault(
            b => b.Action == HotkeyAction.PlaySound &&
                 string.Equals(b.SoundId, soundId, StringComparison.Ordinal));

        var dialog = new Views.HotkeyPromptWindow(_services, displayName, existing)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true) return false;

        if (dialog.Cleared)
        {
            if (existing is not null) _services.Settings.Settings.Hotkeys.Remove(existing);
            StatusText = $"Keybind removed from “{displayName}”.";
        }
        else
        {
            var binding = existing;
            if (binding is null)
            {
                binding = new HotkeyBinding { Action = HotkeyAction.PlaySound, SoundId = soundId };
                _services.Settings.Settings.Hotkeys.Add(binding);
            }

            binding.VirtualKey = dialog.VirtualKey;
            binding.Modifiers = dialog.Modifiers;
            binding.Enabled = true;

            StatusText = $"{binding} plays “{displayName}”.";
        }

        _services.Settings.Save();
        _services.Hotkeys.RegisterAll();
        Reload();
        return true;
    }

    [RelayCommand]
    private void RemoveHotkey(HotkeyRowViewModel? row)
    {
        if (row is null) return;

        _services.Settings.Settings.Hotkeys.Remove(row.Binding);
        _all.Remove(row);
        Rows.Remove(row);
        OnPropertyChanged(nameof(SummaryText));

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

        // These are global, so a plain key is taken from every other application too.
        if (!HotkeyRules.IsSafe(virtualKey, modifiers))
        {
            StatusText = HotkeyRules.Explain(virtualKey, modifiers);
            return true;
        }

        // Against everything, not just what the filter happens to be showing.
        var duplicate = _all.FirstOrDefault(r =>
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
