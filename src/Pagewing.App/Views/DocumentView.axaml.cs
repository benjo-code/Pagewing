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
/// The open document: the pages in the layout of the tab (continuous, a single page, two pages with or without a cover;
/// see DocumentView.LayoutModes.cs), zoom with Ctrl+wheel (around the pointer) and through the view model
/// (Ctrl+Plus/Minus, status bar), the zoom modes of the tab (page width, whole page, actual size; see
/// <see cref="Refit"/>), page jumps, the current page (row) for the status bar, the marks on the pages
/// (DocumentView.Highlights.cs) and selecting text with the mouse (DocumentView.Text.cs). Its
/// <c>DataContext</c> is the active <see cref="TabViewModel"/> of the window; it mirrors the document of the tab's
/// session. A change of the <c>DataContext</c> (another tab) disconnects from the old tab and shows the new one.
/// </summary>
/// <remarks>
/// The view owns the <see cref="DocumentLayout"/> and the scroll position; <see cref="DocumentCanvas"/> draws and
/// plans from them. A shown document takes the zoom of its tab (100 % for a new document, which starts at the top, see
/// <see cref="TabViewModel.Zoom"/>); a tab that was shown before comes back at its saved position
/// (<see cref="TabViewModel.SavedView"/>). While more page sizes arrive, the position at the
/// top of the viewport stays where it is. UI thread only; the session's events are posted to it.
/// </remarks>
public sealed partial class DocumentView : UserControl
{
    /// <summary>Defines the <see cref="PageMargin"/> property.</summary>
    public static readonly StyledProperty<double> PageMarginProperty =
        AvaloniaProperty.Register<DocumentView, double>(nameof(PageMargin), DocumentLayout.Margin);

    /// <summary>
    /// A fit mode changes the zoom only when the zoom it computes differs from the current one by more than this, so
    /// rounding and a viewport that changes by a fraction of a DIP leave the pages alone.
    /// </summary>
    private const double FitTolerance = 0.0005;

    /// <summary>
    /// How often one fit computes the zoom again because its own zoom brought or took away a scroll bar (which takes
    /// its room from the viewport when it does not lie over the pages).
    /// </summary>
    private const int MaxFitPasses = 3;

    /// <summary>
    /// DIPs a fit leaves free in the viewport. Layout rounds the content size up to device pixels, so pages that fill
    /// the viewport exactly up to a floating-point error (684.0000000000001) would be a pixel wider than it and bring a
    /// scroll bar.
    /// </summary>
    private const double FitSlack = 0.01;

    private TabViewModel? _viewModel;
    private ViewerSession? _session;
    private PdfDocument? _document;
    private DocumentLayout? _layout;
    private int _loadedSizes;
    private bool _changingLayout;
    private bool _focusPending;
    private Point? _zoomAnchor;
    private double _wheelSteps;
    private bool _refitPosted;
    private TopLevel? _topLevel;

    // A saved view shown before the viewport had a size (a moved tab in a window not shown yet): restored again, once,
    // when the viewport gets its first size, because its horizontal position depends on the viewport width.
    private ViewState? _pendingRestore;

    public DocumentView()
    {
        InitializeComponent();
        Scroller.PropertyChanged += OnScrollerPropertyChanged;
        Scroller.AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        PageCanvas.FirstSharpPageDrawn += OnFirstSharpPageDrawn;
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

    /// <summary>
    /// Gets or sets the margin around the pages in DIPs (<see cref="DocumentLayout.PageMargin"/>): the resource
    /// <c>Pw.Size.PageMargin</c> of the density (design system 5.4). A change keeps page, zoom and position.
    /// </summary>
    public double PageMargin
    {
        get => GetValue(PageMarginProperty);
        set => SetValue(PageMarginProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageMarginProperty)
        {
            ApplyPageMargin();
        }
    }

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
        if (_viewModel is not null || DataContext is not TabViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _session = viewModel.Session;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PageRequested += OnPageRequested;
        viewModel.DocumentFocusRequested += OnDocumentFocusRequested;
        ConnectHighlights(viewModel);
        ConnectLinks(viewModel);
        _session.Changed += OnSessionChanged;
        Sync();
    }

    private void Disconnect()
    {
        SaveView();
        DisconnectHighlights();
        DropTexts();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PageRequested -= OnPageRequested;
            _viewModel.DocumentFocusRequested -= OnDocumentFocusRequested;
            DisconnectLinks(_viewModel);
            _viewModel = null;
        }

        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session = null;
        }

