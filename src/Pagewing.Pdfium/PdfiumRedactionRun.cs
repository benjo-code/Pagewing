using System.Diagnostics;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>A rectangle to redact, in points from the top left of the displayed page (after rotation and CropBox).</summary>
internal readonly record struct PdfiumRedactionArea(double Left, double Top, double Right, double Bottom);

/// <summary>The colour of the opaque area that replaces what was removed.</summary>
internal readonly record struct PdfiumRedactionFill(byte Red, byte Green, byte Blue)
{
    /// <summary>Gets black.</summary>
    public static PdfiumRedactionFill Black { get; } = new(0, 0, 0);

    /// <summary>Gets white.</summary>
    public static PdfiumRedactionFill White { get; } = new(255, 255, 255);
}

/// <summary>What happens to the pixels of an image under a rectangle (mirror of the Core type).</summary>
internal enum PdfiumImageTreatment
{
    Solid,
    Pixelate,
    Blur,
}

/// <summary>Why an image area got the solid fill although pixelating or blurring was chosen (mirror of the Core type).</summary>
internal enum PdfiumImageSolidReason
{
    TooSmall,
    TurnedOrSheared,
    Clipped,
    Overlapping,
    Unreadable,
    TooManyImages,
}

/// <summary>
/// Hooks for tests that make a run see what PDFium would not show it (a box that is not a number, a page that differs from its scratch
/// copy, a failing read-back, a cancellation at a chosen moment). Production code never sets them. Every hook runs on the PDFium thread.
/// </summary>
internal sealed class PdfiumRedactionSeam
{
    /// <summary>Called with the characters read from the page (and whether it is the scratch copy) before anything is planned from them.</summary>
    public Action<CharTable, bool>? CharsRead { get; set; }

    /// <summary>Called with the characters read back from the rewritten page (and whether it is the scratch copy) before they are checked.</summary>
    public Action<CharTable, bool>? ReadBack { get; set; }

    /// <summary>Called when the edits of a pass are done, before the content is written (the argument tells whether it is the scratch copy).</summary>
    public Action<bool>? AfterEdits { get; set; }

    /// <summary>Asked for the page object index (and whether it is the scratch copy) whether its bounds count as unreadable.</summary>
    public Func<int, bool, bool>? BoundsUnreadable { get; set; }
}

/// <summary>What was removed as a whole instead of precisely.</summary>
internal enum PdfiumFallbackKind
{
    TextObjectRemovedWhole,
    FormObjectRemovedWhole,
    ImageRemovedWhole,
    PathRemovedWhole,
}

/// <summary>One piece of content removed as a whole, and why.</summary>
internal sealed record PdfiumFallback(PdfiumFallbackKind Kind, string Reason);

/// <summary>What redacting one page did or would do (mirror of the Core type; this assembly knows no Core type).</summary>
internal sealed record PdfiumRedactionImpact(
    int CharsRemoved,
    int TextObjectsSplit,
    int TextObjectsRemovedWhole,
    int FormObjectsTouched,
    int AnnotationsRemoved,
    IReadOnlyList<PdfiumFallback> Fallbacks,
    IReadOnlyList<string> RemovedText,
    string? RefusalReason,
    int ImagesOverwritten = 0,
    int ImagesRemovedWhole = 0,
    int PathsCropped = 0,
    int PathsRemovedWhole = 0,
    IReadOnlyList<PdfiumRedactionArea>? RemovedAreas = null,
    int ImageAreasTreated = 0,
    int ImageAreasSolid = 0,
    IReadOnlyList<PdfiumImageSolidReason>? ImageSolidReasons = null,
    int OverlaysDrawn = 0,
    int OverlaysNotShown = 0,
    int OverlaysNotFitting = 0,
    int OverlaysOverLimit = 0,
    int OverlayPaths = 0,
    long OverlaySegments = 0,
    bool OverlayLimitReached = false);

/// <summary>
/// Redacts one page (text, images, paths, form objects and annotations), as steps on the PDFium thread (<see cref="PdfiumThread.InvokeSteppedAsync{T}"/>): a
/// page with 20,000 text objects takes a while, and PDFium keeps page objects in a linked list, so even finding the object at an
/// index costs time; between steps the thread renders for the viewer.
/// </summary>
/// <remarks>
/// <para>
/// The decision what happens to the page is one function, <see cref="Pass"/>, for the dry run and for applying: it plans from
/// the text page, edits the page, lets PDFium write the content (<c>FPDFPage_GenerateContent</c>), loads the page again and
/// reads every surviving character back (Unicode, origin to 0.02 pt, box to 0.05 pt). A text object whose survivors cannot be
/// read back is removed as a whole instead (and listed). Because the page is written before it can be read back, the pass runs
/// first on a scratch copy of the page (a new document that the page is imported into, never saved) as often as it takes to
/// find every such object; only then does applying edit the real page, with that list, and read it back once more. The
/// analysis stops after the scratch copy and changes nothing.
/// </para>
/// <para>
/// Page objects are told apart by their index in the page (the scratch copy has the same order); the edits run from the highest
/// index down so that the indices still to edit stay valid. Images, paths and form objects are planned and applied in
/// <c>PdfiumRedactionRun.Objects.cs</c>: a form object with something under a rectangle is taken apart on the page (PDFium does not
/// write changes of a form object back to its XObject), so its children are edited like objects of the page. The pass also finds the
/// images that PDFium would drop when it writes the page (inline images) from the read-back and replaces them in the next pass.
/// The render check of the written page is not part of the run: it needs the whole document written once and is
/// <c>PdfDocument.VerifyRedactionRenderAsync</c>.
/// </para>
/// </remarks>
internal sealed partial class PdfiumRedactionRun : SteppedWork<PdfiumRedactionImpact>
{
    /// <summary>The work of one step, in <see cref="Stopwatch"/> ticks (about 20 ms).</summary>
    private static readonly long StepTicks = Stopwatch.Frequency / 50;

