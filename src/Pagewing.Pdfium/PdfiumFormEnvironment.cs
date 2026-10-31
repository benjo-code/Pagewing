using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// PDFium's form-fill environment of one document (<c>FPDF_FORMHANDLE</c>), which reads, writes and draws its AcroForm fields.
/// Every member except <see cref="IsDisposed"/> and <see cref="LoadedPages"/> must be called on the PDFium thread and throws
/// <see cref="PdfiumThreadViolationException"/> otherwise; there is never a second environment of a document on another thread.
/// </summary>
/// <remarks>
/// <para>
/// The environment exists only for a document with a form (<c>FPDF_GetFormType</c> is not <c>FORMTYPE_NONE</c>, XFA types
/// included) and is created by <see cref="PdfiumDocument.Open"/> in the open job. Its <c>FPDF_FORMFILLINFO</c> is allocated
/// natively with <see cref="Version"/> 1 and every pointer null (no callback, so no <c>[UnmanagedCallersOnly]</c> method and
/// no action of a field ever runs), and lives until after <c>FPDFDOC_ExitFormFillEnvironment</c>.
/// </para>
/// <para>
/// Lifetime order, kept by the handles: every page loaded by <see cref="PdfiumDocument.LoadPage"/> is announced with
/// <see cref="PageLoaded"/>, and <c>PageHandle</c> calls <see cref="PageClosing"/> before <c>FPDF_ClosePage</c>; the
/// <c>DocumentHandle</c> disposes the environment before <c>FPDF_CloseDocument</c>, which runs only after its last page is
/// closed (each page holds a reference on the document handle). Spike: Docs/DECISIONS.md, "Formulare: Formular-Engine,
/// Hybrid-Architektur, Lebensdauer der Umgebung (M11)".
/// </para>
/// </remarks>
internal sealed unsafe class PdfiumFormEnvironment : IDisposable
{
    /// <summary>The interface version passed in <c>FPDF_FORMFILLINFO</c>: only the stable interfaces are called.</summary>
    internal const int Version = 1;

    private readonly PdfiumThread thread;
    private readonly nint handle;
    private FpdfFormFillInfo* info;
    private volatile bool disposed;
    private int loadedPages;

    private PdfiumFormEnvironment(PdfiumThread thread, nint handle, FpdfFormFillInfo* info)
    {
        this.thread = thread;
        this.handle = handle;
        this.info = info;
    }

    /// <summary>Gets the <c>FPDF_FORMHANDLE</c>, for PDFium calls on the PDFium thread.</summary>
    /// <exception cref="ObjectDisposedException">The environment was exited.</exception>
    public nint Handle
    {
        get
        {
            VerifyOpen();
            return handle;
        }
    }

    /// <summary>Gets whether the environment was exited. Any thread.</summary>
    internal bool IsDisposed => disposed;

    /// <summary>
    /// Gets how many pages are announced to the environment and not yet announced closed (for tests of the closing order). Any
    /// thread; written on the PDFium thread only.
    /// </summary>
    internal int LoadedPages => Volatile.Read(ref loadedPages);

    /// <summary>Gets a copy of the <c>FPDF_FORMFILLINFO</c> handed to PDFium (for tests).</summary>
    /// <exception cref="ObjectDisposedException">The environment was exited.</exception>
    internal FpdfFormFillInfo FillInfo
    {
        get
        {
            VerifyOpen();
            return *info;
        }
    }

    /// <summary>
    /// Creates the environment of <paramref name="document"/> if it has a form, else returns <see langword="null"/> (a document
    /// without a form never gets one). Also <see langword="null"/> if PDFium cannot create it; the document then shows its
    /// fields as it did before milestone 11. Creating it is cheap (about 1 ms at 2,000 fields); the fields are loaded by the
    /// first <see cref="PageLoaded"/>.
    /// </summary>
    public static PdfiumFormEnvironment? CreateIfForm(PdfiumDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var native = document.Access();
        if (Fpdf.FPDF_GetFormType(native) == FpdfConstants.FormTypeNone)
        {
            return null;
        }

        // Zeroed: every callback and action pointer stays null.
        var info = (FpdfFormFillInfo*)NativeMemory.AllocZeroed((nuint)sizeof(FpdfFormFillInfo));
        info->Version = Version;
        nint form;
        try
        {
            form = Fpdf.FPDFDOC_InitFormFillEnvironment(native, info);
        }
        catch
        {
            NativeMemory.Free(info);
            throw;
        }

        if (form == 0)
        {
            NativeMemory.Free(info);
            return null;
        }

        return new PdfiumFormEnvironment(document.Thread, form, info);
    }

    /// <summary>
    /// Announces a page just loaded with <c>FPDF_LoadPage</c> (<c>FORM_OnAfterLoadPage</c>); called by
    /// <see cref="PdfiumDocument.LoadPage"/> only.
    /// </summary>
    /// <remarks>
    /// The first call of a document loads all fields of the whole document (spike: 2 to 6 ms at 100 fields, 72 to 91 ms at
    /// 2,000), every later one 0.2 to 1.6 ms. The environment maps each field to the widget of the page view loaded <b>last</b>:
    /// a page handle that has drawn before a value was written must not draw again after it (an older view of the same page
    /// keeps the old picture of fields that had no appearance stream). Load a new page after every write.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The environment was exited.</exception>
    public void PageLoaded(nint page)
    {
        VerifyOpen();
        Fpdf.FORM_OnAfterLoadPage(page, handle);
        Volatile.Write(ref loadedPages, loadedPages + 1);
    }

    /// <summary>
    /// Announces that a page announced with <see cref="PageLoaded"/> is about to close (<c>FORM_OnBeforeClosePage</c>); called
    /// by <c>PageHandle</c> right before <c>FPDF_ClosePage</c>. Does nothing once the environment was exited, which the handle
    /// order prevents.
    /// </summary>
    public void PageClosing(nint page)
    {
        thread.VerifyAccess();
        if (disposed)
        {
            return;
        }

        Fpdf.FORM_OnBeforeClosePage(page, handle);
        Volatile.Write(ref loadedPages, loadedPages - 1);
    }

    /// <summary>
    /// Exits the environment (<c>FPDFDOC_ExitFormFillEnvironment</c>) and frees its <c>FPDF_FORMFILLINFO</c> afterwards. Called
    /// by <c>DocumentHandle</c> right before <c>FPDF_CloseDocument</c>, after the last page closed; nobody else calls it.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        thread.VerifyAccess();
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            Fpdf.FPDFDOC_ExitFormFillEnvironment(handle);
        }
        finally
        {
            // The header requires the structure to stay valid until the handle is closed, so it is freed only now.
            NativeMemory.Free(info);
            info = null;
        }
    }

    private void VerifyOpen()
    {
        thread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
