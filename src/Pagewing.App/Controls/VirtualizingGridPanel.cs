using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Pagewing.App.Controls;

/// <summary>
/// A virtualizing items panel that lays the items out in rows of <see cref="Columns"/> equal columns, top to bottom
/// (the two columns of thumbnails in the density "Kompakt", design system 5.4). Only the rows in the viewport exist as
/// containers (plus the one being scrolled into view and the one with the keyboard focus); the containers of the other
/// rows are recycled. A row is as high as its tallest item; the items keep their own width and lie in their column
/// as their alignment says. The height of a row that was never realized is the mean of the measured rows. The arrow
/// keys go to the item beside (Left/Right) or the one in the same column of the row above or below (Up/Down);
/// Home and End to the first and the last item. UI thread only.
/// </summary>
/// <remarks>
/// Avalonia 12.1 brings no virtualizing grid or wrap panel (only <see cref="VirtualizingStackPanel"/>); this panel
/// implements the minimum the thumbnail list needs: one kind of container, items that only ever change all at once.
/// </remarks>
internal sealed class VirtualizingGridPanel : VirtualizingPanel
{
    /// <summary>Defines the <see cref="Columns"/> property.</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<VirtualizingGridPanel, int>(nameof(Columns), 2, validate: columns => columns >= 1);

    // The recycle key a container was made for (VirtualizingStackPanel keeps its own, private one).
    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingGridPanel, Control, object?>("RecycleKey");

    /// <summary>The height of a row before any row was measured.</summary>
    private const double InitialRowEstimate = 100;

    // Realized containers by item index; the recycled ones wait, invisible, per recycle key.
    private readonly SortedDictionary<int, Control> _realized = [];
    private readonly Dictionary<object, Stack<Control>> _recycled = [];
    private double[] _rowHeights = [];
    private double[] _rowTops = [];
    private Rect _viewport;
    private int _scrollToIndex = -1;
    private bool _inLayout;

    static VirtualizingGridPanel()
    {
        AffectsMeasure<VirtualizingGridPanel>(ColumnsProperty);
    }

