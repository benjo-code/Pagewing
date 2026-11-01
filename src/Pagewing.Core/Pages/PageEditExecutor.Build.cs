using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

public sealed partial class PageEditExecutor
{
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
                var source = await file.OpenAsync(engine, cancellationToken).ConfigureAwait(false);
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

            // The sources are closed for us now (inside the try, so a source that fails to close cannot leak the copy). PDFium still
            // has them open: the working copy keeps each one that pages were imported from until it closes itself, because imported
            // form pages point into their source and the full rewrite of the copy reads them (PdfiumDocument.KeepOpenFor).
            await DisposeAllAsync(sources, rethrow: true).ConfigureAwait(false);
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
            // Failure path (the success path emptied the list): release what is still open, whatever a close throws.
            await DisposeAllAsync(sources, rethrow: false).ConfigureAwait(false);
        }
    }

    /// <summary>Closes every document (all of them, also when one fails) and empties the list; the first failure goes on if <paramref name="rethrow"/>.</summary>
    private static async Task DisposeAllAsync(Dictionary<int, PdfDocument> documents, bool rethrow)
    {
        Exception? first = null;
        foreach (var document in documents.Values)
        {
            try
            {
                await document.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                first ??= ex;
            }
        }

        documents.Clear();
        if (rethrow && first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

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
}
