using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Scaffold for redaction (milestone 14): reading and removing the page objects of a page of a document that is about to be
/// rewritten (a working copy, never a document that is shown). Only the thin native steps live here; what the spike found about
/// them is in <c>Docs/DECISIONS.md</c>, entry "Schwärzen: Spike und QPDF-Entscheidung (M14)". Every method must be called on the
/// PDFium thread and throws <see cref="PdfiumThreadViolationException"/> otherwise.
/// </summary>
/// <remarks>
/// A page opened here is a raw page like the one of <see cref="PdfiumPageEdit"/>: no form-fill environment is told about it, so a
/// document that was changed through it must not be rendered through <see cref="PdfiumDocument.LoadPage"/> afterwards (write it
/// with <see cref="PdfiumPageEdit.SaveRewrite"/> and reopen it). A page object that was removed belongs to the caller, who frees it
/// with <see cref="RawPage.Destroy"/>. Removing a text object invalidates every text page handle of the page.
/// </remarks>
internal static unsafe class PdfiumRedaction
{
    /// <summary>The most page objects <see cref="RawPage.Describe"/> reads from one page or form object.</summary>
    public const int MaxObjects = 100_000;

    /// <summary>The page of a document opened for object-level edits.</summary>
    public sealed class RawPage : IDisposable
    {
        private readonly PdfiumDocument document;
        private nint page;

        internal RawPage(PdfiumDocument document, nint page)
        {
            this.document = document;
            this.page = page;
        }

        /// <summary>The native document the page belongs to (checks the thread).</summary>
        public nint NativeDocument => document.Access();

        /// <summary>The number of page objects of the page.</summary>
        public int CountObjects() => Fpdf.FPDFPage_CountObjects(Access());

        /// <summary>The page object at an index.</summary>
        /// <exception cref="InvalidOperationException">PDFium has no object at the index.</exception>
        public nint GetObject(int index) =>
            Fpdf.FPDFPage_GetObject(Access(), index) is var value and not 0 ? value : throw new InvalidOperationException($"Page object {index} does not exist.");

        /// <summary>Takes a page object out of the page; the caller owns it afterwards.</summary>
        public bool Remove(nint pageObject) => Fpdf.FPDFPage_RemoveObject(Access(), pageObject) != 0;

        /// <summary>Inserts a page object at the end (the page owns it afterwards).</summary>
        public bool Insert(nint pageObject) => Fpdf.FPDFPage_InsertObject(Access(), pageObject) != 0;

        /// <summary>Inserts a page object at an index (the page owns it afterwards).</summary>
        public bool InsertAt(nint pageObject, int index) => Fpdf.FPDFPage_InsertObjectAtIndex(Access(), pageObject, (nuint)index) != 0;

        /// <summary>Writes the page objects back to the content stream of the page.</summary>
        public bool GenerateContent() => Fpdf.FPDFPage_GenerateContent(Access()) != 0;

        /// <summary>Frees a page object that no page owns (checks the thread).</summary>
        public void Destroy(nint pageObject)
        {
            _ = Access();
            Fpdf.FPDFPageObj_Destroy(pageObject);
        }

        /// <summary>Loads the text of the page; the caller disposes it.</summary>
        public nint LoadTextPage() => Fpdf.FPDFText_LoadPage(Access());

        /// <summary>Closes a text page opened with <see cref="LoadTextPage"/>.</summary>
        public void CloseTextPage(nint textPage)
        {
            _ = Access();
            Fpdf.FPDFText_ClosePage(textPage);
        }

        /// <summary>The type (<c>FPDF_PAGEOBJ_*</c>) and the bounds of a page object.</summary>
        public (int Type, float Left, float Bottom, float Right, float Top) Describe(nint pageObject)
        {
            _ = Access();
            float left = 0;
            float bottom = 0;
            float right = 0;
            float top = 0;
            _ = Fpdf.FPDFPageObj_GetBounds(pageObject, &left, &bottom, &right, &top);
            return (Fpdf.FPDFPageObj_GetType(pageObject), left, bottom, right, top);
        }

        /// <summary>The text of a text object as <see cref="Fpdf.FPDFTextObj_GetText"/> returns it, with the text page of its page.</summary>
        public string ReadText(nint textObject, nint textPage)
        {
            _ = Access();
            var bytes = Fpdf.FPDFTextObj_GetText(textObject, textPage, null, new CULong(0)).Value;
            if (bytes < 2)
            {
                return string.Empty;
            }

            var buffer = new ushort[(int)(bytes / 2)];
            fixed (ushort* pointer = buffer)
            {
                _ = Fpdf.FPDFTextObj_GetText(textObject, textPage, pointer, new CULong(bytes));
            }

            var length = buffer.Length - 1;
            return string.Create(length, buffer, static (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = (char)source[i];
                }
            });
        }

        /// <summary>Reads the matrix of a page object (for a text object: the text matrix with the position of its first character).</summary>
        public bool TryGetMatrix(nint pageObject, out FsMatrix matrix)
        {
            _ = Access();
            FsMatrix value = default;
            var ok = Fpdf.FPDFPageObj_GetMatrix(pageObject, &value) != 0;
            matrix = value;
            return ok;
        }

        /// <summary>Sets the matrix of a page object.</summary>
        public bool SetMatrix(nint pageObject, FsMatrix matrix)
        {
            _ = Access();
            return Fpdf.FPDFPageObj_SetMatrix(pageObject, &matrix) != 0;
        }

        /// <summary>The number of page objects inside a form object, -1 on error.</summary>
        public int CountFormObjects(nint formObject)
        {
            _ = Access();
            return Fpdf.FPDFFormObj_CountObjects(formObject);
        }

        /// <summary>The page object of a form object at an index.</summary>
        public nint GetFormObject(nint formObject, int index)
        {
            _ = Access();
            return Fpdf.FPDFFormObj_GetObject(formObject, new CULong((nuint)index));
        }

        /// <summary>Takes a page object out of a form object; the caller owns it afterwards.</summary>
        public bool RemoveFromForm(nint formObject, nint pageObject)
        {
            _ = Access();
            return Fpdf.FPDFFormObj_RemoveObject(formObject, pageObject) != 0;
        }

        /// <summary>The native page (checks the thread and that the page is open).</summary>
        public nint Access()
        {
            document.Thread.VerifyAccess();
            ObjectDisposedException.ThrowIf(page == 0, this);
            return page;
        }

        /// <summary>Closes the page. Must be called on the PDFium thread.</summary>
        public void Dispose()
        {
            if (page == 0)
            {
                return;
            }

            document.Thread.VerifyAccess();
            var value = page;
            page = 0;
            Fpdf.FPDF_ClosePage(value);
        }
    }

    /// <summary>Opens the page <paramref name="index"/> for object-level edits.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page.</exception>
    public static RawPage OpenPage(PdfiumDocument document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Fpdf.FPDF_GetPageCount(native));
        var page = Fpdf.FPDF_LoadPage(native, index);
        return page != 0 ? new RawPage(document, page) : throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
    }
}
