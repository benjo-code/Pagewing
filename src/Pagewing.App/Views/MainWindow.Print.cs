using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// Printing in the window: the dialog card (<see cref="OverlayKind.Print"/>, shown with the other overlays by
/// MainWindow.Overlays.cs) and the card of the running job at the bottom of the document area. Both are made when the first
/// print asks for them, so nothing of it is built in the startup path.
/// </summary>
public sealed partial class MainWindow
{
    private PrintCard? _printCard;
    private PrintJobCard? _printJobCard;

    private void AttachPrint(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnPrintPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnPrintPropertyChanged;
            next.OwnerWindowHandle = () => TryGetPlatformHandle()?.Handle ?? 0;
        }

        UpdatePrintJobCard();
    }

    private void OnPrintPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PrintJob))
        {
            UpdatePrintJobCard();
        }
    }

    private void UpdatePrintJobCard()
    {
        if (_viewModel?.PrintJob is { } job)
        {
            var card = _printJobCard ??= CreatePrintJobCard();
            card.DataContext = job;
            card.IsVisible = true;
        }
        else if (_printJobCard is { } shown)
        {
            shown.IsVisible = false;
            shown.DataContext = null;
        }
    }

    private PrintJobCard CreatePrintJobCard()
    {
        var card = new PrintJobCard { Name = "PrintJobCard", ZIndex = 5 };
        DocumentAreaLayers.Children.Add(card);
        return card;
    }

    /// <summary>Shows or hides the dialog card for the overlay kind; called by the overlays.</summary>
    private void UpdatePrintCard(OverlayKind kind)
    {
        if (kind == OverlayKind.Print && _viewModel?.Print is { } print)
        {
            var view = _printCard ??= CreatePrintCard();
            view.DataContext = print;
            view.Show();
        }
        else
        {
            _printCard?.Hide();
        }
    }

    private PrintCard CreatePrintCard()
    {
        // Like the settings card: over the work area and the status bar, below the title bar.
        var card = new PrintCard { Name = "PrintCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }
}
