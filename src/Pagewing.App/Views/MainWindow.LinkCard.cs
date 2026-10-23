using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card that asks before an address is opened: shown over the document area while
/// <see cref="MainViewModel.PendingLink"/> holds one, or over the start screen when the window has no tab (the download
/// page of the card "Update", which can be opened there too). It is made when the first address asks for it, so nothing
/// of it is built in the startup path.
/// </summary>
public sealed partial class MainWindow
{
    private LinkConfirmCard? _linkCard;

    private void AttachLinkCard(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnLinkCardPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnLinkCardPropertyChanged;
        }

        UpdateLinkCard();
    }

    private void OnLinkCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PendingLink))
        {
            UpdateLinkCard();
        }
    }

    private void UpdateLinkCard()
    {
        if (_viewModel?.PendingLink is { } pending)
        {
            var linkCard = _linkCard ??= new LinkConfirmCard { Name = "LinkConfirmCard" };
            PlaceLinkCard(linkCard, _viewModel.HasTabs);
            linkCard.Show(pending);
            return;
        }

        if (_linkCard is not { IsVisible: true } card)
        {
            return;
        }

        // The keys go back to the document when they were on the card.
        var hadFocus = card.IsKeyboardFocusWithin;
        card.Hide();
        if (hadFocus && DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    // Over the document area of the active tab, or without a tab over the whole work area (the start screen).
    private void PlaceLinkCard(LinkConfirmCard card, bool hasTabs)
    {
        Panel host = hasTabs ? DocumentAreaLayers : WorkArea;
        if (ReferenceEquals(card.Parent, host))
        {
            return;
        }

        (card.Parent as Panel)?.Children.Remove(card);
        Grid.SetColumnSpan(card, hasTabs ? 1 : 3);
        card.ZIndex = hasTabs ? 0 : 3;
        host.Children.Add(card);
    }
}
