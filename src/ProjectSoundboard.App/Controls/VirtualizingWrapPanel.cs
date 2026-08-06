using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Controls;

/// <summary>
/// A wrap panel that only realises the tiles currently on screen.
///
/// WPF ships a virtualizing <see cref="VirtualizingStackPanel"/> but no virtualizing wrap
/// panel, and a plain WrapPanel realises every item — which is fine for 200 sounds and
/// fatal for 20,000. This implementation assumes uniform tile size (which the grid view
/// guarantees), so the layout maths reduces to simple division and no measuring pass over
/// unrealised children is needed.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(140d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Rows kept realised above and below the viewport, to smooth fast scrolling.</summary>
    public static readonly DependencyProperty CacheRowsProperty =
        DependencyProperty.Register(nameof(CacheRows), typeof(int), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int CacheRows
    {
        get => (int)GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    /// <summary>
    /// Width actually given to each tile. List view sets <see cref="ItemWidth"/> to an
    /// absurd number to force a single column; measuring every row at 100,000px wide is
    /// pure waste, so one column simply takes the width available.
    /// </summary>
    private double _columnWidth = 140d;

    private bool _inMakeVisible;

    /// <summary>
    /// Nesting allowed before the recursion guard gives up. A healthy layout re-enters at
    /// most once or twice (a scroll offset settling); anything deeper is a runaway.
    /// </summary>
    private const int MaxMeasureDepth = 6;

    private int _measureDepth;
    private bool _recursionReported;
    private Size _lastDesired;

    // ---- IScrollInfo ------------------------------------------------------

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private double LineDelta => Math.Max(16, ItemHeight / 3);

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineDelta);
    public void LineDown() => SetVerticalOffset(VerticalOffset + LineDelta);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - LineDelta * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + LineDelta * 3);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(offset - _offset.Y) < 0.5) return;

        _offset.Y = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>
    /// Scroll a child into view. Selection, focus and keyboard navigation all route through
    /// here, so it runs constantly.
    ///
    /// The contract is strict: return the rectangle, in this panel's coordinates, that is
    /// now genuinely visible — or <see cref="Rect.Empty"/> when we cannot help. Returning
    /// the caller's own rectangle instead claims "this is visible exactly where you asked",
    /// so the bring-into-view machinery believes the scroll succeeded, retries after the
    /// next layout pass, gets the same answer, and nests layout passes until the stack runs
    /// out — a StackOverflowException inside FrameworkElement.MeasureCore, which no handler
    /// can catch. It only showed up on large libraries because that is when anything is far
    /// enough off screen to need scrolling at all.
    /// </summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual is null || ReferenceEquals(visual, this)) return Rect.Empty;

        // Focus lands on an element *inside* the tile, never on the container itself, so
        // this must accept any descendant rather than looking for a direct child.
        if (!IsAncestorOf(visual)) return Rect.Empty;

        if (_inMakeVisible) return Rect.Empty;
        _inMakeVisible = true;

        try
        {
            rectangle = visual.TransformToAncestor(this).TransformBounds(rectangle);

            // Arrange positions children with the scroll offset already subtracted, so add
            // it back to work in extent coordinates.
            rectangle.Y += _offset.Y;

            var viewportTop = _offset.Y;
            var viewportBottom = viewportTop + _viewport.Height;

            if (rectangle.Top < viewportTop)
                SetVerticalOffset(rectangle.Top);
            else if (rectangle.Bottom > viewportBottom)
                SetVerticalOffset(Math.Min(rectangle.Top, rectangle.Bottom - _viewport.Height));

            // Report against wherever we actually ended up — SetVerticalOffset clamps.
            var visible = new Rect(_offset.X, _offset.Y, _viewport.Width, _viewport.Height);
            rectangle.Intersect(visible);
            if (rectangle.IsEmpty) return Rect.Empty;

            rectangle.Y -= _offset.Y;
            return rectangle;
        }
        catch (InvalidOperationException)
        {
            // TransformToAncestor throws if the visual was recycled out from under us.
            return Rect.Empty;
        }
        finally
        {
            _inMakeVisible = false;
        }
    }

    /// <summary>
    /// Scroll to an item by index, realising it on the way.
    ///
    /// Every virtualizing panel has to provide this: an item that has been virtualized away
    /// has no container, so <c>ScrollIntoView</c> and keyboard navigation cannot reach it by
    /// walking the visual tree — they call here instead. Without it they silently do nothing
    /// and WPF keeps retrying, which is half of what made large libraries unstable.
    /// </summary>
    protected override void BringIndexIntoView(int index)
    {
        var itemCount = GetItemCount();
        if (index < 0 || index >= itemCount) return;

        var row = index / Math.Max(1, _columns);
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;

        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        // The caller looks for the container as soon as this returns, so it has to exist.
        UpdateLayout();
    }

    // ---- layout -----------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // Runaway layout recursion kills the process outright — a StackOverflowException is
        // the one exception .NET will not let anyone catch, so it leaves nothing in the log
        // and no dialog. Large libraries have crashed this way and it has not been possible
        // to reproduce on demand, so this measures its own nesting: past a handful of levels
        // it stops feeding the recursion and writes down that it happened. The layout is a
        // frame stale, which is invisible; the alternative is the app vanishing.
        if (_measureDepth > MaxMeasureDepth)
        {
            if (!_recursionReported)
            {
                _recursionReported = true;
                Log.Warn($"Layout recursion guard tripped at depth {_measureDepth} " +
                         $"({GetItemCount()} items, offset {_offset.Y:F0}). Layout was left unchanged.");
            }

            return _lastDesired;
        }

        _measureDepth++;
        try
        {
            return _lastDesired = MeasureContent(availableSize);
        }
        finally
        {
            _measureDepth--;
        }
    }

    private Size MeasureContent(Size availableSize)
    {
        // Touching InternalChildren is what forces WPF to create the item container
        // generator. Skip this and ItemContainerGenerator is null on the very first
        // measure pass, which is a null reference rather than a helpful error.
        _ = InternalChildren;

        var itemCount = GetItemCount();

        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        _columns = Math.Max(1, (int)(width / ItemWidth));
        _columnWidth = _columns == 1 ? Math.Max(1, width) : ItemWidth;

        var rows = (int)Math.Ceiling(itemCount / (double)_columns);

        var newExtent = new Size(_columns * _columnWidth, rows * ItemHeight);
        var newViewport = new Size(width,
            double.IsInfinity(availableSize.Height) ? newExtent.Height : availableSize.Height);

        if (newExtent != _extent || newViewport != _viewport)
        {
            _extent = newExtent;
            _viewport = newViewport;
            _offset.Y = Math.Max(0, Math.Min(_offset.Y, Math.Max(0, _extent.Height - _viewport.Height)));
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Handing back Infinity from a measure pass is an immediate InvalidOperationException,
        // so an unconstrained dimension falls back to what the content actually needs.
        var desired = new Size(
            double.IsInfinity(availableSize.Width) ? newExtent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? newExtent.Height : availableSize.Height);

        if (itemCount == 0)
        {
            CleanUpItems(0, -1);
            return new Size(0, 0);
        }

        var generator = ItemContainerGenerator;
        if (generator is null) return desired;

        var (first, last) = GetVisibleRange(itemCount);

        var startPosition = generator.GeneratorPositionFromIndex(first);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            var itemSize = new Size(_columnWidth, ItemHeight);

            for (var i = first; i <= last; i++, childIndex++)
            {
                // Null means the generator ran past the end of the collection, which can
                // happen if items were removed between passes.
                if (generator.GenerateNext(out var isNew) is not UIElement child) break;

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(itemSize);
            }
        }

        CleanUpItems(first, last);
        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator as ItemContainerGenerator;
        var columns = Math.Max(1, _columns);

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = generator?.IndexFromContainer(child) ?? -1;
            if (itemIndex < 0) continue;

            var row = itemIndex / columns;
            var column = itemIndex % columns;

            child.Arrange(new Rect(
                column * _columnWidth,
                row * ItemHeight - _offset.Y,
                _columnWidth,
                ItemHeight));
        }

        return finalSize;
    }

    private (int First, int Last) GetVisibleRange(int itemCount)
    {
        var cache = Math.Max(0, CacheRows);

        var firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - cache);
        var visibleRows = (int)Math.Ceiling(_viewport.Height / ItemHeight) + 1 + cache * 2;

        // Both ends must stay inside the collection. Clamping only one of them lets the
        // range run off the end after the list shrinks, and the generator then hands back
        // null for every item past the end.
        var first = Math.Min(firstRow * _columns, Math.Max(0, itemCount - 1));
        var last = Math.Min(itemCount - 1, first + visibleRows * _columns - 1);

        return (first, Math.Max(first, last));
    }

    /// <summary>Recycle containers for anything that scrolled out of the realised window.</summary>
    private void CleanUpItems(int first, int last)
    {
        // Remove/IndexFromGeneratorPosition live on the interface, not the concrete class.
        var generator = ItemContainerGenerator;
        if (generator is null) return;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);

            if (itemIndex >= first && itemIndex <= last) continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private int GetItemCount()
    {
        var owner = ItemsControl.GetItemsOwner(this);
        return owner?.Items.Count ?? 0;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                // Position.Index is -1 when the affected item was never realised.
                if (args.Position.Index >= 0 && args.ItemUICount > 0)
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // Deliberately keep the scroll position. A Reset arrives for a genuinely new
                // search *and* for an in-place refresh after editing a sound, and jumping to
                // the top on the latter loses the user's place. MeasureOverride clamps the
                // offset if the list got shorter; the view model asks for a scroll to the top
                // explicitly when the query itself changes.
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        InvalidateMeasure();
    }
}
