using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Placing a saved signature (milestone 12): choosing one makes the placing tool, whose ghost follows the pointer; a click on a page
/// places it there (<see cref="PlaceSignatureAsync"/>), then the tool is over and the new signature is selected, so the select tool
/// moves, scales and restyles it like any drawing. Choosing it with the keyboard places it at once in the centre of the visible part
/// of the current page. The tool belongs to no kind of the toolbar (<see cref="ActiveTool"/> stays <see langword="null"/>); it is
/// dropped by Esc, another tool, the end of the mode, another tab, and whatever takes the tab's edits (a save, a print, a card that
/// asks about the tab, an update). Nothing here runs before the document shows its first sharp page.
/// </summary>
public sealed partial class TabViewModel
{
    private SignatureToPlace? _signatureToPlace;

    /// <summary>The signature that waits to be placed and the pixels of an imported one (decoded once, when it was chosen).</summary>
    /// <param name="Signature">The saved signature.</param>
    /// <param name="Image">The pixels of an imported signature, or <see langword="null"/> for a drawn one.</param>
    internal sealed record SignatureToPlace(SavedSignature Signature, SignatureImage? Image)
    {
        /// <summary>Gets width divided by height as it will be placed.</summary>
        public double Aspect => Image is { } image ? (double)image.Width / image.Height : Signature.Aspect;
    }

    /// <summary>Gets the signature that waits to be placed (the placing tool is on), or <see langword="null"/>.</summary>
    internal SignatureToPlace? PendingSignature => _signatureToPlace;

    /// <summary>
    /// Gets whether "Insert signature" can run: the mode is on and a change is possible, or the mode can start (a sharp page is
    /// shown and the document is not known to forbid annotating).
    /// </summary>
    internal bool CanChooseSignature => (_isAnnotating && CanDraw) || CanBeginAnnotate();

    /// <summary>Starts the mode "Comment" without a tool if it is not on (reads the permissions of the document once); nothing without a sharp page.</summary>
    internal Task BeginAnnotateAsync() => _isAnnotating ? Task.CompletedTask : RequestAnnotateAsync(null, apply: false);

    /// <summary>Gets whether the placing tool is on.</summary>
    internal bool IsPlacingSignature => _signatureToPlace is not null;

    /// <summary>Occurs on the UI thread when the signature to place changes or goes (the document view shows or hides the ghost).</summary>
    internal event Action? PendingSignatureChanged;

    /// <summary>
    /// Gets or sets what tells where the centre of the visible part of the current page is (set by the document view): the page and
    /// the point in page points. Without it (no view) the centre of the current page is taken.
    /// </summary>
    internal Func<(int Page, DisplayPoint Centre)?>? VisibleCentre { get; set; }

