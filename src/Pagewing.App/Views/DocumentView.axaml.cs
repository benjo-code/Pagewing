using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The open document: continuous scrolling over all pages, zoom with Ctrl+wheel (around the pointer) and through the
/// view model (Ctrl+Plus/Minus, status bar), page jumps, and the current page for the status bar. Its
/// <c>DataContext</c> is the <see cref="MainViewModel"/>; it mirrors the document of the view model's session.
/// </summary>
/// <remarks>
/// The view owns the <see cref="DocumentLayout"/> and the scroll position; <see cref="DocumentCanvas"/> draws and
/// plans from them. A new document starts at the top with 100 %. While more page sizes arrive, the position at the
/// top of the viewport stays where it is. UI thread only; the session's events are posted to it.
/// </remarks>
public sealed partial class DocumentView : UserControl
{
    private MainViewModel? _viewModel;
    private ViewerSession? _session;
    private PdfDocument? _document;
    private DocumentLayout? _layout;
    private int _loadedSizes;
    private bool _changingLayout;
    private bool _focusPending;
    private Point? _zoomAnchor;
    private double _wheelSteps;
    private TopLevel? _topLevel;

    public DocumentView()
    {
        InitializeComponent();
        Scroller.PropertyChanged += OnScrollerPropertyChanged;
        Scroller.AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Raised on the UI thread once per document, in the frame after the first sharp tile of the first page was drawn.</summary>
    internal event Action? FirstSharpTileDrawn
    {
        add => PageCanvas.FirstSharpTileDrawn += value;
        remove => PageCanvas.FirstSharpTileDrawn -= value;
    }

    /// <summary>Gets the canvas that draws the pages.</summary>
    internal DocumentCanvas Canvas => PageCanvas;

    /// <summary>Gets the scroll viewer of the document.</summary>
    internal ScrollViewer ScrollViewer => Scroller;

    /// <summary>Gets the layout of the shown document, or <see langword="null"/>.</summary>
    internal DocumentLayout? Layout => _layout;

    /// <summary>Gets the shown document, or <see langword="null"/>.</summary>
    internal PdfDocument? Document => _document;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
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

    /// <summary>Follows the view model in <c>DataContext</c> and its session.</summary>
    private void Connect()
    {
        if (_viewModel is not null || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _session = viewModel.Session;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PageRequested += OnPageRequested;
        _session.Changed += OnSessionChanged;
        Sync();
    }

    private void Disconnect()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PageRequested -= OnPageRequested;
            _viewModel = null;
        }

        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session = null;
        }

