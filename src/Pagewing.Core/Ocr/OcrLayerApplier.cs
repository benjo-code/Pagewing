using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Ocr;

/// <summary>What writing a run into a document changes: the layer each page had and the layer it gets (see <see cref="OcrLayerApplier"/>).</summary>
/// <param name="Before">The layers the pages had that a "replace" run takes out.</param>
/// <param name="After">The words each page gets.</param>
/// <param name="AlreadyRecognized">The pages left alone because they have a layer and the run does not replace it.</param>
public sealed record OcrLayerPlan(
    IReadOnlyDictionary<int, IReadOnlyList<OcrPageWord>> Before,
    IReadOnlyDictionary<int, IReadOnlyList<OcrPageWord>> After,
    IReadOnlyList<int> AlreadyRecognized,
    int LeftOut = 0)
{
    /// <summary>Gets whether there is nothing to write.</summary>
    public bool IsEmpty => After.Count == 0;

    /// <summary>Gets the step of the history that stands for the plan.</summary>
    public Annotations.AnnotationCommand.OcrLayer Step => new(Before, After);
}

/// <summary>
/// Writes the result of a recognition run into a document: the one place that decides which pages get a layer (no second layer on a page that has
/// one, nothing for a page without a usable word) and that takes the layers back when PDFium fails part-way. The editor of the app (as a step of the
/// undo history) and the command line (before it saves) both use it.
/// </summary>
public static class OcrLayerApplier
{
    /// <summary>Reads the pages of the result and decides what is written; changes nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A page of the result is not a page of the document.</exception>
    public static async Task<OcrLayerPlan> PlanAsync(PdfDocument document, OcrRunResult result, bool replace, CancellationToken cancellationToken = default)
    {
        var before = new SortedDictionary<int, IReadOnlyList<OcrPageWord>>();
        var after = new SortedDictionary<int, IReadOnlyList<OcrPageWord>>();
        var already = new List<int>();
        var leftOut = 0;
        foreach (var page in result.Pages.OrderBy(static p => p.Page))
        {
            // Only words the font of the layer can carry are planned; the rest is counted (never replaced), and a page with nothing left gets no layer.
            var usable = page.Words.Where(static w => w.IsUsable).ToList();
            var words = usable.Where(static w => w.Text.All(TextBoxCharacters.IsSupported)).ToList();
            leftOut += usable.Count - words.Count;
            if (words.Count == 0)
            {
                continue;
            }

            // Reading first: a page that is not there fails before anything is written.
            var existing = await document.ReadOcrLayerAsync(page.Page, cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0 && !replace)
            {
                already.Add(page.Page);
                continue;
            }

            after[page.Page] = words;
            if (existing.Count > 0)
            {
                before[page.Page] = existing;
            }
        }

        return new OcrLayerPlan(before, after, already, leftOut);
    }

    /// <summary>
    /// Writes the plan: takes the layer of each page out and writes the new one. If anything fails, the pages already touched get their old layer
    /// back (best effort) and the exception reaches the caller.
    /// </summary>
    /// <returns>The words written and the words left out because a character of them is not in the font of the layer.</returns>
    public static async Task<(int Written, int LeftOut)> WriteAsync(PdfDocument document, OcrLayerPlan plan)
    {
        var written = 0;
        var leftOut = 0;
        var touched = new List<int>();
        try
        {
            foreach (var (page, words) in plan.After)
            {
                touched.Add(page);
                await document.RemoveOcrLayerAsync(page).ConfigureAwait(false);
                var added = await document.AddOcrLayerAsync(page, words).ConfigureAwait(false);
                written += added.Written;
                leftOut += added.Skipped;
            }
        }
        catch
        {
            await RestoreAsync(document, touched, plan.Before).ConfigureAwait(false);
            throw;
        }

        return (written, leftOut);
    }

    private static async Task RestoreAsync(PdfDocument document, List<int> pages, IReadOnlyDictionary<int, IReadOnlyList<OcrPageWord>> before)
    {
        foreach (var page in pages)
        {
            try
            {
                await document.RemoveOcrLayerAsync(page).ConfigureAwait(false);
                if (before.TryGetValue(page, out var old))
                {
                    await document.AddOcrLayerAsync(page, old).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                // The page cannot be taken back either; the exception of the run stays the one the caller sees.
            }
        }
    }
}
