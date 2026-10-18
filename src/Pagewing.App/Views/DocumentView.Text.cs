using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// The text of the pages: reading it ahead for the visible pages, the cursor over text and links, and selecting text
/// with the mouse (drag, double click for a word, triple click for a line). The selection belongs to the tab
/// (<see cref="TabViewModel.TextSelection"/>); the view draws it with the other marks (<see cref="RefreshHighlights"/>).
/// </summary>
/// <remarks>
/// Nothing here runs before the first sharp page of a document: the text comes from <see cref="TabViewModel.PageTexts"/>,
/// which is <see langword="null"/> until then. The UI thread never waits for a page: a page that is not read yet is
/// asked for and the view acts once it is there. The source belongs to the shown document and is dropped with it.
/// </remarks>
public sealed partial class DocumentView
{
    /// <summary>How far (DIPs) the pointer may move between press and release for the press to count as a click.</summary>
    private const double ClickSlop = 4;

    // The text of the pages of the shown document, from the tab once it has shown a sharp page; dropped with the document.
    private PageTextSource? _texts;

    /// <summary>
    /// The most pages whose text the view reads ahead and draws the selection on at a time: clearly fewer than the
    /// source keeps, so pages the view uses never push each other out of its cache (which would read them again and again).
    /// </summary>
    internal const int MaxTextPages = PageTextSource.MaxCachedPages - 8;

    // Pages of _texts this view asked for that have not arrived yet: asked for once.
    private readonly HashSet<int> _textRequests = [];

    // Pages of _texts whose text could not be read (damaged): the view does not ask for them again.
    private readonly HashSet<int> _failedTextPages = [];

    // Where Ctrl+C puts the text; made on first use (see Clipboard).
    private IClipboardService? _clipboard;

    // Whether a copy is running: a second one is ignored.
    private bool _copying;

    // The press of the left button that is going on (until it is released), or null.
    private TextPress? _press;

    // The last position of the pointer over the document (canvas coordinates), and the page under it whose text was
    // missing when the cursor was chosen (-1 for none): the cursor is chosen again when that text arrives.
    private Point _pointer;
    private int _hoverPage = -1;

    // The link the last press went down on (followed on release by the link handling), or null.
    private PageLink? _pressedLink;

    // The last link whose target was checked for the cursor, and the result; a pointer usually rests on one link.
    private PageLink? _checkedLink;
    private bool _checkedLinkClickable;

    private CursorKind _cursorKind;
    private Cursor? _textCursor;
    private Cursor? _linkCursor;

    // The pages the selection was last drawn for: the visible part of the selection (plus a page either side).
    private (int First, int Last) _selectionPages = (0, -1);

    // The hint on a page without text; made when it is first needed.
    private Border? _noTextHint;
    private DispatcherTimer? _noTextHintTimer;

    private enum CursorKind
    {
        Arrow,
        Text,
        Link,
    }

    private enum PressKind
    {
        /// <summary>The text of the page is being read; the press acts once it is there.</summary>
        Waiting,

        /// <summary>Dragging moves the end of the selection.</summary>
        Selecting,

        /// <summary>A word or a line was selected, or a double click hit nothing: dragging changes nothing.</summary>
        Done,

        /// <summary>Off the pages, on a page without text or before the text may be read: a click clears the selection.</summary>
        Empty,

        /// <summary>On a link that can be followed: no selection.</summary>
        Link,
    }

