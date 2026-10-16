namespace Pagewing.Core.Pdf;

/// <summary>How a page or tile is rendered.</summary>
public sealed record RenderOptions
{
    /// <summary>Gets the default options.</summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether annotations (except form fields and popups) are drawn. Default: on.</summary>
    public bool Annotations { get; init; } = true;

    /// <summary>Gets the scheduling class. Default: <see cref="PdfPriority.Interactive"/>.</summary>
    public PdfPriority Priority { get; init; } = PdfPriority.Interactive;

    /// <summary>
    /// Gets how long the render may run before it yields to other work on the PDFium thread; it resumes
    /// afterwards. <see cref="TimeSpan.Zero"/> yields at every chance. Must not be negative. Default: 4 ms.
    /// </summary>
    public TimeSpan TimeSlice { get; init; } = TimeSpan.FromMilliseconds(4);
}

/// <summary>Scheduling class of a request on the PDFium thread.</summary>
public enum PdfPriority
{
    /// <summary>Visible to the user right now; runs before any background work.</summary>
    Interactive,

    /// <summary>Prefetching and similar work; yields to interactive work between its time slices.</summary>
    Background,
}
