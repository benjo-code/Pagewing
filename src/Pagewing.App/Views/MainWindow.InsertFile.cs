using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The dialog "Insert pages from a file" of the window: made when it is first shown, never in the startup path.</summary>
public sealed partial class MainWindow
{
    private InsertFileCard? _insertFileCard;

    /// <summary>Shows or hides the dialog card for the overlay kind; called by the overlays.</summary>
    private void UpdateInsertFileCard(OverlayKind kind)
    {
        if (kind == OverlayKind.InsertFile && _viewModel?.InsertFile is { } dialog)
        {
            var view = _insertFileCard ??= CreateInsertFileCard();
            view.DataContext = dialog;
            view.Show();
        }
        else
        {
            _insertFileCard?.Hide();
        }
    }

    private InsertFileCard CreateInsertFileCard()
    {
        // Like the signature card: over the work area and the status bar, below the title bar.
        var card = new InsertFileCard { Name = "InsertFileCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }
}