    /// <summary>The most scratch copies one run makes (the first finds the objects that cannot be rewritten, a second one confirms).</summary>
    internal const int MaxProbes = 3;

    /// <summary>The most passes over the annotations that follow reply and popup links (one pass reaches one more level).</summary>
    private const int MaxLinkPasses = 64;

    private const int SliceChars = 2_000;
    private const int SliceObjects = 64;

    private readonly PdfiumDocument document;
    private readonly int pageIndex;
    private readonly PdfiumRedactionArea[] areas;
    private readonly PdfiumRedactionFill fill;
    private readonly PdfiumImageTreatment treatment;
    private readonly bool veryCoarse;
    private readonly bool apply;
    private readonly Action? onModified;
    private readonly PdfiumRedactionSeam? seam;
    private readonly PdfiumOverlaySpec? overlay;
    private readonly IEnumerator<bool> steps;
    private readonly List<nint> pendingCreated = [];

    private PdfiumRedactionImpact? result;
    private nint scratchDocument;
    private PdfiumRedaction.RawPage? page;
    private nint textPage;
    private nint reloadedPage;

    private PdfiumRedactionRun(
        PdfiumDocument document,
        int pageIndex,
        PdfiumRedactionArea[] areas,
        PdfiumRedactionFill fill,
        PdfiumImageTreatment treatment,
        bool veryCoarse,
        bool apply,
        Action? onModified,
        PdfiumRedactionSeam? seam,
        PdfiumOverlaySpec? overlay)
    {
        this.overlay = overlay;
        this.onModified = onModified;
        this.seam = seam;
        this.document = document;
        this.pageIndex = pageIndex;
        this.areas = areas;
        this.fill = fill;
        this.treatment = treatment;
        this.veryCoarse = veryCoarse;
        this.apply = apply;
        steps = Run().GetEnumerator();
    }

    /// <summary>Starts a run on the PDFium thread.</summary>
    /// <param name="document">The working copy (never a document that is shown).</param>
    /// <param name="pageIndex">The 0-based page.</param>
    /// <param name="areas">The rectangles in points from the top left of the displayed page; each finite with a positive size.</param>
    /// <param name="fill">The colour of the area drawn over each rectangle.</param>
    /// <param name="apply"><see langword="false"/> for the dry run, which changes nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist, or there are more than <see cref="PdfiumRedaction.MaxAreas"/> rectangles.</exception>
    /// <exception cref="ArgumentException">A rectangle is not finite or has no size.</exception>
    /// <param name="onModified">Called on the PDFium thread right before the first change to the page (never in a dry run or a scratch copy).</param>
    /// <param name="seam">Hooks for tests, or <see langword="null"/>.</param>
    /// <param name="treatment">What happens to the pixels of an image under a rectangle: the fill colour, or blocks (see <see cref="PdfiumImageBlocks"/>).</param>
    /// <param name="veryCoarse">Whether pixelating and blurring use at most 3 blocks along a side instead of 6.</param>
    /// <param name="overlay">The text to draw on the rectangles and the budget for it, or <see langword="null"/> for none. Both the dry run and applying decide for it; only applying draws.</param>
    public static PdfiumRedactionRun Start(
        PdfiumDocument document,
        int pageIndex,
        IReadOnlyList<PdfiumRedactionArea> areas,
        PdfiumRedactionFill fill,
        bool apply,
        Action? onModified = null,
        PdfiumRedactionSeam? seam = null,
        PdfiumImageTreatment treatment = PdfiumImageTreatment.Solid,
        bool veryCoarse = false,
        PdfiumOverlaySpec? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(areas);
        var native = document.Access();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, Fpdf.FPDF_GetPageCount(native));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(areas.Count, PdfiumRedaction.MaxAreas);
        var copy = new PdfiumRedactionArea[areas.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var a = areas[i];
            if (!double.IsFinite(a.Left) || !double.IsFinite(a.Top) || !double.IsFinite(a.Right) || !double.IsFinite(a.Bottom) || !(a.Right > a.Left) || !(a.Bottom > a.Top))
            {
                throw new ArgumentException("A rectangle needs finite edges and a positive size.", nameof(areas));
            }

            copy[i] = a;
        }

        if (overlay is not null && overlay.Texts.Count != copy.Length)
        {
            throw new ArgumentException("The overlay needs one text (or none) for each rectangle.", nameof(overlay));
        }

