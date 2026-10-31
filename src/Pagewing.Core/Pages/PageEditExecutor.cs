using System.Globalization;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

/// <summary>A PDF file a page plan takes pages from.</summary>
/// <param name="Path">The path of the file.</param>
/// <param name="Password">The password of an encrypted file, or <see langword="null"/>.</param>
public sealed record PageSourceFile(string Path, string? Password);

/// <summary>
/// Builds the result of a <see cref="PageEditPlan"/> on a working copy and tells beforehand what the plan loses (milestone 13;
/// <c>Docs/DECISIONS.md</c>, "Seiten organisieren: Spike und QPDF-Entscheidung (M13)"). The copy is opened from the
/// <em>file</em> of the primary source, never from the document that is shown, so the shown document is not touched and
/// stays valid; only the copy is edited. Nothing here writes a file.
/// </summary>
/// <param name="engine">Opens the documents.</param>
public sealed class PageEditExecutor(PdfEngine engine)
{
    /// <summary>The most distinct field names a report keeps to find collisions; past it the report says "unchecked".</summary>
    private const int MaxTrackedFieldNames = 200_000;

    /// <summary>Gets or sets a hook called with the working copy right after it was opened (for tests: to see that it is released on failure).</summary>
    internal Action<PdfDocument>? WorkingCopyOpened { get; init; }

    /// <summary>
    /// Builds the result of <paramref name="plan"/>: opens a working copy of the file <paramref name="primary"/> and, in this
    /// order, imports the pages of the other files and the copies of duplicated pages (at the end, in batches of
    /// <see cref="PageEditLimits.ImportBatch"/>), deletes the pages the plan drops, brings the rest into the order of the plan
    /// (<c>FPDF_MovePages</c> in batches), inserts the blank pages and turns the pages. Every step is a short job on the PDFium
    /// thread; the token is checked between them. The result has exactly the pages of the plan in its order. Lesezeichen, form,
    /// page labels and information of the primary file stay (what the plan loses is told by <see cref="AnalyzeAsync"/>). The
    /// copy is for writing (<see cref="PdfDocument.SaveRewriteAsync"/>): it must not be rendered.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="primary">The file the plan was made for (the open document); its page count must be the plan's.</param>
    /// <param name="inserted">The file of each inserted source. A plan that uses more than <see cref="PageEditLimits.MaxInsertedFiles"/> files, and a dictionary with more entries, is refused before any file is opened.</param>
    /// <param name="progress">Receives the share of the steps done (0 to 1, after each step, never going back); or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels between two steps; whatever was opened is released.</param>
    /// <returns>The working copy; the caller disposes it.</returns>
    /// <exception cref="PageEditException">A limit is exceeded, a page of a source does not exist (the plan cannot know the page counts), a source has no file, or the primary file has another number of pages than the plan expects.</exception>
    /// <exception cref="PdfOpenException">A file cannot be opened, for example because of a wrong password; everything opened so far is released.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the working copy is released.</exception>
    public async Task<PdfDocument> BuildAsync(
        PageEditPlan plan,
        PageSourceFile primary,
        IReadOnlyDictionary<PageSourceId, PageSourceFile> inserted,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(inserted);
        var usage = PlanUsage.Of(plan);
        usage.EnsureFilesAreGiven(inserted.Keys, inserted.Count);

        PdfDocument? working = null;
        var sources = new Dictionary<int, PdfDocument>();
        try
        {
            working = await engine.OpenAsync(primary.Path, primary.Password, cancellationToken).ConfigureAwait(false);
            WorkingCopyOpened?.Invoke(working);
            if (working.PageCount != plan.PrimaryPageCount)
            {
                throw new PageEditException(PageEditError.OutOfRange, "The file has another number of pages than the plan was made for.");
            }

            foreach (var id in usage.InsertedIds)
            {
                var file = inserted[new PageSourceId(id)];
                var source = await engine.OpenAsync(file.Path, file.Password, cancellationToken).ConfigureAwait(false);
                sources[id] = source;
                if (usage.HighestIndex(id) >= source.PageCount)
                {
                    throw new PageEditException(PageEditError.OutOfRange, "A page of an inserted file does not exist.");
                }
            }

            await ExecuteAsync(working, plan, sources, progress, cancellationToken).ConfigureAwait(false);
            if (working.PageCount != plan.Slots.Count)
            {
                throw new InvalidOperationException("The working copy does not have the pages of the plan.");
            }

            return working;
        }
        catch
        {
            if (working is not null)
            {
                await working.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            foreach (var source in sources.Values)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

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

    private static async Task ExecuteAsync(PdfDocument working, PageEditPlan plan, Dictionary<int, PdfDocument> sources, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var slots = plan.Slots;
        var primaryCount = plan.PrimaryPageCount;

        // Where each slot's page is once the pages to import are appended: the first appearance of a primary page is the page
        // itself, every other appearance and every page of another file is an import at the end of the copy.
        var workIndex = new int[slots.Count];
        var keptPrimary = new bool[primaryCount];
        var imports = new List<(PdfDocument Source, int Index)>();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Blank is not null)
            {
                workIndex[i] = -1;
            }
            else if (slot.Source.Value == 0 && !keptPrimary[slot.SourceIndex])
            {
                keptPrimary[slot.SourceIndex] = true;
                workIndex[i] = slot.SourceIndex;
            }
            else
            {
                workIndex[i] = primaryCount + imports.Count;
                imports.Add((slot.Source.Value == 0 ? working : sources[slot.Source.Value], slot.SourceIndex));
            }
        }

        var deletedPrimary = new List<int>();
        var rankOfPrimary = new int[primaryCount];
        var keptCount = 0;
        for (var p = 0; p < primaryCount; p++)
        {
            rankOfPrimary[p] = keptCount;
            if (keptPrimary[p])
            {
                keptCount++;
            }
            else
            {
                deletedPrimary.Add(p);
            }
        }

        var desired = new List<int>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (workIndex[i] >= 0)
            {
                desired.Add(workIndex[i] < primaryCount ? rankOfPrimary[workIndex[i]] : keptCount + (workIndex[i] - primaryCount));
            }
        }

        // The imports, one run for each stretch of pages that come from the same source.
        var runs = new List<(PdfDocument Source, int[] Indices)>();
        for (var start = 0; start < imports.Count;)
        {
            var source = imports[start].Source;
            var end = start;
            while (end < imports.Count && ReferenceEquals(imports[end].Source, source))
            {
                end++;
            }

            var indices = new int[end - start];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = imports[start + i].Index;
            }

            runs.Add((source, indices));
            start = end;
        }

