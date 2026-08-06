using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProjectSoundboard.App.Controls;

/// <summary>
/// A live peak/RMS meter drawn with a dB scale, because a linear meter spends 90% of its
/// travel on the top 10% of the useful range and reads as "always full".
/// </summary>
public sealed class LevelMeterControl : Control
{
    // No control template: everything is drawn in OnRender, which keeps a meter that
    // repaints ten times a second down to a single visual.

    public static readonly DependencyProperty PeakProperty =
        DependencyProperty.Register(nameof(Peak), typeof(double), typeof(LevelMeterControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RmsProperty =
        DependencyProperty.Register(nameof(Rms), typeof(double), typeof(LevelMeterControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakHoldProperty =
        DependencyProperty.Register(nameof(PeakHold), typeof(double), typeof(LevelMeterControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(LevelMeterControl),
            new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowScaleProperty =
        DependencyProperty.Register(nameof(ShowScale), typeof(bool), typeof(LevelMeterControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Instantaneous peak, 0..1.</summary>
    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    /// <summary>Smoothed RMS, 0..1.</summary>
    public double Rms
    {
        get => (double)GetValue(RmsProperty);
        set => SetValue(RmsProperty, value);
    }

    public double PeakHold
    {
        get => (double)GetValue(PeakHoldProperty);
        set => SetValue(PeakHoldProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Draw tick marks at −40, −20, −12, −6 and −3 dB.</summary>
    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    private const double FloorDb = -54;
    private static readonly double[] Ticks = { -40, -20, -12, -6, -3 };

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var horizontal = Orientation == Orientation.Horizontal;
        var track = Brush("InputBrush", Brushes.DimGray);
        var radius = Math.Min(4, (horizontal ? height : width) / 2);

        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), radius, radius);

        var low = Brush("MeterLowBrush", Brushes.LimeGreen);
        var mid = Brush("MeterMidBrush", Brushes.Gold);
        var high = Brush("MeterHighBrush", Brushes.OrangeRed);

        // Colour the bar by the level it has reached rather than gradient-filling it,
        // so a glance tells you "safe / hot / clipping" without reading the scale.
        var peakFraction = ToFraction(Peak);
        var rmsFraction = ToFraction(Rms);

        var barBrush = Peak >= 0.95 ? high : Peak >= 0.72 ? mid : low;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));

        if (rmsFraction > 0)
        {
            var rect = horizontal
                ? new Rect(0, 0, width * rmsFraction, height)
                : new Rect(0, height * (1 - rmsFraction), width, height * rmsFraction);
            dc.DrawRectangle(barBrush, null, rect);
        }

        // The peak sits on top of the RMS body as a translucent overhang.
        if (peakFraction > rmsFraction)
        {
            var overhang = barBrush.Clone();
            overhang.Opacity = 0.38;
            overhang.Freeze();

            var rect = horizontal
                ? new Rect(width * rmsFraction, 0, width * (peakFraction - rmsFraction), height)
                : new Rect(0, height * (1 - peakFraction), width, height * (peakFraction - rmsFraction));
            dc.DrawRectangle(overhang, null, rect);
        }

        // Peak-hold tick.
        var holdFraction = ToFraction(PeakHold);
        if (holdFraction > 0.01)
        {
            var pen = new Pen(Brush("TextPrimaryBrush", Brushes.White), 1.5);
            pen.Freeze();

            if (horizontal)
            {
                var x = Math.Min(width - 1, width * holdFraction);
                dc.DrawLine(pen, new Point(x, 1), new Point(x, height - 1));
            }
            else
            {
                var y = Math.Max(1, height * (1 - holdFraction));
                dc.DrawLine(pen, new Point(1, y), new Point(width - 1, y));
            }
        }

        if (ShowScale)
        {
            var tickPen = new Pen(Brush("BorderBrushStrong", Brushes.Gray), 1) { DashStyle = DashStyles.Dot };
            tickPen.Freeze();

            foreach (var db in Ticks)
            {
                var fraction = DbToFraction(db);
                if (horizontal)
                {
                    var x = width * fraction;
                    dc.DrawLine(tickPen, new Point(x, 0), new Point(x, height));
                }
                else
                {
                    var y = height * (1 - fraction);
                    dc.DrawLine(tickPen, new Point(0, y), new Point(width, y));
                }
            }
        }

        dc.Pop();
    }

    private static double ToFraction(double linear)
    {
        if (linear <= 0) return 0;
        var db = 20 * Math.Log10(linear);
        return DbToFraction(db);
    }

    private static double DbToFraction(double db) =>
        Math.Clamp((db - FloorDb) / (0 - FloorDb), 0, 1);

    private Brush Brush(string key, Brush fallback)
    {
        if (TryFindResource(key) is Brush brush) return brush;
        return fallback;
    }
}
