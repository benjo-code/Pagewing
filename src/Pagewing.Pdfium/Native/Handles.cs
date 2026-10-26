using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pagewing.Pdfium.Native;

/// <summary>
/// Base class for PDFium object handles. The native object is always closed on the owning
/// <see cref="PdfiumThread"/>: directly when released there, otherwise the close is posted to it (for example
/// when the handle is disposed on another thread or finalized). A child handle (page, text page) holds a
/// reference on its parent until the child itself is closed, so a document never closes before its pages.
/// </summary>
/// <remarks>
/// Create the handle first, then call the native function and pass its result to <see cref="Attach"/>. If the
/// native call fails, the handle stays invalid; disposing it still releases its parent and owned resources.
/// </remarks>
internal abstract class PdfiumHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private PdfiumHandle? parent;

    protected PdfiumHandle(PdfiumThread thread, PdfiumHandle? parent)
        : base(ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(thread);
        OwningThread = thread;
        if (parent is not null)
        {
            var added = false;
            parent.DangerousAddRef(ref added);
            this.parent = parent;
        }
    }

    /// <summary>Gets the thread on which the native object is used and closed.</summary>
    public PdfiumThread OwningThread { get; }

    /// <summary>Takes ownership of a native object returned by PDFium (0 leaves the handle invalid).</summary>
    public void Attach(nint value)
    {
        if (IsClosed || !IsInvalid)
        {
            throw new InvalidOperationException("The handle is already attached or closed.");
        }

        SetHandle(value);
    }

    /// <summary>Closes the native object. Always runs on <see cref="OwningThread"/>.</summary>
    protected abstract void CloseNative(nint value);

    /// <summary>
    /// Releases what the handle owns besides the native object. Runs once after <see cref="CloseNative"/>, or
    /// on dispose or finalization when no native object was attached. Must be idempotent and safe on any thread.
    /// </summary>
    protected virtual void ReleaseOwnedResources()
    {
    }

    protected sealed override bool ReleaseHandle()
    {
        var value = handle;
        if (OwningThread.IsCurrent)
        {
            Close(value);
        }
        else
        {
            OwningThread.Post(() => Close(value));
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        // Without a native object ReleaseHandle never runs, so release what the handle holds here.
        var neverAttached = IsInvalid;
        base.Dispose(disposing);
        if (neverAttached)
        {
            try
            {
                ReleaseOwnedResources();
            }
            finally
            {
                ReleaseParent();
            }
        }
    }

    private void Close(nint value)
    {
        try
        {
            CloseNative(value);
        }
        finally
        {
            try
            {
                ReleaseOwnedResources();
            }
            finally
            {
                ReleaseParent();
            }
        }
    }

    private void ReleaseParent() => Interlocked.Exchange(ref parent, null)?.DangerousRelease();
}

/// <summary>
/// An <c>FPDF_DOCUMENT</c>. Before <c>FPDF_CloseDocument</c> it exits the form-fill environment (if the document has one);
/// after it, it frees the native <see cref="FpdfFileAccess"/> and disposes the read context that PDFium's read callback uses.
/// </summary>
internal sealed unsafe class DocumentHandle(PdfiumThread thread) : PdfiumHandle(thread, null)
{
    private nint fileAccess;
    private IDisposable? ownedContext;
    private PdfiumFormEnvironment? formEnvironment;

    /// <summary>
    /// Gets or sets the form-fill environment of the document, or <see langword="null"/> without a form. Set once, on the
    /// PDFium thread, right after the document was attached. Closing the handle exits it before the document is closed, which
    /// happens only after every page is closed (each <see cref="PageHandle"/> holds a reference on this handle).
    /// </summary>
    public PdfiumFormEnvironment? FormEnvironment
    {
        get => Volatile.Read(ref formEnvironment);
        set => Volatile.Write(ref formEnvironment, value);
    }

    /// <summary>
    /// Gets or sets the <see cref="FpdfFileAccess"/> passed to <c>FPDF_LoadCustomDocument</c>. It must be
    /// allocated with <see cref="NativeMemory"/>; the handle frees it with <see cref="NativeMemory.Free"/>.
    /// </summary>
    public FpdfFileAccess* FileAccess
    {
        get => (FpdfFileAccess*)Volatile.Read(ref fileAccess);
        set => Volatile.Write(ref fileAccess, (nint)value);
    }

    /// <summary>
    /// Gets or sets the read context (for example the file handle and the <see cref="GCHandle"/> passed as
    /// <see cref="FpdfFileAccess.Param"/>). It is disposed after the document is closed and must be safe to
    /// dispose on any thread.
    /// </summary>
    public IDisposable? OwnedContext
    {
        get => Volatile.Read(ref ownedContext);
        set => Volatile.Write(ref ownedContext, value);
    }

    protected override void CloseNative(nint value)
    {
        try
        {
            // FPDFDOC_ExitFormFillEnvironment before FPDF_CloseDocument (fpdf_formfill.h).
            Interlocked.Exchange(ref formEnvironment, null)?.Dispose();
        }
        finally
        {
            Fpdf.FPDF_CloseDocument(value);
        }
    }

    protected override void ReleaseOwnedResources()
    {
        var access = Interlocked.Exchange(ref fileAccess, 0);
        try
        {
            Interlocked.Exchange(ref ownedContext, null)?.Dispose();
        }
        finally
        {
            if (access != 0)
            {
                NativeMemory.Free((void*)access);
            }
        }
    }
}

/// <summary>
/// An <c>FPDF_PAGE</c>; keeps its document open until the page is closed. A page announced to the form-fill environment
/// leaves it (<c>FORM_OnBeforeClosePage</c>) right before <c>FPDF_ClosePage</c>.
/// </summary>
internal sealed class PageHandle(PdfiumThread thread, DocumentHandle document) : PdfiumHandle(thread, document)
{
    private PdfiumFormEnvironment? formEnvironment;

    /// <summary>
    /// Gets or sets the form-fill environment the page is announced to, or <see langword="null"/>. Set on the PDFium thread
    /// by <see cref="PdfiumDocument.LoadPage"/> before it calls <see cref="PdfiumFormEnvironment.PageLoaded"/>.
    /// </summary>
    public PdfiumFormEnvironment? FormEnvironment
    {
        get => Volatile.Read(ref formEnvironment);
        set => Volatile.Write(ref formEnvironment, value);
    }

    protected override void CloseNative(nint value)
    {
        try
        {
            // FORM_OnBeforeClosePage before FPDF_ClosePage (fpdf_formfill.h).
            Interlocked.Exchange(ref formEnvironment, null)?.PageClosing(value);
        }
        finally
        {
            Fpdf.FPDF_ClosePage(value);
        }
    }
}

/// <summary>An <c>FPDF_TEXTPAGE</c>; keeps its page open until the text page is closed.</summary>
internal sealed class TextPageHandle(PdfiumThread thread, PageHandle page) : PdfiumHandle(thread, page)
{
    protected override void CloseNative(nint value) => Fpdf.FPDFText_ClosePage(value);
}

/// <summary>An <c>FPDF_BITMAP</c>.</summary>
internal sealed class BitmapHandle(PdfiumThread thread) : PdfiumHandle(thread, null)
{
    protected override void CloseNative(nint value) => Fpdf.FPDFBitmap_Destroy(value);
}
