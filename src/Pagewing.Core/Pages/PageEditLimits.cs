namespace Pagewing.Core.Pages;

/// <summary>The limits of page editing (starting values, pinned by a test).</summary>
public static class PageEditLimits
{
    /// <summary>The most pages one <see cref="PageEditPlan"/> may hold; checked before anything is allocated.</summary>
    public const int MaxSlots = 20_000;

    /// <summary>The most different files one plan may take pages from (the open document itself does not count).</summary>
    public const int MaxInsertedFiles = 20;

    /// <summary>The most pages one short PDFium job imports (an import runs as a sequence of such batches).</summary>
    public const int ImportBatch = 50;
}
