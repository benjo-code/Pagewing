namespace Pagewing.Pdfium;

/// <summary>
/// Work that runs in small steps on the <see cref="PdfiumThread"/> so that interactive work can run in
/// between. Every member runs on the PDFium thread.
/// </summary>
/// <typeparam name="T">Result type delivered when the work is complete.</typeparam>
internal abstract class SteppedWork<T> : IDisposable
{
    /// <summary>Runs one step.</summary>
    /// <returns><see langword="true"/> when the work is finished.</returns>
    public abstract bool Step();

    /// <summary>Produces the result after <see cref="Step"/> returned <see langword="true"/>.</summary>
    public abstract T Complete();

    /// <summary>Releases resources. Called exactly once: after completion, failure, or cancellation.</summary>
    public virtual void Dispose()
    {
    }
}
