using System.ComponentModel;
using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card "Set Pagewing as the default for PDFs?" in the work area of the window, over the start screen or the
/// document, at the bottom right. It is made when the question is first answered with "not the default", so nothing of
/// it is built in the startup path.
/// </summary>
public sealed partial class MainWindow
{
    private DefaultAppCard? _defaultAppCard;

    private void AttachDefaultApp(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnDefaultAppPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnDefaultAppPropertyChanged;
        }

        UpdateDefaultAppCard();
    }

    private void OnDefaultAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DefaultAppPrompt))
        {
            UpdateDefaultAppCard();
        }
    }

    private void UpdateDefaultAppCard()
    {
        if (_viewModel?.DefaultAppPrompt is { } prompt)
        {
            var card = _defaultAppCard ??= CreateDefaultAppCard();
            card.DataContext = prompt;
            card.IsVisible = true;
        }
        else if (_defaultAppCard is { } shown)
        {
            // The keys go back to the document when they were on the card.
            var hadFocus = shown.IsKeyboardFocusWithin;
            shown.IsVisible = false;
            shown.DataContext = null;
            if (hadFocus && DocumentView.Layout is not null)
            {
                DocumentView.Focus(Avalonia.Input.NavigationMethod.Unspecified);
            }
        }
    }

    private DefaultAppCard CreateDefaultAppCard()
    {
        // In the work area, last in the tab order of the window; above the document and the print card, below the overlays.
        var card = new DefaultAppCard { Name = "DefaultAppCard", ZIndex = 6 };
        Grid.SetRow(card, 1);
        RootGrid.Children.Add(card);
        return card;
    }
}