        CloseDocument();
    }

    // Any thread: must not wait and must never throw (the session would rethrow it on the thread pool).
    private void OnSessionChanged()
    {
        try
        {
            Dispatcher.UIThread.Post(Sync);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to show.
        }
    }

    /// <summary>Shows what the session holds now: a new document, more page sizes, or nothing.</summary>
    private void Sync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var document = session.State == ViewerState.Ready ? session.Document : null;
        var scheduler = session.Scheduler;
        var cache = session.Cache;
        var sizes = session.LoadedSizes;
        if (!ReferenceEquals(document, session.Document) && document is not null)
        {
            // The session moved on between the reads; its next event brings a consistent state.
            return;
        }

        if (document is null || scheduler is null || cache is null)
        {
            CloseDocument();
            return;
        }

        if (!ReferenceEquals(document, _document))
        {
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

            ShowDocument(document, pageCount, scheduler, cache, sizes);
        }
        else if (sizes.Count > _loadedSizes)
        {
            AddSizes(sizes);
        }
    }

    private void ShowDocument(PdfDocument document, int pageCount, RenderScheduler scheduler, RenderCache cache, IReadOnlyList<PdfPageSize> sizes)
    {
        // Unknown pages take the size of the first page (an invalid estimate becomes A4).
        var layout = new DocumentLayout(pageCount, sizes.Count > 0 ? sizes[0] : default);
        var known = Math.Min(sizes.Count, pageCount);
        layout.SetSizes(0, known == sizes.Count ? sizes : sizes.Take(known).ToArray());
        layout.Zoom = ZoomSteps.Default;

        _document = document;
        _layout = layout;
        _loadedSizes = known;
        PageCanvas.Attach(layout, scheduler, cache);
        if (_viewModel is not null)
        {
            _viewModel.Zoom = ZoomSteps.Default;
        }

        ChangeLayout(new Vector(0, 0));
        _focusPending = true;
        FocusLater();
    }

    /// <summary>After the bindings of the pending changes have run (the password card may be on its way out).</summary>
    private void FocusLater() => Dispatcher.UIThread.Post(FocusIfFree, DispatcherPriority.Loaded);

    /// <summary>
    /// Gives the keys of a newly shown document to the view, unless the user types into a visible text field. Not a
    /// keyboard navigation, so no focus ring appears.
    /// </summary>
    private void FocusIfFree()
    {
        if (!_focusPending || _layout is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { IsEffectivelyVisible: true })
        {
            // The password field of the card that unlocked this document stays until the view model has seen the
            // document (the session may be ahead of it); the view model's change tries again. Any other field keeps
            // the focus for good.
            _focusPending = _viewModel?.IsPasswordPrompt == true;
            return;
        }

        _focusPending = false;
        Focus(NavigationMethod.Unspecified);
    }

    // After the scroll viewer and the scroll bars had the press: a click on the pages (or their scroll bars) gives
    // the keys to the document. With the pointer, so no focus ring appears.
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_layout is not null && !IsFocused)
        {
            Focus(NavigationMethod.Pointer, e.KeyModifiers);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && _layout is not null && ScrollFor(e.Key, e.KeyModifiers) is { } offset)
        {
            e.Handled = true;
            ScrollTo(offset);
        }
    }

    /// <summary>
    /// The offset a key scrolls to: Up/Down a line, Left/Right a line sideways (only when the pages are wider than
    /// the viewport), Space/Shift+Space a viewport height, Home/End (also with Ctrl) the start and end of the
    /// document. Other keys, including Page Up/Page Down (page by page, a binding of the window), return
    /// <see langword="null"/>.
    /// </summary>
    private Vector? ScrollFor(Key key, KeyModifiers modifiers)
    {
        var offset = Scroller.Offset;
        var line = Scroller.SmallChange;
        var viewport = Scroller.Viewport;
        var wider = Scroller.Extent.Width > viewport.Width;
        return (key, modifiers) switch
        {
            (Key.Up, KeyModifiers.None) => offset.WithY(offset.Y - line.Height),
            (Key.Down, KeyModifiers.None) => offset.WithY(offset.Y + line.Height),
            (Key.Left, KeyModifiers.None) when wider => offset.WithX(offset.X - line.Width),
            (Key.Right, KeyModifiers.None) when wider => offset.WithX(offset.X + line.Width),
            (Key.Space, KeyModifiers.None) => offset.WithY(offset.Y + viewport.Height),
            (Key.Space, KeyModifiers.Shift) => offset.WithY(offset.Y - viewport.Height),
            (Key.Home, KeyModifiers.None or KeyModifiers.Control) => offset.WithY(0),
            (Key.End, KeyModifiers.None or KeyModifiers.Control) => offset.WithY(Scroller.Extent.Height),
            _ => null,
        };
    }

    /// <summary>Scrolls to <paramref name="offset"/>, limited to the content.</summary>
    private void ScrollTo(Vector offset)
    {
        var extent = Scroller.Extent;
        var viewport = Scroller.Viewport;
        var x = Math.Clamp(offset.X, 0, Math.Max(0, extent.Width - viewport.Width));
        var y = Math.Clamp(offset.Y, 0, Math.Max(0, extent.Height - viewport.Height));
        ChangeLayout(new Vector(x, y));
    }

    private void CloseDocument()
    {
        if (_document is null && _layout is null)
        {
            return;
        }

        _document = null;
        _layout = null;
        _loadedSizes = 0;
        _focusPending = false;
        PageCanvas.Detach();
        ChangeLayout(new Vector(0, 0));
    }

    /// <summary>Takes the page sizes that arrived since the last call; the position at the top of the viewport stays.</summary>
    private void AddSizes(IReadOnlyList<PdfPageSize> sizes)
    {
        var layout = _layout!;
        var count = Math.Min(sizes.Count, layout.PageCount) - _loadedSizes;
        if (count <= 0)
        {
            return;
        }

        var offset = Scroller.Offset;
        var anchor = layout.GetAnchor(offset.Y);
        var width = layout.ContentWidth;
        var added = new PdfPageSize[count];
        for (var i = 0; i < count; i++)
        {
            added[i] = sizes[_loadedSizes + i];
        }

        layout.SetSizes(_loadedSizes, added);
        _loadedSizes += count;

        // Horizontally the centre of the pages stays in place.
        ChangeLayout(new Vector(offset.X + ((layout.ContentWidth - width) / 2), layout.GetOffset(anchor)));
    }

    /// <summary>
    /// Applies a changed content size and scrolls to <paramref name="offset"/> in one step: the extent must be laid
    /// out first, otherwise the scroll viewer would clamp the offset to the old content size.
    /// </summary>
    private void ChangeLayout(Vector offset)
    {
        _changingLayout = true;
        try
        {
            var width = _layout?.ContentWidth ?? 0;
            var height = _layout?.ContentHeight ?? 0;
            if (Extent.Width != width || Extent.Height != height)
            {
                Extent.Width = width;
                Extent.Height = height;
                Scroller.UpdateLayout();
            }

            Scroller.Offset = offset;
        }
        finally
        {
            _changingLayout = false;
        }

        UpdateViewport();
    }

    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_changingLayout && (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty))
        {
            UpdateViewport();
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateViewport();

    /// <summary>Hands the visible part of the content to the canvas and updates the current page.</summary>
    private void UpdateViewport()
    {
        var scaling = _topLevel?.RenderScaling ?? 1;
        var layout = _layout;
        if (layout is null)
        {
            PageCanvas.SetViewport(default, scaling);
            return;
        }

        var size = Scroller.Viewport;
        var offset = Scroller.Offset;

        // Content narrower than the viewport is centred in it.
        var shift = Math.Max(0, (size.Width - layout.ContentWidth) / 2);
        PageCanvas.SetViewport(new DipRect(offset.X - shift, offset.Y, size.Width, size.Height), scaling);
        if (_viewModel is not null && layout.PageCount > 0 && size.Height > 0)
        {
            _viewModel.CurrentPage = layout.GetMostVisiblePage(offset.Y, offset.Y + size.Height) + 1;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Zoom) && _viewModel is not null)
        {
            ApplyZoom(_viewModel.Zoom);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsPasswordPrompt) && _focusPending)
        {
            FocusLater();
        }
    }

    /// <summary>Zooms so that the content under the zoom anchor (the pointer, or the middle of the viewport) stays in place.</summary>
    private void ApplyZoom(double zoom)
    {
        var anchorPoint = _zoomAnchor;
        _zoomAnchor = null;
        var layout = _layout;
        if (layout is null || zoom == layout.Zoom)
        {
            return;
        }

        var size = Scroller.Viewport;
        var offset = Scroller.Offset;
        var point = anchorPoint ?? new Point(size.Width / 2, size.Height / 2);
        var oldZoom = layout.Zoom;
        var shift = Math.Max(0, (size.Width - layout.ContentWidth) / 2);

        // Vertically the anchor of the layout (page and fraction), horizontally the distance from the centre line of
        // the pages, which grows with the zoom; the margin and the gaps keep their size.
        var x = offset.X - shift + point.X;
        var anchor = layout.GetAnchor(offset.Y + point.Y);
        var fromCentre = x - (layout.ContentWidth / 2);

        layout.Zoom = zoom;
        var newShift = Math.Max(0, (size.Width - layout.ContentWidth) / 2);
        var newX = (layout.ContentWidth / 2) + (fromCentre * layout.Zoom / oldZoom);
        ChangeLayout(new Vector(newX + newShift - point.X, layout.GetOffset(anchor) - point.Y));
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || _viewModel is null || _layout is null)
        {
            return;
        }

        e.Handled = true;

        // A wheel notch is 1; touchpads send fractions, which add up to whole steps.
        if (Math.Sign(e.Delta.Y) != Math.Sign(_wheelSteps))
        {
            _wheelSteps = 0;
        }

        _wheelSteps += e.Delta.Y;
        var steps = (int)Math.Truncate(_wheelSteps);
        if (steps == 0)
        {
            return;
        }

        _wheelSteps -= steps;
        var zoom = _viewModel.Zoom;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            zoom = steps > 0 ? ZoomSteps.Next(zoom) : ZoomSteps.Previous(zoom);
        }

        _zoomAnchor = e.GetPosition(PageCanvas);
        _viewModel.Zoom = zoom;
        _zoomAnchor = null;
    }

    /// <summary>Shows <paramref name="page"/> (1-based) with its top edge at the margin below the top of the viewport.</summary>
    private void OnPageRequested(int page)
    {
        var layout = _layout;
        if (layout is null || page < 1 || page > layout.PageCount)
        {
            return;
        }

        Scroller.Offset = new Vector(Scroller.Offset.X, layout.GetPageRect(page - 1).Y - DocumentLayout.Margin);

        // Near the end the page cannot reach the top; the requested page still is the current one.
        if (_viewModel is not null)
        {
            _viewModel.CurrentPage = page;
        }
    }
}
