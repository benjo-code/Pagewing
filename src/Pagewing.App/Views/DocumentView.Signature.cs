using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// Placing a saved signature (milestone 12, design system 7.7): while a signature waits (<see cref="TabViewModel.PendingSignature"/>)
/// a ghost of it follows the pointer over the pages (<see cref="SignatureGhost"/>, at the size and place the click will give it), and
/// a press of the left button with the mouse or the pen on a page places it there. A finger is left to the scroll viewer. Esc, another
/// tab, a save and whatever else drops the tool drop the ghost with it. The ghost is made when the first signature is chosen, never
/// in the startup path.
/// </summary>
public sealed partial class DocumentView
{
    private SignatureGhost? _signatureGhost;
    private TabViewModel? _signatureTab;
    private Point? _lastPointer;

    /// <summary>Gets the ghost, or <see langword="null"/> before the first signature was chosen (for tests).</summary>
    internal SignatureGhost? SignatureGhost => _signatureGhost;

    private void ConnectSignature(TabViewModel tab)
    {
        DisconnectSignature();
        _signatureTab = tab;
        tab.PendingSignatureChanged += OnPendingSignatureChanged;
        tab.VisibleCentre = VisibleSignatureCentre;
    }

    private void DisconnectSignature()
    {
        if (_signatureTab is { } tab)
        {
            tab.PendingSignatureChanged -= OnPendingSignatureChanged;
            tab.VisibleCentre = null;

            // Whatever the tab does next, the signature it waits to place is not placed by a view that is gone.
            tab.DiscardPendingSignature();
            _signatureTab = null;
        }

        _signatureGhost?.Hide();
    }

    private void OnPendingSignatureChanged()
    {
        if (_signatureTab?.PendingSignature is not { } pending)
        {
            _signatureGhost?.Hide();
            return;
        }

        var ghost = EnsureSignatureGhost();
        var ink = pending.Signature.Kind == SavedSignatureKind.Drawn ? DrawPalette.ColorOf(pending.Signature.Color) : default;
        ghost.SetContent(
            pending.Image is null ? pending.Signature.Strokes : null,
            Color.FromRgb(ink.R, ink.G, ink.B),
            _signatureTab!.StrokeWidthOf(AnnotationKind.Signature) * (_layout?.Zoom ?? 1) * Core.Viewing.DocumentLayout.DipsPerPoint,
            pending.Image);
        if (_lastPointer is { } position)
        {
            PlaceGhostAt(position);
        }
    }

    private SignatureGhost EnsureSignatureGhost()
    {
        if (_signatureGhost is null)
        {
            // Beside the drawing layer, right above the pages and below the scroll viewer and its bars; it takes no input.
            _signatureGhost = new SignatureGhost { Name = "SignatureGhost", IsVisible = false };
            DocumentLayers.Children.Insert(DocumentLayers.Children.IndexOf(PageCanvas) + 1, _signatureGhost);
        }

        return _signatureGhost;
    }

    /// <summary>A press with a signature waiting: the left button of the mouse or the pen places it on the page under the pointer.</summary>
    /// <returns>Whether the placing tool took the press (a finger is left to the scroll viewer).</returns>
    private bool PressForSignature(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { PendingSignature: not null } tab || e.Pointer.Type == PointerType.Touch)
        {
            return false;
        }

        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || _layout is null || IsInScrollBar(e.Source))
        {
            return true;
        }

        if (PageCanvas.PagePointAt(e.GetPosition(PageCanvas)) is { } hit && IsOnPage(hit.Page, hit.Position))
        {
            _ = tab.PlaceSignatureAsync(hit.Page, new DisplayPoint(hit.Position.X, hit.Position.Y));
        }

        return true;
    }

    /// <summary>A move with a signature waiting: the ghost follows the pointer (the move goes on to the others: the cursor, the scroll).</summary>
    private void MoveForSignature(PointerEventArgs e)
    {
        _lastPointer = e.GetPosition(PageCanvas);
        if (_viewModel?.PendingSignature is not null)
        {
            PlaceGhostAt(_lastPointer.Value);
        }
    }

    private void PlaceGhostAt(Point position)
    {
        if (_signatureGhost is not { } ghost || _viewModel?.PendingSignature is not { } pending || _layout is null)
        {
            return;
        }

        if (PageCanvas.PagePointAt(position) is not { } hit || !IsOnPage(hit.Page, hit.Position) || PageCanvas.PagePlacement(hit.Page) is not { } placement)
        {
            ghost.Hide();
            return;
        }

        var size = _layout.GetPageSize(hit.Page);
        var box = SignaturePlacement.Fit(pending.Aspect, new DisplayPoint(hit.Position.X, hit.Position.Y), size.Width, size.Height);
        var scale = placement.DipsPerPoint;
        ghost.Place(new Rect(placement.PageRect.X + (box.Left * scale), placement.PageRect.Y + (box.Top * scale), box.Width * scale, box.Height * scale));
    }

    /// <summary>The page and the point (page points) of the centre of the visible part of the current page.</summary>
    private (int Page, DisplayPoint Centre)? VisibleSignatureCentre()
    {
        if (_viewModel is not { } tab || _layout is null || tab.CurrentPage < 1)
        {
            return null;
        }

        var page = Math.Min(tab.CurrentPage - 1, _layout.PageCount - 1);
        if (PageCanvas.PagePlacement(page) is not { } placement)
        {
            return null;
        }

        var visible = placement.PageRect.Intersect(new Rect(PageCanvas.Bounds.Size));
        var viewportCentre = visible.Width > 0 && visible.Height > 0 ? visible.Center : placement.PageRect.Center;
        return PageCanvas.PointOnPage(page, viewportCentre) is { } point ? (page, new DisplayPoint(point.X, point.Y)) : null;
    }
}
