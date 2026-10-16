using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// An open PDF document. Every member except <see cref="Dispose"/> must be called on <see cref="Thread"/> and
/// throws <see cref="PdfiumThreadViolationException"/> otherwise. The file is read lazily through a read
/// callback and stays open (shared for reading, writing and deleting) until the document and all its pages
/// are closed.
/// </summary>
internal sealed unsafe class PdfiumDocument : IDisposable
{
    /// <summary>
    /// The largest file that can be opened. <c>FPDF_FILEACCESS.m_FileLen</c> is a C <c>unsigned long</c>, which
    /// is 32-bit on Windows; the same limit applies on every OS so behaviour does not differ.
    /// </summary>
    internal const long MaxFileLength = uint.MaxValue;

    private readonly DocumentHandle handle;
    private volatile bool disposed;

    private PdfiumDocument(DocumentHandle handle) => this.handle = handle;

    /// <summary>Gets the PDFium thread the document belongs to.</summary>
    public PdfiumThread Thread => handle.OwningThread;

    /// <summary>Gets the number of pages.</summary>
    public int PageCount => Fpdf.FPDF_GetPageCount(Access());

    /// <summary>Gets the PDF version from the file header (14 for 1.4), or <see langword="null"/> if unknown.</summary>
    public int? FileVersion
    {
        get
        {
            var document = Access();
            int version;
            return Fpdf.FPDF_GetFileVersion(document, &version) != 0 ? version : null;
        }
    }

