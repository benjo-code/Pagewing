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

        RedactionPlan plan;
        try
        {
            plan = RedactionPlan.Empty.WithRange(marks);
        }
        catch (RedactionException ex)
        {
            throw new CliException($"The marks cannot be made: {ex.Message} Nothing was written.", ex);
        }

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, document, inputPath, password, ct).ConfigureAwait(false);
        if (analysis.IsRefused)
        {
            throw new CliException(RedactionRefusalText.Of(analysis.Refusals));
        }

        if (options.DryRun)
        {
            await WriteDryRunAsync(error, found, rects, analysis, ct).ConfigureAwait(false);
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
            throw new CliException(ex.Refusals.Count > 0 ? RedactionRefusalText.Of(ex.Refusals) : ex.Message, ex);
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
        await error.WriteLineAsync(RedactionReportText.Of(analysis, result.Check, showValues: false).AsMemory(), ct).ConfigureAwait(false);
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

        if (terms.Count == 0)
        {
            return new Found(found);
        }

        var incomplete = report.IncompletePages.Where(selected.Contains).ToArray();
        if (incomplete.Length > 0)
        {
            await error.WriteLineAsync($"warning: {PageWord(incomplete)} could not be searched completely; the text may still be there.".AsMemory(), ct).ConfigureAwait(false);
        }

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

        var missing = found.Where(static t => t.Hits.Count == 0).ToArray();
        if (missing.Length > 0)
        {
            var lines = missing.Select(t => $"No match for \"{Printable(t.Text)}\"{(options.Pages is null ? string.Empty : $" on {PageWord(pages)}")}.");
            var tail = incomplete.Length > 0 ? $" {PageWord(incomplete)} could not be searched completely, so the text may be there." : string.Empty;
            throw new CliException($"{string.Join(' ', lines)}{tail} Nothing was written.");
        }

        var unmarkable = found.SelectMany(static t => t.Hits).Where(static h => !h.Markable).ToArray();
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

        return new Found(found);
    }

    /// <summary>The preview: how many matches on which page (never the matched text), then what the redaction would do. Writes nothing to disk.</summary>
    private static async Task WriteDryRunAsync(TextWriter error, Found found, IReadOnlyList<ClippedRect> rects, RedactionAnalysis analysis, CancellationToken ct)
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

        if (rects.Count > 0)
        {
            text.Append(Count(rects.Count, "rectangle", "rectangles")).Append(" on ").Append(PageWord(rects.Select(static r => r.Page).Distinct().Order().ToArray())).Append('\n');
        }

        text.Append('\n').Append(RedactionReportText.Of(analysis, showValues: false)).Append("\n\nThe check of the written file runs only when the file is written; it can still end the command.");
        await error.WriteLineAsync(text.ToString().AsMemory(), ct).ConfigureAwait(false);
    }

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

    private static string Count(int count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record ClippedRect(int Page, PageRect Area);

    private sealed record TermHits(string Text, List<RedactionHit> Hits);

    private sealed record Found(List<TermHits> Terms);
}