        CloseDocument();
    }

    /// <summary>
    /// Remembers where the tab being left is shown, as page and fraction (not pixels, so a resize meanwhile does not
    /// matter) and the distance of the viewport's centre from the centre line of the pages (so the same spot stays in
    /// the middle when the window has another width). Only while the session is Ready with the document shown: during
    /// the load of a replacement the session still holds the old document, but the tab has dropped its saved view
    /// and the new document must not inherit the old position. Also called by the window when it closes, so the
    /// history of recently opened files gets where the active tab is now, not where it was when it was last left.
    /// </summary>
    internal void SaveView()
    {
        if (_viewModel is not { } tab || _layout is null || _document is null
            || _session is not { State: ViewerState.Ready } session || !ReferenceEquals(_document, session.Document))
        {
            return;
        }

        tab.SavedView = CurrentView();
    }

    /// <summary>The scroll offset for a saved view in the current layout.</summary>
    private Vector RestoredOffset(DocumentLayout layout, ViewState view)
    {
        if (view.Anchor.Page < 0 || view.Anchor.Page >= layout.PageCount)
        {
            return default;
        }

        // The scroll viewer limits the offset to the content, whatever size the viewport has now.
        var x = view.HorizontalFromCentre + (layout.ContentWidth / 2) - (Scroller.Viewport.Width / 2) + CentringShift(layout);
        return new Vector(x, layout.GetOffset(view.Anchor));
    }

    /// <summary>How far content narrower than the viewport is moved right to centre it (0 when it is wider).</summary>
    private double CentringShift(DocumentLayout layout) => Math.Max(0, (Scroller.Viewport.Width - layout.ContentWidth) / 2);

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

        // The tab first: a document that is new to it resets its zoom (and drops the saved view) before the view reads
        // them below, whichever of the two posted jobs for this change of the session runs first.
        _viewModel?.SyncDocumentFromSession();

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

        // Zoom, layout and position belong to the tab: a new document starts at the top with the default zoom (the
        // tab reset both in Sync), a tab shown again gets back where the view left it.
        var tab = _viewModel;
        layout.Mode = tab?.LayoutMode ?? PageLayoutMode.Continuous;
        if (layout.Mode == PageLayoutMode.SinglePage && pageCount > 0)
        {
            // The page the tab showed (the saved view refers to it), or the first page of a new document.
            layout.ShowPage(Math.Clamp(tab?.SavedView?.Anchor.Page ?? 0, 0, pageCount - 1));
        }

        layout.Zoom = tab?.Zoom ?? ZoomSteps.Default;
        layout.PageMargin = ValidMargin(PageMargin);

        // The text of the document shown before goes with it.
        DropTexts();
        _document = document;
        _layout = layout;
        _loadedSizes = known;

        // The session of the tab renders in the tab's page theme already (TabViewModel.EffectivePageTheme). Set between
        // Detach and Attach, so that a change plans neither the document that went nor the new one before it is laid out.
        PageCanvas.Detach();
        PageCanvas.PageTheme = tab?.EffectivePageTheme ?? PageTheme.Light;
        PageCanvas.Attach(layout, scheduler, cache);
        RefreshHighlights();

        ChangeLayout(tab?.SavedView is { } saved ? RestoredOffset(layout, saved) : default);
        _pendingRestore = tab?.SavedView is { } pending && !(Scroller.Viewport.Width > 0) ? pending : null;

        // A fit mode of the tab fits the viewport as it is now (the window may have changed since the tab was left).
        Refit(chosen: false);
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

    // The search ended with Esc in its field, which goes with the panel: the keys go back to the document.
    private void OnDocumentFocusRequested() => Focus(NavigationMethod.Unspecified);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && TryEscape())
        {
            e.Handled = true;
        }
        else if (!e.Handled && e.Key == Key.C && e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift)
            && HasCopyableSelection)
        {
            // Here and not as a key binding of the window: those run before every key event, also in a text field, and
            // would take Ctrl+C away from the search field. The view only gets the key while it has the focus.
            e.Handled = true;
            _ = CopySelectionAsync(raw: e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift));
        }
        else if (!e.Handled && _layout is not null && TryTurnPageWithSpace(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
        else if (!e.Handled && _layout is not null && ScrollFor(e.Key, e.KeyModifiers) is { } offset)
        {
            e.Handled = true;
            ScrollTo(offset);
        }
    }

    /// <summary>Esc in the document: clears the selection first; without one it ends an active search.</summary>
    /// <returns>Whether Esc had something to end.</returns>
    private bool TryEscape()
    {
        if (_viewModel is not { } tab)
        {
            return false;
        }

        if (tab.HasTextSelection)
        {
            tab.TextSelection = null;
            return true;
        }

        // Never creates a search that the user did not start.
        if (tab.IsSearchCreated && tab.Search.IsActive)
        {
            tab.Search.EndCommand.Execute(null);
            return true;
        }

        return false;
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

        DropTexts();
        _document = null;
        _layout = null;
        _loadedSizes = 0;
        _focusPending = false;
        _pendingRestore = null;
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
        var first = _loadedSizes;
        var added = new PdfPageSize[count];
        for (var i = 0; i < count; i++)
        {
            added[i] = sizes[_loadedSizes + i];
        }

        layout.SetSizes(_loadedSizes, added);
        _loadedSizes += count;

        // Horizontally the centre of the pages stays in place.
        ChangeLayout(new Vector(offset.X + ((layout.ContentWidth - width) / 2), layout.GetOffset(anchor)));

        // A fit that used the estimate for a page of the current row fits its real size.
        var current = (_viewModel?.CurrentPage ?? 0) - 1;
        var currentLast = Math.Max(current, (_viewModel?.CurrentPageLast ?? 0) - 1);
        if (current < first + count && currentLast >= first)
        {
            Refit(chosen: false);
        }
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
            if (e.Property == ScrollViewer.ViewportProperty)
            {
                PostRestore();
                PostRefit();
            }
        }
    }

    /// <summary>
    /// Restores a saved view that was shown before the viewport had a size (<see cref="_pendingRestore"/>) once the
    /// viewport has its first size; posted for the same reason as <see cref="PostRefit"/>, and before it, so a fit mode
    /// fits around the restored position.
    /// </summary>
    private void PostRestore()
    {
        if (_pendingRestore is not { } view || _layout is not { } layout || !(Scroller.Viewport.Width > 0))
        {
            return;
        }

        _pendingRestore = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_layout, layout))
            {
                ChangeLayout(RestoredOffset(layout, view));
            }
        });
    }

    /// <summary>
    /// Fits again after the viewport changed size. Not at once: the change arrives during a layout pass, in which the
    /// new content size cannot be laid out before the scroll position is set (see <see cref="ChangeLayout"/>).
    /// Several changes before the job runs fit once.
    /// </summary>
    private void PostRefit()
    {
        if (_refitPosted || _viewModel is not { ZoomMode: not ZoomMode.Free } || _layout is null)
        {
            return;
        }

        _refitPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refitPosted = false;
            Refit(chosen: false);
        });
    }

    /// <summary>
    /// In a fit mode of the tab, zooms so that the row of the current page fits the viewport
    /// (<see cref="DocumentLayout.FitZoom"/>), like a zoom step around the middle of the viewport; "whole page" then
    /// shows that row from its top. Free zoom and a zoom that would change by no more than <see cref="FitTolerance"/>
    /// leave the pages alone, so the fit never follows its own rounding. Called when a mode is chosen, a document is
    /// shown, the viewport changes size and a page is requested, never while scrolling.
    /// </summary>
    /// <param name="chosen">
    /// The mode was just chosen: "whole page" shows the row from its top even when the zoom stays.
    /// </param>
    /// <remarks>
    /// A scroll bar that takes its room from the viewport can change the viewport in reply to the new zoom: the fit
    /// then computes again for that viewport, but only towards a smaller zoom. A larger one would bring back the bar
    /// that the smaller zoom took away, and the two would take turns forever.
    /// </remarks>
    private void Refit(bool chosen)
    {
        if (_viewModel is not { ZoomMode: not ZoomMode.Free } tab || _layout is not { PageCount: > 0 } layout)
        {
            return;
        }

        var mode = tab.ZoomMode;
        var page = Math.Clamp(tab.CurrentPage - 1, 0, layout.PageCount - 1);
        var changed = false;
        for (var pass = 0; pass < MaxFitPasses; pass++)
        {
            var size = Scroller.Viewport;
            if (!(size.Width > 0 && size.Height > 0))
            {
                // Not laid out yet: the first size of the viewport fits (OnScrollerPropertyChanged).
                return;
            }

            var zoom = layout.FitZoom(mode, page, size.Width - FitSlack, size.Height - FitSlack);
            if (Math.Abs(zoom - layout.Zoom) <= FitTolerance || (pass > 0 && zoom > layout.Zoom))
            {
                break;
            }

            ApplyZoom(zoom);
            changed = true;
            if (Scroller.Viewport == size)
            {
                break;
            }
        }

        if (mode == ZoomMode.FitPage && (chosen || changed) && layout.IsShown(page))
        {
            var top = layout.GetPageRect(layout.PagesOfRow(layout.RowOf(page)).First).Y;
            ScrollTo(new Vector(Scroller.Offset.X, top - layout.PageMargin));
        }

        tab.ApplyFitZoom(layout.Zoom);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateViewport();

    private static double ValidMargin(double margin) => double.IsFinite(margin) && margin >= 0 ? margin : DocumentLayout.Margin;

    /// <summary>
    /// Another density: the layout takes the new margin; the spot at the top of the viewport (page and fraction) and the
    /// distance of the viewport's centre from the centre line of the pages stay, a fit mode fits the new room.
    /// </summary>
    private void ApplyPageMargin()
    {
        var margin = ValidMargin(PageMargin);
        if (_layout is not { } layout || margin == layout.PageMargin)
        {
            return;
        }

        var offset = Scroller.Offset;
        var anchor = layout.GetAnchor(offset.Y);
        var fromCentre = offset.X - CentringShift(layout) + (Scroller.Viewport.Width / 2) - (layout.ContentWidth / 2);
        layout.PageMargin = margin;
        var x = fromCentre + (layout.ContentWidth / 2) - (Scroller.Viewport.Width / 2) + CentringShift(layout);
        ChangeLayout(new Vector(x, layout.GetOffset(anchor)));
        Refit(chosen: false);
    }

    /// <summary>
    /// The canvas drew the first sharp page of the shown document: the tab may render its page thumbnails now
    /// (<see cref="TabViewModel.HasShownSharpPage"/>). The canvas reports only for the document it still draws, which is
    /// the document of this tab.
    /// </summary>
    private void OnFirstSharpPageDrawn()
    {
        if (_viewModel is { } tab && _document is not null && ReferenceEquals(_document, _session?.Document))
        {
            tab.HasShownSharpPage = true;
        }
    }

    /// <summary>Hands the visible part of the content to the canvas and updates the current row.</summary>
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
            ShowCurrentRow(layout.GetMostVisiblePage(offset.Y, offset.Y + size.Height));
        }

        // The text of what is visible now (DocumentView.Text.cs): read ahead, and the visible part of the selection.
        PreloadText();
        FollowSelectionWithViewport();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Zoom) && _viewModel is not null)
        {
            ApplyZoom(_viewModel.Zoom);
        }
        else if (e.PropertyName == nameof(TabViewModel.ZoomMode))
        {
            Refit(chosen: true);
        }
        else if (e.PropertyName == nameof(TabViewModel.LayoutMode))
        {
            ApplyLayoutMode();
        }
        else if (e.PropertyName == nameof(TabViewModel.IsPasswordPrompt) && _focusPending)
        {
            FocusLater();
        }
        else if (e.PropertyName == nameof(TabViewModel.TextSelection))
        {
            RefreshHighlights();
        }
        else if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage))
        {
            // The text may be read now: first that of the visible pages.
            PreloadText();
        }
        else if (e.PropertyName == nameof(TabViewModel.EffectivePageTheme) && _viewModel is { } tab && _layout is not null)
        {
            // The session renders in the new theme already: the canvas drops the tiles of the old one and plans again.
            // Page, zoom, scroll position and selection stay.
            PageCanvas.PageTheme = tab.EffectivePageTheme;
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
        if (_viewModel is null || _layout is null)
        {
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            if (TryTurnPageWithWheel(e))
            {
                e.Handled = true;
            }

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

    /// <summary>
    /// Shows the row of <paramref name="page"/> (1-based) with its top edge at the margin below the top of the
    /// viewport (in the single-page layout that page alone, from its top); a fit mode first fits that row. The first
    /// page of the row becomes the current page.
    /// </summary>
    private void OnPageRequested(int page)
    {
        var layout = _layout;
        if (layout is null || page < 1 || page > layout.PageCount)
        {
            return;
        }

        var index = page - 1;

        // A page asked for replaces a saved view that still waits for the viewport.
        _pendingRestore = null;
        if (layout.Mode == PageLayoutMode.SinglePage)
        {
            layout.ShowPage(index);
        }

        ShowCurrentRow(index);
        Refit(chosen: false);

        // The pages of a row are top-aligned. Through ChangeLayout: the single page may have changed the content size.
        ChangeLayout(new Vector(Scroller.Offset.X, layout.GetPageRect(index).Y - layout.PageMargin));

        // Near the end the row cannot reach the top; the requested row still is the current one.
        ShowCurrentRow(index);
    }
}
