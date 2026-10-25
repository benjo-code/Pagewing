using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Controls;

/// <summary>
/// One entry of the page thumbnails: the page number, the size of its frame (136 DIP wide, in the density "Kompakt" as
/// wide as its column; as high as the page format asks) and the thumbnail once it is rendered. UI thread only.
/// </summary>
public sealed class ThumbnailItem : ViewModelBase
{
    /// <summary>The tallest frame, in widths: a degenerate page format must not make one entry thousands of DIPs high.</summary>
    private const double MaxAspect = 16;

    private PdfPageSize _format;
    private double _frameWidth;
    private double _frameHeight;
    private Bitmap? _bitmap;
    private bool _isCurrent;

    internal ThumbnailItem(int pageNumber, PdfPageSize format, double frameWidth)
    {
        PageNumber = pageNumber;
        _format = format;
        _frameWidth = frameWidth;
        _frameHeight = FrameHeightFor(format, frameWidth);
    }

    /// <summary>Gets the page number, 1-based.</summary>
    public int PageNumber { get; }

    /// <summary>Gets the page number as shown below the frame.</summary>
    public string PageText => PageNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the width of the frame in DIPs (<see cref="ThumbnailPlanner.Width"/>, or the column width of "Kompakt").</summary>
    public double FrameWidth
    {
        get => _frameWidth;
        internal set
        {
            if (SetProperty(ref _frameWidth, value))
            {
                FrameHeight = FrameHeightFor(_format, value);
            }
        }
    }

    /// <summary>Gets the height of the frame in DIPs: the page format, or the format of the first page while the size of this page is not known.</summary>
    public double FrameHeight
    {
        get => _frameHeight;
        private set => SetProperty(ref _frameHeight, value);
    }

    /// <summary>Gets the rendered thumbnail, or <see langword="null"/> while the frame stays empty. Owned by the list, which disposes it.</summary>
    public Bitmap? Bitmap
    {
        get => _bitmap;
        internal set
        {
            if (SetProperty(ref _bitmap, value))
            {
                OnPropertyChanged(nameof(HasBitmap));
            }
        }
    }

    /// <summary>Gets whether the thumbnail is rendered.</summary>
    public bool HasBitmap => _bitmap is not null;

    /// <summary>Gets whether the page belongs to the current row of the document (two pages in the two-page layouts).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets the name for assistive technology: "Page 3", or "Page 3, current page".</summary>
    public string AutomationName =>
        string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ThumbnailName), PageText)
        + (IsCurrent ? Strings.ThumbnailCurrentSuffix : string.Empty);

    /// <summary>Sets the page format the frame follows (the real size of the page arrived).</summary>
    internal void SetFormat(PdfPageSize format)
    {
        _format = format;
        FrameHeight = FrameHeightFor(format, _frameWidth);
    }

    private static double FrameHeightFor(PdfPageSize format, double width) =>
        Math.Clamp(ThumbnailPlanner.HeightFor(format, width), 1, width * MaxAspect);
}

/// <summary>
/// The page thumbnails of the "Pages" panel (design system 7.3): one entry per page of the tab in its
/// <c>DataContext</c>, virtualized, with the pages of the current row marked. Only the realized entries ask for their
/// thumbnail, nearest to the current page first, and only once the tab has shown a sharp page; the thumbnails come from
/// the tab's own <see cref="ThumbnailSource"/> (cache and scheduler apart from the document view's). A click, Enter or
/// Space shows the page of an entry; the arrow keys only move the focus. In the density "Kompakt" the entries lie in two
/// columns (<see cref="Columns"/>, <see cref="VirtualizingGridPanel"/>) and are narrower (<see cref="ThumbnailWidth"/>).
/// </summary>
/// <remarks>
/// UI thread only. The scheduler raises <see cref="RenderScheduler.TileReady"/> on a pool thread; the handler only
/// posts to the UI thread, where a thumbnail that is no longer wanted is dropped. The list holds bitmaps only for the
/// realized entries and releases all of them when it leaves the tab (another tab, another document, the panel closes).
/// </remarks>
internal sealed class ThumbnailList : ListBox
{
    /// <summary>
    /// What an entry adds to the width of its frame (PagesPanel.axaml): 6 padding, 2 ring of the current page and 1 edge
    /// on each side.
    /// </summary>
    internal const double EntryChrome = 2 * (6 + 2 + 1);

