using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>One part of a card: a heading and its lines (shown as a bulleted list).</summary>
/// <param name="Heading">The heading in the language of the interface.</param>
/// <param name="Lines">The lines; never empty text.</param>
public sealed record RedactionSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// What the cards of the redaction say (milestone 14, task 13): the card before "Apply" (what is removed in numbers, what is removed as
/// a whole, what is cleaned, what changes, what is NOT cleaned and not checked), the cards of a refusal and of a failed check (kind,
/// reason, pages), and the report after a redaction that passed its check. The texts are built from the data of the core, never from
/// its English sentences (<see cref="RedactionRefusalText"/> and <see cref="RedactionReportText"/> are for the command line, which
/// speaks English only); a technical detail that the engine gives only in English (the reason of a page that cannot be redacted, the
/// lines of the check) is shown as given and named as such. Nothing here repeats a removed text or the value of a field: the fields
/// of the document properties are named, not printed, and no sentence calls anything valid or promises more than the check does.
/// </summary>
public sealed class RedactionApplyInfo
{
    private RedactionApplyInfo(string summary, IReadOnlyList<RedactionSection> sections)
    {
        Summary = summary;
        Sections = sections;
    }

    /// <summary>Gets the summary line ("Removes: 38 characters, 1 image; 1 text object as a whole"), or empty.</summary>
    public string Summary { get; }

    /// <summary>Gets the parts of the card, in order.</summary>
    public IReadOnlyList<RedactionSection> Sections { get; }

    /// <summary>Gets whether there is a summary line.</summary>
    public bool HasSummary => Summary.Length > 0;

    /// <summary>Gets every text of the card (summary, headings and lines), for tests that look for words.</summary>
    public IEnumerable<string> AllTexts => Sections.SelectMany(static section => section.Lines.Prepend(section.Heading)).Prepend(Summary).Where(static text => text.Length > 0);

    /// <summary>The card before "Apply": the numbers, the fallbacks, the cleanup, the losses, what is not cleaned, and what is checked.</summary>
    /// <param name="analysis">The analysis of the plan; not refused.</param>
    public static RedactionApplyInfo Create(RedactionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var sections = new List<RedactionSection>();
        if (FallbackLines(analysis) is { Count: > 0 } fallbacks)
        {
            sections.Add(new RedactionSection(Strings.RedactSecFallbacks, fallbacks));
        }

        sections.Add(new RedactionSection(Strings.RedactSecCleaned, CleanedLines(analysis, confirmed: false)));
        if (LostLines(analysis) is { Count: > 0 } lost)
        {
            sections.Add(new RedactionSection(Strings.ApplyLossesHeading, lost));
        }

        sections.Add(new RedactionSection(Strings.RedactSecNotCleaned, NotCleanedLines(analysis)));
        sections.Add(new RedactionSection(Strings.RedactSecAfter, [CheckListOf(analysis), Strings.RedactCheckNothingWritten]));
        return new RedactionApplyInfo(SummaryOf(analysis), sections);
    }

    /// <summary>The card of a refusal: one part for each reason, with the kind, the reason and the pages. It has no "Apply".</summary>
    /// <param name="refusals">Why the file is refused; not empty.</param>
    public static RedactionApplyInfo ForRefusals(IReadOnlyList<RedactionRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        var sections = new List<RedactionSection>();
        foreach (var refusal in refusals)
        {
            sections.Add(RefusalSection(refusal));
        }

        return new RedactionApplyInfo(string.Empty, sections);
    }

    /// <summary>The card of a check that found something (nothing was written): one line for each finding, by kind and page.</summary>
    /// <param name="check">The check from the failure, or <see langword="null"/> when the check itself could not be completed.</param>
    public static RedactionApplyInfo ForFailure(RedactionCheck? check)
    {
        var lines = check is null || check.Findings.Count == 0
            ? [Strings.RedactFailureNotCompleted]
            : check.Findings.Select(FailureLine).ToList();
        return new RedactionApplyInfo(string.Empty, [new RedactionSection(Strings.RedactSecFindings, lines)]);
    }

    /// <summary>
    /// The report of a redaction that was written and passed its check: what was removed, cleaned (only what the read-back of the
    /// written file confirmed), what changed, what is not cleaned, what the check did, and what it did not check.
    /// </summary>
    /// <param name="analysis">The analysis made before applying.</param>
    /// <param name="check">The check the written file passed.</param>
    public static RedactionApplyInfo ForReport(RedactionAnalysis analysis, RedactionCheck check)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(check);
        var sections = new List<RedactionSection>
        {
            new(Strings.RedactSecRemoved, RemovedLines(analysis)),
        };
        if (FallbackLines(analysis) is { Count: > 0 } fallbacks)
        {
            sections.Add(new RedactionSection(Strings.RedactSecFallbacks, fallbacks));
        }

