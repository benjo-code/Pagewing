using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using RenderOptions = Avalonia.Media.RenderOptions;

namespace Pagewing.App.Controls;

/// <summary>
/// Draws the visible pages of a document and keeps the render scheduler fed with what the viewport shows: per page
/// a placeholder until an image exists (design system 7.5), then the page ground (white, or the ground of a dark page,
/// see <see cref="PageTheme"/>) with its edge and shadow (5.3), the coarse preview stretched over it and the sharp tiles
/// on top, swapped without cross-fade (5.6).
/// </summary>
/// <remarks>
/// <para>
/// The canvas does not scroll itself: <c>DocumentView</c> lays it under a <see cref="ScrollViewer"/> and hands it
/// the visible part of the content with <see cref="SetViewport"/>. Pages are drawn in viewport coordinates, so the
/// coordinates stay small however long the document is (a content offset of millions of DIPs would lose sub-pixel
/// precision in the single-precision drawing backend and let tiles drift apart).
/// </para>
/// <para>
/// UI thread only. The scheduler raises its events on pool threads; the handlers only post to the UI thread (never
/// wait, never throw), and a posted tile is dropped when the document changed or the tile is no longer wanted.
/// </para>
/// </remarks>
internal sealed class DocumentCanvas : Control
{
    /// <summary>Room around the viewport for the page shadow (blur 10 + offset 2), so a page just outside still casts it.</summary>
    private const double ShadowExtent = 12;

    /// <summary>Font size of the page number and the failure message on a placeholder (type.caption).</summary>
    private const double PlaceholderFontSize = 12;

