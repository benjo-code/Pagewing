using System.ComponentModel;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card that asks before an address of the document is opened: shown over the document area while
/// <see cref="MainViewModel.PendingLink"/> holds one. It is made when the first address asks for it, so nothing of it is
/// built in the startup path.
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
            (_linkCard ??= CreateLinkCard()).Show(pending);
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

    private LinkConfirmCard CreateLinkCard()
    {
        var card = new LinkConfirmCard { Name = "LinkConfirmCard" };
        DocumentAreaLayers.Children.Add(card);
        return card;
    }
}
