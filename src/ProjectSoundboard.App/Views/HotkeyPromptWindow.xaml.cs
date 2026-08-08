using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

/// <summary>
/// Asks for a key combination. Used to put a keybind on one sound without sending the user
/// off to the hotkeys page to hunt for the row.
/// </summary>
public partial class HotkeyPromptWindow : Window
{
    private readonly AppServices _services;
    private readonly string? _ignoreBindingId;

    public HotkeyPromptWindow(AppServices services, string soundName,
                              HotkeyBinding? existing = null)
    {
        InitializeComponent();

        _services = services;
        _ignoreBindingId = existing?.Id;

        TitleText.Text = $"Keybind for “{soundName}”";

        if (existing is { VirtualKey: not 0 })
        {
            VirtualKey = existing.VirtualKey;
            Modifiers = existing.Modifiers;
            KeyText.Text = existing.ToString();
            AcceptButton.IsEnabled = true;
        }

        ClearButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;

        // Tunnelling, so keys WPF would otherwise spend on navigation — Tab, the arrows,
        // Space — can still be assigned.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public int VirtualKey { get; private set; }
    public HotkeyModifiers Modifiers { get; private set; }

    /// <summary>True when the user asked for the keybind to be taken off.</summary>
    public bool Cleared { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        if (virtualKey == 0x1B && Keyboard.Modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }

        // A modifier on its own is not a hotkey; wait for the key it goes with.
        if (IsModifierKey(virtualKey)) return;

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        var candidate = new HotkeyBinding { VirtualKey = virtualKey, Modifiers = modifiers };
        KeyText.Text = candidate.ToString();

        // Say what is wrong straight away rather than accepting it and silently not working.
        var clash = _services.Settings.Settings.Hotkeys.FirstOrDefault(b =>
            b.Id != _ignoreBindingId && b.VirtualKey == virtualKey && b.Modifiers == modifiers);

        if (clash is not null)
        {
            Problem(clash.SoundId is not null
                ? $"Already used by “{NameOf(clash.SoundId)}”."
                : "Already used by another shortcut in this app.");
            return;
        }

        if (!_services.Hotkeys.IsAvailable(virtualKey, modifiers))
        {
            Problem("Another application already owns that combination.");
            return;
        }

        ProblemText.Visibility = Visibility.Collapsed;
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        AcceptButton.IsEnabled = true;
    }

    private string NameOf(string soundId) =>
        _services.Library.GetById(soundId)?.DisplayName ?? "another sound";

    private void Problem(string message)
    {
        ProblemText.Text = message;
        ProblemText.Visibility = Visibility.Visible;
        AcceptButton.IsEnabled = false;
    }

    private static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        DialogResult = true;
    }
}
