using System.Globalization;
using System.Text;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;

namespace Pagewing.Cli.Commands;

/// <summary>A rectangle of <c>--rect</c>: page counted from 1, points, origin at the top left of the page as it is shown.</summary>
/// <param name="Page">The page, from 1.</param>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width; greater than 0.</param>
/// <param name="Height">The height; greater than 0.</param>
internal readonly record struct RedactRect(int Page, double X, double Y, double Width, double Height)
{
    /// <summary>Reads <c>&lt;page&gt;:&lt;x&gt;,&lt;y&gt;,&lt;w&gt;,&lt;h&gt;</c> (a dot is the decimal separator, whatever the system language).</summary>
    /// <exception cref="UsageException">The text is not of that form, a number is not finite, the page is below 1 or the size is not positive.</exception>
    public static RedactRect Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bad = new UsageException($"Invalid value for --rect: '{text}' (use <page>:<x>,<y>,<w>,<h>, for example 1:72,100,200,24; page from 1, points, origin top left).");
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw bad;
        }

        if (!int.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
        {
            throw bad;
        }

        var parts = text[(colon + 1)..].Split(',');
        if (parts.Length != 4)
        {
            throw bad;
        }

        var numbers = new double[4];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
            {
                throw bad;
            }
        }

        if (numbers[2] <= 0 || numbers[3] <= 0)
        {
            throw new UsageException($"Invalid value for --rect: '{text}' (the width and the height must be greater than 0).");
        }

        return new RedactRect(page, numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}

/// <summary>
/// <c>pagewing redact</c>: removes text and areas for good and writes the result as a new file. The same chain as the app's "Apply"
/// (<see cref="RedactionApplier"/>): the matches of <c>--text</c> are searched in the page text, the texts of annotations and the
/// values of form fields, the plan is analysed first (a refused plan is never written), and the file goes through the checked, atomic
/// full rewrite whose last step reads the written file back. If that check finds anything the command fails with exit code 1 and no
/// output file exists. The report goes to stderr and says what was removed, cleaned, lost, searched, checked and NOT checked; it never
/// contains a removed text. A digitally signed input is announced with a warning before anything is written.
/// </summary>
internal static class RedactCommand
{
    // How many matches that cannot be marked are named one by one before the rest is counted.
    private const int MaxListedUnmarkable = 20;

    public static async Task RunAsync(
        PdfDocument document,
        CliOptions options,
        string? password,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var inputPath = Path.GetFullPath(options.File);
        var engine = new PdfEngine();
        var pages = CliApp.SelectPages(options.Pages, document.PageCount);
        var rects = await ClipRectsAsync(document, options.Rects ?? [], error, ct).ConfigureAwait(false);
        var rasterPages = options.RasterizePages is null ? [] : CliApp.SelectPages(options.RasterizePages, document.PageCount);

        // A file that cannot be redacted at all is named before anything is searched.
        if (await RedactionExecutor.FileRefusalAsync(document, ct).ConfigureAwait(false) is { } refusal)
        {
            throw new CliException(RedactionRefusalText.Of(refusal));
        }

        var found = await SearchAsync(document, options, pages, error, ct).ConfigureAwait(false);
        var marks = new List<RedactionMark>(rects.Count);
        foreach (var rect in rects)
        {
            marks.Add(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, rect.Page, [rect.Area], null, Everywhere: false));
        }

        // --text without --pages is document-wide: every spelling found must be gone from the whole document. With --pages only the
        // matches on those pages are marked, and the check holds the rest of the document to "no more than before".
        var everywhere = options.Pages is null || pages.Count == document.PageCount;
        var searchOptions = new SearchOptions(options.MatchCase, WholeWord: false);
        foreach (var term in found.Terms)
        {
            marks.AddRange(RedactionSearch.ToMarks(term.Hits, term.Text, everywhere, searchOptions, out _));
        }

        // The matches of patterns are never "everywhere": each one is a concrete string at a concrete place.
        marks.AddRange(RedactionSearch.ToMarks(found.RedactedPatternHits, out _));

