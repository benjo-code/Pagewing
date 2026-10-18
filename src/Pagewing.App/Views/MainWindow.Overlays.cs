using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// What lies over the window (<see cref="MainViewModel.Overlay"/>): one at a time, below the title bar (its tabs and the
/// window buttons stay usable), each made when it is first shown, so nothing of it is built in the startup path. The
/// help card is the only one with a view so far; the palette and the settings show nothing yet. When an overlay with the
/// focus goes, the focus goes back to where it was before the overlay came.
/// </summary>
public sealed partial class MainWindow
{
    private HelpCard? _helpCard;

    // The element that had the focus before the first overlay came (the overlays may replace each other meanwhile).
    private IInputElement? _focusBeforeOverlay;
    private bool _overlayShown;

    private void AttachOverlays(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnOverlayPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnOverlayPropertyChanged;
        }

        UpdateOverlay();
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Overlay))
        {
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        var kind = _viewModel?.Overlay ?? OverlayKind.None;
        if (kind != OverlayKind.None && !_overlayShown)
        {
            _overlayShown = true;
            _focusBeforeOverlay = FocusManager?.GetFocusedElement();
        }

        var hadFocus = _helpCard is { IsVisible: true, IsKeyboardFocusWithin: true };
        if (kind == OverlayKind.Help && _viewModel is { } viewModel)
        {
            (_helpCard ??= CreateHelpCard()).Show(viewModel.Commands);
        }
        else
        {
            _helpCard?.Hide();
        }

        // The card had the keys and nothing that is shown now takes them.
        if (hadFocus && kind != OverlayKind.Help)
        {
            RestoreFocus(_focusBeforeOverlay);
        }

        if (kind == OverlayKind.None)
        {
            _overlayShown = false;
            _focusBeforeOverlay = null;
        }
    }

    /// <summary>The keys go back to the element that had them, or to the document when that one is gone.</summary>
    private void RestoreFocus(IInputElement? before)
    {
        if (before is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } element
            && ReferenceEquals(TopLevel.GetTopLevel(element), this))
        {
            element.Focus(NavigationMethod.Unspecified);
        }
        else if (DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    private HelpCard CreateHelpCard()
    {
        // Over the work area and the status bar, below the title bar; above the panel and the document area.
        var card = new HelpCard { Name = "HelpCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }
}