        var turned = slots.Count(s => s.ExtraRotation != 0);
        var blanks = slots.Count(s => s.Blank is not null);
        var steps = runs.Sum(r => Batches(r.Indices.Length)) + Batches(deletedPrimary.Count) + Batches(desired.Count) + blanks + turned;
        var done = 0;

        void Step()
        {
            done++;
            progress?.Report(Math.Min(1.0, (double)done / steps));
        }

        // 1. Import the pages of other files and the copies of duplicated pages, run by run (one source at a time), at the end.
        foreach (var (source, indices) in runs)
        {
            await working.ImportPagesAsync(source, indices, working.PageCount, _ => Step(), cancellationToken).ConfigureAwait(false);
        }

        // 2. Delete the pages of the primary file the plan drops. A plan of blank pages only keeps the first one back, so that
        // the copy never has no page.
        var preInserted = -1;
        if (keptCount == 0 && imports.Count == 0)
        {
            preInserted = 0;
            while (slots[preInserted].Blank is null)
            {
                preInserted++;
            }

            var first = slots[preInserted].Blank!;
            await working.InsertBlankPageAsync(working.PageCount, first.Width, first.Height, cancellationToken).ConfigureAwait(false);
            Step();
        }

        if (deletedPrimary.Count > 0)
        {
            await working.DeletePagesAsync(deletedPrimary, Step, cancellationToken).ConfigureAwait(false);
        }

        // 3. Bring the pages into the order of the plan, a batch at a time (the pages before the batch are final).
        await OrderAsync(working, desired, Step, cancellationToken).ConfigureAwait(false);

