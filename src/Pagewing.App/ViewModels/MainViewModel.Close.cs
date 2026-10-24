using System.Windows.Input;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Closing with unsaved changes: before a tab with changes closes, and before the window closes with such tabs, the card
/// "Save the changes?" asks for each of them in turn (<see cref="ResolveUnsavedChangesAsync"/>); a tab whose changes cannot
/// be saved warns that they are lost instead (<see cref="PendingSaveKind.DiscardChanges"/>). "Cancel" keeps everything open.
/// </summary>
public sealed partial class MainViewModel
{
    // The window asks before closing tabs (one flow at a time).
    private bool _resolving;
    private RelayCommand? _cancelPendingSaveCommand;

    /// <summary>
    /// Gets whether a tab of this window has unsaved changes, so closing the window asks first
    /// (<see cref="ResolveUnsavedChangesAsync"/>).
    /// </summary>
    public bool HasUnsavedChanges => !_disposed && Tabs.Any(tab => tab.AsksBeforeClosing);

    /// <summary>Gets whether the window was disposed (its tabs are released; nothing is left to ask about).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Gets whether the card of <see cref="PendingSave"/> is shown now: a question waits and belongs to the active tab (a
    /// question of another tab waits until that tab is shown again).
    /// </summary>
    internal bool IsPendingSaveShown => _pendingSave is { IsResolved: false } question && ReferenceEquals(question.Tab, ActiveTab);

    /// <summary>
    /// Esc while a card of the save flow is shown, wherever the focus is: answers "Cancel" (every card offers it). Without a
    /// shown card the key goes on.
    /// </summary>
    public ICommand CancelPendingSaveCommand => _cancelPendingSaveCommand ??=
        new RelayCommand(() => _pendingSave?.Resolve(PendingSaveChoice.Cancel), () => IsPendingSaveShown);

    /// <summary>
    /// Asks about every tab of this window with unsaved changes in turn, the active one first, then the others from left to
    /// right: each is shown and its card asks "Save", "Don't save" or "Cancel" (a tab whose changes cannot be saved:
    /// "Close and discard changes" or "Cancel"); "Save" saves it (with the questions of the save, <see cref="SaveTabAsync"/>).
    /// A save that runs ends first. Nothing is closed here: the caller closes the tabs or the window when the answer is
    /// <see langword="true"/>, and their changes go with them.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every tab was saved or its changes may go; <see langword="false"/> as soon as the user
    /// cancels, a save fails or is cancelled, another flow asks already, or the window went meanwhile.
    /// </returns>
    public Task<bool> ResolveUnsavedChangesAsync() => ResolveAsync(null);

    /// <summary>A tab with unsaved changes is to close: asks first (<see cref="ResolveAsync"/>), then closes it unless cancelled.</summary>
    private async Task CloseAfterAskingAsync(TabViewModel tab)
    {
        if (await ResolveAsync(tab))
        {
            CloseTabNow(tab);
        }
    }

    /// <summary>The flow of <see cref="ResolveUnsavedChangesAsync"/>, for every tab with changes or only <paramref name="only"/>.</summary>
    private async Task<bool> ResolveAsync(TabViewModel? only)
    {
        if (_disposed || _resolving)
        {
            return false;
        }

        _resolving = true;
        try
        {
            // A save that runs (it may wait on its own card) ends first; its tab is shown, so the user sees what waits.
            while (_saveRun is { IsCompleted: false } running)
            {
                if (_pendingSave is { } waiting && Tabs.IndexOf(waiting.Tab) >= 0)
                {
                    Tabs.Activate(waiting.Tab);
                }

                await running;
            }

            TabViewModel[] order = only is not null
                ? [only]
                : Tabs.Where(tab => ReferenceEquals(tab, ActiveTab)).Concat(Tabs.Where(tab => !ReferenceEquals(tab, ActiveTab))).ToArray();
            foreach (var tab in order)
            {
                if (_disposed)
                {
                    return false;
                }

                if (Tabs.IndexOf(tab) < 0 || !tab.AsksBeforeClosing)
                {
                    continue;
                }

                if (!await AskBeforeClosingAsync(tab))
                {
                    return false;
                }
            }

            return !_disposed;
        }
        finally
        {
            _resolving = false;
        }
    }

    /// <summary>Shows <paramref name="tab"/> and asks about its changes; "Save" saves it.</summary>
    /// <returns>Whether the tab may close.</returns>
    private async Task<bool> AskBeforeClosingAsync(TabViewModel tab)
    {
        Tabs.Activate(tab);
        var path = tab.FullPath ?? tab.FileName ?? string.Empty;
        var question = tab.UnsaveableReason is { } reason
            ? PendingSave.DiscardChanges(tab, path, reason)
            : PendingSave.SaveChanges(tab, path);
        switch (await AskAsync(question, CancellationToken.None))
        {
            case PendingSaveChoice.Save:
                // Saved (also under another name after the signature card): the tab is clean and may close.
                return await SaveTabAsync(tab, saveAs: false) && !tab.AsksBeforeClosing;
            case PendingSaveChoice.DontSave or PendingSaveChoice.Discard:
                return true;
            default:
                return false;
        }
    }
}
