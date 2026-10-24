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
    /// A save that runs ends first. Then the tabs are looked at again until none is left to ask about: a tab changed after
    /// its "Don't save", or one that came from another window meanwhile, is asked (again). Nothing is closed here: the caller closes the tabs or the window when the answer is
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

    /// <summary>
    /// The flow of <see cref="ResolveUnsavedChangesAsync"/>, for every tab with changes or only <paramref name="only"/>. It
    /// asks again and again until a pass finds no tab left to ask about: the other tabs stay usable meanwhile, so a tab
    /// may change after its answer, or come from another window. A "Don't save" counts only for the version of the tab's
    /// history it was given for (<see cref="TabViewModel.EditVersion"/>); a later change asks again. Each round asks the
    /// user, and "Cancel" ends it, so it ends.
    /// </summary>
    private async Task<bool> ResolveAsync(TabViewModel? only)
    {
        if (_disposed || _resolving)
        {
            return false;
        }

        _resolving = true;
        var discarded = new Dictionary<TabViewModel, int>();
        try
        {
            while (!_disposed)
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

                if (_disposed)
                {
                    break;
                }

                // A change in flight (a markup made a moment ago, on its way to the editor) ends first, so the question
                // sees it and is not skipped; it takes milliseconds.
                if (Tabs.FirstOrDefault(tab => (only is null || ReferenceEquals(tab, only)) && tab.HasEditsInFlight) is { } busy)
                {
                    await busy.WhenEditsSettledAsync();
                    continue;
                }

                if (NextToAsk(only, discarded) is not { } tab)
                {
                    return true;
                }

                if (!await AskBeforeClosingAsync(tab, discarded))
                {
                    return false;
                }
            }

            return false;
        }
        finally
        {
            _resolving = false;
        }
    }

    /// <summary>
    /// The next tab to ask about, the active one first, then from left to right: it has unsaved changes and no "Don't save"
    /// for its present version.
    /// </summary>
    private TabViewModel? NextToAsk(TabViewModel? only, Dictionary<TabViewModel, int> discarded)
    {
        IEnumerable<TabViewModel> candidates = only is not null
            ? Tabs.Where(tab => ReferenceEquals(tab, only))
            : Tabs.Where(tab => ReferenceEquals(tab, ActiveTab)).Concat(Tabs.Where(tab => !ReferenceEquals(tab, ActiveTab)));
        return candidates.FirstOrDefault(tab =>
            tab.AsksBeforeClosing && !(discarded.TryGetValue(tab, out var version) && version == tab.EditVersion));
    }

    /// <summary>
    /// Shows <paramref name="tab"/> and asks about its changes. "Save" saves it; a save that cannot run now (the tab
    /// prints, for example) says why on a card and keeps the tab, unless the tab is clean by then. "Don't save" is
    /// remembered with the version of the tab's history.
    /// </summary>
    /// <returns>Whether the flow goes on (the tab was saved or its changes may go).</returns>
    private async Task<bool> AskBeforeClosingAsync(TabViewModel tab, Dictionary<TabViewModel, int> discarded)
    {
        Tabs.Activate(tab);
        var path = tab.FullPath ?? tab.FileName ?? string.Empty;
        var question = tab.UnsaveableReason is { } reason
            ? PendingSave.DiscardChanges(tab, path, reason)
            : PendingSave.SaveChanges(tab, path);
        switch (await AskAsync(question, CancellationToken.None))
        {
            case PendingSaveChoice.Save:
                // A change made while the card was shown is saved with the others.
                await tab.WhenEditsSettledAsync();
                if (!CanSaveNow(tab))
                {
                    if (!tab.AsksBeforeClosing)
                    {
                        // Clean meanwhile: nothing to save.
                        return true;
                    }

                    await AskAsync(PendingSave.SaveNotPossible(tab, path, printing: tab.IsPrinting), CancellationToken.None);
                    return false;
                }

                // Saved (also under another name after the signature card): the next pass asks again if it changed since.
                // Not saved: the save showed why (its card) or the user cancelled there.
                return await SaveTabAsync(tab, saveAs: false);
            case PendingSaveChoice.DontSave or PendingSaveChoice.Discard:
                discarded[tab] = tab.EditVersion;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether <see cref="SaveTabAsync"/> would start a save of <paramref name="tab"/> now (it refuses silently otherwise).</summary>
    private bool CanSaveNow(TabViewModel tab) =>
        !_disposed && _savingTab is null && _pendingSave is null && Tabs.IndexOf(tab) >= 0 && tab.CanSave()
        && tab.Document is not null && tab.FullPath is not null;
}
