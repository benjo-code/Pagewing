using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.Core.Pages;

/// <summary>A PDF file a page plan takes pages from.</summary>
/// <param name="Path">The path of the file.</param>
/// <param name="Password">The password of an encrypted file, or <see langword="null"/>; <see cref="object.ToString"/> does not show it.</param>
/// <param name="Stamp">
/// The stamp of the file as it was when it was inserted (<see cref="Of"/>), or <see langword="null"/> to take the file as it is.
/// With a stamp every opening of the file for the plan checks that it is still that file: a file that was changed, replaced,
/// deleted or cannot be read since ends in <see cref="SaveFailureReason.SourceModified"/> naming the file, before anything is
/// written.
/// </param>
public sealed record PageSourceFile(string Path, string? Password, FileStamp? Stamp = null)
{
    /// <summary>Prints the members like a record does, with the password replaced (secrets never reach a log or a failed assertion).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Path = {Path}, Password = {(Password is null ? "null" : "(hidden)")}, Stamp = {Stamp}");
        return true;
    }

    /// <summary>Makes the entry for a file that <paramref name="opened"/> was just opened from, remembering its stamp.</summary>
    /// <param name="path">The path the document was opened from.</param>
    /// <param name="password">The password it was opened with, or <see langword="null"/>.</param>
    /// <param name="opened">The document opened from <paramref name="path"/>.</param>
    /// <exception cref="ObjectDisposedException"><paramref name="opened"/> is disposed.</exception>
    public static PageSourceFile Of(string path, string? password, PdfDocument opened)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(opened);
        return new PageSourceFile(path, password, opened.SourceStampAtOpen);
    }

    /// <summary>
    /// Opens the file for the plan and, when it has a <see cref="Stamp"/>, checks that it is the file that was inserted.
    /// </summary>
    /// <exception cref="SaveFailureException">The file is not the one that was inserted (<see cref="SaveFailureReason.SourceModified"/>, <see cref="SaveFailureException.FilePath"/> is the path).</exception>
    /// <exception cref="PdfOpenException">The file cannot be opened (also a wrong password), or has no stamp and is gone.</exception>
    internal async Task<PdfDocument> OpenAsync(PdfEngine engine, CancellationToken cancellationToken)
    {
        PdfDocument opened;
        try
        {
            opened = await engine.OpenAsync(Path, Password, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfOpenException ex) when (Stamp is not null && ex.Error is PdfOpenError.FileNotFound or PdfOpenError.CannotRead or PdfOpenError.NotAPdf)
        {
            // Deleted, locked or overwritten with something else since it was inserted.
            throw Changed(ex);
        }

        if (Stamp is { } expected && opened.SourceStampAtOpen != expected)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw Changed(null);
        }

        return opened;
    }

    private SaveFailureException Changed(Exception? inner) =>
        new(SaveFailureReason.SourceModified, "An inserted file is not the file that was inserted.", inner) { FilePath = Path };
}

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
