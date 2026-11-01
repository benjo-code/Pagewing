using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The cards "Split document", "Extract selected pages" and "Export pages as images" of the window: each is made when it is first
/// shown, never in the startup path.
/// </summary>
public sealed partial class MainWindow
{
    private SplitCard? _splitCard;
    private ExportImagesCard? _exportImagesCard;

    /// <summary>Shows or hides the cards for the overlay kind; called by the overlays.</summary>
    private void UpdatePageOutputCards(OverlayKind kind)
    {
        var card = kind == OverlayKind.PageOutput ? _viewModel?.PageOutput : null;
        if (card is SplitViewModel split)
        {
            var view = _splitCard ??= CreateSplitCard();
            view.DataContext = split;
            view.Show();
        }
        else
        {
            _splitCard?.Hide();
        }

        if (card is ExportImagesViewModel export)
        {
            var view = _exportImagesCard ??= CreateExportImagesCard();
            view.DataContext = export;
            view.Show();
        }
        else
        {
            _exportImagesCard?.Hide();
        }
    }

    private SplitCard CreateSplitCard()
    {
        // Like the insert card: over the work area and the status bar, below the title bar.
        var card = new SplitCard { Name = "SplitCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }

    private ExportImagesCard CreateExportImagesCard()
    {
        var card = new ExportImagesCard { Name = "ExportImagesCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }
}
