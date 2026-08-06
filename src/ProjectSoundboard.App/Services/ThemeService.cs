using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Swaps the palette dictionary, applies the user's accent colour, and honours the
/// accessibility options that affect how everything is drawn.
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settings;

    public ThemeService(SettingsService settings) => _settings = settings;

    /// <summary>Raised after the palette changes so views can re-render cached visuals.</summary>
    public event EventHandler? ThemeChanged;

    public AppTheme EffectiveTheme { get; private set; } = AppTheme.Dark;

    public void Apply()
    {
        var appearance = _settings.Settings.Appearance;
        var access = _settings.Settings.Accessibility;

        var theme = access.HighContrast ? AppTheme.HighContrast : appearance.Theme;
        if (theme == AppTheme.System) theme = DetectSystemTheme();

        EffectiveTheme = theme;

        var source = theme switch
        {
            AppTheme.Light => "Themes/Light.xaml",
            AppTheme.HighContrast => "Themes/HighContrast.xaml",
            _ => "Themes/Dark.xaml"
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        if (dictionaries.Count == 0) dictionaries.Add(palette);
        else dictionaries[0] = palette;

        ApplyAccent(appearance.AccentColor, theme);
        ApplyColorBlindAdjustments(access.ColorBlindMode);

        Log.Info($"Theme applied: {theme}.");
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Overwrite the accent brushes with the user's colour, deriving the hover and pressed
    /// shades from it so a custom accent still behaves like the built-in one.
    /// High contrast keeps its own accent — a custom colour would defeat the point.
    /// </summary>
    private void ApplyAccent(string accentHex, AppTheme theme)
    {
        if (theme == AppTheme.HighContrast) return;

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex)!; }
        catch { return; }

        var resources = Application.Current.Resources;
        resources["AccentColor"] = accent;
        resources["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        resources["AccentHoverBrush"] = Freeze(new SolidColorBrush(Shift(accent, 0.16f)));
        resources["AccentPressedBrush"] = Freeze(new SolidColorBrush(Shift(accent, -0.14f)));
        resources["WaveformPlayedBrush"] = Freeze(new SolidColorBrush(accent));

        // A translucent wash of the accent, used for selected rows and soft chips.
        var soft = theme == AppTheme.Light
            ? Blend(accent, Colors.White, 0.86f)
            : Blend(accent, Color.FromRgb(0x11, 0x14, 0x1A), 0.80f);
        resources["AccentSoftBrush"] = Freeze(new SolidColorBrush(soft));

        // Pick black or white text on the accent by luminance, so custom colours stay legible.
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        resources["TextOnAccentBrush"] = Freeze(new SolidColorBrush(
            luminance > 0.6 ? Color.FromRgb(0x10, 0x13, 0x1A) : Colors.White));
    }

    /// <summary>
    /// Re-map the three status colours for colour vision deficiencies. Shape and text
    /// always carry the same information as well, this just widens the gap.
    /// </summary>
    private static void ApplyColorBlindAdjustments(ColorBlindMode mode)
    {
        if (mode == ColorBlindMode.None) return;

        var resources = Application.Current.Resources;

        (Color ok, Color warn, Color bad) = mode switch
        {
            // Red/green pairs are replaced with blue/orange, which stays distinguishable.
            ColorBlindMode.Protanopia or ColorBlindMode.Deuteranopia =>
                (Color.FromRgb(0x38, 0xA1, 0xDB), Color.FromRgb(0xF0, 0xC0, 0x36), Color.FromRgb(0xE0, 0x6C, 0x00)),
            // Blue/yellow deficiency: lean on red/teal instead.
            _ => (Color.FromRgb(0x1B, 0x9E, 0x8A), Color.FromRgb(0xD0, 0x60, 0x90), Color.FromRgb(0xD1, 0x3B, 0x3B))
        };

        resources["SuccessBrush"] = Freeze(new SolidColorBrush(ok));
        resources["WarningBrush"] = Freeze(new SolidColorBrush(warn));
        resources["DangerBrush"] = Freeze(new SolidColorBrush(bad));
        resources["MeterLowBrush"] = Freeze(new SolidColorBrush(ok));
        resources["MeterMidBrush"] = Freeze(new SolidColorBrush(warn));
        resources["MeterHighBrush"] = Freeze(new SolidColorBrush(bad));
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Color Shift(Color color, float amount)
    {
        static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);

        return amount >= 0
            ? Color.FromRgb(
                Clamp(color.R + (255 - color.R) * amount),
                Clamp(color.G + (255 - color.G) * amount),
                Clamp(color.B + (255 - color.B) * amount))
            : Color.FromRgb(
                Clamp(color.R * (1 + amount)),
                Clamp(color.G * (1 + amount)),
                Clamp(color.B * (1 + amount)));
    }

    private static Color Blend(Color a, Color b, float towardsB)
    {
        static byte Mix(byte x, byte y, float t) => (byte)Math.Clamp(x + (y - x) * t, 0, 255);
        return Color.FromRgb(
            Mix(a.R, b.R, towardsB),
            Mix(a.G, b.G, towardsB),
            Mix(a.B, b.B, towardsB));
    }

    /// <summary>Read the Windows "app mode" preference for AppTheme.System.</summary>
    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int light) return light == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read system theme: {ex.Message}");
        }

        return AppTheme.Dark;
    }

    /// <summary>
    /// Combined zoom factor for the whole window: UI scale, plus a bump for large text mode.
    /// </summary>
    public double EffectiveScale
    {
        get
        {
            var access = _settings.Settings.Accessibility;
            var scale = _settings.Settings.Appearance.UiScale;
            if (access.LargeText) scale *= Math.Max(1.0, access.TextScale <= 1 ? 1.2 : access.TextScale);
            return Math.Clamp(scale, 0.7, 2.0);
        }
    }

    /// <summary>Animations are suppressed entirely in reduced-motion mode.</summary>
    public bool AnimationsEnabled =>
        _settings.Settings.Appearance.EnableAnimations &&
        !_settings.Settings.Accessibility.ReducedMotion;
}
