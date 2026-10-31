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
    /// empty when nothing is lost. <see cref="PageEditReport.IsRefused"/> says whether the plan must not be applied at all. Not
    /// reported, by ruling (nothing of the content is lost): the producer of the information becomes PDFium once pages are
    /// imported.
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
        await ReportFileLossesAsync(plan, usage, documents, losses, cancellationToken).ConfigureAwait(false);
        await ReportBookmarksAsync(usage, primary, losses, cancellationToken).ConfigureAwait(false);
        await ReportFormsAsync(usage, documents, losses, cancellationToken).ConfigureAwait(false);
        await ReportLinksAsync(usage, documents, losses, cancellationToken).ConfigureAwait(false);

        losses.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : PageLossDetail.Compare(a.Detail, b.Detail);
        });
        return new PageEditReport(losses);
    }

    private static async Task ReportFileLossesAsync(PageEditPlan plan, PlanUsage usage, Dictionary<int, PdfDocument> documents, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        var primary = documents[0];
        var signatures = await primary.GetSignatureInfoAsync(cancellationToken).ConfigureAwait(false);
        if (signatures.SignatureCount > 0)
        {
            var detail = signatures.Permission == DocMdpLevel.NotSet ? null : PageLossDetail.DocMdp((int)signatures.Permission);
            losses.Add(new PageLoss(PageLossKind.SignatureInvalidated, detail, null));
        }

        // Page labels are a list by position: they name the same positions afterwards, whatever pages stand there.
        if (!IsPositionPreserving(plan) && await primary.HasPageLabelsAsync(cancellationToken).ConfigureAwait(false))
        {
            losses.Add(new PageLoss(PageLossKind.PageLabelsShifted, PageLossDetail.Shifted, null));
        }

        var primaryEncrypted = (await primary.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted;
        foreach (var id in usage.InsertedIds)
        {
            var document = documents[id];
            var print = await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false);
            if (print.IsEncrypted)
            {
                // Pages of a protected file in an unprotected result would be written without their protection: refused.
                losses.Add(new PageLoss(PageLossKind.EncryptionNotPreserved, PageLossDetail.Source(id), null, Blocks: !primaryEncrypted));
            }

            var outline = await document.GetOutlineAsync(cancellationToken).ConfigureAwait(false);
            if (outline.Count > 0)
            {
                losses.Add(new PageLoss(PageLossKind.ForeignOutlineDropped, PageLossDetail.Source(id), null));
            }

            if (await document.HasPageLabelsAsync(cancellationToken).ConfigureAwait(false))
            {
                losses.Add(new PageLoss(PageLossKind.PageLabelsShifted, PageLossDetail.Source(id), null));
            }
        }
    }

    /// <summary>Gets whether every page keeps its position: the same pages of the open document in the same order, only turned perhaps.</summary>
    private static bool IsPositionPreserving(PageEditPlan plan)
    {
        if (plan.Slots.Count != plan.PrimaryPageCount)
        {
            return false;
        }

        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            if (slot.Blank is not null || slot.Source.Value != 0 || slot.SourceIndex != i)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>Reports the fields of copied pages, the fields left without a page and the collisions of names.</summary>
    private static async Task ReportFormsAsync(PlanUsage usage, Dictionary<int, PdfDocument> documents, List<PageLoss> losses, CancellationToken cancellationToken)
    {
        // The pages of each source that the plan uses and that have at least one widget.
        var withFields = new Dictionary<(int Source, int Index), List<int>>();
        var sourcesWithFields = new SortedSet<int>();
        HashSet<int>? primaryPagesWithFields = null;
        foreach (var (id, document) in documents.OrderBy(d => d.Key))
        {
            if (!document.HasAcroForm)
            {
                continue;
            }

            var pagesWithFields = (await document.GetFormInfoAsync(cancellationToken).ConfigureAwait(false)).PagesWithFields.ToHashSet();
            if (id == 0)
            {
                primaryPagesWithFields = pagesWithFields;
            }

            foreach (var key in usage.Keys.Where(k => k.Source == id && pagesWithFields.Contains(k.Index)))
            {
                withFields[key] = usage.PositionsOf(key);
                sourcesWithFields.Add(id);
            }
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
            losses.Add(new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Duplicated, [.. duplicated]));
        }

        if (insertedPages.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Inserted, [.. insertedPages]));
        }

        var names = new FieldNames(documents);
        var orphanNames = new HashSet<string>(StringComparer.Ordinal);
        if (primaryPagesWithFields is not null && usage.DeletedPrimaryPages.Count > 0)
        {
            var orphanPages = await FindOrphansAsync(usage, primaryPagesWithFields, withFields, names, orphanNames, cancellationToken).ConfigureAwait(false);
            if (orphanPages.Count > 0)
            {
                losses.Add(new PageLoss(PageLossKind.OrphanFormFields, null, [.. orphanPages]));
            }
        }

        var contributing = sourcesWithFields.Count + (orphanNames.Count > 0 && !sourcesWithFields.Contains(0) ? 1 : 0);
        if (contributing > 1)
        {
            await ReportCollisionsAsync(withFields, orphanNames, names, losses, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the fields all of whose widgets are on deleted pages: their names (into <paramref name="orphanNames"/>) and the
    /// deleted pages that carry them. They stay in the form of the file as entries without a page.
    /// </summary>
    private static async Task<SortedSet<int>> FindOrphansAsync(
        PlanUsage usage,
        HashSet<int> primaryPagesWithFields,
        Dictionary<(int Source, int Index), List<int>> withFields,
        FieldNames names,
        HashSet<string> orphanNames,
        CancellationToken cancellationToken)
    {
        var pages = new SortedSet<int>();
        var deletedWithFields = usage.DeletedPrimaryPages.Where(primaryPagesWithFields.Contains).Order().ToList();
        if (deletedWithFields.Count == 0)
        {
            return pages;
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in withFields.Keys.Where(k => k.Source == 0).OrderBy(k => k.Index))
        {
            kept.UnionWith(await names.OfAsync(key, cancellationToken).ConfigureAwait(false));
        }

        foreach (var page in deletedWithFields)
        {
            var carried = false;
            foreach (var name in await names.OfAsync((0, page), cancellationToken).ConfigureAwait(false))
            {
                if (!kept.Contains(name))
                {
                    orphanNames.Add(name);
                    carried = true;
                }
            }

            if (carried)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private static async Task ReportCollisionsAsync(
        Dictionary<(int Source, int Index), List<int>> withFields,
        HashSet<string> orphanNames,
        FieldNames names,
        List<PageLoss> losses,
        CancellationToken cancellationToken)
    {
        // Fields left without a page still occupy their name in the form of the primary file.
        var firstSource = orphanNames.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        var colliding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in withFields.Keys.OrderBy(k => k.Source).ThenBy(k => k.Index))
        {
            foreach (var name in await names.OfAsync(key, cancellationToken).ConfigureAwait(false))
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
                    losses.Add(new PageLoss(PageLossKind.FormFieldNameCollision, PageLossDetail.Unchecked, null));
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
        foreach (var (key, positions) in withFields)
        {
            if ((await names.OfAsync(key, cancellationToken).ConfigureAwait(false)).Any(colliding.Contains))
            {
                pages.UnionWith(positions);
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
            losses.Add(new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.DeletedPage, [.. toDeleted]));
        }

        if (copied.Count > 0)
        {
            losses.Add(new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.CopiedPage, [.. copied]));
        }
    }

    /// <summary>The field names of the pages of the sources, read once each.</summary>
    private sealed class FieldNames(Dictionary<int, PdfDocument> documents)
    {
        private readonly Dictionary<(int Source, int Index), IReadOnlyList<string>> read = [];

        public async Task<IReadOnlyList<string>> OfAsync((int Source, int Index) key, CancellationToken cancellationToken)
        {
            if (!read.TryGetValue(key, out var list))
            {
                list = await documents[key.Source].GetFormFieldNamesAsync(key.Index, cancellationToken).ConfigureAwait(false);
                read[key] = list;
            }

            return list;
        }
    }
}
