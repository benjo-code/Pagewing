namespace Pagewing.Pdfium;

/// <summary>Scheduling class of a work item on the <see cref="PdfiumThread"/>.</summary>
internal enum PdfiumPriority
{
    /// <summary>User-visible work that runs before any background work.</summary>
    Interactive,

    /// <summary>Work that yields to interactive work between its steps.</summary>
    Background,
}