        // 4. Blank pages at their final positions, ascending: every position before is final then.
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Blank is { } blank && i != preInserted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await working.InsertBlankPageAsync(i, blank.Width, blank.Height, cancellationToken).ConfigureAwait(false);
                Step();
            }
        }

        // 5. Turn: the absolute rotation is the page's own plus the extra of the plan.
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].ExtraRotation != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await working.GetPageRotationAsync(i, cancellationToken).ConfigureAwait(false);
                await working.SetPageRotationAsync(i, (current + slots[i].ExtraRotation) % 360, cancellationToken).ConfigureAwait(false);
                Step();
            }
        }

        if (done < steps)
        {
            progress?.Report(1.0);
        }
    }

    /// <summary>
    /// Orders the pages of the copy (which are 0 to <c>desired.Count - 1</c> now) as <paramref name="desired"/> says, in batches
    /// of <see cref="PageEditLimits.ImportBatch"/> moves: batch by batch the pages of the next stretch go to its start, where
    /// the positions come from a model of the move (a list that is cut and pasted the way PDFium moves pages).
    /// </summary>
    private static async Task OrderAsync(PdfDocument working, List<int> desired, Action step, CancellationToken cancellationToken)
    {
        var current = Enumerable.Range(0, desired.Count).ToList();
        for (var start = 0; start < desired.Count; start += PageEditLimits.ImportBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(PageEditLimits.ImportBatch, desired.Count - start);
            var wanted = desired.GetRange(start, length);
            if (current.GetRange(start, length).SequenceEqual(wanted))
            {
                step();
                continue;
            }

            var position = new Dictionary<int, int>(current.Count);
            for (var i = 0; i < current.Count; i++)
            {
                position[current[i]] = i;
            }

            var indices = new int[length];
            for (var i = 0; i < length; i++)
            {
                indices[i] = position[wanted[i]];
            }

            await working.MovePagesAsync(indices, start, cancellationToken).ConfigureAwait(false);
            var moved = new HashSet<int>(wanted);
            current.RemoveAll(moved.Contains);
            current.InsertRange(start, wanted);
            step();
        }
    }

    private static int Batches(int count) => (count + PageEditLimits.ImportBatch - 1) / PageEditLimits.ImportBatch;

    /// <summary>What the slots of a plan use: the checks a plan cannot do for itself, and the positions of every source page.</summary>
    private sealed class PlanUsage
    {
        private readonly Dictionary<(int Source, int Index), List<int>> positions = [];
        private readonly Dictionary<int, int> highest = [];

        private PlanUsage()
        {
        }

        /// <summary>Gets the inserted sources the plan uses, ascending.</summary>
        public SortedSet<int> InsertedIds { get; } = [];

        /// <summary>Gets the pages of the open document no slot uses.</summary>
        public HashSet<int> DeletedPrimaryPages { get; } = [];

        /// <summary>Gets every (source, page) that a slot uses.</summary>
        public IEnumerable<(int Source, int Index)> Keys => positions.Keys;

        public List<int> PositionsOf((int Source, int Index) key) => positions[key];

        public int HighestIndex(int source) => highest[source];

        public static PlanUsage Of(PageEditPlan plan)
        {
            var slots = plan.Slots;
            if (slots.Count < 1 || slots.Count > PageEditLimits.MaxSlots)
            {
                throw new PageEditException(PageEditError.LimitExceeded, "A plan holds between 1 and the allowed number of pages.");
            }

            var usage = new PlanUsage();
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.ExtraRotation is not (0 or 90 or 180 or 270))
                {
                    throw new PageEditException(PageEditError.InvalidRotation, "The rotation of a page is 0, 90, 180 or 270 degrees.");
                }

                if (slot.Blank is { } blank)
                {
                    if (!(blank.Width > 0 && blank.Height > 0 && double.IsFinite(blank.Width) && double.IsFinite(blank.Height)))
                    {
                        throw new PageEditException(PageEditError.OutOfRange, "A blank page needs a positive, finite size.");
                    }

                    continue;
                }

                var source = slot.Source.Value;
                if (slot.SourceIndex < 0 || source < 0 || (source == 0 && slot.SourceIndex >= plan.PrimaryPageCount))
                {
                    throw new PageEditException(PageEditError.OutOfRange, "A page of the plan is not a page of its source.");
                }

                if (!usage.positions.TryGetValue((source, slot.SourceIndex), out var list))
                {
                    usage.positions[(source, slot.SourceIndex)] = list = [];
                }

                list.Add(i);
                usage.highest[source] = Math.Max(usage.highest.GetValueOrDefault(source, -1), slot.SourceIndex);
                if (source != 0)
                {
                    usage.InsertedIds.Add(source);
                }
            }

            if (usage.InsertedIds.Count > PageEditLimits.MaxInsertedFiles)
            {
                throw new PageEditException(PageEditError.LimitExceeded, "The plan takes pages from more files than allowed.");
            }

            for (var p = 0; p < plan.PrimaryPageCount; p++)
            {
                if (!usage.positions.ContainsKey((0, p)))
                {
                    usage.DeletedPrimaryPages.Add(p);
                }
            }

            return usage;
        }

        /// <summary>Refuses a set of files that is too large, or that lacks the file of a source the plan uses, before anything is opened.</summary>
        public void EnsureFilesAreGiven(IEnumerable<PageSourceId> given, int givenCount)
        {
            if (givenCount > PageEditLimits.MaxInsertedFiles)
            {
                throw new PageEditException(PageEditError.LimitExceeded, "More files than allowed were given.");
            }

            var known = given.Select(g => g.Value).ToHashSet();
            if (InsertedIds.Any(id => !known.Contains(id)))
            {
                throw new PageEditException(PageEditError.OutOfRange, "A source of the plan has no file.");
            }
        }
    }
}