    /// <summary>Gets or sets how long the hint on a page without text stays (3 s; shorter in tests).</summary>
    internal TimeSpan NoTextHintDuration { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets how the view reads the text of a page it asks for (read-ahead, selection, a press):
    /// <see cref="PageTextSource.GetAsync"/>. Tests hold the read back to act while a page is certainly not read yet.
    /// </summary>
    internal Func<PageTextSource, int, PdfPriority, Task<PageText>> ReadText { get; set; } =
        static (texts, page, priority) => texts.GetAsync(page, priority);

    /// <summary>Gets the number of pages whose text the view asked for and that have not arrived yet (for tests).</summary>
    internal int PendingTextRequests => _textRequests.Count;

    /// <summary>Gets whether the current press waits for the text of its page (for tests).</summary>
    internal bool IsPressWaitingForText => _press is { Kind: PressKind.Waiting };

    /// <summary>Gets the link the last press of the left button went down on, or <see langword="null"/>.</summary>
    internal PageLink? PressedLink => _pressedLink;

    /// <summary>Gets the cursor over text (I-beam).</summary>
    internal Cursor TextCursor => _textCursor ??= new Cursor(StandardCursorType.Ibeam);

    /// <summary>Gets the cursor over a link that can be followed (hand).</summary>
    internal Cursor LinkCursor => _linkCursor ??= new Cursor(StandardCursorType.Hand);

    /// <summary>
    /// Gets whether a link can be followed and gets the hand cursor: a page of the document, or an address that
    /// <see cref="ExternalLink.TryParse"/> accepts (http, https, mailto). A script, a file or a program never can.
    /// </summary>
    internal static bool IsClickable(PageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.Target switch
        {
            PageTarget => true,
            UriTarget uri => ExternalLink.TryParse(uri.Url, out _),
            _ => false,
        };
    }

    /// <summary>
    /// Selects the characters <paramref name="firstChar"/> to <paramref name="lastCharExclusive"/> of a page (0-based),
    /// reading its text first if needed (for tests). Returns the selection of the tab, or <see langword="null"/> when
    /// there is no text to select (no document, before the first sharp page, or the tab or document changed meanwhile).
    /// </summary>
    internal async Task<TextSelection?> SelectAsync(int page, int firstChar, int lastCharExclusive)
    {
        if (_viewModel is not { } tab || TextSource() is not { } texts)
        {
            return null;
        }

        PageText text;
        try
        {
            text = await texts.GetAsync(page, PdfPriority.Interactive);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }

        if (!ReferenceEquals(tab, _viewModel) || !ReferenceEquals(texts, _texts))
        {
            return null;
        }

        var first = Math.Clamp(firstChar, 0, text.Chars.Count);
        var last = Math.Clamp(lastCharExclusive, first, text.Chars.Count);
        tab.TextSelection = new TextSelection(new TextPosition(page, first), new TextPosition(page, last));
        return tab.TextSelection;
    }

    /// <summary>
    /// Gets or sets where Ctrl+C puts the text: the clipboard of the window, made when the first copy needs it (nothing
    /// is touched before, so it costs nothing at startup). Tests set a fake.
    /// </summary>
    internal IClipboardService Clipboard
    {
        get => _clipboard ??= new AvaloniaClipboardService(this);
        set => _clipboard = value;
    }

    /// <summary>Gets or sets how long a copy waits before it tries again when the clipboard is locked (100 ms).</summary>
    internal TimeSpan ClipboardRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets whether Ctrl+C has something to copy: a selection that covers at least one caret step. Decides whether the
    /// key is taken; without one it goes on to the window.
    /// </summary>
    private bool HasCopyableSelection => _viewModel?.TextSelection is { IsEmpty: false };

    /// <summary>
    /// Copies the selected text to the clipboard: as prose with formatting (plain text and HTML) or, with
    /// <paramref name="raw"/>, as plain text with the line breaks of the PDF and no HTML. Without a selection, before the
    /// first sharp page or when the selection holds no visible text, nothing is copied and the clipboard stays as it was.
    /// </summary>
    /// <remarks>
    /// The text of every page of the selection is read one page at a time (<see cref="ReadText"/>, so a page the view
    /// does not hold is read now, with <see cref="PdfPriority.Interactive"/>); the UI thread never waits for it. The
    /// pages are held until the string is made, because paragraphs run across pages (about 100 to 300 KB per page: a
    /// copy of 200 pages needs some 40 MB for a moment). The strings are made on the thread pool. A second copy while one
    /// runs is ignored. A clipboard that another program holds is tried again once after <see cref="ClipboardRetryDelay"/>;
    /// a failure after that is swallowed.
    /// </remarks>
    /// <param name="raw">Whether to copy plain text only, as the PDF has its lines.</param>
    /// <returns>A task that ends when the clipboard is set or the copy was dropped.</returns>
    internal async Task CopySelectionAsync(bool raw)
    {
        if (_copying || _viewModel is not { } tab || tab.TextSelection is not { IsEmpty: false } selection || TextSource() is not { } texts)
        {
            return;
        }

        _copying = true;
        try
        {
            var pages = new Dictionary<int, PageText>();
            for (var page = selection.Start.Page; page <= selection.End.Page; page++)
            {
                try
                {
                    pages[page] = await ReadText(texts, page, PdfPriority.Interactive);
                }
                catch (Exception)
                {
                    // The document went or a page is damaged: the selection cannot be copied as a whole.
                    return;
                }

                // Back on the UI thread. Another document or tab meanwhile: its text is not on the clipboard to be.
                if (!ReferenceEquals(tab, _viewModel) || !ReferenceEquals(texts, _texts))
                {
                    return;
                }
            }

            var (plain, html) = await Task.Run(() => FormatSelection(selection, pages, raw));
            if (plain.Length == 0)
            {
                return;
            }

            await SetClipboardAsync(plain, html);
        }
        finally
        {
            _copying = false;
        }
    }

    private static (string Plain, string? Html) FormatSelection(TextSelection selection, Dictionary<int, PageText> pages, bool raw)
    {
        var slices = TextFlow.SlicesOf(selection, page => pages[page]);
        return raw ? (TextFlow.Raw(slices), null) : (TextFlow.Plain(slices), TextFlow.Html(slices));
    }

    /// <summary>Puts the text on the clipboard; a locked clipboard is tried again once, a second failure is swallowed.</summary>
    private async Task SetClipboardAsync(string plain, string? html)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Clipboard.SetTextAsync(plain, html);
                return;
            }
            catch (Exception)
            {
                // IOException or COMException of a clipboard that another program holds; the platform throws what it
                // likes. A copy that fails must never take the window with it.
                if (attempt >= 2)
                {
                    return;
                }
            }

