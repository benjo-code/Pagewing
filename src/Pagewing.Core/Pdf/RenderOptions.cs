namespace Pagewing.Core.Pdf;

/// <summary>How a page or tile is rendered.</summary>
public sealed record RenderOptions
{
    /// <summary>Gets the default options.</summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether annotations are drawn, popups excepted. Default: on. Since milestone 11 this includes
    /// the form fields of a document with a form: PDFium draws them only through <c>FPDF_FFLDraw</c>, which the render calls
    /// after the page for every page with widgets.
    /// </summary>
    public bool Annotations { get; init; } = true;

    /// <summary>Gets the scheduling class. Default: <see cref="PdfPriority.Interactive"/>.</summary>
    public PdfPriority Priority { get; init; } = PdfPriority.Interactive;

    /// <summary>
    /// Gets how long the render may run before it yields to other work on the PDFium thread; it resumes
    /// afterwards. <see cref="TimeSpan.Zero"/> yields at every chance. Must not be negative. Default: 4 ms.
    /// </summary>
    public TimeSpan TimeSlice { get; init; } = TimeSpan.FromMilliseconds(4);

    /// <summary>Gets how the page is coloured. Default: <see cref="PageTheme.Light"/>, the page as the document says.</summary>
    public PageTheme PageTheme { get; init; } = PageTheme.Light;

    /// <summary>
    /// Gets a value indicating whether the fillable form fields are tinted with <see cref="FormHighlightColors"/> (drawn by
    /// PDFium; read-only fields, signatures and push buttons get no tint). Only with <see cref="Annotations"/>. Default: off.
    /// </summary>
    public bool FormHighlight { get; init; }
}

/// <summary>Scheduling class of a request on the PDFium thread.</summary>
public enum PdfPriority
{
    /// <summary>Visible to the user right now; runs before any background work.</summary>
    Interactive,

    /// <summary>Prefetching and similar work; yields to interactive work between its time slices.</summary>
    Background,
}
