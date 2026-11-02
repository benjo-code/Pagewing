using System.Globalization;
using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The report of a redaction (milestone 14), English and stable, for the command line and as the source of the card: what is removed,
/// what is cleaned (always; "cleaning is not a switch"), what is lost, what is NOT cleaned, what was checked and what was NOT checked.
/// It never promises more than the checks do: "checked" lines appear only for a result that passed its check, "not checked" lines come
/// from the check itself (the verifier's lines included, among them how many leaked copies could hide in the file bytes) and from what
/// the apply step adds.
/// </summary>
public static class RedactionReportText
{
    /// <summary>The lines the apply step adds to what the check did not check (its own checks have limits too).</summary>
    public static IReadOnlyList<string> ApplyNotChecked { get; } =
    [
        "Render comparison: only the redacted pages are compared with the original, outside the marked areas and the places where content was removed; pages without a mark are not compared, and for a document with a form the annotations (form fields included) are left out of both renders.",
        "Page keys: only /Thumb, /PieceInfo, /Metadata and /AA in the page dictionaries are looked for; the same keys of form objects, image objects and annotations are not.",
        "XMP metadata and JavaScript: removed from the document level by building the file anew; the entries inside page-level objects (form objects, image objects, annotations) are not read back.",
    ];

    /// <summary>The lines of <see cref="ApplyNotChecked"/> as data, in the same order (the interface says them in its own language).</summary>
    public static IReadOnlyList<RedactionNotChecked> ApplyNotCheckedItems { get; } =
    [
        new(RedactionNotCheckedKind.ApplyRender, ApplyNotChecked[0]),
        new(RedactionNotCheckedKind.ApplyPageKeys, ApplyNotChecked[1]),
        new(RedactionNotCheckedKind.ApplyXmp, ApplyNotChecked[2]),
    ];

    /// <summary>
    /// The lines the apply step adds, for a plan with <paramref name="rasterPages"/> pages turned into images: the render comparison
    /// covers the pages redacted in place only (a page turned into an image is checked for its structure, never rendered against the
    /// original), and the line says so. Without such pages these are <see cref="ApplyNotChecked"/>.
    /// </summary>
    /// <param name="rasterPages">How many pages of the plan become images.</param>
    public static IReadOnlyList<string> ApplyNotCheckedFor(int rasterPages) =>
        rasterPages <= 0 ? ApplyNotChecked : [ApplyRenderWithRaster, ApplyNotChecked[1], ApplyNotChecked[2]];

    /// <summary>The lines of <see cref="ApplyNotCheckedFor"/> as data; the render line carries the number of raster pages in <c>Count</c>.</summary>
    /// <param name="rasterPages">How many pages of the plan become images.</param>
    public static IReadOnlyList<RedactionNotChecked> ApplyNotCheckedItemsFor(int rasterPages) =>
        rasterPages <= 0
            ? ApplyNotCheckedItems
            : [new(RedactionNotCheckedKind.ApplyRender, ApplyRenderWithRaster, rasterPages), ApplyNotCheckedItems[1], ApplyNotCheckedItems[2]];

    private const string ApplyRenderWithRaster = "Render comparison: only the pages redacted in place are compared with the original, outside the marked areas and the places where content was removed; pages without a mark and pages turned into images are not compared, and for a document with a form the annotations (form fields included) are left out of both renders.";

    /// <summary>The line under "Not cleaned" that says the annotations outside the marks are kept as they are (always shown).</summary>
    public const string AnnotationsAlongText = "Annotations outside the marked areas (comment texts, authors, replies, popups, notes) come along unchanged; only an annotation under a mark is removed.";

    /// <summary>The sentence that Pagewing says whenever image areas are pixelated or blurred: it cannot tell whether text in the image stays legible.</summary>
    public const string ImageTextCaution = "Text in images can stay legible even when it is pixelated or blurred; Pagewing does not recognise text in images. For text in an image choose the solid fill.";

