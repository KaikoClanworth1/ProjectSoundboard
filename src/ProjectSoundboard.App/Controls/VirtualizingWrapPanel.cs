using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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

    /// <summary>Scroll a child into view — this is what keyboard navigation relies on.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as UIElement;
        if (child is null) return rectangle;

        var index = InternalChildren.IndexOf(child);
        if (index < 0) return rectangle;

        var generator = ItemContainerGenerator as ItemContainerGenerator;
        var itemIndex = generator?.IndexFromContainer(child) ?? -1;
        if (itemIndex < 0) return rectangle;

        var row = itemIndex / Math.Max(1, _columns);
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;

        if (top < VerticalOffset) SetVerticalOffset(top);
        else if (bottom > VerticalOffset + ViewportHeight) SetVerticalOffset(bottom - ViewportHeight);

        return rectangle;
    }

    // ---- layout -----------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // Touching InternalChildren is what forces WPF to create the item container
        // generator. Skip this and ItemContainerGenerator is null on the very first
        // measure pass, which is a null reference rather than a helpful error.
        _ = InternalChildren;

        var itemCount = GetItemCount();

        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        _columns = Math.Max(1, (int)(width / ItemWidth));

        var rows = (int)Math.Ceiling(itemCount / (double)_columns);

        var newExtent = new Size(_columns * ItemWidth, rows * ItemHeight);
        var newViewport = new Size(width,
            double.IsInfinity(availableSize.Height) ? newExtent.Height : availableSize.Height);

        if (newExtent != _extent || newViewport != _viewport)
        {
            _extent = newExtent;
            _viewport = newViewport;
            _offset.Y = Math.Max(0, Math.Min(_offset.Y, Math.Max(0, _extent.Height - _viewport.Height)));
            ScrollOwner?.InvalidateScrollInfo();
        }

        if (itemCount == 0)
        {
            CleanUpItems(0, -1);
            return new Size(0, 0);
        }

        var (first, last) = GetVisibleRange(itemCount);

        var generator = ItemContainerGenerator;
        if (generator is null) return availableSize;

        var startPosition = generator.GeneratorPositionFromIndex(first);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            var itemSize = new Size(ItemWidth, ItemHeight);

            for (var i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);

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
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator as ItemContainerGenerator;

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = generator?.IndexFromContainer(child) ?? -1;
            if (itemIndex < 0) continue;

            var row = itemIndex / _columns;
            var column = itemIndex % _columns;

            child.Arrange(new Rect(
                column * ItemWidth,
                row * ItemHeight - _offset.Y,
                ItemWidth,
                ItemHeight));
        }

        return finalSize;
    }

    private (int First, int Last) GetVisibleRange(int itemCount)
    {
        var cache = Math.Max(0, CacheRows);

        var firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - cache);
        var visibleRows = (int)Math.Ceiling(_viewport.Height / ItemHeight) + 1 + cache * 2;

        var first = firstRow * _columns;
        var last = Math.Min(itemCount - 1, first + visibleRows * _columns - 1);

        return (Math.Min(first, Math.Max(0, itemCount - 1)), Math.Max(first, last));
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
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // The list was swapped wholesale (a new search); start again from the top.
                _offset.Y = 0;
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        InvalidateMeasure();
    }
}
