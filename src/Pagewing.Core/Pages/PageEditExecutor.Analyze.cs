using System.Globalization;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

public sealed partial class PageEditExecutor
{
    /// <summary>The most distinct field names a report keeps to find collisions; past it the report says "unchecked".</summary>
    private const int MaxTrackedFieldNames = 200_000;

    /// <summary>
    /// Tells what applying <paramref name="plan"/> loses, couples or invalidates, from the facts of the spike
    /// (<c>Docs/DECISIONS.md</c>, "Seiten organisieren: Spike und QPDF-Entscheidung (M13)"). It reads the documents it is given,
    /// page by page in short jobs, and changes nothing. The nature of a loss is its <see cref="PageLossKind"/>; the list is
    /// empty when nothing is lost. Not reported (not a loss of content): the producer of the information becomes PDFium once
    /// pages are imported; page labels are a list by position and do not follow the pages.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="primary">The open document the plan was made for.</param>
    /// <param name="inserted">The open documents of the inserted sources (more than the plan uses is fine).</param>
    /// <param name="cancellationToken">Cancels before the next job.</param>
    /// <exception cref="PageEditException">A source of the plan has no document, a page of a source does not exist, or the primary document has another number of pages than the plan expects.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<PageEditReport> AnalyzeAsync(
        PageEditPlan plan,
        PdfDocument primary,
        IReadOnlyDictionary<PageSourceId, PdfDocument> inserted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(inserted);
        var usage = PlanUsage.Of(plan);
        usage.EnsureFilesAreGiven(inserted.Keys, inserted.Count);
        if (primary.PageCount != plan.PrimaryPageCount)
        {
            throw new PageEditException(PageEditError.OutOfRange, "The document has another number of pages than the plan was made for.");
        }

        var documents = new Dictionary<int, PdfDocument> { [0] = primary };
        foreach (var id in usage.InsertedIds)
        {
            var document = inserted[new PageSourceId(id)];
            if (usage.HighestIndex(id) >= document.PageCount)
            {
                throw new PageEditException(PageEditError.OutOfRange, "A page of an inserted file does not exist.");
            }

            documents[id] = document;
        }

        var losses = new List<PageLoss>();
        await ReportFileLossesAsync(usage, documents, losses, cancellationToken).ConfigureAwait(false);
        await ReportBookmarksAsync(usage, primary, losses, cancellationToken).ConfigureAwait(false);
        await ReportFormsAsync(usage, documents, losses, cancellationToken).ConfigureAwait(false);
        await ReportLinksAsync(usage, documents, losses, cancellationToken).ConfigureAwait(false);