    public VirtualizingGridPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    /// <summary>Gets or sets the number of columns; 2 by default.</summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private int ColumnCount => Math.Max(1, Columns);

    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || index < 0 || index >= Items.Count || !IsEffectivelyVisible)
        {
            return null;
        }

        if (_realized.TryGetValue(index, out var container))
        {
            container.BringIntoView();
            return container;
        }

        // Realize the item at its estimated place, bring that place into view, then lay out around it; the item stays
        // realized all along (_scrollToIndex). A second bring-into-view corrects what the measured rows moved.
        _scrollToIndex = index;
        try
        {
            InvalidateMeasure();
            UpdateLayout();
            if (!_realized.TryGetValue(index, out container))
            {
                return null;
            }

            container.BringIntoView();
            UpdateLayout();
            container.BringIntoView();
            UpdateLayout();
            return container;
        }
        finally
        {
            _scrollToIndex = -1;
        }
    }

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
        {
            if (ReferenceEquals(realized, container))
            {
                return index;
            }
        }

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        var fromIndex = from is Control control ? IndexFromContainer(control) : -1;
        if (count == 0 || (fromIndex < 0 && direction is not (NavigationDirection.First or NavigationDirection.Last)))
        {
            return null;
        }

        var columns = ColumnCount;
        var lastRow = (count - 1) / columns;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Right => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Left => fromIndex - 1,
            NavigationDirection.Up => fromIndex - columns,

            // The row below may be shorter: its last item then.
            NavigationDirection.Down when fromIndex / columns < lastRow => Math.Min(fromIndex + columns, count - 1),
            _ => -1,
        };

        if (target < 0 || target >= count)
        {
            return null;
        }

        return target == fromIndex ? from : ScrollIntoView(target);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);

        // The thumbnail list replaces its items all at once: every index is new.
        RecycleAll();
        _rowHeights = [];
        _rowTops = [];
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var count = items.Count;
        var columns = ColumnCount;
        var rowCount = (count + columns - 1) / columns;
        if (_rowHeights.Length != rowCount)
        {
            _rowHeights = new double[rowCount];
            Array.Fill(_rowHeights, double.NaN);
            _rowTops = new double[rowCount];
        }

        if (count == 0)
        {
            RecycleAll();
            return default;
        }

        _inLayout = true;
        try
        {
            var finiteWidth = double.IsFinite(availableSize.Width);
            var columnWidth = finiteWidth ? availableSize.Width / columns : double.PositiveInfinity;
            var estimate = EstimateRowHeight();

            // The first row that reaches into the viewport (the last row when the viewport lies below the content).
            var firstRow = 0;
            var top = 0.0;
            while (firstRow < rowCount - 1 && top + HeightOf(firstRow, estimate) <= _viewport.Top)
            {
                top += HeightOf(firstRow, estimate);
                firstRow++;
            }

            // Realize rows from there until the viewport is full (at least one row).
            var wanted = new HashSet<int>();
            var bottom = top;
            for (var row = firstRow; row < rowCount; row++)
            {
                RealizeRow(items, row, columns, columnWidth, wanted);
                bottom += _rowHeights[row];
                if (bottom >= _viewport.Bottom)
                {
                    break;
                }
            }

            if (_scrollToIndex >= 0 && _scrollToIndex < count)
            {
                RealizeRow(items, _scrollToIndex / columns, columns, columnWidth, wanted);
            }

            // The focused container stays, so the keyboard keeps its place while the list scrolls away from it.
            foreach (var (index, container) in _realized.ToArray())
            {
                if (!wanted.Contains(index) && !container.IsKeyboardFocusWithin)
                {
                    Recycle(index, container);
                }
                else if (!wanted.Contains(index))
                {
                    container.Measure(new Size(columnWidth, double.PositiveInfinity));
                }
            }

            var maxWidth = 0.0;
            foreach (var container in _realized.Values)
            {
                maxWidth = Math.Max(maxWidth, container.DesiredSize.Width);
            }

            // Row tops of all rows: measured heights, the estimate (now from more rows) for the others.
            estimate = EstimateRowHeight();
            var y = 0.0;
            for (var row = 0; row < rowCount; row++)
            {
                _rowTops[row] = y;
                y += HeightOf(row, estimate);
            }

            return new Size(finiteWidth ? availableSize.Width : maxWidth * columns, y);
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount;
        var columnWidth = finalSize.Width / columns;
        foreach (var (index, container) in _realized)
        {
            var row = index / columns;
            var top = row < _rowTops.Length ? _rowTops[row] : 0;
            container.Arrange(new Rect((index % columns) * columnWidth, top, columnWidth, container.DesiredSize.Height));
        }

        return finalSize;
    }

    private double HeightOf(int row, double estimate) => double.IsNaN(_rowHeights[row]) ? estimate : _rowHeights[row];

    private double EstimateRowHeight()
    {
        var sum = 0.0;
        var measured = 0;
        foreach (var height in _rowHeights)
        {
            if (!double.IsNaN(height))
            {
                sum += height;
                measured++;
            }
        }

        return measured > 0 ? sum / measured : InitialRowEstimate;
    }

    /// <summary>Realizes and measures the items of <paramref name="row"/>; the row takes the height of its tallest item.</summary>
    private void RealizeRow(IReadOnlyList<object?> items, int row, int columns, double columnWidth, HashSet<int> wanted)
    {
        var height = 0.0;
        var last = Math.Min(((row + 1) * columns) - 1, items.Count - 1);
        for (var index = row * columns; index <= last; index++)
        {
            var container = Realize(items, index);
            container.Measure(new Size(columnWidth, double.PositiveInfinity));
            height = Math.Max(height, container.DesiredSize.Height);
            wanted.Add(index);
        }

        _rowHeights[row] = height;
    }

    private Control Realize(IReadOnlyList<object?> items, int index)
    {
        if (_realized.TryGetValue(index, out var realized))
        {
            return realized;
        }

        var generator = ItemContainerGenerator!;
        var item = items[index];
        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            if (recycleKey is not null && _recycled.TryGetValue(recycleKey, out var pool) && pool.TryPop(out var recycled))
            {
                container = recycled;
                generator.PrepareItemContainer(container, item, index);
                container.SetCurrentValue(IsVisibleProperty, true);
            }
            else
            {
                container = generator.CreateContainer(item, index, recycleKey);
                container.SetValue(RecycleKeyProperty, recycleKey);
                generator.PrepareItemContainer(container, item, index);
                AddInternalChild(container);
            }
        }
        else
        {
            // The item is its own container.
            container = (Control)item!;
            generator.PrepareItemContainer(container, item, index);
            if (!Children.Contains(container))
            {
                AddInternalChild(container);
            }

            container.SetCurrentValue(IsVisibleProperty, true);
        }

        _realized[index] = container;
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private void Recycle(int index, Control container)
    {
        _realized.Remove(index);
        ItemContainerGenerator?.ClearItemContainer(container);
        container.SetCurrentValue(IsVisibleProperty, false);
        if (container.GetValue(RecycleKeyProperty) is { } key)
        {
            if (!_recycled.TryGetValue(key, out var pool))
            {
                pool = new Stack<Control>();
                _recycled[key] = pool;
            }

            pool.Push(container);
        }
        else
        {
            RemoveInternalChild(container);
        }
    }

    private void RecycleAll()
    {
        foreach (var (index, container) in _realized.ToArray())
        {
            Recycle(index, container);
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        if (Math.Abs(viewport.Top - _viewport.Top) > 0.5 || Math.Abs(viewport.Bottom - _viewport.Bottom) > 0.5)
        {
            _viewport = viewport;
            InvalidateMeasure();
        }
    }
}
