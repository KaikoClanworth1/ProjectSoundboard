using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ProjectSoundboard.App.ViewModels;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();

        // Capture at the tunnel stage so keys that WPF would otherwise treat as navigation
        // (Tab, arrows, Space) can still be assigned as hotkeys.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private HotkeysViewModel? ViewModel => DataContext as HotkeysViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.CapturingRow is null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        if (vm.HandleCapturedKey(virtualKey, modifiers)) e.Handled = true;
    }
}