    /// <summary>
    /// Says in English why an image area got the solid fill although pixelating or blurring was chosen (the reasons are told in the
    /// report; the interface has its own sentence for each).
    /// </summary>
    /// <param name="reason">The reason.</param>
    /// <returns>A phrase that fits after "because".</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a defined value.</exception>
    public static string ImageSolidReasonText(ImageSolidReason reason) => reason switch
    {
        ImageSolidReason.TooSmall => "the area is smaller than 2 x 2 blocks of 8 pixels",
        ImageSolidReason.TurnedOrSheared => "the image is turned or sheared",
        ImageSolidReason.Clipped => "the image is cut by a clipping path",
        ImageSolidReason.Overlapping => "several areas overlap on the same image",
        ImageSolidReason.Unreadable => "the pixels of the image could not be read or written reliably",
        ImageSolidReason.TooManyImages => "too many treated images lie under one area",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>Gets the report before anything is applied: removed, cleaned, lost, not cleaned, and what will be checked.</summary>
    /// <param name="analysis">The analysis.</param>
    /// <param name="showValues">Whether the fields of the information dictionary are listed with their text (the card); <see langword="false"/> lists only that they were there (the command line, whose output ends up in logs).</param>
    public static string Of(RedactionAnalysis analysis, bool showValues = true)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var text = new StringBuilder();
        Removed(text, analysis);
        ImageAreas(text, analysis, done: false);
        OverlayText(text, analysis);
        Cleaned(text, analysis, confirmed: false, showValues);
        Lost(text, analysis);
        NotCleaned(text, analysis);
        Section(
            text,
            "Will be checked after writing",
            [
                WillBeChecked(analysis),
                "Nothing is written to the target if the check finds anything.",
            ]);
        return text.ToString().TrimEnd();
    }

    /// <summary>Gets the report of a redaction that was written and passed its check: removed, cleaned, lost, not cleaned, checked, not checked.</summary>
    /// <param name="analysis">The analysis made before applying.</param>
    /// <param name="check">The check the written file passed.</param>
    /// <param name="showValues">As in <see cref="Of(RedactionAnalysis, bool)"/>.</param>
    public static string Of(RedactionAnalysis analysis, RedactionCheck check, bool showValues = true)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(check);
        var text = new StringBuilder();
        Removed(text, analysis);
        ImageAreas(text, analysis, done: true);
        OverlayText(text, analysis);

        // Only what the read-back of the written file confirmed is called cleaned.
        if (check.IsClean)
        {
            Cleaned(text, analysis, confirmed: true, showValues);
        }

        Lost(text, analysis);
        NotCleaned(text, analysis);

        // "Checked" says what the check did and found nothing; a check with findings has nothing to say here.
        if (check.IsClean)
        {
            var checkedLines = new List<string>();
            if (analysis.Marks > analysis.MarksWithoutTerm)
            {
                checkedLines.Add("Page text: every removed text occurs in the page text of the written file at most as often as before minus the places removed (never, for a text marked everywhere).");
                checkedLines.Add("File bytes: the file was searched for each removed text of at least 4 characters, with strings decoded and streams unpacked, in older revisions, metadata and attachments too.");
            }

            checkedLines.Add(analysis.ImageTreatment == RedactionImageTreatment.Solid
                ? "Marked areas: no character of the written file lies inside a marked area, and the pixels of images under them are the colour of the area."
                : "Marked areas: no character of the written file lies inside a marked area, and the pixels of images under them are either the colour of the area or exactly the block averages worked out from the original (blur: within one level per colour channel; opaque; nothing else, so the image keeps no pixel data of the original beyond these block averages).");
            // The render comparison covers the pages redacted in place only: a page turned into an image is checked for its structure.
            if (analysis.Raster is not { Count: > 0 })
            {
                checkedLines.Add("Pages: every redacted page was rendered from the written file and looks like the original outside the marked areas.");
            }
            else
            {
                if (InPlacePages(analysis) > 0)
                {
                    checkedLines.Add("Pages: every page redacted in place was rendered from the written file and looks like the original outside the marked areas.");
                }

                var raster = analysis.Raster;
                var pages = string.Join(", ", raster.Select(static r => (r.Page + 1).ToString(CultureInfo.InvariantCulture)));
                checkedLines.Add($"Pages turned into images: {(raster.Count == 1 ? "page" : "pages")} {pages}: in the written file {(raster.Count == 1 ? "it has" : "each has")} exactly one image and no text, other object, annotation, link or form field (links and form fields are annotations), no path outside the marked areas, and the pixels under the marks are the colour of the area. These pages are not rendered against the original.");
            }

            checkedLines.Add("Page keys: no page of the written file carries /Thumb, /PieceInfo, /Metadata or /AA.");
            checkedLines.Add("Cleanup: the information dictionary, bookmarks, the names tree (attached files, scripts), page labels, the document's open action, actions, form and permissions were read back from the written file; none is left.");
            Section(text, "Checked", checkedLines);
        }