    /// <summary>The gap between two entries of a row: the same as between two rows (2 x the entry margin of 8).</summary>
    internal const double EntryGap = 16;

    /// <summary>
    /// The step in which the frames of several columns follow the width of the list. Each step renders the thumbnails
    /// again, so dragging the edge of the panel renders them every 2 x 8 DIP of panel width, not on every pixel.
    /// </summary>
    internal const double FrameStep = 8;

    /// <summary>The narrowest frame of several columns.</summary>
    private const double MinColumnFrameWidth = 32;

    /// <summary>Defines the <see cref="Columns"/> property.</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<ThumbnailList, int>(nameof(Columns), 1);

    /// <summary>Defines the <see cref="ThumbnailWidth"/> property.</summary>
    public static readonly StyledProperty<double> ThumbnailWidthProperty =
        AvaloniaProperty.Register<ThumbnailList, double>(nameof(ThumbnailWidth), ThumbnailPlanner.Width);

    /// <summary>The format of an entry while no page size is known at all (A4 portrait, as the core uses for an unreadable page).</summary>
    private static readonly PdfPageSize StandInSize = new(595.28, 841.89);

    private readonly HashSet<int> _withBitmaps = [];

    // Entries whose thumbnail shows content that changed (an annotation, or the saved file in place of the document): kept
    // until the new image replaces it, so the frame does not go empty in between.
    private readonly HashSet<int> _stale = [];
    private ThumbnailItem[] _items = [];
    private TabViewModel? _tab;
    private ViewerSession? _session;
    private PdfDocument? _document;

    // The render cache of the session when _document was shown: the same cache with another document is the saved file.
    private RenderCache? _documentCache;
    private int _knownSizes;
    private ThumbnailSource? _source;
    private SourceHandler? _handler;
    private (int First, int Last) _marked = (-1, -1);
    private bool _planPosted;
    private TopLevel? _topLevel;
    private double _renderScaling = 1;
    private IReadOnlyList<RenderRequest> _lastRequests = [];

    // The width the entries and the thumbnails have now (FrameWidth when it was last applied).
    private double _frameWidth = ThumbnailPlanner.Width;

    // The pixel size the last plan asked for, per entry: a cached image of another size (another render scaling) is not shown.
    private Dictionary<int, (int Width, int Height)> _plannedSizes = [];

    // The thumbnails and the render scaling for which the list emptied the cache of stale images: everything rendered
    // into it since then was planned by this list at this scaling, so an image of another size comes from a page whose
    // size was only estimated (the A4 stand-in after a failed batch of page sizes), not from another scaling. At most
    // one such clear per source and scaling, so such a page is not rendered again and again; it keeps its empty frame.
    private (ThumbnailSource Source, double Scaling)? _clearedFor;

    public ThumbnailList()
    {
        ContainerPrepared += (_, _) => SchedulePlan();
        ContainerClearing += (_, _) => SchedulePlan();
        ContainerIndexChanged += (_, _) => SchedulePlan();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(TappedEvent, OnTapped);

        // Key bindings of the window (Page Up/Down turn the document's pages) are matched from the focused element
        // upwards before any key event is raised, so the list claims these two keys with bindings of its own.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => MoveByPage(forward: true)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => MoveByPage(forward: false)) });
    }

    /// <summary>Raised on the UI thread for every rendered thumbnail that reaches the list, wanted or not (for tests).</summary>
    internal event Action<TileKey>? ThumbnailReceived;

    /// <summary>Gets the entries, one per page of the shown document (for tests).</summary>
    internal IReadOnlyList<ThumbnailItem> Thumbnails => _items;

    /// <summary>Gets the requests last handed to the thumbnail scheduler, most important first (for tests).</summary>
    internal IReadOnlyList<RenderRequest> LastRequests => _lastRequests;

    /// <summary>Gets how often a non-empty wish list went to the thumbnail scheduler (for tests).</summary>
    internal int PlanCount { get; private set; }

    /// <summary>Gets the thumbnails the list draws from, or <see langword="null"/> before it asked for them (for tests).</summary>
    internal ThumbnailSource? Source => _source;

    /// <summary>Gets the entries whose thumbnail shows content that changed and waits for its new image (for tests).</summary>
    internal IReadOnlySet<int> StaleEntries => _stale;