    public static readonly StyledProperty<IBrush?> PlaceholderBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(PlaceholderBrush));

    public static readonly StyledProperty<IBrush?> PlaceholderBorderBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(PlaceholderBorderBrush));

    public static readonly StyledProperty<IBrush?> PlaceholderTextBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(PlaceholderTextBrush));

    public static readonly StyledProperty<BoxShadows> PageShadowProperty =
        AvaloniaProperty.Register<DocumentCanvas, BoxShadows>(nameof(PageShadow));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DocumentCanvas, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<IBrush?> SearchHitBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SearchHitBrush));

    public static readonly StyledProperty<IBrush?> SearchCurrentBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SearchCurrentBrush));

    public static readonly StyledProperty<IBrush?> SearchOnBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SearchOnBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<PageTheme> PageThemeProperty =
        AvaloniaProperty.Register<DocumentCanvas, PageTheme>(nameof(PageTheme));

    public static readonly StyledProperty<IBrush?> DarkPageBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(DarkPageBrush));

    public static readonly StyledProperty<IBrush?> DarkPageForegroundBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(DarkPageForegroundBrush));

    public static readonly StyledProperty<IBrush?> SearchHitOnDarkBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SearchHitOnDarkBrush));

    public static readonly StyledProperty<IBrush?> SearchCurrentOnDarkBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SearchCurrentOnDarkBrush));

    public static readonly StyledProperty<IBrush?> SelectionOnDarkBrushProperty =
        AvaloniaProperty.Register<DocumentCanvas, IBrush?>(nameof(SelectionOnDarkBrush));

    /// <summary>Width of the frame around the current search match (design system 7.4).</summary>
    private const double CurrentFrameThickness = 1.5;

    private static readonly RenderOptions PreviewOptions = new() { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality };
    // Sharp tiles are drawn 1:1 onto device pixels, so nothing may be interpolated (bilinear would soften them).
    private static readonly RenderOptions SharpOptions = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    // The yellow of a search match lies behind the text, as a marker pen does: multiplied with the page, so the black
    // text stays black on it, whatever the page image is.
    private static readonly RenderOptions MultiplyOptions = new()
    {
        BitmapBlendingMode = BitmapBlendingMode.Multiply,
        BitmapInterpolationMode = BitmapInterpolationMode.None,
    };

    // Where the resources are missing (a canvas outside the app): the colours PDFium paints a dark page with.
    private static readonly IBrush FallbackDarkPage = new ImmutableSolidColorBrush(PageThemeColors.DarkBackground);
    private static readonly IBrush FallbackDarkForeground = new ImmutableSolidColorBrush(PageThemeColors.DarkForeground);

    private static readonly HighlightKind[] DrawingOrder = [HighlightKind.Selection, HighlightKind.SearchHit, HighlightKind.SearchCurrent];

    private readonly TileBitmaps _bitmaps = new();
    private readonly PageShadowPainter _shadows = new();
    private readonly HashSet<int> _failedPages = [];
    private Source? _source;
    private DipRect _viewport;
    private double _renderScaling = 1;
    private IReadOnlyList<RenderRequest> _lastRequests = [];
    private readonly List<(TileKey Key, Rect Destination)> _lastSharpDestinations = [];
    private HashSet<TileKey> _wanted = [];
    private HashSet<TileKey> _shown = [];

    // Bitmaps of pages whose content changed (InvalidatePages): drawn until the new image of the same key replaces them,
    // so the page does not flash empty, and never kept once that image is there.
    private readonly HashSet<TileKey> _stale = [];

    // The marks on the pages, grouped by page once per SetHighlights and sorted in drawing order; Render only looks up
    // the visible pages.
    private Dictionary<int, PageHighlight[]> _highlights = [];
    private readonly List<(PageHighlight Highlight, Rect Destination)> _lastHighlightDestinations = [];
    private Pen? _currentFrame;
    private WriteableBitmap? _hitFill;
    private WriteableBitmap? _currentFill;
    private Pen? _currentFrameOnDark;

    // Counts the changes of PageTheme. Read on the pool threads of the scheduler events, so that a result posted for one
    // theme is dropped when it arrives after a switch to another (see OnPageThemeChanged).
    private long _themeEpoch;

    static DocumentCanvas()
    {
        AffectsRender<DocumentCanvas>(
            PlaceholderBrushProperty, PlaceholderBorderBrushProperty, PlaceholderTextBrushProperty, PageShadowProperty, CornerRadiusProperty,
            SearchHitBrushProperty, SearchCurrentBrushProperty, SearchOnBrushProperty, SelectionBrushProperty,
            DarkPageBrushProperty, DarkPageForegroundBrushProperty, SearchHitOnDarkBrushProperty, SearchCurrentOnDarkBrushProperty,
            SelectionOnDarkBrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DocumentCanvas>(false);
    }

    /// <summary>Raised on the UI thread once per document, in the frame after the first sharp tile of the first page was drawn.</summary>
    internal event Action? FirstSharpTileDrawn;

    /// <summary>
    /// Raised on the UI thread once per document, in the frame after the first sharp tile of any page was drawn (after
    /// <see cref="FirstSharpTileDrawn"/> when both come in the same frame).
    /// </summary>
    internal event Action? FirstSharpPageDrawn;

    /// <summary>Raised on the UI thread for every rendered tile that reaches the canvas, wanted or not (for tests).</summary>
    internal event Action<TileKey>? TileReceived;

    /// <summary>Fill of a page that has no image yet (surface.raised).</summary>
    public IBrush? PlaceholderBrush
    {
        get => GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    /// <summary>Edge of a page that has no image yet (border.subtle).</summary>
    public IBrush? PlaceholderBorderBrush
    {
        get => GetValue(PlaceholderBorderBrushProperty);
        set => SetValue(PlaceholderBorderBrushProperty, value);
    }

    /// <summary>Page number and failure message on a placeholder (text.secondary).</summary>
    public IBrush? PlaceholderTextBrush
    {
        get => GetValue(PlaceholderTextBrushProperty);
        set => SetValue(PlaceholderTextBrushProperty, value);
    }

    /// <summary>
    /// Edge and shadow of a page (design system 5.3: <c>Pw.Shadow.Page</c>). Drawn from a pre-rendered image
    /// (<see cref="PageShadowPainter"/>), never as a box shadow of the page rectangle.
    /// </summary>
    public BoxShadows PageShadow
    {
        get => GetValue(PageShadowProperty);
        set => SetValue(PageShadowProperty, value);
    }

    /// <summary>The corners the pages are clipped to (the document area has radius.overlay at the top left).</summary>
    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Fill of a search match (<c>Pw.Brush.Search.Hit</c>).</summary>
    public IBrush? SearchHitBrush
    {
        get => GetValue(SearchHitBrushProperty);
        set => SetValue(SearchHitBrushProperty, value);
    }

    /// <summary>Fill of the current search match (<c>Pw.Brush.Search.Current</c>).</summary>
    public IBrush? SearchCurrentBrush
    {
        get => GetValue(SearchCurrentBrushProperty);
        set => SetValue(SearchCurrentBrushProperty, value);
    }

    /// <summary>Frame of the current search match (<c>Pw.Brush.Search.On</c>), so the current match differs not by colour alone.</summary>
    public IBrush? SearchOnBrush
    {
        get => GetValue(SearchOnBrushProperty);
        set => SetValue(SearchOnBrushProperty, value);
    }

    /// <summary>Overlay of selected text (<c>Pw.Brush.Selection.Page</c>, translucent).</summary>
    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets the theme the pages are rendered in: the ground of a page and of its placeholder, and how the marks
    /// are drawn on it. Set it after the scheduler renders in the new theme (<see cref="RenderScheduler.SetPageTheme"/>):
    /// a change drops every bitmap and failure of the old theme, also those still on their way to the UI thread, and
    /// plans the viewport again. Default: <see cref="PageTheme.Light"/>.
    /// </summary>
    public PageTheme PageTheme
    {
        get => GetValue(PageThemeProperty);
        set => SetValue(PageThemeProperty, value);
    }

    /// <summary>Ground of a dark page and of its placeholder (<c>Pw.Brush.Page.DarkBackground</c>).</summary>
    public IBrush? DarkPageBrush
    {
        get => GetValue(DarkPageBrushProperty);
        set => SetValue(DarkPageBrushProperty, value);
    }

    /// <summary>
    /// Foreground of a dark page (<c>Pw.Brush.Page.DarkForeground</c>): the page number on its placeholder and the frame
    /// of the current search match, which <c>search.on</c> would not show on the dark ground.
    /// </summary>
    public IBrush? DarkPageForegroundBrush
    {
        get => GetValue(DarkPageForegroundBrushProperty);
        set => SetValue(DarkPageForegroundBrushProperty, value);
    }

    /// <summary>Fill of a search match on a dark page (<c>Pw.Brush.Search.HitOnDark</c>, translucent, drawn over the page).</summary>
    public IBrush? SearchHitOnDarkBrush
    {
        get => GetValue(SearchHitOnDarkBrushProperty);
        set => SetValue(SearchHitOnDarkBrushProperty, value);
    }

    /// <summary>Fill of the current search match on a dark page (<c>Pw.Brush.Search.CurrentOnDark</c>, translucent).</summary>
    public IBrush? SearchCurrentOnDarkBrush
    {
        get => GetValue(SearchCurrentOnDarkBrushProperty);
        set => SetValue(SearchCurrentOnDarkBrushProperty, value);
    }

    /// <summary>Overlay of selected text on a dark page (<c>Pw.Brush.Selection.PageOnDark</c>, translucent).</summary>
    public IBrush? SelectionOnDarkBrush
    {
        get => GetValue(SelectionOnDarkBrushProperty);
        set => SetValue(SelectionOnDarkBrushProperty, value);
    }

    /// <summary>Gets the pages whose rendering failed in the current theme; their placeholder says so (for tests).</summary>
    internal IReadOnlySet<int> FailedPages => _failedPages;

    /// <summary>Gets where the last frame drew the highlights, in canvas coordinates (for tests).</summary>
    internal IReadOnlyList<(PageHighlight Highlight, Rect Destination)> LastHighlightDestinations => _lastHighlightDestinations;

    /// <summary>Gets the requests of the last plan, most important first (for tests).</summary>
    internal IReadOnlyList<RenderRequest> LastRequests => _lastRequests;

    /// <summary>Gets where the last frame drew the sharp tiles, in canvas coordinates (for tests).</summary>
    internal IReadOnlyList<(TileKey Key, Rect Destination)> LastSharpDestinations => _lastSharpDestinations;

    /// <summary>Gets the bitmaps the canvas holds (for tests).</summary>
    internal TileBitmaps Bitmaps => _bitmaps;

    /// <summary>Gets the layout of the attached document, or <see langword="null"/>.</summary>
    internal DocumentLayout? Layout => _source?.Layout;

    /// <summary>Gets the visible part of the content (DIPs), as last set with <see cref="SetViewport"/>.</summary>
    internal DipRect Viewport => _viewport;

    /// <summary>Gets the scheduler of the attached document, or <see langword="null"/> (for tests).</summary>
    internal RenderScheduler? Scheduler => _source?.Scheduler;

    /// <summary>Gets the cache of the attached document, or <see langword="null"/>.</summary>
    internal RenderCache? Cache => _source?.Cache;

    /// <summary>Gets the keys whose bitmap shows content that changed and waits for its new image (for tests).</summary>
    internal IReadOnlySet<TileKey> StaleKeys => _stale;

    /// <summary>
    /// Starts drawing a document. The layout stays owned by the caller, who calls <see cref="SetViewport"/> after
    /// every change of it; the scheduler and the cache belong to the session and may be disposed on another thread.
    /// </summary>
    internal void Attach(DocumentLayout layout, RenderScheduler scheduler, RenderCache cache)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(cache);
        Detach();
        var source = new Source(this, layout, scheduler, cache);
        scheduler.TileReady += source.OnTileReady;
        scheduler.TileFailed += source.OnTileFailed;
        _source = source;
        InvalidateVisual();
    }

    /// <summary>Stops drawing the document: unsubscribes, stops its pending work and frees the bitmaps.</summary>
    internal void Detach()
    {
        var source = _source;
        _source = null;
        _lastRequests = [];
        _wanted = [];
        _shown = [];
        _failedPages.Clear();
        _bitmaps.Clear();
        _stale.Clear();

        // The marks belong to the document that goes.
        _highlights = [];
        _lastHighlightDestinations.Clear();
        if (source is not null)
        {
            source.Scheduler.TileReady -= source.OnTileReady;
            source.Scheduler.TileFailed -= source.OnTileFailed;

            // Nothing of this document is wanted any more; does nothing when the session disposed the scheduler.
            source.Scheduler.Update([]);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The session swapped the document for the file it just saved (<see cref="ViewerSession.AdoptSavedDocumentAsync"/>):
    /// the same pages in the same cache, rendered from now on by <paramref name="scheduler"/>. Layout, bitmaps and marks
    /// stay; what the old scheduler still reports is dropped, and the viewport is planned again with the new one.
    /// </summary>
    internal void ReplaceScheduler(RenderScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (_source is not { } old || ReferenceEquals(old.Scheduler, scheduler))
        {
            return;
        }

        old.Scheduler.TileReady -= old.OnTileReady;
        old.Scheduler.TileFailed -= old.OnTileFailed;
        var source = new Source(this, old.Layout, scheduler, old.Cache)
        {
            FirstSharpTileReported = old.FirstSharpTileReported,
            FirstSharpPageReported = old.FirstSharpPageReported,
        };
        scheduler.TileReady += source.OnTileReady;
        scheduler.TileFailed += source.OnTileFailed;
        _source = source;

        // A page that failed in the old document may render in the new one.
        _failedPages.Clear();
        SetViewport(_viewport, _renderScaling);
    }

    /// <summary>
    /// The content of <paramref name="pages"/> changed (<see cref="ViewerSession.PagesInvalidated"/>): the session dropped
    /// their images from the cache, so their bitmaps here show the old content. They stay until their new images arrive
    /// (no empty page in between), and the viewport is planned again, which renders those pages anew: the preview first,
    /// then the sharp tiles.
    /// </summary>
    internal void InvalidatePages(IReadOnlySet<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (_source is null || pages.Count == 0)
        {
            return;
        }

        foreach (var key in _bitmaps.Keys)
        {
            if (pages.Contains(key.Page))
            {
                _stale.Add(key);
            }
        }

        _failedPages.RemoveWhere(pages.Contains);
        SetViewport(_viewport, _renderScaling);
    }

    /// <summary>
    /// Sets the visible part of the content (DIPs, content coordinates of the layout) and the device pixels per DIP,
    /// plans the render jobs for it and hands them to the scheduler. Call after every change of the scroll offset,
    /// the viewport size, the zoom or the page sizes.
    /// </summary>
    internal void SetViewport(DipRect viewport, double renderScaling)
    {
        _viewport = viewport;
        _renderScaling = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1;
        var source = _source;
        if (source is null)
        {
            InvalidateVisual();
            return;
        }

        IReadOnlyList<RenderRequest> requests;
        try
        {
            requests = ViewportPlanner.Plan(source.Layout, viewport, _renderScaling);
        }
        catch (PdfRenderException)
        {
            // A page so large that its pixel size leaves the int range at this zoom: nothing to render for now.
            requests = [];
        }

        var wanted = new HashSet<TileKey>(requests.Count);
        var shown = new HashSet<TileKey>(requests.Count);
        foreach (var request in requests)
        {
            wanted.Add(request.Key);
            if (!request.Background)
            {
                shown.Add(request.Key);
            }
        }

        _lastRequests = requests;
        _wanted = wanted;
        _shown = shown;
        source.Scheduler.Update(requests);
        _bitmaps.Retain(wanted);
        if (_stale.Count > 0)
        {
            _stale.RemoveWhere(key => !_bitmaps.Contains(key));
        }

        // What the cache already holds (scrolling back, a preview from the background buffer) shows at once; the
        // scheduler skips cached keys, so no TileReady would come for them. A stale bitmap is replaced as soon as the
        // cache has the new image of its key.
        foreach (var request in requests)
        {
            if (!request.Background && NeedsBitmap(request.Key) && source.Cache.TryGet(request.Key, out var image))
            {
                AddBitmap(request.Key, image);
            }
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Replaces all highlights (search matches and the selection together): groups them by page once, in drawing order
    /// (the selection first, the current match last, so its frame stays on top), and redraws.
    /// </summary>
    internal void SetHighlights(IReadOnlyList<PageHighlight> highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        if (highlights.Count == 0)
        {
            if (_highlights.Count > 0)
            {
                _highlights = [];
                InvalidateVisual();
            }

            return;
        }

        var grouped = new Dictionary<int, List<PageHighlight>>();
        foreach (var highlight in highlights)
        {
            if (!grouped.TryGetValue(highlight.Page, out var list))
            {
                grouped[highlight.Page] = list = [];
            }

            list.Add(highlight);
        }

        var ordered = new Dictionary<int, PageHighlight[]>(grouped.Count);
        foreach (var (page, list) in grouped)
        {
            var array = new PageHighlight[list.Count];
            var next = 0;
            foreach (var kind in DrawingOrder)
            {
                foreach (var highlight in list)
                {
                    if (highlight.Kind == kind)
                    {
                        array[next++] = highlight;
                    }
                }
            }

            ordered[page] = array;
        }

        _highlights = ordered;
        InvalidateVisual();
    }

    /// <summary>
    /// Converts a point of the viewport (DIPs) to a page and a position on it in PDF points, or <see langword="null"/>
    /// in a gap or margin between rows. In a row of two pages it is the page under the point, or the nearer one beside
    /// them; the position may then lie outside that page.
    /// </summary>
    internal (int Page, Point Position)? PagePointAt(Point viewportPoint)
    {
        var layout = _source?.Layout;
        if (layout is null || layout.PageCount == 0)
        {
            return null;
        }

        var x = viewportPoint.X + _viewport.X;
        var y = viewportPoint.Y + _viewport.Y;
        var (first, last) = layout.GetVisiblePages(y, y);
        if (first > last)
        {
            return null;
        }

        var page = first;
        var rect = layout.GetPageRect(first);
        for (var other = first + 1; other <= last; other++)
        {
            var candidate = layout.GetPageRect(other);
            if (DistanceAcross(x, candidate) < DistanceAcross(x, rect))
            {
                page = other;
                rect = candidate;
            }
        }

        var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
        return (page, new Point((x - rect.X) / dipsPerPoint, (y - rect.Y) / dipsPerPoint));

        static double DistanceAcross(double x, DipRect rect) => x < rect.X ? rect.X - x : x > rect.Right ? x - rect.Right : 0;
    }

    /// <summary>
    /// Converts a point of the viewport (DIPs) to PDF points on <paramref name="page"/> (from the top left of the page as
    /// shown), wherever the point lies, also beside the page; <see langword="null"/> without a document or for no page.
    /// </summary>
    internal Point? PointOnPage(int page, Point viewportPoint)
    {
        var layout = _source?.Layout;
        if (layout is null || page < 0 || page >= layout.PageCount)
        {
            return null;
        }

        var rect = layout.GetPageRect(page);
        var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
        return new Point((viewportPoint.X + _viewport.X - rect.X) / dipsPerPoint, (viewportPoint.Y + _viewport.Y - rect.Y) / dipsPerPoint);
    }

    /// <summary>
    /// Gets where <paramref name="page"/> is drawn in the viewport (DIPs, its origin on whole device pixels as <see cref="Render"/>
    /// puts it) and the DIPs per PDF point, or <see langword="null"/> without a document or for no page.
    /// </summary>
    internal (Rect PageRect, double DipsPerPoint)? PagePlacement(int page)
    {
        var layout = _source?.Layout;
        if (layout is null || page < 0 || page >= layout.PageCount)
        {
            return null;
        }

        var rect = layout.GetPageRect(page);
        return (new Rect(Snap(rect.X - _viewport.X), Snap(rect.Y - _viewport.Y), rect.Width, rect.Height), DocumentLayout.DipsPerPoint * layout.Zoom);
    }

    public override void Render(DrawingContext context)
    {
        var source = _source;
        if (source is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var layout = source.Layout;
        var (first, last) = layout.GetVisiblePages(_viewport.Y - ShadowExtent, _viewport.Bottom + ShadowExtent);
        if (first > last)
        {
            return;
        }

        var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
        var scaleKey = TileKey.ToScaleKey(dipsPerPoint * _renderScaling);
        var drewFirstSharpTile = false;
        var drewSharpTile = false;
        var dark = PageTheme == PageTheme.Dark;
        _lastSharpDestinations.Clear();
        _lastHighlightDestinations.Clear();

        using (context.PushClip(new RoundedRect(new Rect(Bounds.Size), CornerRadius)))
        {
            for (var page = first; page <= last; page++)
            {
                var rect = layout.GetPageRect(page);

                // Page origin on whole device pixels: the tiles then meet without seams and match the device grid.
                var pageRect = new Rect(Snap(rect.X - _viewport.X), Snap(rect.Y - _viewport.Y), rect.Width, rect.Height);
                _bitmaps.TryGet(new TileKey(page, TileKey.PreviewScaleKey, 0, 0), out var preview);
                var hasSharp = HasSharpTile(page, scaleKey);
                if (preview is null && !hasSharp)
                {
                    DrawPlaceholder(context, pageRect, page, dark);
                    continue;
                }

                _shadows.Draw(context, pageRect, PageShadow, _renderScaling);
                context.DrawRectangle(dark ? DarkPageBrush ?? FallbackDarkPage : Brushes.White, null, pageRect);
                using (context.PushClip(pageRect))
                {
                    if (preview is not null)
                    {
                        using (context.PushRenderOptions(PreviewOptions))
                        {
                            context.DrawImage(preview, new Rect(preview.Size), pageRect);
                        }
                    }

                    if (hasSharp)
                    {
                        using (context.PushRenderOptions(SharpOptions))
                        {
                            foreach (var key in _bitmaps.Keys)
                            {
                                if (key.Page != page || key.ScaleKey != scaleKey || !_bitmaps.TryGet(key, out var tile))
                                {
                                    continue;
                                }

                                // One image pixel per device pixel: the scale key is rounded, so the image may be
                                // up to a device pixel larger or smaller than the page rectangle. The clip cuts the
                                // excess; a gap shows the preview below, never a hairline.
                                var destination = new Rect(
                                    pageRect.X + (key.Column * TilePlanner.TileSize / _renderScaling),
                                    pageRect.Y + (key.Row * TilePlanner.TileSize / _renderScaling),
                                    tile.PixelSize.Width / _renderScaling,
                                    tile.PixelSize.Height / _renderScaling);
                                context.DrawImage(tile, new Rect(tile.Size), destination);
                                _lastSharpDestinations.Add((key, destination));
                            }
                        }

                        drewFirstSharpTile |= page == 0;
                        drewSharpTile = true;
                    }

                    // On the page image, so the marks stay glued to the text at every zoom (and on the snapped page rectangle).
                    if (_highlights.Count > 0 && _highlights.TryGetValue(page, out var marks))
                    {
                        DrawHighlights(context, marks, pageRect, dipsPerPoint, dark);
                    }
                }
            }
        }

        // In the startup path both come in the same frame and share one animation frame.
        var reportFirstTile = drewFirstSharpTile && !source.FirstSharpTileReported;
        var reportFirstPage = drewSharpTile && !source.FirstSharpPageReported;
        if (reportFirstTile || reportFirstPage)
        {
            source.FirstSharpTileReported |= reportFirstTile;
            source.FirstSharpPageReported = true;
            if (reportFirstTile)
            {
                StartupPhases.Mark("tile-drawn");
            }

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
            {
                if (!ReferenceEquals(_source, source))
                {
                    return;
                }

                if (reportFirstTile)
                {
                    StartupPhases.Mark("frame-after-tile");
                    FirstSharpTileDrawn?.Invoke();
                }

                if (reportFirstPage)
                {
                    FirstSharpPageDrawn?.Invoke();
                }
            });
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
        _shadows.Clear();
        ClearFills();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageShadowProperty)
        {
            // Another theme variant: the images of the old shadow are not needed any more.
            _shadows.Clear();
        }
        else if (change.Property == PageThemeProperty)
        {
            OnPageThemeChanged();
        }
        else if (change.Property == SearchOnBrushProperty)
        {
            _currentFrame = null;
        }
        else if (change.Property == DarkPageForegroundBrushProperty)
        {
            _currentFrameOnDark = null;
        }
        else if (change.Property == SearchHitBrushProperty || change.Property == SearchCurrentBrushProperty)
        {
            // Another theme variant or colour: the one-pixel images are made again from the new brushes.
            ClearFills();
        }
    }

    /// <summary>
    /// Draws the marks of one page in canvas coordinates: a rectangle in page points becomes DIPs from the (snapped)
    /// page origin. On a light page a search match is multiplied with the page, so its text stays; the selection is
    /// translucent. On a dark page (<paramref name="dark"/>) multiplying would let the matches vanish on the dark ground:
    /// every mark is drawn over the page with its translucent brush for dark pages, and the current match is framed in
    /// the page foreground.
    /// </summary>
    private void DrawHighlights(DrawingContext context, PageHighlight[] marks, Rect pageRect, double dipsPerPoint, bool dark)
    {
        // The blend mode for the search matches is pushed once per page, when the first match comes (the marks are in
        // drawing order: the selection first, then the matches). It applies to images only, so the frame of the current
        // match is drawn as usual inside it.
        DrawingContext.PushedState? multiply = null;
        try
        {
            foreach (var mark in marks)
            {
                var rect = mark.Rect;
                var destination = new Rect(
                    pageRect.X + (rect.Left * dipsPerPoint),
                    pageRect.Y + (rect.Top * dipsPerPoint),
                    rect.Width * dipsPerPoint,
                    rect.Height * dipsPerPoint);
                if (!double.IsFinite(destination.X + destination.Y + destination.Width + destination.Height))
                {
                    continue;
                }

                switch (mark.Kind)
                {
                    case HighlightKind.SearchHit when dark:
                        context.DrawRectangle(SearchHitOnDarkBrush, null, destination);
                        break;
                    case HighlightKind.SearchHit:
                        multiply ??= context.PushRenderOptions(MultiplyOptions);
                        FillMultiplied(context, SearchHitBrush, ref _hitFill, destination);
                        break;
                    case HighlightKind.SearchCurrent when dark:
                        context.DrawRectangle(SearchCurrentOnDarkBrush, null, destination);
                        context.DrawRectangle(
                            null, _currentFrameOnDark ??= new Pen(DarkPageForegroundBrush ?? FallbackDarkForeground, CurrentFrameThickness), destination);
                        break;
                    case HighlightKind.SearchCurrent:
                        multiply ??= context.PushRenderOptions(MultiplyOptions);
                        FillMultiplied(context, SearchCurrentBrush, ref _currentFill, destination);
                        if (SearchOnBrush is { } on)
                        {
                            context.DrawRectangle(null, _currentFrame ??= new Pen(on, CurrentFrameThickness), destination);
                        }

                        break;
                    default:
                        context.DrawRectangle(dark ? SelectionOnDarkBrush : SelectionBrush, null, destination);
                        break;
                }

                _lastHighlightDestinations.Add((mark, destination));
            }
        }
        finally
        {
            multiply?.Dispose();
        }
    }

    /// <summary>
    /// The pages render in another theme: nothing of the old one may be drawn again. The new epoch makes every result of
    /// the scheduler that was posted before the switch harmless (a late failure would otherwise mark a page failed in the
    /// new theme); the bitmaps and failures go, and the viewport is planned again, which shows what the cache already
    /// holds of the new theme (the scheduler emptied it of the old one).
    /// </summary>
    private void OnPageThemeChanged()
    {
        Interlocked.Increment(ref _themeEpoch);
        _bitmaps.Clear();
        _stale.Clear();
        _failedPages.Clear();
        if (_source is not null)
        {
            SetViewport(_viewport, _renderScaling);
        }
        else
        {
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with a colour multiplied with what is below (the page), inside the
    /// <see cref="MultiplyOptions"/> the caller pushed. Avalonia applies a blend mode to images only, not to the fill of
    /// a rectangle, so the colour is a one-pixel image stretched over the rectangle (made once per brush). A brush that
    /// is not a plain colour fills normally.
    /// </summary>
    private static void FillMultiplied(DrawingContext context, IBrush? brush, ref WriteableBitmap? fill, Rect destination)
    {
        fill ??= brush is ISolidColorBrush solid ? CreateFill(solid.Color) : null;
        if (fill is null)
        {
            context.DrawRectangle(brush, null, destination);
            return;
        }

        context.DrawImage(fill, new Rect(fill.Size), destination);
    }

    private static WriteableBitmap CreateFill(Color color)
    {
        var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var pixels = bitmap.Lock();

        // Premultiplied alpha, blue first.
        static byte Scale(byte channel, byte alpha) => (byte)((channel * alpha / 255) & 0xFF);
        byte[] bgra = [Scale(color.B, color.A), Scale(color.G, color.A), Scale(color.R, color.A), color.A];
        Marshal.Copy(bgra, 0, pixels.Address, bgra.Length);
        return bitmap;
    }

    private void ClearFills()
    {
        _hitFill?.Dispose();
        _currentFill?.Dispose();
        _hitFill = null;
        _currentFill = null;
    }

    /// <summary>Gets the theme epoch (any thread).</summary>
    private long CurrentThemeEpoch => Interlocked.Read(ref _themeEpoch);

    private double Snap(double value) => Math.Round(value * _renderScaling) / _renderScaling;

    private bool HasSharpTile(int page, int scaleKey)
    {
        foreach (var key in _bitmaps.Keys)
        {
            if (key.Page == page && key.ScaleKey == scaleKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A page without an image yet: on a light page <c>surface.raised</c> with the page number in <c>text.secondary</c>
    /// (design system 7.5); on a dark page (<paramref name="dark"/>) the ground of the dark page with the number in its
    /// foreground, so the page does not flash when its image comes.
    /// </summary>
    private void DrawPlaceholder(DrawingContext context, Rect pageRect, int page, bool dark)
    {
        _shadows.Draw(context, pageRect, PageShadow, _renderScaling);
        context.DrawRectangle(dark ? DarkPageBrush ?? FallbackDarkPage : PlaceholderBrush, null, pageRect);
        if (PlaceholderBorderBrush is { } border)
        {
            context.DrawRectangle(new Pen(border, 1), pageRect.Deflate(0.5));
        }

        var failed = _failedPages.Contains(page);

        // A line of text is at least as high as the font size: when even that does not fit, the text below would not
        // be drawn either, so it is not made (at low zoom this runs for every visible page in every frame).
        if (pageRect.Height < PlaceholderFontSize || (failed && pageRect.Width < 48))
        {
            return;
        }

        var text = new FormattedText(
            failed ? Strings.PagePlaceholderFailed : (page + 1).ToString(Strings.Culture),
            Strings.Culture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this)),
            PlaceholderFontSize,
            dark ? DarkPageForegroundBrush ?? FallbackDarkForeground : PlaceholderTextBrush ?? Brushes.Gray)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(1, pageRect.Width - 16),
        };

        if (text.Height > pageRect.Height)
        {
            // Too small to carry text (low zoom); the empty placeholder says enough.
            return;
        }

        using (context.PushClip(pageRect))
        {
            var origin = new Point(pageRect.X + ((pageRect.Width - text.MaxTextWidth) / 2), pageRect.Y + ((pageRect.Height - text.Height) / 2));
            context.DrawText(text, origin);
        }
    }

    /// <summary>Whether <paramref name="key"/> has no bitmap yet, or one of content that changed.</summary>
    private bool NeedsBitmap(TileKey key) => !_bitmaps.Contains(key) || _stale.Contains(key);

    private void AddBitmap(TileKey key, RenderedImage image)
    {
        try
        {
            // Replaces (and disposes) a stale bitmap of the same key.
            _bitmaps.Add(key, TileBitmaps.Create(image));
            _stale.Remove(key);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            // The bitmap could not be made (memory, or the platform refused the size): the page shows what it has.
            _failedPages.Add(key.Page);
        }
    }

    /// <summary>
    /// A tile is ready in the cache (UI thread, posted by <see cref="Source.OnTileReady"/> in the theme epoch
    /// <paramref name="epoch"/>; a post of an earlier theme is dropped).
    /// </summary>
    private void ApplyTile(Source source, TileKey key, long epoch)
    {
        if (!ReferenceEquals(source, _source) || epoch != CurrentThemeEpoch)
        {
            return;
        }

        // Only what the viewport shows becomes a bitmap; a background preview stays in the cache until it is shown.
        if (_shown.Contains(key) && NeedsBitmap(key) && source.Cache.TryGet(key, out var image))
        {
            AddBitmap(key, image);
            InvalidateVisual();
            if (key.Page == 0)
            {
                StartupPhases.Mark(key.IsPreview ? "preview-applied" : "tile-applied");
            }
        }

        TileReceived?.Invoke(key);
    }

    /// <summary>
    /// Rendering a key failed (UI thread, posted in the theme epoch <paramref name="epoch"/>). The scheduler does not try
    /// it again in this theme; the page keeps what it has. A failure of an earlier theme says nothing about the new one.
    /// </summary>
    private void ApplyFailure(Source source, TileKey key, long epoch)
    {
        if (ReferenceEquals(source, _source) && epoch == CurrentThemeEpoch && _failedPages.Add(key.Page))
        {
            InvalidateVisual();
        }
    }

    /// <summary>One attached document: what the canvas draws and the handlers it gave the scheduler.</summary>
    private sealed class Source(DocumentCanvas owner, DocumentLayout layout, RenderScheduler scheduler, RenderCache cache)
    {
        public DocumentLayout Layout { get; } = layout;

        public RenderScheduler Scheduler { get; } = scheduler;

        public RenderCache Cache { get; } = cache;

        public bool FirstSharpTileReported { get; set; }

        public bool FirstSharpPageReported { get; set; }

        // Pool thread: must not block and must never throw (it would end the process). The epoch is read here, when the
        // scheduler reports: it reports nothing of an old theme after its SetPageTheme returned, and the canvas counts the
        // switch only after that (PageTheme is set once the scheduler renders in the new theme).
        public void OnTileReady(TileKey key)
        {
            var epoch = owner.CurrentThemeEpoch;
            Post(() => owner.ApplyTile(this, key, epoch));
        }

        public void OnTileFailed(TileKey key, Exception error)
        {
            var epoch = owner.CurrentThemeEpoch;
            Post(() => owner.ApplyFailure(this, key, epoch));
        }

        private static void Post(Action action)
        {
            try
            {
                Dispatcher.UIThread.Post(action);
            }
            catch (Exception)
            {
                // The dispatcher is shutting down: nobody draws any more.
            }
        }
    }
}
