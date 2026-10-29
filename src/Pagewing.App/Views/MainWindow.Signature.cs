using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The dialog "New signature" of the window: made when it is first shown, never in the startup path.</summary>
public sealed partial class MainWindow
{
    private SignatureCard? _newSignatureCard;

    /// <summary>Shows or hides the dialog card for the overlay kind; called by the overlays.</summary>
    private void UpdateNewSignatureCard(OverlayKind kind)
    {
        if (kind == OverlayKind.Signature && _viewModel?.SignatureCard is { } signature)
        {
            var view = _newSignatureCard ??= CreateNewSignatureCard();
            view.DataContext = signature;
            view.Show();
        }
        else
        {
            _newSignatureCard?.Hide();
        }
    }

    private SignatureCard CreateNewSignatureCard()
    {
        // Like the print card: over the work area and the status bar, below the title bar.
        var card = new SignatureCard { Name = "SignatureCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }
}
