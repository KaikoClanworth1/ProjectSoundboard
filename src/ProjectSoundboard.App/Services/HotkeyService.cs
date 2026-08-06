using System.Runtime.InteropServices;
using System.Windows.Interop;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// System-wide hotkeys. Ordinary actions use RegisterHotKey, which is well behaved and
/// does not look like a keylogger to anti-cheat software. Push-to-talk additionally needs
/// key-release events, which RegisterHotKey cannot provide, so it uses a low level hook
/// that only ever inspects the one key the user assigned.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private readonly SettingsService _settings;

    private HwndSource? _source;
    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc; // kept alive so the GC cannot collect it
    private int _pushToTalkKey;
    private bool _pushToTalkHeld;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(SettingsService settings) => _settings = settings;

    /// <summary>Raised on the UI thread when a registered hotkey fires.</summary>
    public event EventHandler<HotkeyBinding>? HotkeyPressed;

    /// <summary>Raised when the push-to-talk key goes down or comes back up.</summary>
    public event EventHandler<bool>? PushToTalkChanged;

    /// <summary>Bindings that Windows refused, usually because another app owns them.</summary>
    public IReadOnlyList<HotkeyBinding> Conflicts { get; private set; } = Array.Empty<HotkeyBinding>();

    public bool IsAttached => _source is not null;

    /// <summary>Attach to the main window's message loop. Call once, after the handle exists.</summary>
    public void Attach(IntPtr windowHandle)
    {
        if (_source is not null) return;

        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);

        RegisterAll();
    }

    /// <summary>Re-read the bindings from settings and re-register everything.</summary>
    public void RegisterAll()
    {
        UnregisterAll();
        if (_source is null) return;

        var conflicts = new List<HotkeyBinding>();
        var handle = _source.Handle;

        foreach (var binding in _settings.Settings.Hotkeys)
        {
            if (!binding.Enabled || !binding.IsValid) continue;

            // Push-to-talk is driven by the hook, not RegisterHotKey, because it needs
            // to know when the key is released.
            if (binding.Action == HotkeyAction.PushToTalk)
            {
                _pushToTalkKey = binding.VirtualKey;
                continue;
            }

            var id = _nextId++;
            var modifiers = ToWin32(binding.Modifiers) | ModNoRepeat;

            if (RegisterHotKey(handle, id, modifiers, (uint)binding.VirtualKey))
            {
                _registered[id] = binding;
            }
            else
            {
                conflicts.Add(binding);
                Log.Warn($"Hotkey '{binding}' for {binding.Action} is already taken by another application.");
            }
        }

        Conflicts = conflicts;
        UpdatePushToTalkHook();

        Log.Info($"Registered {_registered.Count} global hotkey(s), {conflicts.Count} conflict(s).");
    }

    public void UnregisterAll()
    {
        if (_source is null) return;

        foreach (var id in _registered.Keys.ToList())
        {
            try { UnregisterHotKey(_source.Handle, id); } catch { /* already gone */ }
        }

        _registered.Clear();
        _pushToTalkKey = 0;
        Conflicts = Array.Empty<HotkeyBinding>();
    }

    /// <summary>
    /// Check whether a key combination is free before the user commits to it.
    /// Registers and immediately releases, so it never leaves state behind.
    /// </summary>
    public bool IsAvailable(int virtualKey, HotkeyModifiers modifiers)
    {
        if (_source is null || virtualKey == 0) return true;

        var id = 0xBFFF; // a fixed probe id, outside the range we hand out
        var ok = RegisterHotKey(_source.Handle, id, ToWin32(modifiers) | ModNoRepeat, (uint)virtualKey);
        if (ok) UnregisterHotKey(_source.Handle, id);
        return ok;
    }

    private static uint ToWin32(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= ModControl;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= ModShift;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) result |= ModWin;
        return result;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        var id = wParam.ToInt32();
        if (!_registered.TryGetValue(id, out var binding)) return IntPtr.Zero;

        handled = true;

        try { HotkeyPressed?.Invoke(this, binding); }
        catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }

        return IntPtr.Zero;
    }

    // ---- push to talk -----------------------------------------------------

    private void UpdatePushToTalkHook()
    {
        var wanted = _pushToTalkKey != 0 && _settings.Settings.Microphone.PushToTalkEnabled;

        if (wanted && _hookHandle == IntPtr.Zero)
        {
            _hookProc = PushToTalkHook;
            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);

            if (_hookHandle == IntPtr.Zero)
                Log.Warn("Push-to-talk hook could not be installed.");
            else
                Log.Info("Push-to-talk hook installed.");
        }
        else if (!wanted && _hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookProc = null;

            if (_pushToTalkHeld)
            {
                _pushToTalkHeld = false;
                PushToTalkChanged?.Invoke(this, false);
            }
        }
    }

    private IntPtr PushToTalkHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // The hook runs for every keystroke on the machine, so this path must stay
        // trivial: read the key code, ignore anything that is not our one key, pass on.
        if (nCode >= 0 && _pushToTalkKey != 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (data.vkCode == (uint)_pushToTalkKey)
                {
                    var message = wParam.ToInt32();

                    if (message is WmKeyDown or WmSysKeyDown && !_pushToTalkHeld)
                    {
                        _pushToTalkHeld = true;
                        PushToTalkChanged?.Invoke(this, true);
                    }
                    else if (message is WmKeyUp or WmSysKeyUp && _pushToTalkHeld)
                    {
                        _pushToTalkHeld = false;
                        PushToTalkChanged?.Invoke(this, false);
                    }
                }
            }
            catch { /* never let a hook exception break the whole system's input */ }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>Call after toggling push-to-talk in settings.</summary>
    public void RefreshPushToTalk() => UpdatePushToTalkHook();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
