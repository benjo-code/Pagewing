using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using RenderOptions = Avalonia.Media.RenderOptions;

namespace Pagewing.App.Controls;

/// <summary>
/// Draws the visible pages of a document and keeps the render scheduler fed with what the viewport shows: per page
/// a placeholder until an image exists (design system 7.5), then the white page with its edge and shadow (5.3), the
/// coarse preview stretched over it and the sharp tiles on top, swapped without cross-fade (5.6).
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

    private static readonly RenderOptions PreviewOptions = new() { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality };
    // Sharp tiles are drawn 1:1 onto device pixels, so nothing may be interpolated (bilinear would soften them).
    private static readonly RenderOptions SharpOptions = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    private readonly TileBitmaps _bitmaps = new();
    private readonly HashSet<int> _failedPages = [];
    private Source? _source;
    private DipRect _viewport;
    private double _renderScaling = 1;
    private IReadOnlyList<RenderRequest> _lastRequests = [];
    private readonly List<(TileKey Key, Rect Destination)> _lastSharpDestinations = [];
    private HashSet<TileKey> _wanted = [];
    private HashSet<TileKey> _shown = [];

    static DocumentCanvas()
    {
        AffectsRender<DocumentCanvas>(
            PlaceholderBrushProperty, PlaceholderBorderBrushProperty, PlaceholderTextBrushProperty, PageShadowProperty, CornerRadiusProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DocumentCanvas>(false);
    }

    /// <summary>Raised on the UI thread once per document, in the frame after the first sharp tile of the first page was drawn.</summary>
    internal event Action? FirstSharpTileDrawn;

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

    /// <summary>Edge and shadow of a page (design system 5.3: <c>Pw.Shadow.Page</c>).</summary>
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

        // What the cache already holds (scrolling back, a preview from the background buffer) shows at once; the
        // scheduler skips cached keys, so no TileReady would come for them.
        foreach (var request in requests)
        {
            if (!request.Background && !_bitmaps.Contains(request.Key) && source.Cache.TryGet(request.Key, out var image))
            {
                AddBitmap(request.Key, image);
            }
        }

        InvalidateVisual();
    }

    /// <summary>Converts a point of the viewport (DIPs) to a page and a position on it in PDF points, or <see langword="null"/> off the pages.</summary>
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

        var rect = layout.GetPageRect(first);
        var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
        return (first, new Point((x - rect.X) / dipsPerPoint, (y - rect.Y) / dipsPerPoint));
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
        _lastSharpDestinations.Clear();

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
                    DrawPlaceholder(context, pageRect, page);
                    continue;
                }

                context.DrawRectangle(Brushes.White, null, new RoundedRect(pageRect), PageShadow);
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
                    }
                }
            }
        }

        if (drewFirstSharpTile && !source.FirstSharpTileReported)
        {
            source.FirstSharpTileReported = true;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
            {
                if (ReferenceEquals(_source, source))
                {
                    FirstSharpTileDrawn?.Invoke();
                }
            });
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

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

    private void DrawPlaceholder(DrawingContext context, Rect pageRect, int page)
    {
        context.DrawRectangle(PlaceholderBrush, null, new RoundedRect(pageRect), PageShadow);
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
            PlaceholderTextBrush ?? Brushes.Gray)
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

    private void AddBitmap(TileKey key, RenderedImage image)
    {
        try
        {
            _bitmaps.Add(key, TileBitmaps.Create(image));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            // The bitmap could not be made (memory, or the platform refused the size): the page shows what it has.
            _failedPages.Add(key.Page);
        }
    }

    /// <summary>A tile is ready in the cache (UI thread, posted by <see cref="Source.OnTileReady"/>).</summary>
    private void ApplyTile(Source source, TileKey key)
    {
        if (!ReferenceEquals(source, _source))
        {
            return;
        }

        // Only what the viewport shows becomes a bitmap; a background preview stays in the cache until it is shown.
        if (_shown.Contains(key) && !_bitmaps.Contains(key) && source.Cache.TryGet(key, out var image))
        {
            AddBitmap(key, image);
            InvalidateVisual();
        }

        TileReceived?.Invoke(key);
    }

    /// <summary>Rendering a key failed (UI thread). The scheduler never tries it again; the page keeps what it has.</summary>
    private void ApplyFailure(Source source, TileKey key)
    {
        if (ReferenceEquals(source, _source) && _failedPages.Add(key.Page))
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

        // Pool thread: must not block and must never throw (it would end the process).
        public void OnTileReady(TileKey key) => Post(() => owner.ApplyTile(this, key));

        public void OnTileFailed(TileKey key, Exception error) => Post(() => owner.ApplyFailure(this, key));

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
