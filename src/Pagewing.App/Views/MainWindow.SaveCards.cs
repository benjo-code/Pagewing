using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The cards of the save flow (<see cref="MainViewModel.PendingSave"/>): shown over the document area of the tab the question
/// is about, while that tab is the active one (other tabs stay usable; the question waits until its tab is shown again).
/// Each card is made when its first question comes, so nothing of it is built in the startup path. Closing the window with
/// unsaved changes asks first (<see cref="MainViewModel.ResolveUnsavedChangesAsync()"/>).
/// </summary>
public sealed partial class MainWindow
{
    private SaveChangesCard? _saveChangesCard;
    private SignatureWarningCard? _signatureCard;
    private FileChangedCard? _fileChangedCard;
    private SaveErrorCard? _saveErrorCard;

    // The user's close of the window was answered (or the app ends): the next close goes through without asking.
    private bool _closeConfirmed;
    private bool _askingToClose;

    /// <summary>Gets the cards of the save flow made so far (for tests: none before the first question).</summary>
    internal IEnumerable<SaveCard> SaveCards =>
        new SaveCard?[] { _saveChangesCard, _signatureCard, _fileChangedCard, _saveErrorCard }.OfType<SaveCard>();

    /// <summary>Closes the window without asking about unsaved changes (the app ends: <see cref="Services.Workspace.Dispose"/>).</summary>
    internal void CloseWithoutAsking()
    {
        _closeConfirmed = true;
        Close();
    }

    private void AttachSaveCards(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnSaveCardPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnSaveCardPropertyChanged;
        }

        UpdateSaveCards();
    }

    private void OnSaveCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PendingSave) or nameof(MainViewModel.ActiveTab))
        {
            UpdateSaveCards();
        }
    }

    private void UpdateSaveCards()
    {
        var question = _viewModel is { IsPendingSaveShown: true } viewModel ? viewModel.PendingSave : null;
        SaveCard? shown = question is null ? null : CardFor(question.Kind);
        var hadFocus = false;
        foreach (var card in SaveCards)
        {
            if (!ReferenceEquals(card, shown) && card.IsVisible)
            {
                hadFocus |= card.IsKeyboardFocusWithin;
                card.Hide();
            }
        }

        if (shown is not null && question is not null)
        {
            if (!shown.IsVisible || !ReferenceEquals(shown.Pending, question))
            {
                shown.Show(question);
            }

            return;
        }

        // The keys go back to the document when they were on the card.
        if (hadFocus && DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    private SaveCard CardFor(PendingSaveKind kind) => kind switch
    {
        PendingSaveKind.SaveChanges or PendingSaveKind.DiscardChanges => _saveChangesCard ??= AddSaveCard(new SaveChangesCard { Name = "SaveChangesCard" }),
        PendingSaveKind.SignatureWarning or PendingSaveKind.SignatureNote => _signatureCard ??= AddSaveCard(new SignatureWarningCard { Name = "SignatureWarningCard" }),
        PendingSaveKind.FileChanged => _fileChangedCard ??= AddSaveCard(new FileChangedCard { Name = "FileChangedCard" }),
        _ => _saveErrorCard ??= AddSaveCard(new SaveErrorCard { Name = "SaveErrorCard" }),
    };

    // Over the document area of the active tab, like the password, the error and the link card.
    private T AddSaveCard<T>(T card)
        where T : SaveCard
    {
        DocumentAreaLayers.Children.Add(card);
        return card;
    }

    /// <summary>
    /// The user (or the program) closes the window while tabs have unsaved changes: the close is cancelled, the tabs are
    /// asked about in turn, and the window closes when every one was saved or may go. The end of the app and of the system
    /// session close it without asking (Pagewing does not hold up a shutdown; the changes are lost there).
    /// </summary>
    /// <returns>Whether the close goes on now.</returns>
    private bool LetCloseGoOn(WindowClosingEventArgs e, MainViewModel viewModel)
    {
        if (_closeConfirmed || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown
            || !viewModel.HasUnsavedChanges)
        {
            return true;
        }

        e.Cancel = true;
        if (!_askingToClose)
        {
            _ = CloseAfterAskingAsync(viewModel);
        }

        return false;
    }

    private async Task CloseAfterAskingAsync(MainViewModel viewModel)
    {
        _askingToClose = true;
        try
        {
            // A window whose view model went meanwhile has nothing left to protect.
            if (await viewModel.ResolveUnsavedChangesAsync() || viewModel.IsDisposed)
            {
                CloseWithoutAsking();
            }
        }
        finally
        {
            _askingToClose = false;
        }
    }
}
