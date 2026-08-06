using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ProjectSoundboard.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is not Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound count is greater than zero (or zero, with parameter "invert").</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection col => col.Count,
            _ => 0
        };

        var invert = string.Equals(p as string, "invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? count == 0 : count > 0;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Formats seconds or a TimeSpan as m:ss (or h:mm:ss when long enough).</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var span = value switch
        {
            TimeSpan ts => ts,
            double d => TimeSpan.FromSeconds(d),
            float f => TimeSpan.FromSeconds(f),
            int i => TimeSpan.FromSeconds(i),
            _ => TimeSpan.Zero
        };

        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var bytes = System.Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture);
        string[] units = { "B", "KB", "MB", "GB", "TB" };

        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:0} B" : $"{bytes:0.#} {units[unit]}";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>0..1 (or 0..2 for gains) rendered as a percentage.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = System.Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture);
        return $"{v * 100:0}%";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class DecibelConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = System.Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture);
        if (double.IsNegativeInfinity(v) || v <= -96) return "−∞ dB";
        return $"{(v > 0 ? "+" : "")}{v:0.#} dB";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = System.Convert.ToDouble(value ?? 1d, CultureInfo.InvariantCulture);
        return $"{v:0.00}×";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Two-way binding of an enum value to a RadioButton's IsChecked.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is not null && p is not null &&
        string.Equals(value.ToString(), p.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not true || p is null) return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(t) ?? t;
        return enumType.IsEnum ? Enum.Parse(enumType, p.ToString()!, true) : Binding.DoNothing;
    }
}

/// <summary>Visible when the bound value equals the converter parameter.</summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var equal = string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
        return equal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>#RRGGBB string to a brush, with a transparent fallback for bad input.</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Brushes.Transparent;

        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var cached)) return cached;

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)!);
                brush.Freeze();
                Cache[text] = brush;
                return brush;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Multiplies the bound number by the converter parameter (for derived sizes).</summary>
public sealed class MathMultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = System.Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture);
        var factor = double.TryParse(p as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var f)
            ? f : 1d;
        return v * factor;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