        // The command line has no single marks: the overlay text goes on every mark of the call. Whether it repeats a redacted term is the
        // business of the analysis below (the terms are only known after the search), not decided here.
        if (options.OverlayText is { } overlayText)
        {
            for (var i = 0; i < marks.Count; i++)
            {
                marks[i] = marks[i] with { OverlayText = overlayText };
            }
        }

        RedactionPlan plan;
        try
        {
            plan = RedactionPlan.Empty.WithFill(options.Fill).WithImageTreatment(options.ImageFill).WithImageStrength(options.ImageStrength)
                .WithOverlayStyle(new RedactionOverlayStyle(Repeat: options.OverlayRepeat)).WithRange(marks);
        }
        catch (RedactionException ex)
        {
            throw new CliException($"The marks cannot be made: {ex.Message} Nothing was written.", ex);
        }

        try
        {
            plan = WithRasterPages(plan, rasterPages, options.RasterDpi);
        }
        catch (RedactionException ex)
        {
            throw new CliException($"The pages cannot be turned into images: {ex.Message} Nothing was written.", ex);
        }

        var executor = new RedactionExecutor(engine);
        var analysis = await executor.AnalyzeAsync(plan, document, inputPath, password, ct).ConfigureAwait(false);

        // --rasterize-unredactable: exactly the pages the analysis refuses as not redactable become images. The plan is analysed once more,
        // and what is still refused (a page too large for 72 dpi, one that cannot be read, a file refused for another reason) stays a
        // refusal; the pages added are never added a second time, so there is no loop.
        IReadOnlyList<int> rescued = [];
        if (analysis.IsRefused && options.RasterizeUnredactable)
        {
            rescued = [.. analysis.Refusals
                .Where(static r => r.Kind == RedactionRefusalKind.PageNotRedactable)
                .SelectMany(static r => r.Pages)
                .Where(page => !plan.RasterPages.Contains(page))
                .Distinct()
                .Order()];
            if (rescued.Count > 0)
            {
                try
                {
                    plan = WithRasterPages(plan, rescued, options.RasterDpi);
                }
                catch (RedactionException ex)
                {
                    throw new CliException($"The pages cannot be turned into images: {ex.Message} Nothing was written.", ex);
                }

                analysis = await executor.AnalyzeAsync(plan, document, inputPath, password, ct).ConfigureAwait(false);
            }
        }

        if (analysis.IsRefused)
        {
            throw new CliException(RefusalMessage(analysis.Refusals, options, plan.RasterPages));
        }

        if (rescued.Count > 0)
        {
            var one = rescued.Count == 1;
            await error.WriteLineAsync($"warning: --rasterize-unredactable turns {PageWord(rescued)} into {(one ? "an image" : "images")}: {(one ? "it cannot" : "they cannot")} be redacted in place.".AsMemory(), ct).ConfigureAwait(false);
        }

        if (options.DryRun)
        {
            await WriteDryRunAsync(error, found, options.Patterns ?? [], rects, analysis, plan.RasterDpi, ct).ConfigureAwait(false);
            return;
        }

        var target = options.OutputFile ?? throw new UsageException("The 'redact' command needs --output <out.pdf> (not needed with --dry-run).");

        // The user is told about the signature before the first byte is written.
        foreach (var loss in analysis.Losses)
        {
            if (loss.Kind == RedactionLossKind.SignatureInvalidated)
            {
                error.WriteLine($"warning: {PageLossText.Describe(new PageLoss(PageLossKind.SignatureInvalidated, null, [], false))}");
            }
        }

        // The pages that become images are told before the first byte is written, too.
        if (RasterSection(analysis, plan.RasterDpi) is { Length: > 0 } rasterSection)
        {
            await error.WriteLineAsync(rasterSection.AsMemory(), ct).ConfigureAwait(false);
        }

        await error.FlushAsync(ct).ConfigureAwait(false);

