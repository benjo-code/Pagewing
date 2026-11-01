using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Page operations on a document that is about to be rewritten (a working copy, never a document that is shown): import,
/// delete, move, rotate, insert a blank page, and the full rewrite (and a read of the internal links of a page, which also works on
/// a document that is shown). Every method must be called on the PDFium thread and
/// throws <see cref="PdfiumThreadViolationException"/> otherwise; every page handle is closed before a method returns.
/// </summary>
/// <remarks>
/// <para>
/// A document that was changed here must not be rendered or read through <see cref="PdfiumDocument.LoadPage"/> afterwards:
/// the per-page widget cache of <see cref="PdfiumDocument"/> and the form-fill environment are keyed by page index and do not
/// follow the changes. The result is written with <see cref="SaveRewrite"/> and reopened.
/// </para>
/// <para>
/// <see cref="Import"/> makes the destination keep the source open until the destination itself is closed
/// (<see cref="PdfiumDocument.KeepOpenFor"/>): pages with widgets keep objects that point into the source, and the rewrite
/// reads them (<c>FPDF_SaveAsCopy</c> crashed with an access violation when the source was closed before). Closing the
/// source early only closes it for the caller.
/// </para>
/// <para>
/// PDFium reports a failure of the import, the move and the creation of a page as <c>FALSE</c> or a null handle, which these
/// methods turn into <see cref="InvalidOperationException"/>; deleting a page and setting a rotation have no result, so the
/// index is checked here first (<see cref="ArgumentOutOfRangeException"/>). What the operations keep of the outline, the page
/// labels, the form and the metadata is the subject of <c>Docs/DECISIONS.md</c>, entry "Seiten organisieren: Spike und
/// QPDF-Entscheidung (M13)".
/// </para>
/// </remarks>
internal static unsafe class PdfiumPageEdit
{
    /// <summary>The most annotations of one page <see cref="ReadInternalLinkTargets"/> looks at (the same cap as the annotation reader).</summary>
    public const int MaxLinksScanned = PdfiumAnnotations.MaxScannedPerPage;

    /// <summary>
    /// Copies the pages <paramref name="indices"/> of <paramref name="source"/>, in this order (an index may repeat), into
    /// <paramref name="dest"/> so that the first one gets the index <paramref name="destination"/>. <paramref name="source"/>
    /// may be <paramref name="dest"/> (duplicating pages).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="indices"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of <paramref name="source"/>, or <paramref name="destination"/> is not between 0 and the page count of <paramref name="dest"/>.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused the import.</exception>
    public static void Import(PdfiumDocument dest, PdfiumDocument source, ReadOnlySpan<int> indices, int destination)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(source);
        var destinationDocument = dest.Access();
        var sourceDocument = source.Access();
        if (indices.IsEmpty)
        {
            throw new ArgumentException("At least one page must be imported.", nameof(indices));
        }

