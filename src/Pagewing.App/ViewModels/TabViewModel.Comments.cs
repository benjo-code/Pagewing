using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>The "Comments" panel of the tab (milestone 9): the list of the document's annotations, made when it first opens.</summary>
public sealed partial class TabViewModel
{
    // The list of the annotations: made on the first access (only the "Comments" panel gets here, never the startup path),
    // dropped with the document it belongs to.
    private CommentsViewModel? _comments;

    /// <summary>
    /// Gets the list of the annotations of the open document, made on the first access (only the "Comments" panel asks for
    /// it). It reads nothing before the first sharp page of the document. Closing the panel keeps it; another document in
    /// the tab drops it (the next access makes a new one, and <see cref="ViewModelBase.PropertyChanged"/> tells the panel),
    /// a saved document that the session adopted keeps it.
    /// </summary>
    public CommentsViewModel Comments => _comments ??= new CommentsViewModel(this);

    /// <summary>Gets whether <see cref="Comments"/> was made (for tests: nothing makes it before the panel opens).</summary>
    internal bool IsCommentsCreated => _comments is not null;

    /// <summary>
    /// Gets or sets how <see cref="Comments"/> reads the annotations of a page: <see cref="PdfDocument.GetAnnotationsAsync"/>
    /// (replaceable for tests).
    /// </summary>
    internal Func<PdfDocument, int, CancellationToken, Task<IReadOnlyList<PdfAnnotation>>> AnnotationReader { get; set; } =
        static (document, page, cancellationToken) => document.GetAnnotationsAsync(page, cancellationToken);

    /// <summary>
    /// Applies the comment typed in the "Comments" panel and not applied yet, as Ctrl+Enter would (before the update asks
    /// about unsaved changes, so a typed comment is asked about and never dropped silently when the app ends).
    /// </summary>
    internal void ApplyCommentDraft() => _comments?.ApplyDraft();

    /// <summary>The list belongs to the document that was replaced or closed: it goes, a later access makes a new one.</summary>
    private void DiscardComments()
    {
        var comments = _comments;
        if (comments is null)
        {
            return;
        }

        _comments = null;
        comments.Dispose();
        OnPropertyChanged(nameof(Comments));
    }
}
