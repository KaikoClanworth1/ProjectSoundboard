namespace ProjectSoundboard.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

/// <summary>A single global (system wide) hotkey assignment.</summary>
public sealed class HotkeyBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public HotkeyAction Action { get; set; }

    /// <summary>Target sound id — only used when <see cref="Action"/> is PlaySound.</summary>
    public string? SoundId { get; set; }

    /// <summary>Win32 virtual key code.</summary>
    public int VirtualKey { get; set; }

    public HotkeyModifiers Modifiers { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsValid => VirtualKey != 0;

    public override string ToString()
    {
        if (!IsValid) return "Not set";
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.Describe(VirtualKey));
        return string.Join(" + ", parts);
    }
}

/// <summary>Friendly names for the virtual key codes we are likely to see.</summary>
public static class KeyNames
{
    public static string Describe(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key {vk}"
    };
}