        // Only what the read-back confirmed is called cleaned: a check with findings has nothing to say here.
        if (check.IsClean)
        {
            sections.Add(new RedactionSection(Strings.RedactSecCleanedDone, CleanedLines(analysis, confirmed: true)));
        }

        if (LostLines(analysis) is { Count: > 0 } lost)
        {
            sections.Add(new RedactionSection(Strings.ApplyLossesHeading, lost));
        }

        sections.Add(new RedactionSection(Strings.RedactSecNotCleaned, NotCleanedLines(analysis)));
        if (check.IsClean)
        {
            var checkedLines = new List<string>();
            if (analysis.Marks > analysis.MarksWithoutTerm)
            {
                checkedLines.Add(Strings.RedactCheckedText);
                checkedLines.Add(Strings.RedactCheckedBytes);
            }

            checkedLines.Add(Strings.RedactCheckedAreas);
            if (analysis.Raster is not { Count: > 0 })
            {
                checkedLines.Add(Strings.RedactCheckedPages);
            }
            else
            {
                // The render comparison covers the pages redacted in place only; a page turned into an image is checked for its structure.
                if (InPlacePages(analysis) > 0)
                {
                    checkedLines.Add(Strings.RedactCheckedPagesInPlace);
                }

                checkedLines.Add(Format(Strings.RedactCheckedRaster, PagesOf([.. analysis.Raster.Select(static r => r.Page)])));
            }

            checkedLines.Add(Strings.RedactCheckedKeys);
            checkedLines.Add(Strings.RedactCheckedCleanup);
            sections.Add(new RedactionSection(Strings.RedactSecChecked, checkedLines));
        }

        var notChecked = new List<string>();
        if (analysis.MarksWithoutTerm > 0)
        {
            notChecked.Add(CountText(analysis.MarksWithoutTerm, Strings.RedactNotCheckedNoTermOne, Strings.RedactNotCheckedNoTerm));
        }

        if (!analysis.CleanupComplete)
        {
            notChecked.Add(Strings.RedactCleanIncomplete);
        }

        // The lines of the check come as data (a kind and numbers) and are said in the language of the interface; only a line that
        // has no kind of its own, or a check made of text alone, is shown in English, and the card says so.
        var untranslated = false;
        var items = check.Items.Count == check.NotChecked.Count && check.Items.Count > 0
            ? check.Items
            : [.. check.NotChecked.Select(static line => new RedactionNotChecked(RedactionNotCheckedKind.Other, line))];
        foreach (var item in items)
        {
            var said = SayNotChecked(item);
            if (said is null)
            {
                untranslated = true;
            }

            notChecked.Add(said ?? item.Text);
        }

        if (untranslated && !string.Equals(Strings.Culture.TwoLetterISOLanguageName, "en", StringComparison.Ordinal))
        {
            notChecked.Add(Strings.RedactSecNotCheckedNote);
        }