    /// <summary>
    /// Gets or sets the number of entries per row: 1 (density "Normal", the list box's own VirtualizingStackPanel) or
    /// more (a <see cref="VirtualizingGridPanel"/>); the resource <c>Pw.Size.ThumbnailColumns</c> of the density.
    /// </summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>
    /// Gets or sets the width of a thumbnail in DIPs: the resource <c>Pw.Size.ThumbnailWidth</c> of the density. With one
    /// column it is the width of every frame; with several it only applies until the list knows its own width (see
    /// <see cref="FrameWidth"/>).
    /// </summary>
    public double ThumbnailWidth
    {
        get => GetValue(ThumbnailWidthProperty);
        set => SetValue(ThumbnailWidthProperty, value);
    }

    /// <summary>
    /// Gets the width of the frames in DIPs, which the thumbnails are rendered at. One column: <see cref="ThumbnailWidth"/>
    /// (136). Several columns (design system 5.4, "Spaltenbreite"): the width of a column, so the entries fill the list
    /// with <see cref="EntryGap"/> between them and never overflow at any panel width: (content width − gaps) / columns
    /// − <see cref="EntryChrome"/>, rounded down to <see cref="FrameStep"/>. No upper cap: the widest panel (400) gives
    /// 160, the narrowest (200) 56, the default of "Kompakt" (236) 72.
    /// </summary>
    internal double FrameWidth
    {
        get
        {
            var resource = double.IsFinite(ThumbnailWidth) && ThumbnailWidth >= 1 ? ThumbnailWidth : ThumbnailPlanner.Width;
            var columns = Columns;
            var content = Bounds.Width - Padding.Left - Padding.Right;
            if (columns <= 1 || !(content > 0))
            {
                return resource;
            }

            var column = ((content - ((columns - 1) * EntryGap)) / columns) - EntryChrome;
            return Math.Max(MinColumnFrameWidth, Math.Floor(column / FrameStep) * FrameStep);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Columns > 1 && e.WidthChanged)
        {
            ApplyFrameWidth();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty)
        {
            // One column: the default items panel of the list box (a VirtualizingStackPanel).
            var columns = Columns;
            if (columns > 1)
            {
                ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingGridPanel { Columns = columns, ColumnSpacing = EntryGap });
            }
            else
            {
                ClearValue(ItemsPanelProperty);
            }

            ApplyFrameWidth();
            SchedulePlan();
        }
        else if (change.Property == ThumbnailWidthProperty)
        {
            ApplyFrameWidth();
        }
    }

    // The Fluent theme of the list box applies.
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _renderScaling = ValidScaling(_topLevel.RenderScaling);
            _topLevel.ScalingChanged += OnScalingChanged;
        }

        Connect();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        Disconnect();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Disconnect();
        if (_topLevel is not null)
        {
            Connect();
        }
    }

    private static double ValidScaling(double scaling) => double.IsFinite(scaling) && scaling > 0 ? scaling : 1;

    private static bool IsUsable(PdfPageSize size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private static TileKey KeyOf(int index) => new(index, TileKey.PreviewScaleKey, 0, 0);

    /// <summary>Follows the tab in <c>DataContext</c> and its session.</summary>
    private void Connect()
    {
        if (_tab is not null || DataContext is not TabViewModel tab)
        {
            return;
        }

        _tab = tab;
        _session = tab.Session;
        tab.PropertyChanged += OnTabPropertyChanged;
        _session.Changed += OnSessionChanged;
        _session.PagesInvalidated += OnPagesInvalidated;
        SyncDocument();
    }

    private void Disconnect()
    {
        if (_tab is null)
        {
            return;
        }

        _tab.PropertyChanged -= OnTabPropertyChanged;
        _session!.Changed -= OnSessionChanged;
        _session.PagesInvalidated -= OnPagesInvalidated;
        ResetDocument();
        _tab = null;
        _session = null;
    }

    // Any thread: must not wait and must never throw (the session would rethrow it on the thread pool).
    private void OnSessionChanged()
    {
        try
        {
            Dispatcher.UIThread.Post(SyncDocument);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to show.
        }
    }

    // Any thread (the editor's): must not wait and must never throw. The thumbnails of those pages are rendered anew.
    private void OnPagesInvalidated(IReadOnlySet<int> pages)
    {
        var session = _session;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (session is not null && ReferenceEquals(session, _session))
                {
                    MarkStale(pages);
                }
            });
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to show.
        }
    }

    /// <summary>
    /// The thumbnails of <paramref name="pages"/> show content that changed: they stay until their new images arrive (the
    /// session dropped the old ones from the cache), and the next plan renders them anew.
    /// </summary>
    private void MarkStale(IEnumerable<int> pages)
    {
        foreach (var index in pages)
        {
            if (_withBitmaps.Contains(index))
            {
                _stale.Add(index);
            }
        }

        SchedulePlan();
    }

    /// <summary>Shows what the session holds now: a new document (new entries), more page sizes, or nothing.</summary>
    private void SyncDocument()
    {
        if (_tab is not { } tab || _session is not { } session)
        {
            return;
        }

        // The tab first, so its current page belongs to the document read below.
        tab.SyncDocumentFromSession();
        var document = session.State == ViewerState.Ready ? session.Document : null;
        var cache = session.Cache;
        var sizes = session.LoadedSizes;
        if (document is not null && !ReferenceEquals(document, session.Document))
        {
            // The session moved on between the reads; its next event brings a consistent state.
            return;
        }

        if (document is not null && _document is not null && !ReferenceEquals(document, _document)
            && cache is not null && ReferenceEquals(cache, _documentCache) && PageCountOf(document) == _items.Length)
        {
            // The saved file in place of the document (the session kept the cache): the same pages, so the entries and the
            // position of the list stay. Its thumbnails are new (the old ones went with the old document): every image
            // shown is replaced once the new one is there.
            StopWork();
            DetachSource();
            _document = document;
            MarkStale([.. _withBitmaps]);
            if (sizes.Count > _knownSizes)
            {
                AddSizes(sizes);
            }

            return;
        }

        if (!ReferenceEquals(document, _document))
        {
            ResetDocument();
            if (document is null)
            {
                return;
            }

            int pageCount;
            try
            {
                pageCount = document.PageCount;
            }
            catch (ObjectDisposedException)
            {
                // Replaced or closed meanwhile; the event of that change follows.
                return;
            }

            _document = document;
            _documentCache = cache;
            BuildItems(pageCount, sizes);
            UpdateCurrent(scroll: false);

            // Once the entries are laid out: the current page in view.
            var items = _items;
            Dispatcher.UIThread.Post(() => ScrollToCurrent(items), DispatcherPriority.Loaded);
            SchedulePlan();
        }
        else if (document is not null && sizes.Count > _knownSizes)
        {
            AddSizes(sizes);
            SchedulePlan();
        }
    }

    /// <summary>The number of pages of <paramref name="document"/>, or -1 when it was closed meanwhile.</summary>
    private static int PageCountOf(PdfDocument document)
    {
        try
        {
            return document.PageCount;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    /// <summary>One entry per page; a page whose size is not known yet takes the format of the first page.</summary>
    private void BuildItems(int pageCount, IReadOnlyList<PdfPageSize> sizes)
    {
        var estimate = sizes.Count > 0 && IsUsable(sizes[0]) ? sizes[0] : StandInSize;
        var known = Math.Min(sizes.Count, pageCount);
        var items = new ThumbnailItem[pageCount];
        for (var index = 0; index < pageCount; index++)
        {
            var size = index < known && IsUsable(sizes[index]) ? sizes[index] : estimate;
            items[index] = new ThumbnailItem(index + 1, size, _frameWidth);
        }

        _items = items;
        _knownSizes = known;
        ItemsSource = items;
    }

    /// <summary>Gives the entries whose page size arrived their real format.</summary>
    private void AddSizes(IReadOnlyList<PdfPageSize> sizes)
    {
        var count = Math.Min(sizes.Count, _items.Length);
        for (var index = _knownSizes; index < count; index++)
        {
            if (IsUsable(sizes[index]))
            {
                _items[index].SetFormat(sizes[index]);
            }
        }

        _knownSizes = Math.Max(_knownSizes, count);
    }

    /// <summary>Drops the entries of the shown document: no more work, no bitmaps.</summary>
    private void ResetDocument()
    {
        StopWork();
        DetachSource();
        ReleaseBitmaps(keep: null);
        _items = [];
        _document = null;
        _documentCache = null;
        _knownSizes = 0;
        _marked = (-1, -1);
        ItemsSource = null;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.CurrentPage) or nameof(TabViewModel.CurrentPageLast):
                UpdateCurrent(scroll: true);
                SchedulePlan();
                break;
            case nameof(TabViewModel.EffectivePageTheme):
                // The session renders the thumbnails in the new theme and emptied their cache of the old one: the empty
                // frames show until the new images come (a post of an old one finds nothing in the cache).
                ReleaseBitmaps(keep: null);
                SchedulePlan();
                break;
            case nameof(TabViewModel.HasShownSharpPage) or nameof(TabViewModel.IsActive) or nameof(TabViewModel.SidePanel) or nameof(TabViewModel.State):
                // Work that is no longer allowed stops at once (the panel closes, the tab is left); new work waits for the plan.
                if (_tab is { } tab && !MayRender(tab))
                {
                    StopWork();
                }

                SchedulePlan();
                break;
        }
    }

    /// <summary>
    /// Marks the pages of the current row; with <paramref name="scroll"/> the cursor of the list moves to its first
    /// page, which comes into view.
    /// </summary>
    private void UpdateCurrent(bool scroll)
    {
        var tab = _tab;
        var first = -1;
        var last = -1;
        if (tab is not null && _items.Length > 0 && tab.CurrentPage >= 1)
        {
            first = Math.Min(tab.CurrentPage, _items.Length) - 1;
            last = Math.Clamp(Math.Max(tab.CurrentPage, tab.CurrentPageLast) - 1, first, _items.Length - 1);
        }

        if ((first, last) != _marked)
        {
            for (var index = Math.Max(0, _marked.First); index <= _marked.Last && index < _items.Length; index++)
            {
                if (index < first || index > last)
                {
                    _items[index].IsCurrent = false;
                }
            }

            for (var index = Math.Max(0, first); index <= last; index++)
            {
                _items[index].IsCurrent = true;
            }

            _marked = (first, last);
        }

        if (scroll && first >= 0)
        {
            SelectedIndex = first;
            ScrollIntoView(first);
        }
    }

    private void ScrollToCurrent(ThumbnailItem[] items)
    {
        if (ReferenceEquals(items, _items) && _marked.First >= 0)
        {
            SelectedIndex = _marked.First;
            ScrollIntoView(_marked.First);
        }
    }

    /// <summary>Whether the thumbnails of <paramref name="tab"/> may render now.</summary>
    private bool MayRender(TabViewModel tab) =>
        tab.IsActive && tab.IsReady && tab.HasShownSharpPage && tab.SidePanel == SidePanelKind.Pages && _topLevel is not null;

    /// <summary>
    /// Plans once for all changes until the dispatcher gets to it (realized entries come and go in bursts while the
    /// list scrolls), with a lower priority than input and rendering.
    /// </summary>
    private void SchedulePlan()
    {
        if (_planPosted || _tab is null)
        {
            return;
        }

        _planPosted = true;
        Dispatcher.UIThread.Post(Plan, DispatcherPriority.Background);
    }

    /// <summary>
    /// Hands the thumbnails of the realized entries to the scheduler, nearest to the current page first; frees the
    /// bitmaps of the entries that are no longer realized and shows what the cache already holds.
    /// </summary>
    private void Plan()
    {
        _planPosted = false;
        var tab = _tab;
        if (tab is null || _document is null || _items.Length == 0 || !MayRender(tab))
        {
            StopWork();
            return;
        }

        if (!ReferenceEquals(tab.Session.Document, _document))
        {
            // Another document is on its way; its sync plans again.
            return;
        }

        var source = tab.EnsureThumbnails();
        if (source is null)
        {
            StopWork();
            return;
        }

        AttachSource(source);
        var trusted = IsCacheTrusted(source);
        var realized = new List<int>();
        foreach (var container in GetRealizedContainers())
        {
            var index = IndexFromContainer(container);
            if (index >= 0 && index < _items.Length)
            {
                realized.Add(index);
            }
        }

        // The planner orders by the distance to the first page it is given: the realized page nearest to the current one.
        var current = Math.Clamp(tab.CurrentPage - 1, 0, _items.Length - 1);
        realized.Sort((a, b) =>
        {
            var byDistance = Math.Abs(a - current).CompareTo(Math.Abs(b - current));
            return byDistance != 0 ? byDistance : a.CompareTo(b);
        });

        var plan = ThumbnailPlanner.Plan(tab.Session.LoadedSizes, realized, _renderScaling, _frameWidth);
        var planned = new Dictionary<int, (int Width, int Height)>(plan.Count);
        var stale = false;
        foreach (var request in plan)
        {
            var size = (request.Rect.Width, request.Rect.Height);
            planned[request.Key.Page] = size;
            stale |= !trusted
                && ((source.Cache.TryGet(request.Key, out var cached) && (cached.Width, cached.Height) != size)
                    || (_items[request.Key.Page].Bitmap is { } shown && (shown.PixelSize.Width, shown.PixelSize.Height) != size));
        }

        if (stale)
        {
            // The keys do not carry the render scaling: the cache may hold images of another scaling (rendered before
            // the window moved to another monitor, also while the panel was closed). All of them are rendered again;
            // the wish list goes first, so the running job stores nothing.
            source.Scheduler.Update([]);
            source.Cache.Clear();
            ReleaseBitmaps(keep: null);
            _clearedFor = (source, _renderScaling);
            trusted = true;
        }

        source.Scheduler.Update(plan);
        _plannedSizes = planned;
        _lastRequests = plan;
        if (plan.Count > 0)
        {
            PlanCount++;
        }

        ReleaseBitmaps(keep: [.. realized]);

        // The scheduler skips what the cache holds, so no TileReady comes for it.
        foreach (var index in realized)
        {
            if (NeedsBitmap(index) && source.Cache.TryGet(KeyOf(index), out var image) && IsPlannedSize(index, image))
            {
                SetBitmap(index, image);
            }
        }
    }

    /// <summary>Whether every image in the cache of <paramref name="source"/> was rendered for the current scaling (see <see cref="_clearedFor"/>).</summary>
    private bool IsCacheTrusted(ThumbnailSource source) =>
        _clearedFor is { } cleared && ReferenceEquals(cleared.Source, source) && cleared.Scaling == _renderScaling;

    /// <summary>Whether <paramref name="image"/> has the pixel size the last plan asked for at entry <paramref name="index"/>.</summary>
    private bool IsPlannedSize(int index, RenderedImage image) =>
        _plannedSizes.TryGetValue(index, out var size) && size == (image.Width, image.Height);

    /// <summary>Empties the wish list of the thumbnail scheduler; does nothing when the session disposed it.</summary>
    private void StopWork()
    {
        _source?.Scheduler.Update([]);
        _lastRequests = [];
        _plannedSizes = [];
    }

    private void AttachSource(ThumbnailSource source)
    {
        if (ReferenceEquals(source, _source))
        {
            return;
        }

        DetachSource();
        _source = source;
        _handler = new SourceHandler(this, source);
        source.Scheduler.TileReady += _handler.OnTileReady;
    }

    private void DetachSource()
    {
        if (_source is not null && _handler is not null)
        {
            _source.Scheduler.TileReady -= _handler.OnTileReady;
        }

        _source = null;
        _handler = null;

        // While the list does not follow the thumbnails, others may render into their cache (another window).
        _clearedFor = null;
    }

    /// <summary>A thumbnail is in the cache (UI thread, posted by <see cref="SourceHandler.OnTileReady"/>); a failed one keeps its empty frame.</summary>
    private void ApplyThumbnail(ThumbnailSource source, TileKey key)
    {
        if (!ReferenceEquals(source, _source))
        {
            return;
        }

        var index = key.Page;
        if (index >= 0 && index < _items.Length && NeedsBitmap(index) && ContainerFromIndex(index) is not null
            && source.Cache.TryGet(key, out var image))
        {
            if (IsPlannedSize(index, image))
            {
                SetBitmap(index, image);
            }
            else if (!IsCacheTrusted(source) || !_plannedSizes.ContainsKey(index))
            {
                // Perhaps rendered for another scaling, or no longer planned: the next plan replaces it if it is still
                // wanted. A planned page of the current scaling whose image has another size (its size was only
                // estimated) keeps its empty frame instead of being rendered again.
                SchedulePlan();
            }
        }

        ThumbnailReceived?.Invoke(key);
    }

    /// <summary>Whether entry <paramref name="index"/> has no thumbnail yet, or one of content that changed.</summary>
    private bool NeedsBitmap(int index) => _items[index].Bitmap is null || _stale.Contains(index);

    private void SetBitmap(int index, RenderedImage image)
    {
        Bitmap bitmap;
        try
        {
            bitmap = TileBitmaps.Create(image);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            // The bitmap could not be made (memory, or the platform refused the size): the frame keeps what it shows.
            return;
        }

        // A stale thumbnail goes once its new image is there; the entry lets go of it first.
        var old = _items[index].Bitmap;
        _items[index].Bitmap = bitmap;
        old?.Dispose();
        _stale.Remove(index);
        _withBitmaps.Add(index);
    }

    /// <summary>Disposes the bitmaps of all entries except those in <paramref name="keep"/>; the entries let go of them first.</summary>
    private void ReleaseBitmaps(HashSet<int>? keep)
    {
        if (_withBitmaps.Count == 0)
        {
            return;
        }

        foreach (var index in _withBitmaps.ToArray())
        {
            if (keep is not null && keep.Contains(index))
            {
                continue;
            }

            _withBitmaps.Remove(index);
            _stale.Remove(index);
            if (index < _items.Length && _items[index].Bitmap is { } bitmap)
            {
                _items[index].Bitmap = null;
                bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// Another render scaling: the thumbnails are rendered again for it. Their keys do not carry the scaling, so the
    /// cache is emptied first (after the wish list, so the running job stores nothing).
    /// </summary>
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        var scaling = ValidScaling(_topLevel?.RenderScaling ?? 1);
        if (scaling == _renderScaling)
        {
            return;
        }

        _renderScaling = scaling;
        _clearedFor = null;
        if (_source is { } source)
        {
            StopWork();
            source.Cache.Clear();
        }

        ReleaseBitmaps(keep: null);
        SchedulePlan();
    }

    /// <summary>
    /// Another density: the entries take the new width (and the height that goes with it) and the thumbnails are rendered
    /// again at that width. Their keys do not carry the width, so the cache is emptied first, as for another scaling.
    /// </summary>
    private void ApplyFrameWidth()
    {
        var width = FrameWidth;
        if (width == _frameWidth)
        {
            return;
        }

        _frameWidth = width;
        foreach (var item in _items)
        {
            item.FrameWidth = width;
        }

        _clearedFor = null;
        if (_source is { } source)
        {
            StopWork();
            source.Cache.Clear();
        }

        ReleaseBitmaps(keep: null);
        SchedulePlan();
    }

    // Enter and Space show the page of the focused entry, before the list box or its scroll viewer use the keys.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
            && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            && IndexFromContainer(container) is var index and >= 0)
        {
            e.Handled = true;
            ShowPage(index);
        }
    }

    /// <summary>
    /// Moves the cursor of the list by about a view (as many rows as are in view, less one, so the old entry stays in
    /// sight; whole rows, so it keeps its column); the document keeps its page.
    /// </summary>
    private void MoveByPage(bool forward)
    {
        if (_items.Length == 0)
        {
            return;
        }

        var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
            && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
                ? IndexFromContainer(container)
                : -1;
        if (current < 0)
        {
            current = Math.Max(SelectedIndex, 0);
        }

        var inView = 0;
        foreach (var realized in GetRealizedContainers())
        {
            if (realized.TranslatePoint(default, this) is { } origin
                && origin.Y >= -1
                && origin.Y + realized.Bounds.Height <= Bounds.Height + 1)
            {
                inView++;
            }
        }

        var columns = Math.Max(1, Columns);
        var step = Math.Max(1, (inView / columns) - 1) * columns;
        var target = Math.Clamp(forward ? current + step : current - step, 0, _items.Length - 1);
        SelectedIndex = target;
        ScrollIntoView(target);
        if (ContainerFromIndex(target) is { } targetContainer)
        {
            targetContainer.Focus(NavigationMethod.Directional);
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            && IndexFromContainer(container) is var index and >= 0)
        {
            e.Handled = true;
            ShowPage(index);
        }
    }

    private void ShowPage(int index) => _tab?.ShowPageFromPanel(index + 1);

    /// <summary>The scheduler handler of one <see cref="ThumbnailSource"/>.</summary>
    private sealed class SourceHandler(ThumbnailList owner, ThumbnailSource source)
    {
        // Pool thread: must not block and must never throw (it would end the process).
        public void OnTileReady(TileKey key)
        {
            try
            {
                Dispatcher.UIThread.Post(() => owner.ApplyThumbnail(source, key), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // The dispatcher is shutting down: nobody draws any more.
            }
        }
    }
}