        losses.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Detail, b.Detail);
        });
        return new PageEditReport(losses);
    }

    private static async Task ReportFileLossesAsync(PlanUsage usage, Dictionary<int, PdfDocument> documents, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        var signatures = await documents[0].GetSignatureInfoAsync(cancellationToken).ConfigureAwait(false);
        if (signatures.SignatureCount > 0)
        {
            var detail = signatures.Permission == DocMdpLevel.NotSet ? null : string.Create(CultureInfo.InvariantCulture, $"docmdp:{(int)signatures.Permission}");
            losses.Add(new PageLoss(PageLossKind.SignatureInvalidated, detail, null));
        }

        foreach (var id in usage.InsertedIds)
        {
            var document = documents[id];
            var print = await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false);
            if (print.IsEncrypted)
            {
                losses.Add(new PageLoss(PageLossKind.EncryptionNotPreserved, SourceKey(id), null));
            }

            var outline = await document.GetOutlineAsync(cancellationToken).ConfigureAwait(false);
            if (outline.Count > 0)
            {
                losses.Add(new PageLoss(PageLossKind.ForeignOutlineDropped, SourceKey(id), null));
            }
        }
    }

    private static async Task ReportBookmarksAsync(PlanUsage usage, PdfDocument primary, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        if (usage.DeletedPrimaryPages.Count == 0)
        {
            return;
        }

        var outline = await primary.GetOutlineAsync(cancellationToken).ConfigureAwait(false);
        var hit = new SortedSet<int>();
        var pending = new Stack<PdfOutlineItem>(outline);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (item.PageIndex is { } page && usage.DeletedPrimaryPages.Contains(page))
            {
                hit.Add(page);
            }

            foreach (var child in item.Children)
            {
                pending.Push(child);
            }
        }

        if (hit.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.BookmarkToDeletedPage, null, [.. hit]));
        }
    }

    /// <summary>Reports the fields of copied pages and the collisions of names.</summary>
    private static async Task ReportFormsAsync(PlanUsage usage, Dictionary<int, PdfDocument> documents, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        // The pages of each source that the plan uses and that have at least one widget.
        var withFields = new Dictionary<(int Source, int Index), List<int>>();
        var sourcesWithFields = new SortedSet<int>();
        foreach (var (id, document) in documents.OrderBy(d => d.Key))
        {
            if (!document.HasAcroForm)
            {
                continue;
            }

            var pagesWithFields = (await document.GetFormInfoAsync(cancellationToken).ConfigureAwait(false)).PagesWithFields.ToHashSet();
            foreach (var key in usage.Keys.Where(k => k.Source == id && pagesWithFields.Contains(k.Index)))
            {
                withFields[key] = usage.PositionsOf(key);
                sourcesWithFields.Add(id);
            }
        }

        if (withFields.Count == 0)
        {
            return;
        }

        var duplicated = new SortedSet<int>();
        var insertedPages = new SortedSet<int>();
        foreach (var (key, positions) in withFields)
        {
            if (positions.Count > 1)
            {
                duplicated.UnionWith(positions);
            }

            if (key.Source != 0)
            {
                insertedPages.UnionWith(positions);
            }
        }

        if (duplicated.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.DuplicatedFormFields, "duplicated", [.. duplicated]));
        }

        if (insertedPages.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.DuplicatedFormFields, "inserted", [.. insertedPages]));
        }

        if (sourcesWithFields.Count > 1)
        {
            await ReportCollisionsAsync(documents, withFields, losses, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReportCollisionsAsync(
        Dictionary<int, PdfDocument> documents,
        Dictionary<(int Source, int Index), List<int>> withFields,
        List<PageLoss> losses,
        CancellationToken cancellationToken)
    {
        var namesOf = new Dictionary<(int Source, int Index), IReadOnlyList<string>>();
        var firstSource = new Dictionary<string, int>(StringComparer.Ordinal);
        var colliding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in withFields.Keys.OrderBy(k => k.Source).ThenBy(k => k.Index))
        {
            var names = await documents[key.Source].GetFormFieldNamesAsync(key.Index, cancellationToken).ConfigureAwait(false);
            namesOf[key] = names;
            foreach (var name in names)
            {
                if (firstSource.TryGetValue(name, out var source))
                {
                    if (source != key.Source)
                    {
                        colliding.Add(name);
                    }
                }
                else if (firstSource.Count >= MaxTrackedFieldNames)
                {
                    // Too many names to compare: say so instead of reporting nothing.
                    losses.Add(new PageLoss(PageLossKind.FormFieldNameCollision, "unchecked", null));
                    return;
                }
                else
                {
                    firstSource[name] = key.Source;
                }
            }
        }

        if (colliding.Count == 0)
        {
            return;
        }

        var pages = new SortedSet<int>();
        foreach (var (key, names) in namesOf)
        {
            if (names.Any(colliding.Contains))
            {
                pages.UnionWith(withFields[key]);
            }
        }

        losses.Add(new PageLoss(PageLossKind.FormFieldNameCollision, null, [.. pages]));
    }

    private static async Task ReportLinksAsync(PlanUsage usage, Dictionary<int, PdfDocument> documents, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        // A page loses the target of its internal links when it is copied (any copy: an inserted page, or the second and later
        // appearance of a page); a kept page of the primary file loses a link whose target is deleted.
        var copied = new SortedSet<int>();
        var toDeleted = new SortedSet<int>();
        foreach (var (id, document) in documents.OrderBy(d => d.Key))
        {
            var scan = new List<(int Index, List<int> Positions)>();
            foreach (var key in usage.Keys.Where(k => k.Source == id).OrderBy(k => k.Index))
            {
                var positions = usage.PositionsOf(key);
                var isCopy = id != 0 || positions.Count > 1;
                var checksTargets = id == 0 && usage.DeletedPrimaryPages.Count > 0;
                if (isCopy || checksTargets)
                {
                    scan.Add((key.Index, positions));
                }
            }

            if (scan.Count == 0)
            {
                continue;
            }

            var targets = await document.GetInternalLinkTargetsAsync([.. scan.Select(s => s.Index)], cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < scan.Count; i++)
            {
                if (targets[i].Length == 0)
                {
                    continue;
                }

                var positions = scan[i].Positions;
                if (id != 0)
                {
                    copied.UnionWith(positions);
                    continue;
                }

                // A primary page: its first appearance is the page itself, the others are copies.
                copied.UnionWith(positions.Skip(1));
                if (usage.DeletedPrimaryPages.Count > 0 && targets[i].Any(usage.DeletedPrimaryPages.Contains))
                {
                    toDeleted.Add(positions[0]);
                }
            }
        }

        if (toDeleted.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.InternalLinkLost, "deleted-page", [.. toDeleted]));
        }

        if (copied.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.InternalLinkLost, "copied-page", [.. copied]));
        }
    }

    private static string SourceKey(int id) => string.Create(CultureInfo.InvariantCulture, $"source:{id}");
}
