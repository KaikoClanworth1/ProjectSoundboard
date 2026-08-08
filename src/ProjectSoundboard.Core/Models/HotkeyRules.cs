namespace ProjectSoundboard.Core.Models;

/// <summary>
/// What is safe to register as a *global* hotkey.
///
/// A global hotkey is taken from the whole machine, not just from this application. Binding
/// a plain letter therefore stops that letter reaching anything else — bind S on its own and
/// nobody can type an s anywhere in Windows until the soundboard is closed. That has happened,
/// and nothing warned about it, so the rule is enforced here rather than left to the user to
/// discover.
/// </summary>
public static class HotkeyRules
{
    /// <summary>
    /// Keys that may be bound with no modifier. Function keys and media keys do not produce
    /// text, so taking them machine-wide costs nothing.
    ///
    /// The numpad is included on purpose: it is the surface soundboards have always used and
    /// people dedicate it to exactly this. It is the one exception that can still swallow
    /// typing, and it is a deliberate trade.
    /// </summary>
    public static bool CanStandAlone(int virtualKey) =>
        virtualKey is >= 0x70 and <= 0x87        // F1 – F24
            or >= 0x60 and <= 0x6F               // numpad digits and operators
            or >= 0xA6 and <= 0xB7               // browser / media / launch keys
            or 0x13                              // Pause
            or 0x91                              // Scroll Lock
            or 0x2D;                             // Insert

    /// <summary>
    /// Shift does not count. Shift+D is still how you type a capital D, so claiming it
    /// globally breaks ordinary typing just as thoroughly as D on its own.
    /// </summary>
    public static bool HasRealModifier(HotkeyModifiers modifiers) =>
        modifiers.HasFlag(HotkeyModifiers.Control)
        || modifiers.HasFlag(HotkeyModifiers.Alt)
        || modifiers.HasFlag(HotkeyModifiers.Win);

    public static bool IsSafe(int virtualKey, HotkeyModifiers modifiers) =>
        virtualKey != 0 && (CanStandAlone(virtualKey) || HasRealModifier(modifiers));

    public static bool IsSafe(HotkeyBinding binding) =>
        IsSafe(binding.VirtualKey, binding.Modifiers);

    /// <summary>Why a combination was refused, in words that say what to do about it.</summary>
    public static string Explain(int virtualKey, HotkeyModifiers modifiers)
    {
        var name = new HotkeyBinding { VirtualKey = virtualKey, Modifiers = modifiers }.ToString();

        return modifiers.HasFlag(HotkeyModifiers.Shift) && !HasRealModifier(modifiers)
            ? $"{name} would be taken from every other application — Shift on its own is still " +
              "how you type, so add Ctrl, Alt or Win. Function keys and the numpad work without one."
            : $"{name} would be taken from every other application, so you could not use that key " +
              "anywhere else. Add Ctrl, Alt or Win. Function keys and the numpad work without one.";
    }
}
