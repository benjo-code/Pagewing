using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card "Pagewing X is available" under the button "Update" of the title bar (design system 7.11), in the work area
/// of the window at the top right, and in its place the note after the command "Check for updates" (7.13). It is made
/// when it is first opened, so nothing of it is built in the startup path. Opened by keyboard the card takes the focus (on
/// "Later"); when it goes with the focus, the focus goes back to the button. The note never takes the focus.
/// </summary>
public sealed partial class MainWindow
{
    // Between the card and the title bar (space.2).
    private const double UpdateCardGap = 8;

    private UpdateCard? _updateCard;
    private UpdateViewModel? _update;

    private void AttachUpdate(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnUpdateWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnUpdateWindowPropertyChanged;
        }

        FollowUpdate(next?.Update);
    }

    private void OnUpdateWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Update))
        {
            FollowUpdate(_viewModel?.Update);
        }
    }

    private void FollowUpdate(UpdateViewModel? update)
    {
        if (ReferenceEquals(_update, update))
        {
            return;
        }

        if (_update is not null)
        {
            _update.PropertyChanged -= OnUpdatePropertyChanged;
            _update.ProgressWindowRequested -= OnProgressWindowRequested;
        }

        _update = update;
        if (update is not null)
        {
            update.PropertyChanged += OnUpdatePropertyChanged;
            update.ProgressWindowRequested += OnProgressWindowRequested;
        }

        UpdateUpdateCard();
    }

    // "Install", or the button while an installation runs: the progress window of the process, over this window.
    private void OnProgressWindowRequested()
    {
        if (_update is { } update)
        {
            UpdateWindow.ShowFor(update, this);
        }
    }

    private void OnUpdatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateViewModel.IsCardOpen) or nameof(UpdateViewModel.IsNoteOpen))
        {
            UpdateUpdateCard();
        }
    }

    private void UpdateUpdateCard()
    {
        // The offer, or the note after the command "Check for updates" in its place (design system 7.13).
        if (_update is { } update && (update.IsCardOpen || update.IsNoteOpen))
        {
            var button = TitleBar.UpdateButton;

            // Enter or Space on the button (it shows the focus ring only then): the keys follow onto the card. The note
            // never takes the focus.
            var byKeyboard = update.IsCardOpen && button.IsFocused && button.Classes.Contains(":focus-visible");
            var card = _updateCard ??= CreateUpdateCard();
            card.DataContext = update;
            card.Margin = new Thickness(0, UpdateCardGap, RightOfUpdateButton(button), 0);
            card.IsVisible = true;
            if (byKeyboard)
            {
                card.UpdateLayout();
                card.UpdateLaterButton.Focus(NavigationMethod.Tab);
            }

            return;
        }

        if (_updateCard is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        if (!hadFocus)
        {
            return;
        }

        // Back where the card came from: the button, if it is still there, else the document.
        if (TitleBar.UpdateButton.IsEffectivelyVisible)
        {
            TitleBar.UpdateButton.Focus(NavigationMethod.Tab);
        }
        else if (DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    // The distance from the right edge of the button to the right edge of the window (the caption buttons on Windows and
    // the margin of the button), so the card ends under the button.
    private double RightOfUpdateButton(Button button)
    {
        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), RootGrid);
        return right is { } point && button.IsEffectivelyVisible ? Math.Max(0, RootGrid.Bounds.Width - point.X) : UpdateCardGap;
    }

    private UpdateCard CreateUpdateCard()
    {
        // In the work area, after the default app card in the tab order; above the document and the print card, below the overlays.
        var card = new UpdateCard { Name = "UpdateCard", ZIndex = 6 };
        Grid.SetRow(card, 1);
        RootGrid.Children.Add(card);
        return card;
    }
}