            await Task.Delay(ClipboardRetryDelay);
        }
    }

    /// <summary>The text of the shown document, or <see langword="null"/> before its first sharp page (see the remarks).</summary>
    private PageTextSource? TextSource()
    {
        if (_texts is null && _document is not null && _viewModel is { HasShownSharpPage: true } tab
            && ReferenceEquals(_document, _session?.Document))
        {
            _texts = tab.PageTexts;
        }

        return _texts;
    }

    /// <summary>Forgets the text of the document that goes, the press on it and the hint; the loads still running are ignored.</summary>
    private void DropTexts()
    {
        _texts = null;
        _textRequests.Clear();
        _failedTextPages.Clear();

        // A press that waits for its text must not begin later: after a tab switch the same source may come back.
        if (_press is { } press)
        {
            press.Released = true;
        }

        _press = null;
        _pressedLink = null;
        _checkedLink = null;
        _hoverPage = -1;
        _selectionPages = (0, -1);
        SetCursor(CursorKind.Arrow);
        HideNoTextHint();
    }

    /// <summary>Gets the text of a page if it is read; a source disposed with its document is dropped.</summary>
    private bool TryGetText(PageTextSource texts, int page, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PageText? text)
    {
        try
        {
            return texts.TryGet(page, out text);
        }
        catch (ObjectDisposedException)
        {
            // The document went; its event follows. Until then there is no text.
            if (ReferenceEquals(texts, _texts))
            {
                _texts = null;
                _textRequests.Clear();
                _failedTextPages.Clear();
            }

            text = null;
            return false;
        }
    }

    /// <summary>
    /// Asks for the text of a page once; <see cref="OnTextArrived"/> follows when it is there. A page whose text could
    /// not be read is not asked for again by the view (a press on it still tries, see <see cref="BeginWhenReadAsync"/>).
    /// </summary>
    private void RequestText(PageTextSource texts, int page, PdfPriority priority)
    {
        if (_layout is not { } layout || page < 0 || page >= layout.PageCount || _failedTextPages.Contains(page)
            || !_textRequests.Add(page))
        {
            return;
        }

        _ = LoadTextAsync(texts, page, priority);
    }

    private async Task LoadTextAsync(PageTextSource texts, int page, PdfPriority priority)
    {
        var failed = false;
        try
        {
            await ReadText(texts, page, priority);
        }
        catch (ObjectDisposedException)
        {
            // Disposed with its document: the check below drops the result.
        }
        catch (Exception)
        {
            // A damaged page: there is no text to show, and reading it on every scroll would only repeat the failure.
            failed = true;
        }

        // Back on the UI thread (the continuation of an await there). The document may have gone meanwhile.
        if (!ReferenceEquals(texts, _texts))
        {
            return;
        }

        _textRequests.Remove(page);
        if (failed)
        {
            _failedTextPages.Add(page);
        }
        else
        {
            OnTextArrived(page);
        }
    }

    /// <summary>The text of a page arrived: whatever waited for it (the selection, a drag, the cursor) uses it now.</summary>
    private void OnTextArrived(int page)
    {
        if (_viewModel?.TextSelection is { } selection && page >= selection.Start.Page && page <= selection.End.Page)
        {
            RefreshHighlights();
        }

        if (_press is { Kind: PressKind.Selecting } press)
        {
            DragTo(press, _pointer);
        }
        else if (_press is null && page == _hoverPage)
        {
            UpdateCursor(_pointer);
        }
    }

    /// <summary>
    /// Reads ahead the text of the visible pages and one page either side, in the background (once a sharp page of the
    /// document was shown; pages that are read or being read are skipped). Called after every change of the viewport.
    /// </summary>
    private void PreloadText()
    {
        if (_layout is not { PageCount: > 0 } layout || TextSource() is not { } texts)
        {
            return;
        }

        var (first, last) = VisibleTextPages(layout);
        for (var page = first; page <= last; page++)
        {
            if (!_textRequests.Contains(page) && !TryGetText(texts, page, out _))
            {
                RequestText(texts, page, PdfPriority.Background);
            }
        }
    }

    /// <summary>
    /// The visible pages and one page either side, at most <see cref="MaxTextPages"/> around the middle of them, or
    /// <c>(0, -1)</c>. Only these pages are read ahead and carry the drawn selection.
    /// </summary>
    private (int First, int Last) VisibleTextPages(Core.Viewing.DocumentLayout layout)
    {
        var size = Scroller.Viewport;
        var top = Scroller.Offset.Y;
        if (!(size.Height > 0))
        {
            return (0, -1);
        }

        var (first, last) = layout.GetVisiblePages(top, top + size.Height);
        if (first > last)
        {
            return (0, -1);
        }

        first = Math.Max(0, first - 1);
        last = Math.Min(layout.PageCount - 1, last + 1);
        if (last - first + 1 > MaxTextPages)
        {
            // Far zoomed out: more pages than the source keeps. The middle ones; the rest is too small to read anyway.
            first = Math.Max(first, ((first + last) / 2) - (MaxTextPages / 2));
            last = first + MaxTextPages - 1;
        }

        return (first, last);
    }

    /// <summary>The pages of the selection that are drawn now: its visible part, or <c>(0, -1)</c>.</summary>
    private (int First, int Last) SelectionPagesToDraw(TextSelection selection)
    {
        if (_layout is not { PageCount: > 0 } layout)
        {
            return (0, -1);
        }

        var (first, last) = VisibleTextPages(layout);
        first = Math.Max(first, selection.Start.Page);
        last = Math.Min(last, selection.End.Page);
        return first <= last ? (first, last) : (0, -1);
    }

    /// <summary>
    /// After a scroll: a selection whose visible part is now another range of pages is drawn again (only those pages
    /// are drawn, so a selection over many pages does not read them all).
    /// </summary>
    private void FollowSelectionWithViewport()
    {
        if (_viewModel?.TextSelection is { } selection && SelectionPagesToDraw(selection) != _selectionPages)
        {
            RefreshHighlights();
        }
    }

    /// <summary>
    /// Adds the rectangles of the selection on its visible pages to <paramref name="marks"/>: from the anchor to the
    /// focus, all characters of the pages in between. A page whose text is not read yet is asked for and drawn once it
    /// is there.
    /// </summary>
    private void AddSelectionMarks(List<PageHighlight> marks)
    {
        _selectionPages = (0, -1);
        if (_viewModel?.TextSelection is not { } selection || TextSource() is not { } texts)
        {
            return;
        }

        var (first, last) = SelectionPagesToDraw(selection);
        _selectionPages = (first, last);
        for (var page = first; page <= last; page++)
        {
            if (!TryGetText(texts, page, out var text))
            {
                RequestText(texts, page, PdfPriority.Interactive);
                continue;
            }

            var count = text.Chars.Count;
            var start = page == selection.Start.Page ? Math.Min(selection.Start.Caret, count) : 0;
            var end = page == selection.End.Page ? Math.Min(selection.End.Caret, count) : count;
            if (start >= end)
            {
                continue;
            }

            foreach (var rect in text.RectsOf(start, end))
            {
                marks.Add(new PageHighlight(page, rect, HighlightKind.Selection));
            }
        }
    }

    // After the scroll viewer and the scroll bars had the press (handledEventsToo).
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A click on the pages (or their scroll bars) gives the keys to the document. With the pointer, so no focus ring appears.
        if (_layout is not null && !IsFocused)
        {
            Focus(NavigationMethod.Pointer, e.KeyModifiers);
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || _layout is null || _viewModel is null
            || (e.KeyModifiers & KeyModifiers.Control) != 0 || IsInScrollBar(e.Source))
        {
            return;
        }

        var position = e.GetPosition(PageCanvas);
        _pointer = position;
        _pressedLink = null;
        var press = new TextPress(position, e.ClickCount);
        _press = press;
        e.Pointer.Capture(this);

        if (PageCanvas.PagePointAt(position) is not { } hit || !IsOnPage(hit.Page, hit.Position) || TextSource() is not { } texts)
        {
            press.Kind = PressKind.Empty;
            return;
        }

        press.Page = hit.Page;
        press.PagePoint = hit.Position;
        if (TryGetText(texts, hit.Page, out var text))
        {
            Begin(press, text);
        }
        else
        {
            press.Kind = PressKind.Waiting;
            _ = BeginWhenReadAsync(press, texts);
        }
    }

    /// <summary>The text of the pressed page is read only now: the press acts as if it had been there (the hint even after a release).</summary>
    private async Task BeginWhenReadAsync(TextPress press, PageTextSource texts)
    {
        PageText text;
        try
        {
            text = await ReadText(texts, press.Page, PdfPriority.Interactive);
        }
        catch (Exception)
        {
            // Disposed with its document or a damaged page: the press stays a press on nothing.
            press.Kind = PressKind.Empty;
            return;
        }

        if (!ReferenceEquals(texts, _texts) || press.Kind != PressKind.Waiting)
        {
            return;
        }

        if (press.Released)
        {
            press.Kind = PressKind.Empty;
            if (!text.HasText)
            {
                ShowNoTextHint();
            }

            return;
        }

        Begin(press, text);
        if (press.Kind == PressKind.Selecting)
        {
            // The pointer may have moved on while the text was read.
            DragTo(press, _pointer);
        }
    }

    /// <summary>A press on a page whose text is there: a link, a caret, a word or a line.</summary>
    private void Begin(TextPress press, PageText text)
    {
        if (_viewModel is not { } tab)
        {
            return;
        }

        var (x, y) = (press.PagePoint.X, press.PagePoint.Y);
        if (!text.HasText)
        {
            press.Kind = PressKind.Empty;
            ShowNoTextHint();
            return;
        }

        if (text.LinkAt(x, y) is { } link && IsClickableCached(link))
        {
            press.Kind = PressKind.Link;
            _pressedLink = link;
            return;
        }

        if (press.ClickCount <= 1)
        {
            press.Kind = PressKind.Selecting;
            press.Anchor = new TextPosition(press.Page, text.CaretAt(x, y));
            tab.TextSelection = null;
            return;
        }

        press.Kind = PressKind.Done;
        var index = text.CharAt(x, y);
        if (index < 0)
        {
            return;
        }

        var (start, end) = press.ClickCount == 2 ? text.WordAt(index) : text.LineAt(index);
        tab.TextSelection = new TextSelection(new TextPosition(press.Page, start), new TextPosition(press.Page, end));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(PageCanvas);
        if (_press is { } press)
        {
            _pointer = position;
            if (!press.Moved && Distance(position, press.Start) >= ClickSlop)
            {
                press.Moved = true;
            }

            if (press.Kind == PressKind.Selecting)
            {
                DragTo(press, position);
            }

            return;
        }

        if (IsInScrollBar(e.Source))
        {
            _hoverPage = -1;
            SetCursor(CursorKind.Arrow);
            return;
        }

        _pointer = position;
        UpdateCursor(position);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _press is not { } press)
        {
            return;
        }

        var position = e.GetPosition(PageCanvas);
        _pointer = position;
        if (press.Kind == PressKind.Selecting)
        {
            DragTo(press, position);
        }

        // A click (no drag) on nothing clears the selection.
        if (!press.Moved && Distance(position, press.Start) < ClickSlop && (press.Kind is PressKind.Empty or PressKind.Waiting) && _viewModel is { } tab)
        {
            tab.TextSelection = null;
        }

        EndPress(press);
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }

        UpdateCursor(position);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_press is { } press)
        {
            EndPress(press);
        }
    }

    private void EndPress(TextPress press)
    {
        press.Released = true;
        if (ReferenceEquals(_press, press))
        {
            _press = null;
        }
    }

    /// <summary>Moves the end of the selection to the caret under <paramref name="position"/>; off the pages it stays where it was.</summary>
    private void DragTo(TextPress press, Point position)
    {
        if (_viewModel is not { } tab || TextSource() is not { } texts || PageCanvas.PagePointAt(position) is not { } hit)
        {
            return;
        }

        if (!TryGetText(texts, hit.Page, out var text))
        {
            // Read first; the drag goes on from the latest pointer position when it is there (OnTextArrived).
            RequestText(texts, hit.Page, PdfPriority.Interactive);
            return;
        }

        SetCursor(CursorKind.Text);
        var focus = new TextPosition(hit.Page, text.CaretAt(hit.Position.X, hit.Position.Y));
        tab.TextSelection = new TextSelection(press.Anchor, focus);
    }

    /// <summary>The cursor for the point: hand over a link that can be followed, I-beam over a character, else the arrow.</summary>
    private void UpdateCursor(Point position)
    {
        _hoverPage = -1;
        var kind = CursorKind.Arrow;
        if (PageCanvas.PagePointAt(position) is { } hit && IsOnPage(hit.Page, hit.Position) && TextSource() is { } texts)
        {
            if (TryGetText(texts, hit.Page, out var text))
            {
                var (x, y) = (hit.Position.X, hit.Position.Y);
                kind = text.LinkAt(x, y) is { } link && IsClickableCached(link) ? CursorKind.Link
                    : text.CharAt(x, y) >= 0 ? CursorKind.Text
                    : CursorKind.Arrow;
            }
            else
            {
                // Chosen again when the text arrives (the read-ahead asks for the visible pages).
                _hoverPage = hit.Page;
            }
        }

        SetCursor(kind);
    }

    private void SetCursor(CursorKind kind)
    {
        if (kind == _cursorKind)
        {
            return;
        }

        _cursorKind = kind;
        Scroller.Cursor = kind switch
        {
            CursorKind.Text => TextCursor,
            CursorKind.Link => LinkCursor,
            _ => null,
        };
    }

    private bool IsClickableCached(PageLink link)
    {
        if (!ReferenceEquals(link, _checkedLink))
        {
            _checkedLink = link;
            _checkedLinkClickable = IsClickable(link);
        }

        return _checkedLinkClickable;
    }

    /// <summary>Whether a point (PDF points) lies on the page itself, not beside it in its row or in the margin.</summary>
    private bool IsOnPage(int page, Point point)
    {
        if (_layout is not { } layout || page < 0 || page >= layout.PageCount)
        {
            return false;
        }

        var size = layout.GetPageSize(page);
        return point.X >= 0 && point.Y >= 0 && point.X <= size.Width && point.Y <= size.Height;
    }

    private static bool IsInScrollBar(object? source)
    {
        for (var visual = source as Visual; visual is not null and not DocumentView; visual = visual.GetVisualParent())
        {
            if (visual is ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Shows for <see cref="NoTextHintDuration"/> that the page has no text to select, at the bottom of the document
    /// area; a press while it is shown does not make it stay longer.
    /// </summary>
    private void ShowNoTextHint()
    {
        if (_noTextHint is { IsVisible: true })
        {
            return;
        }

        var hint = _noTextHint ??= CreateNoTextHint();

        // The text is set anew each time, so the live region announces it each time (the same value would not change).
        var line = (TextBlock)hint.Child!;
        line.Text = string.Empty;
        line.Text = Strings.NoTextOnPage;
        hint.IsVisible = true;
        if (_noTextHintTimer is null)
        {
            _noTextHintTimer = new DispatcherTimer();
            _noTextHintTimer.Tick += (_, _) => HideNoTextHint();
        }

        _noTextHintTimer.Interval = NoTextHintDuration;
        _noTextHintTimer.Start();
    }

    private void HideNoTextHint()
    {
        _noTextHintTimer?.Stop();
        if (_noTextHint is not null)
        {
            _noTextHint.IsVisible = false;
        }
    }

    /// <summary>
    /// The hint card: one line in type.body on surface.raised with a 1 px border.subtle edge and radius.overlay, centred
    /// at the bottom of the document area. Made in code when it is first shown, so it costs nothing at startup. It takes
    /// no pointer and no focus; screen readers announce it.
    /// </summary>
    private Border CreateNoTextHint()
    {
        // The text is set when the hint is shown (ShowNoTextHint).
        var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        line.Classes.Add("body");
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);

        var hint = new Border
        {
            Name = "NoTextHint",
            Child = line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Space(3), Space(2)),
            Margin = new Thickness(Space(4), 0, Space(4), Space(5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Focusable = false,
            IsVisible = false,
        };
        hint.Bind(Border.BackgroundProperty, this.GetResourceObservable("Pw.Brush.Surface.Raised"));
        hint.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Pw.Brush.Border.Subtle"));
        if (this.TryFindResource("Pw.Radius.Overlay", ActualThemeVariant, out var radius) && radius is CornerRadius corners)
        {
            hint.CornerRadius = corners;
        }

        DocumentLayers.Children.Add(hint);
        return hint;
    }

    private double Space(int step) =>
        this.TryFindResource($"Pw.Space.{step}", ActualThemeVariant, out var value) && value is double space ? space : 0;

    /// <summary>A press of the left button on the document, from the press until the release.</summary>
    private sealed class TextPress(Point start, int clickCount)
    {
        /// <summary>Where the button went down (canvas coordinates).</summary>
        public Point Start { get; } = start;

        public int ClickCount { get; } = clickCount;

        public PressKind Kind { get; set; }

        /// <summary>The pressed page and the point on it in PDF points (only on a page).</summary>
        public int Page { get; set; }

        public Point PagePoint { get; set; }

        /// <summary>Where the selection started (while <see cref="PressKind.Selecting"/>).</summary>
        public TextPosition Anchor { get; set; }

        /// <summary>Whether the pointer moved by <see cref="ClickSlop"/> or more since the press.</summary>
        public bool Moved { get; set; }

        public bool Released { get; set; }
    }
}