    /// <summary>
    /// Opens a document. Must be called on <paramref name="thread"/>. A <see langword="null"/> password is
    /// passed as an empty one; passwords are passed as UTF-8.
    /// </summary>
    /// <exception cref="PdfiumOpenException">PDFium rejected the file (see <see cref="PdfiumOpenException.Error"/>).</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied, or the path is a directory.</exception>
    /// <exception cref="IOException">The file cannot be read or is larger than <see cref="MaxFileLength"/>.</exception>
    public static PdfiumDocument Open(PdfiumThread thread, string path, string? password)
    {
        ArgumentNullException.ThrowIfNull(thread);
        thread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(path);

        var handle = new DocumentHandle(thread);
        try
        {
            var context = FileReadContext.Open(path);
            handle.OwnedContext = context;
            var access = (FpdfFileAccess*)NativeMemory.Alloc((nuint)sizeof(FpdfFileAccess));
            handle.FileAccess = access;
            access->FileLen = new CULong((nuint)context.Length);
            access->GetBlock = &ReadBlock;
            access->Param = (void*)context.Cookie;

            var document = LoadDocument(access, password);
            if (document == 0)
            {
                throw new PdfiumOpenException(ToError(Fpdf.FPDF_GetLastError().Value));
            }

            try
            {
                handle.Attach(document);
            }
            catch
            {
                // The handle does not own the document, so close it here before the file access is freed.
                Fpdf.FPDF_CloseDocument(document);
                throw;
            }

            return new PdfiumDocument(handle);
        }
        catch
        {
            // Not attached: frees the file access and the read context. Attached: closes the document first.
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads an entry of the document information dictionary (for example <c>Title</c>, <c>CreationDate</c>).
    /// Returns <see langword="null"/> when the entry is missing or empty.
    /// </summary>
    public string? GetMetaText(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var document = Access();
        var tagBytes = new byte[Encoding.UTF8.GetByteCount(tag) + 1];
        Encoding.UTF8.GetBytes(tag, tagBytes);
        fixed (byte* tagPointer = tagBytes)
        {
            // The length is in bytes of UTF-16LE and includes the 2-byte terminator.
            var length = Fpdf.FPDF_GetMetaText(document, tagPointer, null, new CULong(0)).Value;
            if (length <= 2 || length > int.MaxValue)
            {
                return null;
            }

            var buffer = new byte[(int)length];
            nuint written;
            fixed (byte* bufferPointer = buffer)
            {
                written = Fpdf.FPDF_GetMetaText(document, tagPointer, bufferPointer, new CULong(length)).Value;
            }

            var byteCount = (int)Math.Min(written, length) - 2;
            return byteCount > 0 ? Encoding.Unicode.GetString(buffer, 0, byteCount) : null;
        }
    }

    /// <summary>
    /// Gets the size of a page in points, after rotation and CropBox, like <see cref="PdfiumPage.Width"/> and
    /// <see cref="PdfiumPage.Height"/>. Unlike <see cref="LoadPage"/> it reads only the page dictionary: it does
    /// not parse the content stream and loads no fonts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">PDFium could not read the page object.</exception>
    public (double Width, double Height) GetPageSize(int index)
    {
        var document = Access();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Fpdf.FPDF_GetPageCount(document));

        FsSizeF size;
        if (Fpdf.FPDF_GetPageSizeByIndexF(document, index, &size) == 0)
        {
            // A BCL exception, so no PDFium type leaks through the public Core API.
            throw new InvalidDataException($"Page {index + 1} could not be loaded.");
        }

        return (size.Width, size.Height);
    }

    /// <summary>Loads a page. The page keeps the document open until it is disposed.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">PDFium could not load the page (damaged page object).</exception>
    public PdfiumPage LoadPage(int index)
    {
        var document = Access();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Fpdf.FPDF_GetPageCount(document));

        var page = new PageHandle(Thread, handle);
        try
        {
            var value = Fpdf.FPDF_LoadPage(document, index);
            if (value == 0)
            {
                // A BCL exception, so no PDFium type leaks through the public Core API.
                throw new InvalidDataException($"Page {index + 1} could not be loaded.");
            }

            try
            {
                page.Attach(value);
            }
            catch
            {
                Fpdf.FPDF_ClosePage(value);
                throw;
            }

            return new PdfiumPage(page, Fpdf.FPDF_GetPageWidthF(value), Fpdf.FPDF_GetPageHeightF(value));
        }
        catch
        {
            page.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes the document once all its pages are closed. On another thread the close is queued to the
    /// PDFium thread instead of throwing.
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        handle.Dispose();
    }

    private static PdfiumError ToError(nuint lastError) =>
        lastError is >= FpdfConstants.ErrUnknown and <= FpdfConstants.ErrPage ? (PdfiumError)(int)lastError : PdfiumError.Unknown;

    /// <summary>Encodes a password as PDFium expects it: UTF-8, null-terminated; <see langword="null"/> is empty.</summary>
    internal static byte[] EncodePassword(string? password)
    {
        var text = password ?? string.Empty;
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    private static nint LoadDocument(FpdfFileAccess* access, string? password)
    {
        var bytes = EncodePassword(password);
        try
        {
            fixed (byte* passwordPointer = bytes)
            {
                return Fpdf.FPDF_LoadCustomDocument(access, passwordPointer);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary><c>m_GetBlock</c> of <c>FPDF_FILEACCESS</c>. Never throws across the native boundary.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadBlock(void* param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            return GCHandle.FromIntPtr((nint)param).Target is FileReadContext context
                && context.Read(position.Value, buffer, size.Value) ? 1 : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private nint Access()
    {
        Thread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        return handle.DangerousGetHandle();
    }

    /// <summary>
    /// The open file and the <see cref="GCHandle"/> that PDFium's read callback receives. Disposed by the
    /// <see cref="DocumentHandle"/> after the document is closed, possibly on another thread.
    /// </summary>
    private sealed class FileReadContext : IDisposable
    {
        private readonly SafeFileHandle file;
        private GCHandle self;
        private int disposed;

        private FileReadContext(SafeFileHandle file, long length)
        {
            this.file = file;
            Length = length;
            self = GCHandle.Alloc(this);
        }

        public long Length { get; }

        /// <summary>Gets the value passed as <c>m_Param</c>.</summary>
        public nint Cookie => GCHandle.ToIntPtr(self);

        public static FileReadContext Open(string path)
        {
            var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var length = RandomAccess.GetLength(file);
                if (length > MaxFileLength)
                {
                    throw new IOException($"The file is larger than {MaxFileLength} bytes and cannot be opened.");
                }

                return new FileReadContext(file, length);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>Reads exactly <paramref name="size"/> bytes at <paramref name="position"/>.</summary>
        public bool Read(nuint position, byte* buffer, nuint size)
        {
            if (position > (ulong)Length || size > (ulong)Length - position)
            {
                return false;
            }

            var offset = (long)position;
            var remaining = (long)size;
            while (remaining > 0)
            {
                var read = RandomAccess.Read(file, new Span<byte>(buffer, (int)Math.Min(remaining, int.MaxValue)), offset);
                if (read <= 0)
                {
                    return false;
                }

                buffer += read;
                offset += read;
                remaining -= read;
            }

            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            self.Free();
            file.Dispose();
        }
    }
}