        RedactionApplyResult result;
        try
        {
            // The output is never the input (OutputGuard), so there is no stamp of "the original" to compare at the target.
            var request = new RedactionApplyRequest(plan, Path.GetFullPath(target), ReplaceExisting: options.Force);
            result = await RedactionApplier.WriteAsync(engine, document, inputPath, password, openedStamp: null, request, progress, ct).ConfigureAwait(false);
        }
        catch (RedactionRefusedException ex)
        {
            throw new CliException(ex.Refusals.Count > 0 ? RefusalMessage(ex.Refusals, options, plan.RasterPages) : ex.Message, ex);
        }
        catch (SaveFailureException ex) when (ex.Reason == SaveFailureReason.RedactionFailed)
        {
            // The check found something, so nothing was written: the report says what was done and what was not, the error what was found.
            if (ex.Details is RedactionCheck failed)
            {
                await error.WriteLineAsync(RedactionReportText.Of(analysis, failed, showValues: false).AsMemory(), ct).ConfigureAwait(false);
                await error.WriteLineAsync().ConfigureAwait(false);
            }

            throw new CliException(RedactionRefusalText.OfFailure(ex), ex);
        }
        catch (SaveFailureException ex)
        {
            throw new CliException(SaveFailureText.Describe(ex, target), ex);
        }
        catch (RedactionException ex)
        {
            throw new CliException(ex.Message, ex);
        }

        await result.Saved.Reopened.DisposeAsync().ConfigureAwait(false);
        await error.WriteLineAsync((RedactionReportText.Of(analysis, result.Check, showValues: false) + AcceptedSection(found)).AsMemory(), ct).ConfigureAwait(false);
        await output.WriteLineAsync(target.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Checks the rectangles against the pages and clips what reaches beyond the page; says so on <paramref name="error"/>.</summary>
    private static async Task<List<ClippedRect>> ClipRectsAsync(PdfDocument document, IReadOnlyList<RedactRect> rects, TextWriter error, CancellationToken ct)
    {
        var clipped = new List<ClippedRect>(rects.Count);
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            if (rect.Page > document.PageCount)
            {
                var noun = document.PageCount == 1 ? "page" : "pages";
                throw new CliException($"Page {rect.Page} of --rect {Number(i + 1)} is out of range (the document has {document.PageCount} {noun}).");
            }

            var size = await document.GetPageSizeAsync(rect.Page - 1, ct).ConfigureAwait(false);
            var area = new PageRect(Math.Max(0, rect.X), Math.Max(0, rect.Y), Math.Min(size.Width, rect.X + rect.Width), Math.Min(size.Height, rect.Y + rect.Height));
            if (!(area.Right > area.Left) || !(area.Bottom > area.Top))
            {
                throw new CliException($"The rectangle of --rect {Number(i + 1)} lies outside page {rect.Page} ({Number(size.Width)} x {Number(size.Height)} pt). Nothing was written.");
            }

            if (area != new PageRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height))
            {
                await error.WriteLineAsync(
                    $"warning: The rectangle of --rect {Number(i + 1)} reaches beyond page {rect.Page} ({Number(size.Width)} x {Number(size.Height)} pt) and is clipped to x={Number(area.Left)}, y={Number(area.Top)}, w={Number(area.Width)}, h={Number(area.Height)}.".AsMemory(),
                    ct).ConfigureAwait(false);
            }

            clipped.Add(new ClippedRect(rect.Page - 1, area));
        }

