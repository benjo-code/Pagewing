using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

/// <summary>A PDF file a page plan takes pages from.</summary>
/// <param name="Path">The path of the file.</param>
/// <param name="Password">The password of an encrypted file, or <see langword="null"/>.</param>
public sealed record PageSourceFile(string Path, string? Password);

/// <summary>
/// Builds the result of a <see cref="PageEditPlan"/> on a working copy and tells beforehand what the plan loses (milestone 13;
/// <c>Docs/DECISIONS.md</c>, "Seiten organisieren: Spike und QPDF-Entscheidung (M13)"). The copy is opened from the
/// <em>file</em> of the primary source, never from the document that is shown, so the shown document is not touched and
/// stays valid; only the copy is edited. Nothing here writes a file.
/// </summary>
/// <param name="engine">Opens the documents.</param>
public sealed partial class PageEditExecutor(PdfEngine engine)
{
    /// <summary>Gets or sets a hook called with the working copy right after it was opened (for tests: to see that it is released on failure).</summary>
    internal Action<PdfDocument>? WorkingCopyOpened { get; init; }
}