        var notChecked = new List<string>(check.NotChecked);
        if (analysis.MarksWithoutTerm > 0)
        {
            notChecked.Add(Count(analysis.MarksWithoutTerm, "mark has no term (a rectangle dragged on the page): the text under it is removed, but no text is searched for in the file bytes for it, only the characters inside the area are looked for", "marks have no term (rectangles dragged on the page): the text under them is removed, but no text is searched for in the file bytes for them, only the characters inside the areas are looked for"));
        }

        if (!analysis.CleanupComplete)
        {
            notChecked.Add("The scan of the file could not read every stream, so the list of what was cleaned may be incomplete.");
        }

        Section(text, "Not checked", notChecked);
        return text.ToString().TrimEnd();
    }

    // Pages with marks that stay vectors and text: only these are rendered against the original.
    private static int InPlacePages(RedactionAnalysis analysis) =>
        analysis.Pages.Count(page => analysis.Raster is not { } raster || !raster.Any(r => r.Page == page.Page));

    private static string WillBeChecked(RedactionAnalysis analysis)
    {
        const string Head = "The written file is read back: the removed texts, the characters inside the marked areas, the file bytes, the pixels of images under the marks, the page keys, the information dictionary, bookmarks, attached files and page labels";
        if (analysis.Raster is not { Count: > 0 })
        {
            return Head + ", and a render of every redacted page against the original.";
        }

        return InPlacePages(analysis) > 0
            ? Head + ", and a render of every page redacted in place against the original. Pages turned into images are checked for their structure instead (exactly one image, nothing else but overlay text inside the marked areas); they are not rendered against the original."
            : Head + ". Pages turned into images are checked for their structure (exactly one image, nothing else but overlay text inside the marked areas); they are not rendered against the original.";
    }

    private static void Removed(StringBuilder text, RedactionAnalysis analysis)
    {
        var lines = new List<string>();
        foreach (var page in analysis.Pages)
        {
            var parts = new List<string>();
            Add(parts, page.CharsRemoved, "character", "characters", "removed");
            Add(parts, page.TextObjectsSplit, "text object", "text objects", "split");
            Add(parts, page.TextObjectsRemovedWhole, "text object", "text objects", "removed as a whole");
            Add(parts, page.ImagesOverwritten, "image", "images", "overwritten under the mark");
            Add(parts, page.ImagesRemovedWhole, "image", "images", "removed as a whole");
            Add(parts, page.PathsCropped, "path", "paths", "cropped");
            Add(parts, page.PathsRemovedWhole, "path", "paths", "removed as a whole");
            Add(parts, page.FormObjectsTouched, "form object", "form objects", "taken apart or removed");
            Add(parts, page.AnnotationsRemoved, "annotation", "annotations", "removed (with their popups and replies)");
            var number = (page.Page + 1).ToString(CultureInfo.InvariantCulture);
            lines.Add(parts.Count == 0 ? $"Page {number}: no content was under the marks; the areas are drawn." : $"Page {number}: {string.Join("; ", parts)}.");
        }

        foreach (var fallback in analysis.Fallbacks)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Page {fallback.Page + 1}: removed as a whole instead of precisely ({fallback.Kind}): {fallback.Reason}"));
        }

        // A page turned into an image is not "nothing removed": its text and everything else of it but the picture is gone.
        foreach (var info in analysis.Raster ?? [])
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Page {info.Page + 1}: turned into an image ({info.Dpi} dpi); only the picture of the page stays, its text, links, comments and form fields are not kept."));
        }

        Section(text, "Removed", lines);
    }

    // Image areas of a plan that pixelates or blurs: how many were treated, how many got the solid fill and why, and the caution that
    // belongs to it. A plan that fills image areas solid has nothing to say here.
    private static void ImageAreas(StringBuilder text, RedactionAnalysis analysis, bool done)
    {
        if (analysis.ImageTreatment == RedactionImageTreatment.Solid)
        {
            return;
        }

        var treated = analysis.Pages.Sum(static p => p.ImageAreasTreated);
        var solid = analysis.Pages.Sum(static p => p.ImageAreasSolid);
        var verb = analysis.ImageTreatment == RedactionImageTreatment.Pixelate ? (done ? "pixelated" : "will be pixelated") : (done ? "blurred" : "will be blurred");
        var lines = new List<string>
        {
            Count(treated, $"image area {verb}: only coarse block averages of the original remain in it.", $"image areas {verb}: only coarse block averages of the original remain in them."),
        };
        if (solid > 0)
        {
            var reasons = analysis.Pages
                .SelectMany(static p => p.ImageSolidReasons ?? [])
                .GroupBy(static r => r)
                .OrderBy(static g => g.Key)
                .Select(static g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} because {ImageSolidReasonText(g.Key)}"));
            var filled = done ? "got" : solid == 1 ? "gets" : "get";
            lines.Add($"{solid.ToString(CultureInfo.InvariantCulture)} {(solid == 1 ? "image area" : "image areas")} {filled} the solid fill instead ({string.Join("; ", reasons)}).");
        }

        lines.Add(ImageTextCaution);
        Section(text, "Image areas", lines);
    }

    // Overlay text that is left out: over a pixelated or blurred image area, where it does not fit, where the limit of the document is
    // reached. The areas stay filled; a plan without such an area has nothing to say here.
    private static void OverlayText(StringBuilder text, RedactionAnalysis analysis)
    {
        var lines = new List<string>();
        if (analysis.OverlaysNotShown > 0)
        {
            lines.Add(Count(analysis.OverlaysNotShown, "area gets no overlay text because a pixelated or blurred image lies under it (its background depends on the image).", "areas get no overlay text because a pixelated or blurred image lies under them (their background depends on the image)."));
        }

        if (analysis.OverlaysNotFitting > 0)
        {
            lines.Add(Count(analysis.OverlaysNotFitting, "overlay text does not fit its area; the area stays filled without text.", "overlay texts do not fit their areas; the areas stay filled without text."));
            if (analysis.OverlaysOverLimit > 0)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{analysis.OverlaysOverLimit} of them because the limit for the overlay text was reached (a page takes at most {RedactionLimits.MaxOverlayPathsPerPage:N0} lines or {RedactionLimits.MaxOverlaySegmentsPerPage:N0} outline segments, a document {RedactionLimits.MaxOverlayPaths:N0} or {RedactionLimits.MaxOverlaySegments:N0}); the others did not fit."));
            }
        }

        if (lines.Count > 0)
        {
            Section(text, "Overlay text", lines);
        }
    }

    private static void Cleaned(StringBuilder text, RedactionAnalysis analysis, bool confirmed, bool showValues)
    {
        var lines = new List<string>();
        foreach (var item in analysis.Cleanup)
        {
            // XMP and scripts are counted all over the file, page-level objects included, and cannot be read back as gone: after writing
            // they are told under "not checked", not called cleaned.
            if (confirmed && item.Kind is CleanupKind.Xmp or CleanupKind.JavaScript)
            {
                continue;
            }

            lines.Add(item.Kind switch
            {
                CleanupKind.Author => Field("Author", item, showValues),
                CleanupKind.Title => Field("Title", item, showValues),
                CleanupKind.Subject => Field("Subject", item, showValues),
                CleanupKind.Keywords => Field("Keywords", item, showValues),
                CleanupKind.Creator => Field("Creator", item, showValues),
                CleanupKind.Producer => Field("Producer", item, showValues),
                CleanupKind.Xmp => $"XMP metadata: up to {item.Detail} (the count can include metadata of objects inside the pages, which stays; see below).",
                CleanupKind.Bookmarks => $"Bookmarks: up to {item.Detail}.",
                CleanupKind.Attachments => $"Attached files: {item.Detail}.",
                CleanupKind.JavaScript => $"JavaScript: up to {item.Detail} (the count can include actions of annotations, which stay; see below).",
                _ => $"Page labels: {item.Detail}.",
            });
        }

        if (lines.Count == 0)
        {
            lines.Add(analysis.CleanupComplete ? "The file has none of the document-level data that the cleanup removes." : "None found; the scan could not read every stream, so there may be more.");
        }
        else if (!analysis.CleanupComplete)
        {
            lines.Add("The scan could not read every stream, so there may be more.");
        }

        Section(text, confirmed ? "Cleaned (read back from the written file)" : "Will be cleaned (always, with every redaction)", lines);
    }

    // A field of the information dictionary with its text (a card shows what was in the file), or only that it was there (a log of the
    // command line must not print what was just redacted).
    private static string Field(string name, CleanupItem item, bool showValues) =>
        showValues ? $"{name} in the information dictionary: {item.Detail}" : $"{name} in the information dictionary (present; its text is not repeated here).";

    private static void Lost(StringBuilder text, RedactionAnalysis analysis)
    {
        var lines = new List<string>();
        foreach (var loss in analysis.Losses)
        {
            lines.Add(loss.Kind switch
            {
                RedactionLossKind.InternalLinksLoseTheirTarget =>
                    Count(loss.Count, "internal link loses its target: it stays on the page but leads nowhere.", "internal links lose their target: they stay on the pages but lead nowhere."),
                RedactionLossKind.FormFieldsNotFillable =>
                    Count(loss.Count, "form field is no longer reliably fillable: its widget stays on the page as an annotation only.", "form fields are no longer reliably fillable: their widgets stay on the pages as annotations only."),
                RedactionLossKind.ColorSpaceBecomesRgb =>
                    Count(loss.Count, "image overwritten under a mark is written again as RGB; colours of a CMYK or indexed image may look different in other viewers.", "images overwritten under a mark are written again as RGB; colours of CMYK or indexed images may look different in other viewers."),
                _ =>
                    Count(loss.Count, "digital signature no longer verifies after the change; its data stays in the file as inert data and is not removed.", "digital signatures no longer verify after the change; their data stays in the file as inert data and is not removed."),
            });
        }

        // The widgets of a page turned into an image are not left as annotations (as the line above says for the others): they are gone.
        var rasterFields = analysis.Raster?.Sum(static r => r.FormFields) ?? 0;
        if (rasterFields > 0)
        {
            lines.Add(Count(rasterFields, "form field is gone from the pages turned into images: its widget is not kept, not even as an annotation.", "form fields are gone from the pages turned into images: their widgets are not kept, not even as annotations."));
        }

        Section(text, "Lost", lines);
    }

    private static void NotCleaned(StringBuilder text, RedactionAnalysis analysis)
    {
        var lines = new List<string>
        {
            "Metadata and private data (/Metadata, /PieceInfo) of form objects and image objects that come with the pages.",
            "Actions of annotations (/AA, links and other actions) that come with the pages.",
            AnnotationsAlongText,
        };
        if (analysis.AttachmentAnnotations > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"File attachments of annotations (paperclip notes): up to {analysis.AttachmentAnnotations}. Their embedded files come with the pages and stay in the file."));
        }

        if (analysis.Losses.Any(static l => l.Kind == RedactionLossKind.FormFieldsNotFillable))
        {
            lines.Add("The values (/V, /DV) of form fields stay on their widgets in the file; a marked text inside them is still found by the check of the file bytes, and then nothing is written.");
        }

        if (analysis.Losses.Any(static l => l.Kind == RedactionLossKind.SignatureInvalidated))
        {
            lines.Add("The data of a digital signature (certificate name, contents) stays in the file as inert data.");
        }

        Section(text, "Not cleaned", lines);
    }

    private static void Section(StringBuilder text, string heading, List<string> lines)
    {
        text.Append(heading).Append(':').Append('\n');
        if (lines.Count == 0)
        {
            text.Append("- none\n\n");
            return;
        }

        foreach (var line in lines)
        {
            text.Append("- ").Append(line).Append('\n');
        }

        text.Append('\n');
    }

    private static void Add(List<string> parts, int count, string one, string many, string what)
    {
        if (count > 0)
        {
            parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)} {what}");
        }
    }

    private static string Count(int number, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many)}");
}