        return clipped;
    }

    /// <summary>
    /// Searches every <c>--text</c> in the whole document and keeps the matches on the selected pages. Every search is read to its end,
    /// and what could not be searched is said on <paramref name="error"/> whatever the outcome. A search that found nothing, that
    /// stopped at its limit or whose matches cannot be marked ends the command (nothing is written).
    /// </summary>
    private static async Task<Found> SearchAsync(PdfDocument document, CliOptions options, IReadOnlyList<int> pages, TextWriter error, CancellationToken ct)
    {
        var terms = options.Texts ?? [];
        var kinds = options.Patterns ?? [];
        var selected = pages.ToHashSet();
        var searchOptions = new SearchOptions(options.MatchCase, WholeWord: false);
        var report = new RedactionSearchReport();
        var found = new List<TermHits>(terms.Count);
        foreach (var term in terms)
        {
            var hits = new List<RedactionHit>();

            // Always to the end: the report is complete only then.
            await foreach (var hit in RedactionSearch.FindAsync(document, term, searchOptions, report, texts: null, ct).ConfigureAwait(false))
            {
                if (selected.Contains(hit.Page))
                {
                    hits.Add(hit);
                }
            }

            found.Add(new TermHits(term, hits));
        }

        // One search for all kinds asked for; --pages limits the matches here as it does for the terms.
        var patternHits = new List<RedactionHit>();
        if (kinds.Count > 0)
        {
            await foreach (var hit in RedactionPatternSearch.FindAsync(document, kinds.ToHashSet(), report, texts: null, ct).ConfigureAwait(false))
            {
                if (selected.Contains(hit.Page))
                {
                    patternHits.Add(hit);
                }
            }
        }

        if (terms.Count == 0 && kinds.Count == 0)
        {
            return new Found(found, [], [], options.IncludeUnchecked);
        }

        var incomplete = report.IncompletePages.Where(selected.Contains).ToArray();
        await error.WriteLineAsync("Not searched:".AsMemory(), ct).ConfigureAwait(false);
        foreach (var line in RedactionSearch.NotSearched)
        {
            await error.WriteLineAsync($"- {line}".AsMemory(), ct).ConfigureAwait(false);
        }

        await error.WriteLineAsync().ConfigureAwait(false);

        if (report.LimitReached)
        {
            throw new CliException($"The search stopped at {Number(RedactionSearch.MaxHits)} matches and more may exist, so the text cannot be removed everywhere. Nothing was written.");
        }

        // A page that was not searched completely may still hold the text, and the check of the result has the same blind spots (the
        // file bytes are searched only for texts of at least 4 characters, and with --pages "no more than before" is all there is), so
        // the command stops, also as a dry run, unless the user accepts exactly that.
        if (incomplete.Length > 0)
        {
            if (!options.AllowUnsearched)
            {
                throw new CliException($"{Capitalize(PageWord(incomplete))} could not be searched completely, so the text may still be there. Nothing was written. Use --allow-unsearched to accept that these pages were not fully searched.");
            }

            await error.WriteLineAsync($"warning: {PageWord(incomplete)} could not be searched completely; the text may still be there (accepted with --allow-unsearched).".AsMemory(), ct).ConfigureAwait(false);
        }

        // Matches whose checksum or length is wrong are not redacted unless the user asked for them. What was left alone is said (how
        // many, of which kind, on which pages), never what it is.
        var all = new Found(found, options.AllowUnsearched ? incomplete : [], patternHits, options.IncludeUnchecked);
        if (all.SkippedPatternHits.Count > 0)
        {
            await error.WriteLineAsync(SkippedLine(all.SkippedPatternHits).AsMemory(), ct).ConfigureAwait(false);
        }

        var missing = found.Where(static t => t.Hits.Count == 0).ToArray();
        var noPattern = kinds.Count > 0 && all.RedactedPatternHits.Count == 0;
        if (missing.Length > 0 || noPattern)
        {
            var lines = missing.Select(t => $"No match for \"{Printable(t.Text)}\"{(options.Pages is null ? string.Empty : $" on {PageWord(pages)}")}.").ToList();
            if (noPattern)
            {
                var names = string.Join(", ", CliOptions.PatternNames.Where(p => kinds.Contains(p.Kind)).Select(static p => p.Name));
                lines.Add($"No match for the patterns ({names}){(options.Pages is null ? string.Empty : $" on {PageWord(pages)}")}.");
            }

            var tail = incomplete.Length > 0 ? $" {PageWord(incomplete)} could not be searched completely, so the text may be there." : string.Empty;
            var hint = noPattern && all.SkippedPatternHits.Count > 0 ? " The matches that were skipped are redacted with --include-unchecked." : string.Empty;
            throw new CliException($"{string.Join(' ', lines)}{tail}{hint} Nothing was written.");
        }

        var unmarkable = found.SelectMany(static t => t.Hits).Concat(all.RedactedPatternHits).Where(static h => !h.Markable).ToArray();
        if (unmarkable.Length > 0)
        {
            var text = new StringBuilder("A match cannot be marked, so the text cannot be removed there and nothing was written:");
            foreach (var hit in unmarkable.Take(MaxListedUnmarkable))
            {
                text.Append("\n- page ").Append(Number(hit.Page + 1)).Append(" (").Append(SourceName(hit.Source)).Append("): ").Append(hit.NotMarkableReason ?? "it cannot be marked.");
            }

            if (unmarkable.Length > MaxListedUnmarkable)
            {
                text.Append("\n- and ").Append(Number(unmarkable.Length - MaxListedUnmarkable)).Append(" more.");
            }

            throw new CliException(text.ToString());
        }

        return all;
    }

    /// <summary>"Skipped (not redacted): 2 pattern matches whose checksum or length is wrong (iban: 1, card: 1) on page 1." Counts and pages only.</summary>
    private static string SkippedLine(IReadOnlyList<RedactionHit> skipped)
    {
        var kinds = string.Join(", ", CliOptions.PatternNames
            .Select(p => (p.Name, Count: skipped.Count(h => h.Pattern == p.Kind)))
            .Where(static k => k.Count > 0)
            .Select(static k => $"{k.Name}: {Number(k.Count)}"));
        var pages = skipped.Select(static h => h.Page).Distinct().Order().ToArray();
        var one = skipped.Count == 1;
        return $"Skipped (not redacted): {Count(skipped.Count, "pattern match", "pattern matches")} whose checksum or length is wrong ({kinds}) on {PageWord(pages)}. Use --include-unchecked to redact {(one ? "it" : "them")} too.";
    }

    /// <summary>The preview: how many matches on which page (never the matched text), then what the redaction would do. Writes nothing to disk.</summary>
    private static async Task WriteDryRunAsync(TextWriter error, Found found, IReadOnlyList<RedactionPatternKind> kinds, IReadOnlyList<ClippedRect> rects, RedactionAnalysis analysis, int rasterDpi, CancellationToken ct)
    {
        var text = new StringBuilder("Dry run: nothing is written.\n\n");
        var number = 0;
        foreach (var term in found.Terms)
        {
            number++;
            text.Append("Text ").Append(Number(number)).Append(": ").Append(Count(term.Hits.Count, "match", "matches")).Append('\n');
            foreach (var group in term.Hits.GroupBy(static h => h.Page).OrderBy(static g => g.Key))
            {
                text.Append("  page ").Append(Number(group.Key + 1)).Append(": ").Append(Number(group.Count()));
                var elsewhere = group.Count(static h => h.Source != RedactionHitSource.PageText);
                var invisible = group.Count(static h => h.Invisible);
                if (elsewhere > 0 || invisible > 0)
                {
                    text.Append(" (");
                    text.Append(string.Join(", ", new[] { elsewhere > 0 ? $"{Number(elsewhere)} in annotations or form fields" : null, invisible > 0 ? $"{Number(invisible)} in invisible text" : null }.Where(static s => s is not null)));
                    text.Append(')');
                }

                text.Append('\n');
            }
        }

        foreach (var (name, kind) in CliOptions.PatternNames.Where(p => kinds.Contains(p.Kind)))
        {
            var hits = found.PatternHits.Where(h => h.Pattern == kind).ToArray();
            text.Append("Pattern ").Append(name).Append(": ").Append(Count(hits.Length, "match", "matches"));
            var parts = new List<string>();
            if (hits.Count(static h => h.Certainty == PatternCertainty.ChecksumOk) is > 0 and var ok)
            {
                parts.Add($"{Number(ok)} checksum ok");
            }

            if (hits.Count(static h => h.Certainty == PatternCertainty.ShapeOnly) is > 0 and var shape)
            {
                parts.Add($"{Number(shape)} shape only, no checksum");
            }

            if (hits.Count(static h => h.Certainty == PatternCertainty.ChecksumFailed) is > 0 and var failed)
            {
                parts.Add($"{Number(failed)} checksum wrong - {(found.IncludeUnchecked ? "redacted with" : "not redacted without")} --include-unchecked");
            }

            if (parts.Count > 0)
            {
                text.Append(" (").Append(string.Join(", ", parts)).Append(')');
            }

            text.Append('\n');
            foreach (var group in hits.GroupBy(static h => h.Page).OrderBy(static g => g.Key))
            {
                text.Append("  page ").Append(Number(group.Key + 1)).Append(": ").Append(Number(group.Count())).Append('\n');
            }
        }

        if (rects.Count > 0)
        {
            text.Append(Count(rects.Count, "rectangle", "rectangles")).Append(" on ").Append(PageWord(rects.Select(static r => r.Page).Distinct().Order().ToArray())).Append('\n');
        }

        text.Append('\n');
        if (RasterSection(analysis, rasterDpi) is { Length: > 0 } rasterSection)
        {
            text.Append(rasterSection).Append("\n\n");
        }

        if (OverlayDryRunLine(analysis) is { Length: > 0 } overlayLine)
        {
            text.Append(overlayLine).Append("\n\n");
        }

        text.Append(RedactionReportText.Of(analysis, showValues: false)).Append('\n').Append(AcceptedSection(found)).Append("\nThe check of the written file runs only when the file is written; it can still end the command.");
        await error.WriteLineAsync(text.ToString().AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>The plan with the pages (0-based) added to the pages that become images, at <paramref name="dpi"/>.</summary>
    private static RedactionPlan WithRasterPages(RedactionPlan plan, IReadOnlyList<int> pages, int dpi)
    {
        plan = plan.WithRasterDpi(dpi);
        foreach (var page in pages)
        {
            plan = plan.WithRasterPage(page, on: true);
        }

        return plan;
    }

    /// <summary>The text of a refusal; for pages that cannot be redacted in place it also names the way out the user has to choose.</summary>
    private static string RefusalMessage(IReadOnlyList<RedactionRefusal> refusals, CliOptions options, IReadOnlySet<int> rasterPages)
    {
        // A page the user already chose to turn into an image is refused for its own reason (too large, unreadable): no hint then.
        var text = RedactionRefusalText.Of(refusals);
        if (!options.RasterizeUnredactable && refusals.Any(r => r.Kind == RedactionRefusalKind.PageNotRedactable && r.Pages.Any(page => !rasterPages.Contains(page))))
        {
            text += "\nTo turn pages that cannot be redacted in place into images instead, use --rasterize-unredactable: all text, links, comments and form fields of such a page are lost.";
        }

        return text;
    }

    /// <summary>
    /// What turning pages into images does, before it is done: the resolution really used (never lowered in silence), the size, what
    /// each page loses, and what the check cannot do for such a page. Page numbers count from 1; no content of the document is repeated.
    /// Empty when no page becomes an image.
    /// </summary>
    private static string RasterSection(RedactionAnalysis analysis, int requestedDpi)
    {
        if (analysis.Raster is not { Count: > 0 } raster)
        {
            return string.Empty;
        }

        var text = new StringBuilder("Pages turned into images:");
        foreach (var info in raster)
        {
            text.Append("\n- page ").Append(Number(info.Page + 1)).Append(": ").Append(Number(info.Dpi)).Append(" dpi");
            if (info.Lowered)
            {
                text.Append(", lowered from ").Append(Number(requestedDpi)).Append(" dpi because the page is large");
            }

            text.Append(" (").Append(Number(info.WidthPx)).Append(" x ").Append(Number(info.HeightPx)).Append(" pixels, the file grows by up to about ").Append(SizeText(info.EstimatedBytes)).Append("). Lost: ");
            var lost = new List<string>();
            if (info.HasText)
            {
                lost.Add("all of its text");
            }

            if (info.Links > 0)
            {
                lost.Add(Count(info.Links, "link", "links"));
            }

            if (info.Annotations > 0)
            {
                lost.Add($"{Count(info.Annotations, "annotation", "annotations")} (comments, links and form fields are among them)");
            }

            if (info.FormFields > 0)
            {
                lost.Add($"{Count(info.FormFields, "form field", "form fields")}, no longer fillable");
            }

            text.Append(lost.Count == 0 ? "nothing else; it has no text, links, annotations or form fields" : string.Join("; ", lost)).Append('.');
        }

        text.Append("\nAll other pages keep their text and vectors. A page turned into an image is an image only: a visible occurrence of a removed text outside the marked areas stays in it and cannot be found without OCR, so these pages cannot be checked for visible occurrences outside the marks.");
        return text.ToString();
    }

    /// <summary>
    /// The areas that get their overlay text, before it is drawn (counts only, never the text). What does not fit or is left out stands in
    /// the report that follows. The text is drawn as shapes: it is not selectable and the check of the written file does not read it.
    /// Empty when no area has an overlay text.
    /// </summary>
    private static string OverlayDryRunLine(RedactionAnalysis analysis)
    {
        var drawn = analysis.Pages.Sum(static p => p.OverlaysDrawn) + (analysis.Raster?.Sum(static r => r.OverlaysDrawn) ?? 0);
        return drawn == 0
            ? string.Empty
            : $"Overlay text: {Count(drawn, "area gets", "areas get")} the overlay text. It is drawn as shapes, so it is not selectable, and the check of the written file does not read it.";
    }

    // Megabytes with at most one decimal; never below 0.1 (a size is an upper bound).
    private static string SizeText(long bytes) =>
        Math.Max(0.1, Math.Round(bytes / (1024.0 * 1024.0), 1)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB";

    private static string SourceName(RedactionHitSource source) => source switch
    {
        RedactionHitSource.Annotation => "annotation",
        RedactionHitSource.FormField => "form field",
        _ => "page text",
    };

    // 0-based pages as "page 3" or "pages 1-3, 6".
    private static string PageWord(IReadOnlyList<int> zeroBasedPages) =>
        $"{(zeroBasedPages.Distinct().Count() == 1 ? "page" : "pages")} {PageLossText.FormatPages(zeroBasedPages)}";

    // The term as typed, but never a control character into the terminal.
    private static string Printable(string term) =>
        string.Create(term.Length, term, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The section that tells which pages the user accepted as not searched completely, and what is never searched.</summary>
    private static string AcceptedSection(Found found)
    {
        if (found.AcceptedUnsearched.Length == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder("\nNot searched (accepted):\n");
        text.Append("- ").Append(PageWord(found.AcceptedUnsearched)).Append(" could not be searched completely; the text may still be there. Accepted with --allow-unsearched.\n");
        foreach (var line in RedactionSearch.NotSearched)
        {
            text.Append("- ").Append(line).Append('\n');
        }

        return text.ToString();
    }

    private static string Count(int count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record ClippedRect(int Page, PageRect Area);

    private sealed record TermHits(string Text, List<RedactionHit> Hits);

    /// <param name="Terms">The matches of each <c>--text</c>.</param>
    /// <param name="AcceptedUnsearched">The 0-based pages accepted as not fully searched.</param>
    /// <param name="PatternHits">Every match of the patterns on the selected pages, whatever its certainty.</param>
    /// <param name="IncludeUnchecked">Whether matches with a wrong checksum are redacted too.</param>
    private sealed record Found(List<TermHits> Terms, int[] AcceptedUnsearched, IReadOnlyList<RedactionHit> PatternHits, bool IncludeUnchecked)
    {
        /// <summary>Gets the pattern matches that become marks.</summary>
        public IReadOnlyList<RedactionHit> RedactedPatternHits { get; } = [.. PatternHits.Where(h => IncludeUnchecked || h.Certainty != PatternCertainty.ChecksumFailed)];

        /// <summary>Gets the pattern matches that are left alone because their checksum or length is wrong.</summary>
        public IReadOnlyList<RedactionHit> SkippedPatternHits { get; } = [.. PatternHits.Where(h => !IncludeUnchecked && h.Certainty == PatternCertainty.ChecksumFailed)];
    }
}