    /// <summary>
    /// Chooses a saved signature: starts the mode if needed (reading the permissions of the document once), makes the placing tool and,
    /// when <paramref name="fromKeyboard"/>, places the signature in the centre of the visible part of the current page at once.
    /// Nothing happens before the first sharp page, while the document forbids annotations, or while the tab takes no edit.
    /// </summary>
    /// <param name="signature">The saved signature.</param>
    /// <param name="png">The PNG of an imported signature (from the library), or <see langword="null"/> for a drawn one.</param>
    /// <param name="fromKeyboard">Whether the choice came from the keyboard (no pointer to place it with).</param>
    /// <returns>Completes when the tool is on or the signature placed (or when nothing happened); it never fails.</returns>
    internal async Task ChooseSignatureAsync(SavedSignature signature, byte[]? png, bool fromKeyboard)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!CanRequestAnnotate())
        {
            return;
        }

        var generation = _documentGeneration;
        if (!_isAnnotating)
        {
            await RequestAnnotateAsync(null, apply: false).ConfigureAwait(true);
        }

        if (_disposed || generation != _documentGeneration || !_isAnnotating || !CanDraw)
        {
            return;
        }

        SignatureImage? image = null;
        if (signature.Kind == SavedSignatureKind.Image)
        {
            if (png is null)
            {
                DrawingRefused?.Invoke(Strings.SignatureProblemUnreadable);
                return;
            }

            try
            {
                image = await SignatureImageCodec.DecodeAsync(png, CancellationToken.None).ConfigureAwait(true);
            }
            catch (SignatureImageException)
            {
                DrawingRefused?.Invoke(Strings.SignatureProblemUnreadable);
                return;
            }

            if (_disposed || generation != _documentGeneration || !CanDraw)
            {
                return;
            }
        }

        // The placing tool takes the place of the tool and the selection that were there.
        ClearSelection();
        ActiveTool = null;
        IsSelectTool = false;
        SetSignatureToPlace(new SignatureToPlace(signature, image));
        if (fromKeyboard)
        {
            var spot = VisibleCentre?.Invoke() ?? FallbackCentre();
            if (spot is { } place)
            {
                await PlaceSignatureAsync(place.Page, place.Centre).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Places the waiting signature with its centre at <paramref name="centre"/> of <paramref name="page"/> (<see cref="SignaturePlacement.Fit"/>:
    /// 160 points wide or a third of the page, the aspect ratio fixed, wholly on the page); the placing tool is over from here on, and the
    /// new signature is selected once it is made. A drawn signature takes the colour it was saved with and the stroke width of the tool.
    /// Runs in the background through the editor, in the order of the other drawings; the tab counts as changed from the call on. A
    /// refusal changes nothing; <see cref="DrawingRefused"/> says why.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="centre">The point in page points of the page as shown.</param>
    /// <returns>Completes when the signature was made, refused or dropped; it never fails.</returns>
    internal Task PlaceSignatureAsync(int page, DisplayPoint centre)
    {
        if (_signatureToPlace is not { } pending || _disposed || !CanDraw || CurrentSizeOf(page) is not { } size)
        {
            return Task.CompletedTask;
        }

        var box = SignaturePlacement.Fit(pending.Aspect, centre, size.Width, size.Height);
        SetSignatureToPlace(null);
        if (pending.Image is { } image)
        {
            return QueueSignatureImage(page, box, image);
        }

        var strokes = pending.Signature.Strokes ?? [];
        var geometry = DrawingGeometry.Signature(
            strokes.Select(stroke => (IReadOnlyList<DisplayPoint>)[.. stroke.Select(p => new DisplayPoint(box.Left + (p.X * box.Width), box.Top + (p.Y * box.Height)))]),
            StrokeWidthOf(AnnotationKind.Signature));
        return geometry.IsEmpty ? Task.CompletedTask : QueueDrawing(page, AnnotationKind.Signature, Task.FromResult(geometry), DrawPalette.ColorOf(pending.Signature.Color));
    }

    /// <summary>Drops the placing tool without placing anything; returns whether it was on.</summary>
    internal bool DiscardPendingSignature()
    {
        if (_signatureToPlace is null)
        {
            return false;
        }

        SetSignatureToPlace(null);
        return true;
    }

    private void SetSignatureToPlace(SignatureToPlace? value)
    {
        if (!ReferenceEquals(_signatureToPlace, value))
        {
            _signatureToPlace = value;
            PendingSignatureChanged?.Invoke();
        }
    }

    private (int Page, DisplayPoint Centre)? FallbackCentre() =>
        CurrentPage >= 1 && CurrentPageSize is { } size ? (CurrentPage - 1, new DisplayPoint(size.Width / 2, size.Height / 2)) : null;

    private PdfPageSize? CurrentSizeOf(int page)
    {
        var sizes = _session.LoadedSizes;
        return page >= 0 && page < sizes.Count ? sizes[page] : null;
    }

    /// <summary>Takes an image signature in like <see cref="QueueDrawing"/> takes a drawing: made after the drawings queued before it.</summary>
    private Task QueueSignatureImage(int page, PageRect box, SignatureImage image)
    {
        var author = AnnotationSettings.Current.AuthorName;
        var run = Track(ApplySignatureImageCoreAsync(_documentGeneration, page, box, image, _drawingTail, author));
        _drawingTail = run;
        return run;
    }

    private async Task ApplySignatureImageCoreAsync(int generation, int page, PageRect box, SignatureImage image, Task before, string? author)
    {
        try
        {
            _pendingEdits++;
            _drawingsInFlight++;
            RaiseStepCanExecuteChanged();
            try
            {
                await before;
            }
            catch (Exception)
            {
                // The drawings before it never fail; only their end matters.
            }

            if (_disposed || generation != _documentGeneration)
            {
                return;
            }

            PdfAnnotation? created;
            try
            {
                created = await Editor.ApplySignatureImageAsync(page, box, image, author);
            }
            catch (AnnotationHistoryFullException)
            {
                DrawingRefused?.Invoke(Strings.SignatureHistoryFull);
                return;
            }
            catch (PdfAnnotationLimitException)
            {
                DrawingRefused?.Invoke(string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.DrawingLimitReached), PdfDocument.MaxAnnotations));
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                DrawingRefused?.Invoke(Strings.DrawingFailed);
                return;
            }

            if (created is not null)
            {
                _comments?.SelectWhenListed(created);
                SelectPlaced(created);
            }
        }
        finally
        {
            _pendingEdits--;
            _drawingsInFlight--;
            RaiseStepCanExecuteChanged();
            SyncFromEditor();
        }
    }

    /// <summary>A signature was just placed: the select tool takes over with it selected, so it can be moved, scaled and restyled at once.</summary>
    private void SelectPlaced(PdfAnnotation created)
    {
        if (_disposed || !_isAnnotating)
        {
            return;
        }

        IsSelectTool = true;
        SelectFromList(created);
    }
}