        var sourceCount = Fpdf.FPDF_GetPageCount(sourceDocument);
        foreach (var index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, sourceCount);
        }

        CheckInsertPosition(destinationDocument, destination);

        // The imported pages of a form point into the source until the destination is closed (see DocumentHandle.KeepOpenUntilClosed).
        // Before the call, whatever it does: a refused import may have copied part of the pages.
        if (!ReferenceEquals(dest, source))
        {
            dest.KeepOpenFor(source);
        }

        fixed (int* pointer = indices)
        {
            if (Fpdf.FPDF_ImportPagesByIndex(destinationDocument, sourceDocument, pointer, new CULong((nuint)indices.Length), destination) == 0)
            {
                throw new InvalidOperationException("PDFium could not import the pages.");
            }
        }
    }

    /// <summary>Deletes the page <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    public static void Delete(PdfiumDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        CheckPageIndex(native, index);
        Fpdf.FPDFPage_Delete(native, index);
    }

    /// <summary>
    /// Moves the pages <paramref name="indices"/> (ascending or in the wanted order, without duplicates) so that the first one
    /// gets the index <paramref name="destination"/> in the document without the moved pages (<c>FPDF_MovePages</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="indices"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused the move (a duplicate, or a destination out of range).</exception>
    public static void Move(PdfiumDocument document, ReadOnlySpan<int> indices, int destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        if (indices.IsEmpty)
        {
            throw new ArgumentException("At least one page must be moved.", nameof(indices));
        }

        var count = Fpdf.FPDF_GetPageCount(native);
        foreach (var index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        }

        fixed (int* pointer = indices)
        {
            if (Fpdf.FPDF_MovePages(native, pointer, new CULong((nuint)indices.Length), destination) == 0)
            {
                throw new InvalidOperationException("PDFium could not move the pages.");
            }
        }
    }

    /// <summary>Sets the absolute rotation of the page <paramref name="index"/> (<c>/Rotate</c>).</summary>
    /// <param name="document">The document.</param>
    /// <param name="index">The page.</param>
    /// <param name="degrees">0, 90, 180 or 270 (clockwise).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page, or <paramref name="degrees"/> is not 0, 90, 180 or 270.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page.</exception>
    public static void SetRotation(PdfiumDocument document, int index, int degrees)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        CheckPageIndex(native, index);
        if (degrees is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The rotation must be 0, 90, 180 or 270.");
        }

        var page = LoadRaw(native, index);
        try
        {
            Fpdf.FPDFPage_SetRotation(page, degrees / 90);
        }
        finally
        {
            Fpdf.FPDF_ClosePage(page);
        }
    }

    /// <summary>Gets the absolute rotation of the page <paramref name="index"/> in degrees (0, 90, 180 or 270).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page.</exception>
    public static int GetRotation(PdfiumDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        CheckPageIndex(native, index);
        var page = LoadRaw(native, index);
        try
        {
            return Fpdf.FPDFPage_GetRotation(page) * 90;
        }
        finally
        {
            Fpdf.FPDF_ClosePage(page);
        }
    }

    /// <summary>Inserts an empty page of <paramref name="width"/> x <paramref name="height"/> points at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not between 0 and the page count, or a size is not a positive finite number.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page.</exception>
    public static void InsertBlank(PdfiumDocument document, int index, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        CheckInsertPosition(native, index);
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be a positive number.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be a positive number.");
        }

        var page = Fpdf.FPDFPage_New(native, index, width, height);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not create the page.");
        }

        Fpdf.FPDF_ClosePage(page);
    }

    /// <summary>
    /// Writes the whole document to <paramref name="destination"/> (<c>FPDF_NO_INCREMENTAL</c>) without removing its
    /// encryption (never <c>FPDF_REMOVE_SECURITY</c>).
    /// </summary>
    /// <exception cref="IOException">PDFium could not save the document, or the stream could not be written.</exception>
    public static void SaveRewrite(PdfiumDocument document, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.SaveRewrite(destination);
    }

    /// <summary>
    /// Reads where the internal links (link annotations whose action is a go-to, or that have a destination and no action) of
    /// the page <paramref name="index"/> lead: the 0-based target page, or -1 if PDFium cannot resolve the destination. Links
    /// to other files, addresses and every other action are not internal and are left out. At most
    /// <see cref="MaxLinksScanned"/> annotations of the page are looked at (the page of a hostile file cannot keep the thread).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page.</exception>
    public static int[] ReadInternalLinkTargets(PdfiumDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        CheckPageIndex(native, index);
        var count = Fpdf.FPDF_GetPageCount(native);
        var page = LoadRaw(native, index);
        try
        {
            var targets = new List<int>();
            var position = 0;
            nint link;
            for (var scanned = 0; scanned < MaxLinksScanned && Fpdf.FPDFLink_Enumerate(page, &position, &link) != 0; scanned++)
            {
                if (link == 0)
                {
                    continue;
                }

                nint dest;
                var action = Fpdf.FPDFLink_GetAction(link);
                if (action != 0)
                {
                    if (Fpdf.FPDFAction_GetType(action).Value != FpdfConstants.ActionGoTo)
                    {
                        continue;
                    }

                    dest = Fpdf.FPDFAction_GetDest(native, action);
                }
                else
                {
                    dest = Fpdf.FPDFLink_GetDest(native, link);
                    if (dest == 0)
                    {
                        continue;
                    }
                }

                var target = dest == 0 ? -1 : Fpdf.FPDFDest_GetDestPageIndex(native, dest);
                targets.Add(target >= 0 && target < count ? target : -1);
            }

            return [.. targets];
        }
        finally
        {
            Fpdf.FPDF_ClosePage(page);
        }
    }

    private static void CheckPageIndex(nint document, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Fpdf.FPDF_GetPageCount(document));
    }

    private static void CheckInsertPosition(nint document, int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Fpdf.FPDF_GetPageCount(document));
    }

    private static nint LoadRaw(nint document, int index)
    {
        // The raw page, not PdfiumDocument.LoadPage: no form-fill environment is told about it (the copy is never rendered).
        var page = Fpdf.FPDF_LoadPage(document, index);
        return page != 0 ? page : throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
    }
}