        return new PdfiumRedactionRun(document, pageIndex, copy, fill, treatment, veryCoarse, apply, onModified, seam, overlay);
    }

    /// <inheritdoc />
    public override bool Step()
    {
        _ = document.Access();
        var deadline = Stopwatch.GetTimestamp() + StepTicks;
        while (true)
        {
            if (!steps.MoveNext())
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public override PdfiumRedactionImpact Complete() => result ?? throw new InvalidOperationException("The redaction has not finished.");

    /// <inheritdoc />
    public override void Dispose()
    {
        // Runs on the PDFium thread (the thread calls Dispose after completion, failure or cancellation).
        foreach (var created in pendingCreated)
        {
            Fpdf.FPDFPageObj_Destroy(created);
        }

        pendingCreated.Clear();
        CloseHandles();
        steps.Dispose();
        base.Dispose();
    }

    private void CloseHandles()
    {
        if (textPage != 0)
        {
            Fpdf.FPDFText_ClosePage(textPage);
            textPage = 0;
        }

        if (reloadedPage != 0)
        {
            Fpdf.FPDF_ClosePage(reloadedPage);
            reloadedPage = 0;
        }

        page?.Dispose();
        page = null;
        if (scratchDocument != 0)
        {
            Fpdf.FPDF_CloseDocument(scratchDocument);
            scratchDocument = 0;
        }
    }

    /// <summary>
    /// Whether a character is white space: space, tab, form feed, line feed, carriage return, no-break space. Nothing else counts as blank,
    /// not even the codes below 32: for a glyph without a Unicode value (a subset font without ToUnicode) PDFium reports the character code,
    /// and the glyph is drawn.
    /// </summary>
    private static bool IsWhite(uint unicode) => unicode is 0x20 or 0x09 or 0x0A or 0x0C or 0x0D or 0xA0;

    /// <summary>Whether a character has no Unicode value but a code in the control range (see <see cref="IsWhite"/>).</summary>
    private static bool IsControlCode(uint unicode) => unicode < 0x20 && !IsWhite(unicode);

    private static bool UsesFill(int mode) => mode is 0 or 2 or 4 or 6;

    private static bool UsesStroke(int mode) => mode is 1 or 2 or 5 or 6;

    private IEnumerable<bool> Run()
    {
        var forced = new Dictionary<long, string>();
        PassState pass;
        var probes = 0;
        while (true)
        {
            pass = new PassState();
            foreach (var step in Pass(scratch: true, forced, null, pass))
            {
                yield return step;
            }

            CloseHandles();
            if (pass.Refusal is not null || (pass.Unreadable.Count == 0 && pass.LostImages.Count == 0))
            {
                break;
            }

            if (++probes >= MaxProbes)
            {
                pass.Refusal = "The text could not be rewritten reliably after several attempts.";
                break;
            }

            foreach (var entry in pass.Unreadable)
            {
                forced[entry.Key] = entry.Value;
            }

            foreach (var entry in pass.LostImages)
            {
                forced[entry.Key] = entry.Value;
            }
        }

        if (pass.Refusal is not null)
        {
            result = Build(pass);
            yield break;
        }

        if (!apply)
        {
            // The dry run reports the annotations from the real page, read only.
            var plan = new AnnotationPlan();
            var userAreas = new List<UserRect>();
            nint pageHandle = 0;
            try
            {
                pageHandle = Fpdf.FPDF_LoadPage(document.Access(), pageIndex);
                if (pageHandle == 0)
                {
                    throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");
                }

                var ok = TryUserAreas(pageHandle, areas, userAreas);
                if (ok && userAreas.Count > 0)
                {
                    foreach (var step in PlanAnnotations(pageHandle, new AreaIndex(userAreas), plan))
                    {
                        yield return step;
                    }
                }
            }
            finally
            {
                if (pageHandle != 0)
                {
                    Fpdf.FPDF_ClosePage(pageHandle);
                }
            }

            pass.Refusal ??= plan.Refusal;
            pass.AnnotationsRemoved = plan.Remove.Count;
            result = Build(pass);
            yield break;
        }

        var scratchSignature = pass.Signature;

        // What the last scratch pass could not create is the same on the real page: the real pass takes it as given.
        foreach (var entry in pass.CreationFailed)
        {
            forced[entry.Key] = entry.Value;
        }

        var real = new PassState();
        foreach (var step in Pass(scratch: false, forced, scratchSignature, real))
        {
            yield return step;
        }

        CloseHandles();
        if (real.Refusal is null && real.Unreadable.Count > 0)
        {
            real.Refusal = "The characters that stay could not be read back after the page was rewritten.";
        }

        if (real.Refusal is null && real.LostImages.Count > 0)
        {
            real.Refusal = "An image was lost when the page was written again.";
        }

        result = Build(real);
    }

    private static PdfiumRedactionImpact Build(PassState pass) => new(
        pass.CharsRemoved,
        pass.Split,
        pass.RemovedWhole,
        pass.FormsTouched,
        pass.AnnotationsRemoved,
        pass.Fallbacks,
        pass.RemovedText,
        pass.Refusal,
        pass.ImagesOverwritten,
        pass.ImagesRemovedWhole,
        pass.PathsCropped,
        pass.PathsRemovedWhole,
        RemovedAreasOf(pass),
        pass.ImageAreasTreated,
        pass.ImageAreasSolid,
        [.. pass.SolidReasons],
        pass.OverlaysDrawn,
        pass.OverlaysNotShown,
        pass.OverlaysNotFitting,
        pass.OverlaysOverLimit,
        pass.OverlayPaths,
        pass.OverlaySegments,
        pass.OverlayLimitReached);

    /// <summary>The removed regions as rectangles in points from the top left of the displayed page (what the render check has to leave out besides the rectangles).</summary>
    private static List<PdfiumRedactionArea> RemovedAreasOf(PassState pass)
    {
        var result = new List<PdfiumRedactionArea>(pass.Removed.Count);
        if (pass.Transform is not { } transform)
        {
            return result;
        }

        foreach (var region in pass.Removed)
        {
            // Something that spans most of the page (a long diagonal line) must not exempt the whole page from the check: the page is refused instead.
            if ((region.Right - region.Left) * (region.Top - region.Bottom) > PdfiumRedaction.LargeShapeShare * pass.PageArea)
            {
                continue;
            }

            var a = transform.ToDisplay(region.Left, region.Bottom);
            var b = transform.ToDisplay(region.Right, region.Bottom);
            var c = transform.ToDisplay(region.Left, region.Top);
            var d = transform.ToDisplay(region.Right, region.Top);
            var left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            var right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            var top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            var bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            if (double.IsFinite(left) && double.IsFinite(right) && double.IsFinite(top) && double.IsFinite(bottom) && right > left && bottom > top)
            {
                result.Add(new PdfiumRedactionArea(left, top, right, bottom));
            }
        }

        return result;
    }

    /// <summary>Converts the rectangles to user space of the loaded page, clipped to the page; rectangles outside the page are dropped.</summary>
    internal static bool TryUserAreas(nint pageHandle, PdfiumRedactionArea[] areas, List<UserRect> userAreas) => TryUserAreas(pageHandle, areas, userAreas, out _);

    internal static bool TryUserAreas(nint pageHandle, PdfiumRedactionArea[] areas, List<UserRect> userAreas, out DisplayTransform transform) =>
        TryUserAreas(pageHandle, areas, userAreas, out transform, null);

    /// <summary>As above; <paramref name="kept"/> also receives, for every rectangle that stays, its index among <paramref name="areas"/> and its clipped rectangle in points from the top left.</summary>
    internal static bool TryUserAreas(nint pageHandle, PdfiumRedactionArea[] areas, List<UserRect> userAreas, out DisplayTransform transform, List<(int Source, PdfiumRedactionArea Rect)>? kept)
    {
        transform = default;
        double width = Fpdf.FPDF_GetPageWidthF(pageHandle);
        double height = Fpdf.FPDF_GetPageHeightF(pageHandle);
        if (!(width > 0) || !(height > 0) || !DisplayTransform.TryCreate(pageHandle, width, height, out transform))
        {
            return false;
        }

        for (var source = 0; source < areas.Length; source++)
        {
            var area = areas[source];
            var left = Math.Max(area.Left, 0);
            var top = Math.Max(area.Top, 0);
            var right = Math.Min(area.Right, width);
            var bottom = Math.Min(area.Bottom, height);
            if (!(right > left) || !(bottom > top))
            {
                continue;
            }

            var (x1, y1) = transform.ToUser(left, top);
            var (x2, y2) = transform.ToUser(right, bottom);
            userAreas.Add(new UserRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)));
            kept?.Add((source, new PdfiumRedactionArea(left, top, right, bottom)));
        }

        return true;
    }

    /// <summary>
    /// One pass: plan, edit, write the content, load the page again and read back. On the scratch copy it only edits text; on
    /// the real page it also removes the annotations and draws the areas.
    /// </summary>
    private IEnumerable<bool> Pass(bool scratch, IReadOnlyDictionary<long, string> forced, string? expectedSignature, PassState pass)
    {
        nint nativeDocument;
        int index;
        if (scratch)
        {
            nativeDocument = Fpdf.FPDF_CreateNewDocument();
            if (nativeDocument == 0)
            {
                throw new InvalidOperationException("PDFium could not create the scratch document.");
            }

            scratchDocument = nativeDocument;
            if (!PdfiumRedactionNative.ImportPage(nativeDocument, document.Access(), pageIndex))
            {
                throw new InvalidOperationException($"PDFium could not copy page {pageIndex + 1}.");
            }

            index = 0;
        }
        else
        {
            nativeDocument = document.Access();
            index = pageIndex;
        }

        var handle = Fpdf.FPDF_LoadPage(nativeDocument, index);
        if (handle == 0)
        {
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");
        }

        var raw = new PdfiumRedaction.RawPage(document, handle, scratch ? nativeDocument : 0);
        page = raw;

        var objectCount = raw.CountObjects();
        if (objectCount > PdfiumRedaction.MaxObjects)
        {
            pass.Refusal = $"The page has {objectCount} objects; at most {PdfiumRedaction.MaxObjects} are handled.";
            yield break;
        }

        var userAreas = new List<UserRect>();
        var keptAreas = new List<(int Source, PdfiumRedactionArea Rect)>();
        if (!TryUserAreas(handle, areas, userAreas, out var displayTransform, keptAreas))
        {
            pass.Refusal = "The geometry of the page could not be read.";
            yield break;
        }

        if (userAreas.Count == 0)
        {
            yield break;
        }

        pass.Transform = displayTransform;
        pass.PageArea = (double)Fpdf.FPDF_GetPageWidthF(handle) * Fpdf.FPDF_GetPageHeightF(handle);
        var areaIndex = new AreaIndex(userAreas);

        // ---- Read the text page and the objects of the page. ----
        textPage = raw.LoadTextPage();
        if (textPage == 0)
        {
            pass.Refusal = "The text of the page could not be read.";
            yield break;
        }

        var charCount = Fpdf.FPDFText_CountChars(textPage);
        if (charCount < 0 || charCount > PdfiumRedaction.MaxChars)
        {
            pass.Refusal = $"The page has {charCount} characters; at most {PdfiumRedaction.MaxChars} are handled.";
            yield break;
        }

        var table = new CharTable(charCount);
        for (var from = 0; from < charCount; from += SliceChars)
        {
            PdfiumRedactionNative.ReadChars(textPage, table, from, Math.Min(charCount, from + SliceChars));
            yield return true;
        }

        seam?.CharsRead?.Invoke(table, scratch);

        var handles = new nint[objectCount];
        var types = new int[objectCount];
        var objectIndex = new Dictionary<nint, int>();
        for (var i = 0; i < objectCount; i++)
        {
            var obj = raw.GetObject(i);
            handles[i] = obj;
            var type = Fpdf.FPDFPageObj_GetType(obj);
            types[i] = type;
            if (type is FpdfConstants.PageObjText or FpdfConstants.PageObjForm)
            {
                objectIndex[obj] = i;
            }

            if ((i + 1) % SliceObjects == 0)
            {
                yield return true;
            }
        }

        // ---- Plan: which characters are hit, grouped by text object. ----
        var hit = new bool[charCount];
        var byObject = new Dictionary<nint, List<int>>();
        var order = new List<nint>();
        for (var i = 0; i < charCount; i++)
        {
            if ((i & 1023) == 1023)
            {
                yield return true;
            }

            if (table.Generated[i])
            {
                continue;
            }

            if (!IsWhite(table.Unicode[i]) && !table.BoxUsable(i))
            {
                pass.Refusal = "The box of a character could not be read (it is not a finite number), so it cannot be told where the character is.";
                yield break;
            }

            var obj = table.TextObject[i];
            var isHit = table.BoxUsable(i) && areaIndex.Hit(table.Box(i));
            if (obj == 0)
            {
                if (isHit && !IsWhite(table.Unicode[i]))
                {
                    pass.Refusal = "A character under a rectangle belongs to no text object PDFium can edit.";
                    yield break;
                }

                continue;
            }

            hit[i] = isHit;
            if (!byObject.TryGetValue(obj, out var list))
            {
                byObject[obj] = list = [];
                order.Add(obj);
            }

            list.Add(i);
        }

        var edits = new List<Edit>();
        var unknown = new List<nint>();
        foreach (var obj in order)
        {
            var chars = byObject[obj];
            var visibleHit = false;
            foreach (var c in chars)
            {
                if (hit[c] && !IsWhite(table.Unicode[c]))
                {
                    visibleHit = true;
                    break;
                }
            }

            if (!visibleHit)
            {
                continue;
            }

            if (objectIndex.TryGetValue(obj, out var at) && types[at] == FpdfConstants.PageObjText)
            {
                edits.Add(new Edit(KeyOf(at, -1), at, obj, EditKind.Text) { Chars = [.. chars] });
            }
            else
            {
                unknown.Add(obj);
            }
        }

        // Everything that is not a hit text object of the page: text objects the text page knows no character of, images, paths and form objects
        // (taken apart where they are touched).
        var plan = new PlanContext
        {
            Raw = raw,
            NativeDocument = nativeDocument,
            Handles = handles,
            Types = types,
            Count = objectCount,
            Table = table,
            Hit = hit,
            ByObject = byObject,
            Areas = areaIndex,
            UserAreas = userAreas,
            Forced = forced,
            Edits = edits,
            Unknown = unknown,
            Pass = pass,
            Scratch = scratch,
            PageArea = (double)Fpdf.FPDF_GetPageWidthF(handle) * Fpdf.FPDF_GetPageHeightF(handle),
            Treatment = treatment,
            VeryCoarse = veryCoarse,
        };
        foreach (var step in PlanObjects(plan))
        {
            yield return step;
        }

        if (pass.Refusal is not null)
        {
            yield break;
        }

        // Before anything is written: an area that would need more opaque pieces than allowed around its treated images gets the solid fill on them.
        if (treatment != PdfiumImageTreatment.Solid)
        {
            DowngradeCrowdedAreas(edits, userAreas);
        }

        // ---- The text of what is removed as a whole, while the text page is open. ----
        foreach (var edit in AllEdits(edits))
        {
            ReadWholeText(raw, textPage, edit);
        }

        Fpdf.FPDFText_ClosePage(textPage);
        textPage = 0;
        edits.RemoveAll(static e => e.Skip || (e.Kind == EditKind.Flatten && e.Leaves.All(static l => l.Edit is null || l.Edit.Skip)));
        edits.Sort(static (a, b) => b.Index.CompareTo(a.Index));

        // The real page must be the page the scratch copy was made from: the same objects at the same places with the same text.
        pass.Signature = Signature(objectCount, edits, table, hit);
        if (expectedSignature is not null && expectedSignature != pass.Signature)
        {
            pass.Refusal = "The page does not match its scratch copy.";
            yield break;
        }

        // ---- Annotations (real page only). ----
        var annotations = new AnnotationPlan();
        if (!scratch)
        {
            foreach (var step in PlanAnnotations(handle, areaIndex, annotations))
            {
                yield return step;
            }

            if (annotations.Refusal is not null)
            {
                pass.Refusal = annotations.Refusal;
                yield break;
            }
        }

        // From here the real page changes: whoever tells the document about it must know before the first change, not after.
        if (!scratch)
        {
            onModified?.Invoke();
        }

        // ---- Edit the page, from the highest index down. ----
        var splitChecks = new List<(long Key, int[] Survivors)>();
        var editsDone = 0;
        foreach (var edit in edits)
        {
            ApplyEdit(raw, nativeDocument, edit, table, hit, forced, pass, splitChecks);
            if (++editsDone % 16 == 0)
            {
                yield return true;
            }
        }

        // ---- Annotations and the areas (real page only). ----
        var changed = edits.Count > 0;
        if (!scratch)
        {
            foreach (var removeIndex in annotations.Remove.OrderByDescending(static i => i))
            {
                if (Fpdf.FPDFPage_RemoveAnnot(handle, removeIndex) == 0)
                {
                    throw new InvalidOperationException("PDFium could not remove an annotation.");
                }

                pass.AnnotationsRemoved++;
                changed = true;
            }

            // The opaque area lies over every rectangle except where a treated image shows: that part keeps the blocks.
            var pieces = SolidPieces(userAreas, pass);
            for (var j = 0; j < userAreas.Count; j++)
            {
                foreach (var piece in pieces[j])
                {
                    var rectangle = PdfiumRedactionNative.CreateFillRectangle(piece, fill.Red, fill.Green, fill.Blue);
                    if (rectangle == 0)
                    {
                        throw new InvalidOperationException("PDFium could not create the area.");
                    }

                    if (!raw.Insert(rectangle))
                    {
                        // FPDFPage_InsertObject frees the object itself on failure (fpdf_edit.h): destroying it here would be a double free.
                        throw new InvalidOperationException("PDFium could not add the area to the page.");
                    }

                    changed = true;
                }
            }
        }

        // ---- Overlay text: both passes decide (the dry run says what applying does), only the real page draws. ----
        if (overlay is not null)
        {
            var treatedAreas = new HashSet<int>();
            foreach (var hole in pass.Treated)
            {
                treatedAreas.Add(hole.Area);
            }

            var overlayPlan = PdfiumRedactionOverlay.Decide(overlay, keptAreas, treatedAreas.Contains, PdfiumRedaction.MaxObjects - objectCount);
            pass.OverlaysDrawn = overlayPlan.Drawn;
            pass.OverlaysNotShown = overlayPlan.NotShown;
            pass.OverlaysNotFitting = overlayPlan.NotFitting;
            pass.OverlaysOverLimit = overlayPlan.OverLimit;
            pass.OverlayPaths = overlayPlan.Paths;
            pass.OverlaySegments = overlayPlan.Segments;
            pass.OverlayLimitReached = overlayPlan.LimitReached;
            yield return true;
            if (!scratch && overlayPlan.Drawn > 0)
            {
                foreach (var step in PdfiumRedactionOverlay.Draw(raw.Access(), overlayPlan, overlay.Color, displayTransform.ToUser(0, 0), displayTransform.AlongX, displayTransform.AlongY))
                {
                    yield return step;
                }

                changed = true;
            }
        }

        if (!changed)
        {
            yield break;
        }

        seam?.AfterEdits?.Invoke(scratch);

        if (!raw.GenerateContent())
        {
            pass.Refusal = "PDFium could not write the content of the page.";
            yield break;
        }

        yield return true;

        // ---- Read back from a freshly loaded page. ----
        reloadedPage = Fpdf.FPDF_LoadPage(nativeDocument, index);
        if (reloadedPage == 0)
        {
            pass.Refusal = "The rewritten page could not be loaded again.";
            yield break;
        }

        textPage = Fpdf.FPDFText_LoadPage(reloadedPage);
        if (textPage == 0)
        {
            pass.Refusal = "The text of the rewritten page could not be read.";
            yield break;
        }

        var count2 = Fpdf.FPDFText_CountChars(textPage);
        if (count2 < 0 || count2 > PdfiumRedaction.MaxChars)
        {
            pass.Refusal = $"The rewritten page has {count2} characters; at most {PdfiumRedaction.MaxChars} are handled.";
            yield break;
        }

        var after = new CharTable(count2);
        for (var from = 0; from < count2; from += SliceChars)
        {
            PdfiumRedactionNative.ReadChars(textPage, after, from, Math.Min(count2, from + SliceChars));
            yield return true;
        }

        seam?.ReadBack?.Invoke(after, scratch);

        var lookup = new Dictionary<(uint, long, long), List<int>>();
        for (var j = 0; j < count2; j++)
        {
            if ((j & 4095) == 4095)
            {
                yield return true;
            }

            if (after.Generated[j])
            {
                continue;
            }

            var key = (after.Unicode[j], Cell(after.OriginX[j]), Cell(after.OriginY[j]));
            if (!lookup.TryGetValue(key, out var list))
            {
                lookup[key] = list = [];
            }

            list.Add(j);
        }

        var matched = new bool[count2];
        var checkedSplits = 0;
        foreach (var (editIndex, survivors) in splitChecks)
        {
            if (++checkedSplits % 256 == 0)
            {
                yield return true;
            }

            var allFound = true;
            foreach (var s in survivors)
            {
                var found = FindSurvivor(table, s, after, lookup);
                if (found < 0)
                {
                    allFound = false;
                }
                else
                {
                    matched[found] = true;
                }
            }

            if (!allFound)
            {
                pass.Unreadable[editIndex] = "the characters that stay could not be read back after rewriting the text object";
            }
        }

        // PDFium does not write every image object again (an inline image is dropped): find the ones that are gone, so that the next
        // pass replaces them by image objects it can write.
        if (plan.Expected.Count > 0)
        {
            foreach (var lostKey in PdfiumRedactionObjects.FindLostImages(reloadedPage, plan.Expected))
            {
                pass.LostImages[lostKey] = "the image is not written again with the page (an inline image)";
            }

            yield return true;
        }

        // The scratch copy only looks for leaks when its survivors are all there (otherwise it is run again anyway); the real page always does.
        if (!scratch || pass.Unreadable.Count == 0)
        {
            for (var j = 0; j < count2; j++)
            {
                if ((j & 1023) == 1023)
                {
                    yield return true;
                }

                if (!after.Generated[j] && !matched[j] && !IsWhite(after.Unicode[j]) && (!after.BoxUsable(j) || areaIndex.Hit(after.Box(j))))
                {
                    pass.Refusal = "Text under a rectangle is still readable after the page was rewritten.";
                    yield break;
                }
            }
        }

        if (!scratch && pass.Unreadable.Count > 0)
        {
            pass.Refusal = "The characters that stay could not be read back after the page was rewritten.";
        }
    }

    private static string Signature(int objectCount, List<Edit> edits, CharTable table, bool[] hit)
    {
        var hash = new HashCode();
        hash.Add(objectCount);
        var count = 0;
        foreach (var edit in AllEdits(edits))
        {
            count++;
            hash.Add(edit.Key);
            hash.Add(edit.Index);
            hash.Add(edit.Kind);
            hash.Add(edit.Chars.Length);
            foreach (var c in edit.Chars)
            {
                hash.Add(table.Unicode[c]);
                hash.Add(hit[c]);
            }
        }

        return $"{count}:{hash.ToHashCode()}";
    }

    private static long Cell(double value) => (long)Math.Floor(value / 0.1);

    private static int FindSurvivor(CharTable original, int s, CharTable after, Dictionary<(uint, long, long), List<int>> lookup)
    {
        var cx = Cell(original.OriginX[s]);
        var cy = Cell(original.OriginY[s]);
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!lookup.TryGetValue((original.Unicode[s], cx + dx, cy + dy), out var list))
                {
                    continue;
                }

                foreach (var j in list)
                {
                    if (Math.Abs(after.OriginX[j] - original.OriginX[s]) < 0.02 && Math.Abs(after.OriginY[j] - original.OriginY[s]) < 0.02
                        && Math.Abs(after.Left[j] - original.Left[s]) < 0.05 && Math.Abs(after.Right[j] - original.Right[s]) < 0.05
                        && Math.Abs(after.Bottom[j] - original.Bottom[s]) < 0.05 && Math.Abs(after.Top[j] - original.Top[s]) < 0.05)
                    {
                        return j;
                    }
                }
            }
        }

        return -1;
    }

    private static void RemoveWhole(PdfiumRedaction.RawPage raw, Edit edit)
    {
        if (!raw.Remove(edit.Handle))
        {
            throw new InvalidOperationException("PDFium could not remove a page object.");
        }

        raw.Destroy(edit.Handle);
    }

    /// <summary>Splits one text object around the hit characters, or removes it as a whole (listed) when the rest cannot be rewritten.</summary>
    private void EditText(
        PdfiumRedaction.RawPage raw,
        nint nativeDocument,
        Edit edit,
        CharTable table,
        bool[] hit,
        IReadOnlyDictionary<long, string> forced,
        PassState pass,
        List<(long Key, int[] Survivors)> splitChecks)
    {
        var chars = edit.Chars;
        var runs = new List<int[]>();
        var current = new List<int>();
        var hitCount = 0;
        var visibleKept = 0;
        var removedRuns = new List<string>();
        var removedRun = new System.Text.StringBuilder();
        foreach (var c in chars)
        {
            if (hit[c])
            {
                hitCount++;
                if (current.Count > 0)
                {
                    runs.Add([.. current]);
                    current.Clear();
                }

                removedRun.Append(ToText(table.Unicode[c]));
            }
            else
            {
                if (!IsWhite(table.Unicode[c]))
                {
                    visibleKept++;
                }

                current.Add(c);
                FlushRemoved(removedRun, removedRuns);
            }
        }

        FlushRemoved(removedRun, removedRuns);
        if (current.Count > 0)
        {
            runs.Add([.. current]);
        }

        // Runs of white space only draw nothing and are not recreated.
        runs.RemoveAll(run => run.All(c => IsWhite(table.Unicode[c])));

        // A glyph without a Unicode value (PDFium then reports its character code, below 32) cannot be checked afterwards: the whole object goes.
        var controlHit = chars.Where(c => hit[c] && IsControlCode(table.Unicode[c])).Select(c => (int)table.Unicode[c]).DefaultIfEmpty(-1).First();
        if (controlHit >= 0)
        {
            RemoveWhole(raw, edit);
            NoteChars(pass, table, chars);
            pass.RemovedWhole++;
            pass.CharsRemoved += chars.Length;
            pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.TextObjectRemovedWhole, $"a character under a rectangle has no Unicode value (character code {controlHit}), so the rest of the object cannot be checked"));
            pass.RemovedText.Add(string.Concat(chars.Select(c => ToText(table.Unicode[c]))));
            return;
        }

        if (visibleKept == 0)
        {
            RemoveWhole(raw, edit);
            NoteChars(pass, table, chars.Where(c => hit[c]));
            pass.RemovedWhole++;
            pass.CharsRemoved += hitCount;
            pass.RemovedText.AddRange(removedRuns);
            return;
        }

        var reason = forced.TryGetValue(edit.Key, out var forcedReason) ? forcedReason : null;
        TextFacts? facts = null;
        if (reason is null)
        {
            facts = PdfiumRedactionNative.ReadTextFacts(edit.Handle);
            reason = CheckSplittable(facts, chars, hit, table);
        }

        var created = new List<nint>();
        if (reason is null)
        {
            try
            {
                reason = PdfiumRedactionNative.CreateRuns(nativeDocument, facts!, table, runs, created);
            }
            finally
            {
                // Objects no page owns yet are freed by Dispose if anything below throws.
                pendingCreated.AddRange(created);
            }
        }

        if (reason is not null)
        {
            foreach (var obj in created)
            {
                raw.Destroy(obj);
                pendingCreated.Remove(obj);
            }

            RemoveWhole(raw, edit);
            NoteChars(pass, table, chars);
            pass.RemovedWhole++;
            pass.CharsRemoved += chars.Length;
            pass.Fallbacks.Add(new PdfiumFallback(PdfiumFallbackKind.TextObjectRemovedWhole, reason));
            pass.RemovedText.Add(string.Concat(chars.Select(c => ToText(table.Unicode[c]))));
            if (!forced.ContainsKey(edit.Key))
            {
                pass.CreationFailed[edit.Key] = reason;
            }

            return;
        }

        if (!raw.Remove(edit.Handle))
        {
            throw new InvalidOperationException("PDFium could not remove a page object.");
        }

        raw.Destroy(edit.Handle);
        for (var k = 0; k < created.Count; k++)
        {
            if (!raw.InsertAt(created[k], edit.Index + k))
            {
                throw new InvalidOperationException("PDFium could not insert a page object.");
            }

            pendingCreated.Remove(created[k]);
        }

        NoteChars(pass, table, chars.Where(c => hit[c]));
        pass.Split++;
        pass.CharsRemoved += hitCount;
        pass.RemovedText.AddRange(removedRuns);
        splitChecks.Add((edit.Key, [.. chars.Where(c => !hit[c] && !IsWhite(table.Unicode[c]))]));
    }

    private static string ToText(uint unicode) => unicode > 0xFFFF || IsControlCode(unicode) ? "\ufffd" : ((char)unicode).ToString();

    private static void FlushRemoved(System.Text.StringBuilder builder, List<string> into)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var text = builder.ToString();
        builder.Clear();
        if (text.Trim().Length > 0)
        {
            into.Add(text);
        }
    }

    /// <summary>Why the characters that stay cannot be recreated, or <see langword="null"/> if they can.</summary>
    private static string? CheckSplittable(TextFacts facts, int[] chars, bool[] hit, CharTable table)
    {
        foreach (var c in chars)
        {
            if (hit[c])
            {
                continue;
            }

            var u = table.Unicode[c];
            if (u is 0 or 0xFFFD or >= 0xD800 || IsControlCode(u))
            {
                return "a character that stays has no Unicode value PDFium can write";
            }
        }

        if (facts.Font == 0 || !(facts.Size > 0) || !facts.MatrixRead)
        {
            return "the font, size or matrix of the text object could not be read";
        }

        var m = facts.Matrix;
        if (Math.Abs(((double)m.A * m.D) - ((double)m.B * m.C)) < 1e-9)
        {
            return "the text matrix is degenerate";
        }

        if (UsesFill(facts.Mode) && !facts.FillRead)
        {
            return "the fill colour of the text could not be read";
        }

        if (UsesStroke(facts.Mode) && !facts.StrokeRead)
        {
            return "the stroke colour of the text could not be read";
        }

        return null;
    }

    /// <summary>
    /// Finds the annotations to remove: those whose rectangle touches a rectangle to redact, then, following the links, their
    /// popups and replies (and the replies to those). Refuses when the links cannot be followed within the work limit.
    /// </summary>
    private static IEnumerable<bool> PlanAnnotations(nint pageHandle, AreaIndex userAreas, AnnotationPlan plan)
    {
        var entries = Fpdf.FPDFPage_GetAnnotCount(pageHandle);
        if (entries <= 0)
        {
            yield break;
        }

        if (entries > PdfiumAnnotations.MaxScannedPerPage)
        {
            plan.Refusal = $"The page has {entries} annotations; at most {PdfiumAnnotations.MaxScannedPerPage} are handled.";
            yield break;
        }

        var remove = new bool[entries];
        var irt = new int[entries];
        var popup = new int[entries];
        var parent = new int[entries];
        var budget = PdfiumAnnotations.MaxLinkWork;
        for (var i = 0; i < entries; i++)
        {
            var read = PdfiumRedactionNative.TryReadAnnotation(pageHandle, i, out var rect, out _, out irt[i], out popup[i], out parent[i], ref budget, entries);
            if (irt[i] == PdfiumAnnotations.LinkUnresolved || popup[i] == PdfiumAnnotations.LinkUnresolved || parent[i] == PdfiumAnnotations.LinkUnresolved)
            {
                plan.Refusal = "The replies and popups of the annotations of the page could not be followed within the work limit.";
                yield break;
            }

            remove[i] = read && userAreas.Hit(rect);
            if ((i + 1) % SliceObjects == 0)
            {
                yield return true;
            }
        }

        for (var pass = 0; ; pass++)
        {
            if (pass >= MaxLinkPasses)
            {
                plan.Refusal = $"Replies to annotations nest deeper than {MaxLinkPasses} levels.";
                yield break;
            }

            var changed = false;
            for (var i = 0; i < entries; i++)
            {
                if (remove[i])
                {
                    // A removed annotation takes its popup with it.
                    if (popup[i] >= 0 && popup[i] < entries && !remove[popup[i]])
                    {
                        remove[popup[i]] = true;
                        changed = true;
                    }

                    continue;
                }

                if ((irt[i] >= 0 && irt[i] < entries && remove[irt[i]]) || (parent[i] >= 0 && parent[i] < entries && remove[parent[i]]))
                {
                    remove[i] = true;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }

            yield return true;
        }

        for (var i = 0; i < entries; i++)
        {
            if (remove[i])
            {
                plan.Remove.Add(i);
            }
        }
    }

    private sealed class AnnotationPlan
    {
        public List<int> Remove { get; } = [];

        public string? Refusal { get; set; }
    }

    private sealed class PassState
    {
        public int CharsRemoved { get; set; }

        public int Split { get; set; }

        public int RemovedWhole { get; set; }

        public int FormsTouched { get; set; }

        public int AnnotationsRemoved { get; set; }

        public int ImagesOverwritten { get; set; }

        public int ImagesRemovedWhole { get; set; }

        public int PathsCropped { get; set; }

        public int PathsRemovedWhole { get; set; }

        /// <summary>The image areas that were pixelated or blurred.</summary>
        public int ImageAreasTreated { get; set; }

        /// <summary>The image areas that got the solid fill although pixelating or blurring was chosen.</summary>
        public int ImageAreasSolid { get; set; }

        /// <summary>Why each of those fell back.</summary>
        public List<PdfiumImageSolidReason> SolidReasons { get; } = [];

        /// <summary>Where a treated image shows under an area (written, not planned): the opaque area leaves these out.</summary>
        public List<TreatedHole> Treated { get; } = [];

        /// <summary>What the pass removed as a whole or character by character, in user space: the page may differ there (the check leaves it out).</summary>
        public List<UserRect> Removed { get; } = [];

        /// <summary>The areas that got overlay text.</summary>
        public int OverlaysDrawn { get; set; }

        /// <summary>The areas without overlay text because a treated image lies under them.</summary>
        public int OverlaysNotShown { get; set; }

        /// <summary>The areas whose overlay text does not fit (the areas over the limit included).</summary>
        public int OverlaysNotFitting { get; set; }

        /// <summary>The areas without overlay text because the budget of the document is reached.</summary>
        public int OverlaysOverLimit { get; set; }

        /// <summary>The path objects of the overlay text.</summary>
        public int OverlayPaths { get; set; }

        /// <summary>The path segments of the overlay text.</summary>
        public long OverlaySegments { get; set; }

        /// <summary>Whether the budget of the document is reached after this page.</summary>
        public bool OverlayLimitReached { get; set; }

        public DisplayTransform? Transform { get; set; }

        /// <summary>The area of the page in square points (a region larger than <see cref="PdfiumRedaction.LargeShapeShare"/> of it is not left out of the check).</summary>
        public double PageArea { get; set; }

        public List<PdfiumFallback> Fallbacks { get; } = [];

        public List<string> RemovedText { get; } = [];

        public string? Refusal { get; set; }

        /// <summary>A fingerprint of what the pass found to edit (same page, same objects, same text), to tell that the real page is the page the scratch copy was made from.</summary>
        public string? Signature { get; set; }

        /// <summary>Objects (by key) whose survivors could not be read back: the next pass removes them as a whole.</summary>
        public Dictionary<long, string> Unreadable { get; } = [];

        /// <summary>Images (by key) that PDFium dropped when it wrote the page: the next pass replaces them by new image objects.</summary>
        public Dictionary<long, string> LostImages { get; } = [];

        /// <summary>Objects (by key) whose survivors could not be created: the real pass removes them as a whole without trying.</summary>
        public Dictionary<long, string> CreationFailed { get; } = [];
    }
}
