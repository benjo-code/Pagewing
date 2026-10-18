using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
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
/// One entry of the page thumbnails: the page number, the size of its frame (136 DIP wide, as high as the page format
/// asks) and the thumbnail once it is rendered. UI thread only.
/// </summary>
public sealed class ThumbnailItem : ViewModelBase
{
    private double _frameHeight;
    private Bitmap? _bitmap;
    private bool _isCurrent;

    internal ThumbnailItem(int pageNumber, double frameHeight)
    {
        PageNumber = pageNumber;
        _frameHeight = frameHeight;
    }

    /// <summary>Gets the page number, 1-based.</summary>
    public int PageNumber { get; }

    /// <summary>Gets the page number as shown below the frame.</summary>
    public string PageText => PageNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the width of the frame in DIPs (<see cref="ThumbnailPlanner.Width"/>).</summary>
    public double FrameWidth { get; } = ThumbnailPlanner.Width;

    /// <summary>Gets the height of the frame in DIPs: the page format, or the format of the first page while the size of this page is not known.</summary>
    public double FrameHeight
    {
        get => _frameHeight;
        internal set => SetProperty(ref _frameHeight, value);
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
}

/// <summary>
/// The page thumbnails of the "Pages" panel (design system 7.3): one entry per page of the tab in its
/// <c>DataContext</c>, virtualized, with the pages of the current row marked. Only the realized entries ask for their
/// thumbnail, nearest to the current page first, and only once the tab has shown a sharp page; the thumbnails come from
/// the tab's own <see cref="ThumbnailSource"/> (cache and scheduler apart from the document view's). A click, Enter or
/// Space shows the page of an entry; the arrow keys only move the focus.
/// </summary>
/// <remarks>
/// UI thread only. The scheduler raises <see cref="RenderScheduler.TileReady"/> on a pool thread; the handler only
/// posts to the UI thread, where a thumbnail that is no longer wanted is dropped. The list holds bitmaps only for the
/// realized entries and releases all of them when it leaves the tab (another tab, another document, the panel closes).
/// </remarks>
internal sealed class ThumbnailList : ListBox
{
    /// <summary>The tallest frame, in widths: a degenerate page format must not make one entry thousands of DIPs high.</summary>
    private const double MaxAspect = 16;

    /// <summary>The format of an entry while no page size is known at all (A4 portrait, as the core uses for an unreadable page).</summary>
    private static readonly PdfPageSize StandInSize = new(595.28, 841.89);

    private readonly HashSet<int> _withBitmaps = [];
    private ThumbnailItem[] _items = [];
    private TabViewModel? _tab;
    private ViewerSession? _session;
    private PdfDocument? _document;
    private int _knownSizes;
    private ThumbnailSource? _source;
    private SourceHandler? _handler;
    private (int First, int Last) _marked = (-1, -1);
    private bool _planPosted;
    private TopLevel? _topLevel;
    private double _renderScaling = 1;
    private IReadOnlyList<RenderRequest> _lastRequests = [];

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

    private static double FrameHeightFor(PdfPageSize size) =>
        Math.Clamp(ThumbnailPlanner.HeightFor(size), 1, ThumbnailPlanner.Width * MaxAspect);

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
        var sizes = session.LoadedSizes;
        if (document is not null && !ReferenceEquals(document, session.Document))
        {
            // The session moved on between the reads; its next event brings a consistent state.
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

    /// <summary>One entry per page; a page whose size is not known yet takes the format of the first page.</summary>
    private void BuildItems(int pageCount, IReadOnlyList<PdfPageSize> sizes)
    {
        var estimate = sizes.Count > 0 && IsUsable(sizes[0]) ? sizes[0] : StandInSize;
        var known = Math.Min(sizes.Count, pageCount);
        var items = new ThumbnailItem[pageCount];
        for (var index = 0; index < pageCount; index++)
        {
            var size = index < known && IsUsable(sizes[index]) ? sizes[index] : estimate;
            items[index] = new ThumbnailItem(index + 1, FrameHeightFor(size));
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
                _items[index].FrameHeight = FrameHeightFor(sizes[index]);
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

        var plan = ThumbnailPlanner.Plan(tab.Session.LoadedSizes, realized, _renderScaling);
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
            if (_items[index].Bitmap is null && source.Cache.TryGet(KeyOf(index), out var image) && IsPlannedSize(index, image))
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
        if (index >= 0 && index < _items.Length && _items[index].Bitmap is null && ContainerFromIndex(index) is not null
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

    private void SetBitmap(int index, RenderedImage image)
    {
        Bitmap bitmap;
        try
        {
            bitmap = TileBitmaps.Create(image);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            // The bitmap could not be made (memory, or the platform refused the size): the frame stays empty.
            return;
        }

        _items[index].Bitmap = bitmap;
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
    /// Moves the cursor of the list by about a view (as many entries as are in view, less one, so the old entry stays in
    /// sight); the document keeps its page.
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

        var step = Math.Max(1, inView - 1);
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
