using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectSoundboard.Audio;

namespace ProjectSoundboard.App.Controls;

/// <summary>
/// Draws a min/max waveform with the played portion highlighted, the trim region shaded,
/// and click-to-seek. Used in the properties panel and the sound editor.
/// </summary>
public sealed class WaveformControl : Control
{
    public static readonly DependencyProperty WaveformProperty =
        DependencyProperty.Register(nameof(Waveform), typeof(WaveformData), typeof(WaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimStartProperty =
        DependencyProperty.Register(nameof(TrimStart), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimEndProperty =
        DependencyProperty.Register(nameof(TrimEnd), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(WaveformControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public WaveformData? Waveform
    {
        get => (WaveformData?)GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    /// <summary>Playback position, 0..1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Trim start in seconds.</summary>
    public double TrimStart
    {
        get => (double)GetValue(TrimStartProperty);
        set => SetValue(TrimStartProperty, value);
    }

    /// <summary>Trim end in seconds; 0 means "to the end of the file".</summary>
    public double TrimEnd
    {
        get => (double)GetValue(TrimEndProperty);
        set => SetValue(TrimEndProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>Raised with a 0..1 position as the user clicks or drags along the waveform.</summary>
    public event EventHandler<double>? Seeked;

    /// <summary>Raised when a drag finishes, so the caller can settle playback state.</summary>
    public event EventHandler? ScrubCompleted;

    private bool _scrubbing;

    public WaveformControl()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        Background = Brushes.Transparent;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0) return;

        _scrubbing = true;
        CaptureMouse();
        RaiseSeek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_scrubbing || ActualWidth <= 0) return;

        // Dragging scrubs continuously. Seeks are queued and applied on the audio thread,
        // so firing one per mouse move is safe however fast the pointer moves.
        RaiseSeek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_scrubbing) return;

        _scrubbing = false;
        ReleaseMouseCapture();
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _scrubbing = false;
    }

    private void RaiseSeek(double x) =>
        Seeked?.Invoke(this, Math.Clamp(x / ActualWidth, 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var data = Waveform;
        if (data is null || data.Buckets == 0)
        {
            DrawPlaceholder(dc, width, height);
            return;
        }

        var baseBrush = Resource("WaveformBrush", Brushes.SlateGray);
        var playedBrush = Resource("WaveformPlayedBrush", Brushes.CornflowerBlue);

        var mid = height / 2;
        var scale = height / 2 - 1;
        var playedX = width * Math.Clamp(Progress, 0, 1);

        // One vertical bar per pixel column keeps the draw cost proportional to the
        // control's width, not the length of the file.
        var columns = (int)Math.Max(1, width);
        var basePen = new Pen(baseBrush, 1);
        var playedPen = new Pen(playedBrush, 1);
        basePen.Freeze();
        playedPen.Freeze();

        var guidelines = new GuidelineSet();
        dc.PushGuidelineSet(guidelines);

        for (var x = 0; x < columns; x++)
        {
            var bucket = (int)((double)x / columns * data.Buckets);
            if (bucket >= data.Buckets) bucket = data.Buckets - 1;

            var min = data.Min[bucket];
            var max = data.Max[bucket];

            var top = mid - max * scale;
            var bottom = mid - min * scale;

            // Always draw at least a hairline so silence still reads as a centre line.
            if (bottom - top < 1) { top = mid - 0.5; bottom = mid + 0.5; }

            var pen = x <= playedX ? playedPen : basePen;
            dc.DrawLine(pen, new Point(x + 0.5, top), new Point(x + 0.5, bottom));
        }

        dc.Pop();

        DrawTrimShading(dc, width, height, data.DurationSeconds);

        // Playhead
        if (Progress > 0)
        {
            var pen = new Pen(Resource("TextPrimaryBrush", Brushes.White), 1.5);
            pen.Freeze();
            dc.DrawLine(pen, new Point(playedX, 0), new Point(playedX, height));
        }
    }

    private void DrawTrimShading(DrawingContext dc, double width, double height, double duration)
    {
        if (duration <= 0) return;

        var shade = Resource("WindowBackgroundBrush", Brushes.Black).Clone();
        shade.Opacity = 0.62;
        shade.Freeze();

        var markerPen = new Pen(Resource("AccentBrush", Brushes.CornflowerBlue), 1.5);
        markerPen.Freeze();

        if (TrimStart > 0)
        {
            var x = Math.Clamp(TrimStart / duration, 0, 1) * width;
            dc.DrawRectangle(shade, null, new Rect(0, 0, x, height));
            dc.DrawLine(markerPen, new Point(x, 0), new Point(x, height));
        }

        if (TrimEnd > 0 && TrimEnd < duration)
        {
            var x = Math.Clamp(TrimEnd / duration, 0, 1) * width;
            dc.DrawRectangle(shade, null, new Rect(x, 0, width - x, height));
            dc.DrawLine(markerPen, new Point(x, 0), new Point(x, height));
        }
    }

    private void DrawPlaceholder(DrawingContext dc, double width, double height)
    {
        var brush = Resource("BorderBrushSoft", Brushes.Gray);
        var pen = new Pen(brush, 1);
        pen.Freeze();

        dc.DrawLine(pen, new Point(0, height / 2), new Point(width, height / 2));

        var text = new FormattedText(
            IsLoading ? "Analysing waveform…" : "No waveform available",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Resource("TextMutedBrush", Brushes.Gray),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((width - text.Width) / 2, height / 2 - text.Height - 4));
    }

    private Brush Resource(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
