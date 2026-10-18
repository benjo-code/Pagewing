using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// What lies over the window (<see cref="MainViewModel.Overlay"/>): one at a time, below the title bar (its tabs and the
/// window buttons stay usable), each made when it is first shown, so nothing of it is built in the startup path. The
/// help card and the command palette have a view so far; the settings show nothing yet. When an overlay with the focus
/// goes and nothing that comes takes the focus, it goes back to where it was before the overlay came: the element that
/// had it when the last overlay came or replaced another, unless that was an overlay itself. The palette also closes
/// when the focus leaves it or a press lands beside it.
/// </summary>
public sealed partial class MainWindow
{
    private HelpCard? _helpCard;
    private CommandPalette? _palette;

    // The element that had the focus before the overlays came (taken again whenever one comes from outside them).
    private IInputElement? _focusBeforeOverlay;
    private OverlayKind _shownOverlay;

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
        var focused = FocusManager?.GetFocusedElement();
        var overlayHadFocus = IsInShownOverlay(focused);
        if (kind != OverlayKind.None && kind != _shownOverlay && !overlayHadFocus)
        {
            _focusBeforeOverlay = focused;
        }

        if (kind == OverlayKind.Help && _viewModel is { } helpWindow)
        {
            (_helpCard ??= CreateHelpCard()).Show(helpWindow.Commands);
        }
        else
        {
            _helpCard?.Hide();
        }

        if (kind == OverlayKind.Palette && _viewModel?.Palette is { } palette)
        {
            var view = _palette ??= CreatePalette();
            view.DataContext = palette;
            view.Show();
        }
        else
        {
            _palette?.Hide();
        }

        // An overlay had the keys and nothing that is shown now takes them.
        if (overlayHadFocus && kind is not (OverlayKind.Help or OverlayKind.Palette))
        {
            RestoreFocus(_focusBeforeOverlay);
        }

        _shownOverlay = kind;
        if (kind == OverlayKind.None)
        {
            _focusBeforeOverlay = null;
        }
    }

    private bool IsInShownOverlay(IInputElement? element) =>
        element is Visual visual
        && (IsWithin(_helpCard, visual) || IsWithin(_palette, visual));

    private static bool IsWithin(Control? overlay, Visual visual) =>
        overlay is { IsVisible: true } && (ReferenceEquals(overlay, visual) || overlay.IsVisualAncestorOf(visual));

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

    private CommandPalette CreatePalette()
    {
        // Just below the title bar, centred, above the panel and the document area; only as large as its card, so the
        // rest of the window keeps its input.
        var palette = new CommandPalette { Name = "CommandPalette" };
        Grid.SetRow(palette, 1);
        Grid.SetRowSpan(palette, 2);
        palette.ZIndex = 10;
        palette.FocusLeft += OnPaletteFocusLeft;
        RootGrid.Children.Add(palette);

        // Before anything below it sees the press (the palette's own handler marks its presses handled).
        AddHandler(PointerPressedEvent, OnPressWhilePaletteIsOpen, RoutingStrategies.Tunnel, handledEventsToo: true);
        return palette;
    }

    private void OnPaletteFocusLeft()
    {
        if (_viewModel?.Overlay == OverlayKind.Palette)
        {
            _viewModel.HideOverlay();
        }
    }

    /// <summary>
    /// A press beside the open palette closes it and goes on to where it landed (a tab, the document, the window
    /// buttons). On the title bar's palette button it only closes it: the button would open it again.
    /// </summary>
    private void OnPressWhilePaletteIsOpen(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.Overlay != OverlayKind.Palette || _palette is null || e.Source is not Visual source || IsWithin(_palette, source))
        {
            return;
        }

        if (TitleBar.PaletteButton is { } button && (ReferenceEquals(button, source) || button.IsVisualAncestorOf(source)))
        {
            e.Handled = true;
        }

        _viewModel.HideOverlay();
    }
}
