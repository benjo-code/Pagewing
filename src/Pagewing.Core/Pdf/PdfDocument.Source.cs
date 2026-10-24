using Microsoft.Win32.SafeHandles;
using Pagewing.Core.Saving;

namespace Pagewing.Core.Pdf;

/// <summary>The file the document is read from, as PDFium sees it (for the checks of <see cref="DocumentSaver"/>).</summary>
public sealed partial class PdfDocument
{
    /// <summary>Gets the length of the source file when the document was opened; PDFium reads (and copies when saving) this many bytes.</summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    internal long SourceLengthAtOpen
    {
        get
        {
            ThrowIfDisposed();
            return document.SourceLengthAtOpen;
        }
    }

    /// <summary>
    /// Reads the stamp of the file PDFium reads from, through its open handle: after the path was renamed or replaced it is
    /// still the opened file; after the file was rewritten in place it shows the new length and time. Any thread.
    /// </summary>
    /// <exception cref="IOException">The stamp cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    internal FileStamp GetSourceStamp()
    {
        ThrowIfDisposed();
        var file = document.SourceFile;
        return new FileStamp(RandomAccess.GetLength(file), File.GetLastWriteTimeUtc(file));
    }

    /// <summary>
    /// Opens a read-only stream over the file PDFium reads from, from its first byte, through PDFium's own handle (which the
    /// stream does not close). Any thread; PDFium may read the same handle at the same time.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    internal Stream OpenSourceReader()
    {
        ThrowIfDisposed();
        return new SourceReader(document.SourceFile);
    }

    /// <summary>Reads a file handle with positional reads, so it does not move a shared file pointer.</summary>
    private sealed class SourceReader(SafeFileHandle file) : Stream
    {
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => RandomAccess.GetLength(file);

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = RandomAccess.Read(file, buffer, position);
            position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await RandomAccess.ReadAsync(file, buffer, position, cancellationToken).ConfigureAwait(false);
            position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
