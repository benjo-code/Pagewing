using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The document info of the status bar (design system 7.6): the button at the right edge opens a popup above the
/// status bar. Neither the popup nor its content exist before the first click; the content and its
/// <see cref="DocumentInfoViewModel"/> are created each time it opens and dropped when it closes, and only then is the
/// metadata read. Esc or a click beside it closes it, and the focus goes back to the button. Another tab, another
/// document or a tab that is no longer ready closes it.
/// </summary>
public sealed partial class StatusBar
{
    /// <summary>Gap between the popup and the top edge of the button: 4 px above the status bar (32 high, button 24).</summary>
    private const double InfoPopupGap = 8;

    private DocumentInfoViewModel? _info;

    // Whether Esc closed the popup: the focus then goes back with its ring (keyboard), otherwise without.
    private bool _infoClosedByKey;

    /// <summary>Gets the document info popup, or <see langword="null"/> before it first opened.</summary>
    internal Popup? InfoPopup { get; private set; }

    /// <summary>
    /// Opens the document info popup for the tab of the status bar, which must be ready (the button does it). Does
    /// nothing while it is open or without an open document.
    /// </summary>
    internal void OpenDocumentInfo()
    {
        if (DataContext is not TabViewModel { IsReady: true } tab || InfoPopup is { IsOpen: true })
        {
            return;
        }

        var popup = InfoPopup ??= CreateInfoPopup();
        var info = new DocumentInfoViewModel(tab);
        _info = info;
        var content = new DocumentInfoPopup { DataContext = info };
        content.KeyDown += OnInfoKeyDown;
        _infoClosedByKey = false;
        popup.Child = content;
        popup.IsOpen = true;

        // In the popup the keys reach it (Esc), and a screen reader reads it; as a flyout does with its content.
        content.Focus();
        _ = info.LoadAsync();
    }

    /// <summary>Closes the document info popup if it is open; the focus goes back to the button.</summary>
    internal void CloseDocumentInfo()
    {
        if (InfoPopup is { IsOpen: true } popup)
        {
            popup.IsOpen = false;
        }
    }

    private void InitializeDocumentInfo() => DocumentInfoButton.Click += OnDocumentInfoClick;

    private void OnDocumentInfoClick(object? sender, RoutedEventArgs e) => OpenDocumentInfo();

    /// <summary>
    /// The popup, placed above the button with their right edges aligned. A click beside it closes it, and does
    /// nothing else (it does not reach the control under it): a click on the button of the open popup only closes it.
    /// </summary>
    private Popup CreateInfoPopup()
    {
        var popup = new Popup
        {
            PlacementTarget = DocumentInfoButton,
            Placement = PlacementMode.TopEdgeAlignedRight,
            VerticalOffset = -InfoPopupGap,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = true,
        };
        popup.Closed += OnInfoPopupClosed;

        // In the tree of the status bar, for the theme variant and the resources of the window.
        BarLayout.Children.Add(popup);
        return popup;
    }

    private void OnInfoKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _infoClosedByKey = true;
            CloseDocumentInfo();
        }
    }

    /// <summary>Drops the content and its view model (they hold the tab), and gives the focus back to the button.</summary>
    private void OnInfoPopupClosed(object? sender, EventArgs e)
    {
        var popup = (Popup)sender!;
        if (popup.Child is DocumentInfoPopup content)
        {
            content.KeyDown -= OnInfoKeyDown;
        }

        popup.Child = null;
        _info?.Dispose();
        _info = null;
        if (DocumentInfoButton.IsEffectivelyVisible)
        {
            DocumentInfoButton.Focus(_infoClosedByKey ? NavigationMethod.Tab : NavigationMethod.Unspecified);
        }
    }

    /// <summary>Closes the popup when its tab leaves it: no longer ready, or another document.</summary>
    private void OnTabChangedForDocumentInfo(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.IsReady) or nameof(TabViewModel.Document))
        {
            CloseDocumentInfo();
        }
    }
}