        sections.Add(new RedactionSection(Strings.RedactSecNotChecked, notChecked));
        return new RedactionApplyInfo(string.Empty, sections);
    }

    /// <summary>
    /// The line of the preview: the numbers of what redacting removes ("Removes: 38 characters, 1 image; 1 text object as a whole"),
    /// or, for a file that cannot be redacted, what stands in the way.
    /// </summary>
    /// <param name="analysis">The analysis of the plan.</param>
    public static string PreviewLine(RedactionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.IsRefused)
        {
            return string.Format(Strings.Culture, CompositeFormat.Parse(Strings.RedactPreviewRefused), RefusalLine(analysis.Refusals[0]));
        }

        return SummaryOf(analysis);
    }

    /// <summary>The first sentence of a refusal, in the language of the interface.</summary>
    internal static string RefusalLine(RedactionRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Kind switch
        {
            RedactionRefusalKind.Encrypted => Strings.RedactRefusalEncrypted,
            RedactionRefusalKind.UnremovablePageKeys when refusal.PageKeysUnverified => Strings.RedactRefusalKeysUnverified,
            RedactionRefusalKind.UnremovablePageKeys => Format(Strings.RedactRefusalKeys, PagesOf(refusal.Pages)),
            RedactionRefusalKind.PageNotRedactable => PageLine(refusal),
            _ => Strings.RedactRefusalOther,
        };
    }

    private static RedactionSection RefusalSection(RedactionRefusal refusal)
    {
        var lines = new List<string> { RefusalLine(refusal) };
        string heading;
        switch (refusal.Kind)
        {
            case RedactionRefusalKind.Encrypted:
                heading = Strings.RedactRefusalHeadingEncrypted;
                break;
            case RedactionRefusalKind.UnremovablePageKeys when refusal.PageKeysUnverified:
                heading = Strings.RedactRefusalHeadingKeysUnverified;
                break;
            case RedactionRefusalKind.UnremovablePageKeys:
                heading = Strings.RedactRefusalHeadingKeys;

                // The names of the keys are not language: they are read from the reason of the core.
                var keys = PageKeyScanner.Keys.Where(key => refusal.Reason.Contains(key, StringComparison.Ordinal)).ToList();
                if (keys.Count > 0)
                {
                    lines.Add(Format(Strings.RedactRefusalKeyNames, string.Join(", ", keys)));
                }

                break;
            case RedactionRefusalKind.PageNotRedactable:
                heading = Strings.RedactRefusalHeadingPage;
                var cause = RedactionPageCauses.Classify(refusal.Reason);

                // A reason that is in no known group is shown as the engine gave it (English) and named so; the hint belongs to the
                // one cause it is true for, never to a page whose cause is not known.
                if (cause == RedactionPageCause.Unknown && !string.IsNullOrWhiteSpace(refusal.Reason))
                {
                    lines.Add(Format(Strings.RedactRefusalPageReason, refusal.Reason.Trim()));
                }
                else if (cause == RedactionPageCause.AnnotationsTooComplex)
                {
                    lines.Add(Strings.RedactRefusalPageHint);
                }

                break;
            default:
                heading = Strings.RedactRefusalOther;
                break;
        }

        return new RedactionSection(heading, lines);
    }

    /// <summary>
    /// Says one line of what the check did not check in the language of the interface, from its kind and numbers; <see langword="null"/>
    /// for a line without a kind of its own (the caller shows the English text). A test keeps every kind of
    /// <see cref="RedactionNotCheckedKind"/> but <see cref="RedactionNotCheckedKind.Other"/> mapped.
    /// </summary>
    internal static string? SayNotChecked(RedactionNotChecked item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind switch
        {
            RedactionNotCheckedKind.BlankTexts => CountText(item.Count, Strings.RedactNcBlankOne, Strings.RedactNcBlank),
            RedactionNotCheckedKind.UnreadableChars => CountText(item.Count, Strings.RedactNcUnreadableOne, Strings.RedactNcUnreadable),
            RedactionNotCheckedKind.TruncatedTexts => CountText(item.Count, Strings.RedactNcTruncatedOne, Strings.RedactNcTruncated),
            RedactionNotCheckedKind.FurtherTexts => Format(Strings.RedactNcFurther, Number(item.Count), Number(item.Second)),
            RedactionNotCheckedKind.RawShortTexts => Format(Strings.RedactNcRawShort, Number(item.Count), Number(item.Second)),
            RedactionNotCheckedKind.RawNothingRemoved => Strings.RedactNcRawNothing,
            RedactionNotCheckedKind.RawStillOccur => Format(Strings.RedactNcRawStillOccur, Number(item.Count), Number(item.Second)),
            RedactionNotCheckedKind.RawFilter => Format(Strings.RedactNcRawFilter, WhereOf(item), item.Detail),
            RedactionNotCheckedKind.RawImageStreams => Format(Strings.RedactNcRawImageStreams, WhereOf(item)),
            RedactionNotCheckedKind.RawUnreadableStream => Format(Strings.RedactNcRawUnreadable, WhereOf(item), item.Detail),
            RedactionNotCheckedKind.RawStreamTooLarge => Format(Strings.RedactNcRawTooLarge, WhereOf(item)),
            RedactionNotCheckedKind.RawTotalLimit => Format(Strings.RedactNcRawTotal, WhereOf(item)),
            RedactionNotCheckedKind.RawPredictor => Format(Strings.RedactNcRawPredictor, WhereOf(item)),
            RedactionNotCheckedKind.RawEncrypted => Strings.RedactNcRawEncrypted,
            RedactionNotCheckedKind.RawFontMapping => Strings.RedactNcRawFont,
            RedactionNotCheckedKind.RawEncodings => Strings.RedactNcRawEncodings,
            RedactionNotCheckedKind.RawSplitText => Strings.RedactNcRawSplit,
            RedactionNotCheckedKind.RawImageAttachments => Strings.RedactNcRawImageAttach,
            RedactionNotCheckedKind.PixelsBeyond => Format(Strings.RedactNcPixelsBeyond, Number(item.Count), Number(item.Second)),
            RedactionNotCheckedKind.PixelsOnlyImages => Strings.RedactNcPixelsOnly,
            RedactionNotCheckedKind.OtherViewer => Strings.RedactNcOtherViewer,
            RedactionNotCheckedKind.ApplyRender => item.Count > 0 ? Strings.RedactNcApplyRenderRaster : Strings.RedactNcApplyRender,
            RedactionNotCheckedKind.ApplyPageKeys => Strings.RedactNcApplyKeys,
            RedactionNotCheckedKind.ApplyXmp => Strings.RedactNcApplyXmp,
            RedactionNotCheckedKind.RasterOutsideAreas => Format(Strings.RedactNcRasterOutside, Number(item.Count)),
            _ => null,
        };
    }

    // Where the streams of a note of the raw scan are: "3 streams, the first at byte 120", "stream at byte 120" or "file".
    private static string WhereOf(RedactionNotChecked item) =>
        item.Count > 1
            ? Format(Strings.RedactNcWhereMany, Number(item.Count), Number(item.Offset))
            : item.Offset >= 0 ? Format(Strings.RedactNcWhereOne, Number(item.Offset)) : Strings.RedactNcWhereFile;

    // "Page 3 cannot be redacted, because ...": the clause is the cause the engine named, in the language of the interface.
    private static string PageLine(RedactionRefusal refusal)
    {
        var pages = PagesOf(refusal.Pages);
        var clause = RedactionPageCauses.Classify(refusal.Reason) switch
        {
            RedactionPageCause.ChangesOutsideAreas => Strings.RedactCauseChangesOutsideAreas,
            RedactionPageCause.SizeChanged => Strings.RedactCauseSizeChanged,
            RedactionPageCause.RenderFailed => Strings.RedactCauseRenderFailed,
            RedactionPageCause.ReopenFailed => Strings.RedactCauseReopenFailed,
            RedactionPageCause.TooLarge => Strings.RedactCauseTooLarge,
            RedactionPageCause.Unreadable => Strings.RedactCauseUnreadable,
            RedactionPageCause.RewriteUnreliable => Strings.RedactCauseRewriteUnreliable,
            RedactionPageCause.BigShape => Strings.RedactCauseBigShape,
            RedactionPageCause.AnnotationsTooComplex => Strings.RedactCauseAnnotations,
            _ => null,
        };
        return Capitalize(clause is null ? Format(Strings.RedactRefusalPageUnknown, pages) : Format(Strings.RedactRefusalPage, pages, clause));
    }

    private static string FailureLine(RedactionFinding finding)
    {
        var pages = RedactionRefusalText.PagesOf(finding);
        var where = pages.Count == 0 ? string.Empty : " (" + PagesOf(pages) + ")";
        return RedactionRefusalText.KindOf(finding) switch
        {
            RedactionFailureKind.UnremovableContent => Format(Strings.RedactFailureUnremovable, where),
            RedactionFailureKind.TextStillPresent => Format(Strings.RedactFailureText, where),
            RedactionFailureKind.CharInsideArea => Format(Strings.RedactFailureChar, where),
            RedactionFailureKind.ImagePixelNotOverwritten => Format(Strings.RedactFailureImage, where),
            RedactionFailureKind.UnreadableStream => Format(Strings.RedactFailureUnreadable, where),
            RedactionFailureKind.PageKeysStillPresent => Format(Strings.RedactFailureKeys, where),
            RedactionFailureKind.PageChangedOutsideAreas => Format(Strings.RedactFailurePage, where),
            RedactionFailureKind.CleanupItemStillPresent => Format(Strings.RedactFailureCleanup, where, finding.Detail),
            RedactionFailureKind.RasterPageInvalid => Format(Strings.RedactFailureRaster, where),
            _ => Format(Strings.RedactFailureUnverifiable, where),
        };
    }

    // The summary line: the numbers of the pages added up ("Removes: 38 characters, 1 image, 2 annotations; 1 text object as a whole").
    private static string SummaryOf(RedactionAnalysis analysis)
    {
        var chars = 0;
        var images = 0;
        var annotations = 0;
        var paths = 0;
        var forms = 0;
        var textWhole = 0;
        foreach (var page in analysis.Pages)
        {
            chars += page.CharsRemoved;
            images += page.ImagesOverwritten + page.ImagesRemovedWhole;
            annotations += page.AnnotationsRemoved;
            paths += page.PathsCropped + page.PathsRemovedWhole;
            forms += page.FormObjectsTouched;
            textWhole += page.TextObjectsRemovedWhole;
        }

        var text = Join(Parts(chars, images, annotations, paths, forms), textWhole);
        return text.Length == 0
            ? Format(Strings.RedactSumFormat, Strings.RedactSumNothing)
            : Format(Strings.RedactSumFormat, text);
    }

    private static List<string> Parts(int chars, int images, int annotations, int paths, int forms)
    {
        var parts = new List<string>();
        Add(parts, chars, Strings.RedactSumCharOne, Strings.RedactSumChars);
        Add(parts, images, Strings.RedactSumImageOne, Strings.RedactSumImages);
        Add(parts, annotations, Strings.RedactSumAnnotationOne, Strings.RedactSumAnnotations);
        Add(parts, paths, Strings.RedactSumPathOne, Strings.RedactSumPaths);
        Add(parts, forms, Strings.RedactSumFormObjectOne, Strings.RedactSumFormObjects);
        return parts;
    }

    private static string Join(List<string> parts, int textWhole)
    {
        var text = string.Join(", ", parts);
        if (textWhole > 0)
        {
            var whole = CountText(textWhole, Strings.RedactSumTextObjectWholeOne, Strings.RedactSumTextObjectsWhole);
            text = text.Length == 0 ? whole : text + "; " + whole;
        }

        return text;
    }

    // One line for each marked page: what removing does there.
    private static List<string> RemovedLines(RedactionAnalysis analysis)
    {
        var lines = new List<string>();
        foreach (var page in analysis.Pages)
        {
            var number = (page.Page + 1).ToString(Strings.Culture);
            var parts = Parts(page.CharsRemoved, page.ImagesOverwritten + page.ImagesRemovedWhole, 0, page.PathsCropped + page.PathsRemovedWhole, page.FormObjectsTouched);
            if (page.AnnotationsRemoved > 0)
            {
                parts.Add(CountText(page.AnnotationsRemoved, Strings.RedactSumAnnotationOne, Strings.RedactSumAnnotations) + " (" + Strings.RedactRemovedAnnotationsNote + ")");
            }

            var text = Join(parts, page.TextObjectsRemovedWhole);
            lines.Add(text.Length == 0
                ? Format(Strings.RedactNothingUnderMarks, number)
                : Format(Strings.RedactPageRemoved, number, text));
        }

        return lines.Count == 0 ? [Format(Strings.RedactSumFormat, Strings.RedactSumNothing)] : lines;
    }

    private static List<string> FallbackLines(RedactionAnalysis analysis) =>
    [
        .. analysis.Fallbacks.Select(static fallback => Format(
            fallback.Kind switch
            {
                RedactionFallbackKind.TextObjectRemovedWhole => Strings.RedactFallbackText,
                RedactionFallbackKind.ImageRemovedWhole => Strings.RedactFallbackImage,
                RedactionFallbackKind.PathRemovedWhole => Strings.RedactFallbackPath,
                _ => Strings.RedactFallbackForm,
            },
            (fallback.Page + 1).ToString(Strings.Culture))),
    ];

    // What the cleanup removes in this file, from the data of the scan; the fields of the document properties are named, never printed.
    private static List<string> CleanedLines(RedactionAnalysis analysis, bool confirmed)
    {
        var lines = new List<string>();
        var skipped = false;
        foreach (var item in analysis.Cleanup)
        {
            // XMP and scripts are counted all over the file, objects inside the pages included, and cannot be read back as gone:
            // after writing they are told under "not cleaned", not called cleaned.
            if (confirmed && item.Kind is CleanupKind.Xmp or CleanupKind.JavaScript)
            {
                skipped = true;
                continue;
            }

            lines.Add(item.Kind switch
            {
                CleanupKind.Author => Format(Strings.RedactCleanField, Strings.RedactFieldAuthor),
                CleanupKind.Title => Format(Strings.RedactCleanField, Strings.RedactFieldTitle),
                CleanupKind.Subject => Format(Strings.RedactCleanField, Strings.RedactFieldSubject),
                CleanupKind.Keywords => Format(Strings.RedactCleanField, Strings.RedactFieldKeywords),
                CleanupKind.Creator => Format(Strings.RedactCleanField, Strings.RedactFieldCreator),
                CleanupKind.Producer => Format(Strings.RedactCleanField, Strings.RedactFieldProducer),
                CleanupKind.Xmp => Format(Strings.RedactCleanXmp, item.Detail),
                CleanupKind.Bookmarks => Format(Strings.RedactCleanBookmarks, item.Detail),
                CleanupKind.Attachments => Format(Strings.RedactCleanAttachments, item.Detail),
                CleanupKind.JavaScript => Format(Strings.RedactCleanScripts, item.Detail),
                _ => Format(Strings.RedactCleanLabels, item.Detail),
            });
        }

        if (lines.Count == 0)
        {
            lines.Add(skipped ? Strings.RedactCleanSeeBelow : analysis.CleanupComplete ? Strings.RedactCleanNone : Strings.RedactCleanNoneIncomplete);
        }
        else if (!analysis.CleanupComplete)
        {
            lines.Add(Strings.RedactCleanIncomplete);
        }

        return lines;
    }

    private static List<string> LostLines(RedactionAnalysis analysis)
    {
        var lines = new List<string>();
        foreach (var loss in analysis.Losses)
        {
            lines.Add(loss.Kind switch
            {
                RedactionLossKind.InternalLinksLoseTheirTarget => CountText(loss.Count, Strings.RedactLossLinksOne, Strings.RedactLossLinks),
                RedactionLossKind.FormFieldsNotFillable => CountText(loss.Count, Strings.RedactLossFieldsOne, Strings.RedactLossFields),
                RedactionLossKind.ColorSpaceBecomesRgb => CountText(loss.Count, Strings.RedactLossRgbOne, Strings.RedactLossRgb),
                _ => Strings.RedactLossSignature,
            });
        }

        return lines;
    }

    // What Pagewing neither cleans nor checks: always the objects and actions that come with the pages, and what depends on the file.
    private static List<string> NotCleanedLines(RedactionAnalysis analysis)
    {
        var lines = new List<string> { Strings.RedactNotCleanedObjects, Strings.RedactNotCleanedActions, Strings.RedactNotCleanedAnnotations };
        if (analysis.AttachmentAnnotations > 0)
        {
            lines.Add(Strings.RedactNotCleanedAnnotationFiles(analysis.AttachmentAnnotations));
        }

        if (analysis.Losses.Any(static loss => loss.Kind == RedactionLossKind.FormFieldsNotFillable))
        {
            lines.Add(Strings.RedactNotCleanedFieldValues);
        }

        if (analysis.Losses.Any(static loss => loss.Kind == RedactionLossKind.SignatureInvalidated))
        {
            lines.Add(Strings.RedactNotCleanedSignature);
        }

        if (analysis.MarksWithoutTerm > 0)
        {
            lines.Add(CountText(analysis.MarksWithoutTerm, Strings.RedactNotCheckedNoTermOne, Strings.RedactNotCheckedNoTerm));
        }

        return lines;
    }

    // Pages with marks that stay vectors and text: only these are rendered against the original.
    private static int InPlacePages(RedactionAnalysis analysis) =>
        analysis.Pages.Count(page => analysis.Raster is not { } raster || !raster.Any(r => r.Page == page.Page));

    private static string CheckListOf(RedactionAnalysis analysis) =>
        analysis.Raster is not { Count: > 0 }
            ? Strings.RedactCheckList
            : InPlacePages(analysis) > 0 ? Strings.RedactCheckListRaster : Strings.RedactCheckListRasterOnly;

    private static string Number(long value) => value.ToString("N0", Strings.Culture);

    private static string PagesOf(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0)
        {
            return Strings.RedactPageFile;
        }

        var sorted = pages.Distinct().Order().ToArray();
        return Format(sorted.Length == 1 ? Strings.RedactPageOne : Strings.RedactPageMany, ApplyPagesInfo.GroupsOf(sorted));
    }

    private static void Add(List<string> parts, int count, string one, string many)
    {
        if (count > 0)
        {
            parts.Add(CountText(count, one, many));
        }
    }

    private static string CountText(int count, string one, string many) =>
        count == 1 ? Format(one, count.ToString("N0", Strings.Culture)) : Format(many, count.ToString("N0", Strings.Culture));

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : string.Create(text.Length, text, static (span, source) =>
        {
            source.AsSpan().CopyTo(span);
            span[0] = char.ToUpper(span[0], Strings.Culture);
        });

    private static string Format(string format, params string[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), [.. arguments.Cast<object>()]);
}
