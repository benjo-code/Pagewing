namespace Pagewing.Pdfium;

/// <summary>What a link on a page leads to.</summary>
internal enum PdfiumLinkKind
{
    /// <summary>A page of the same document.</summary>
    Page,

    /// <summary>A URI (from a link annotation or a web address PDFium found in the text). The scheme is not checked.</summary>
    Uri,

    /// <summary>Anything else (launch, remote go-to, JavaScript, invalid targets); never followed.</summary>
    Unsupported,
}

/// <summary>
/// A link of a page: a link annotation or a web address PDFium detected in the text.
/// </summary>
/// <remarks>
/// The generated equality compares <see cref="Rects"/> by list reference, not by content: two links read
/// separately are never equal. Compare the members when value equality is needed.
/// </remarks>
/// <param name="Rects">The clickable areas in page points, origin at the top left of the displayed page.</param>
/// <param name="Kind">What the link leads to.</param>
/// <param name="PageIndex">The 0-based target page when <paramref name="Kind"/> is <see cref="PdfiumLinkKind.Page"/>, otherwise -1.</param>
/// <param name="Top">
/// For <see cref="PdfiumLinkKind.Page"/>: the target position in points from the top of the target page, when the
/// destination sets one; otherwise <see langword="null"/>.
/// </param>
/// <param name="Uri">The address when <paramref name="Kind"/> is <see cref="PdfiumLinkKind.Uri"/>, otherwise <see langword="null"/>.</param>
internal readonly record struct PdfiumLink(
    IReadOnlyList<(double Left, double Top, double Right, double Bottom)> Rects,
    PdfiumLinkKind Kind,
    int PageIndex,
    double? Top,
    string? Uri);
