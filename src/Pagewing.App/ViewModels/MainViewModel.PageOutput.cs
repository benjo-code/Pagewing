using System.ComponentModel;
using Pagewing.App.Services;
using Pagewing.Core.Pages;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The cards "Extract selected pages", "Split document" and "Export pages as images" in a window (milestone 13, spec 5.3): a tab's
/// command asks for the card (<see cref="TabViewModel.PageOutputRequested"/>), the window reads the document the tab shows
/// together with its file (<see cref="DocumentSplitter.SourceOf"/>) and the selection, and shows the card as the overlay
/// <see cref="OverlayKind.PageOutput"/>. The card is the job: while files are being written no other overlay replaces it
/// (<see cref="Overlay"/>), it ends with the tab that wrote them, and closing the window cancels the run. Nothing of it is made
/// before the first use.
/// </summary>
public sealed partial class MainViewModel
{
    private PageOutputViewModel? _pageOutput;
    private TabViewModel? _pageOutputTab;
    private PropertyChangedEventHandler? _pageOutputTabHandler;

    /// <summary>Gets the card that is shown (its view model), or <see langword="null"/>; a new one is made for each use.</summary>
    public PageOutputViewModel? PageOutput => _pageOutput;

    /// <summary>
    /// Gets or sets what shows the folder dialog for the cards (the dialog of the window's file picker when <see langword="null"/>; tests
    /// answer without a dialog).
    /// </summary>
    internal Func<string?, Task<string?>>? FolderPicker { get; set; }

    private bool IsPageOutputRunning => _pageOutput is { IsRunning: true };

    /// <summary>Shows the card of <paramref name="kind"/> for <paramref name="tab"/> when the commands may run now.</summary>
    internal void ShowPageOutput(TabViewModel tab, PageOutputKind kind)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || Tabs.IndexOf(tab) < 0 || !tab.CanRunPageOutput || IsPageOutputRunning)
        {
            return;
        }

        if (kind == PageOutputKind.Extract && tab.OutputSelection.Count == 0)
        {
            return;
        }

        // The document the user sees with the file it was opened from, read together (a tab that closed meanwhile has none).
        if (DocumentSplitter.SourceOf(tab.Session) is not { } source)
        {
            return;
        }

        var selection = tab.OutputSelection;
        var input = new PageOutputSource(source.Document, source.File, selection, SaveEngine);
        PageOutputViewModel card = kind == PageOutputKind.ExportImages
            ? new ExportImagesViewModel(input, PickOutputFolderAsync)
            : new SplitViewModel(input, kind == PageOutputKind.Extract, PickOutputFolderAsync, PickOutputFileAsync);
        card.Closed += HideOverlay;

        // The tab decides whether its document still allows it: a change of the lock or of the document ends the card.
        _pageOutputTabHandler = (_, e) =>
        {
            if (_pageOutput is { IsFinished: false }
                && (e.PropertyName == nameof(TabViewModel.Document) || (e.PropertyName == nameof(TabViewModel.CanRunPageOutput) && !tab.CanRunPageOutput)))
            {
                CancelPageOutputOf(tab);
            }
        };
        tab.PropertyChanged += _pageOutputTabHandler;
        _pageOutput = card;
        _pageOutputTab = tab;
        ShowOverlay(OverlayKind.PageOutput);
        if (card is SplitViewModel split)
        {
            _ = split.BeginAsync();
        }
    }

    private Task<string?> PickOutputFolderAsync(string? start) =>
        FolderPicker is { } pick ? pick(start) : _picker is IFolderPicker folders ? folders.PickFolderAsync(start) : Task.FromResult<string?>(null);

    private Task<string?> PickOutputFileAsync(string suggestedName, string? folder) => _picker.PickSavePdfAsync(suggestedName, folder);

    /// <summary>The card of <paramref name="tab"/> ends: a run stops at its next file, and the card goes (nothing was written by the cancel itself).</summary>
    private void CancelPageOutputOf(TabViewModel? tab)
    {
        if (_pageOutput is null || (tab is not null && !ReferenceEquals(_pageOutputTab, tab)))
        {
            return;
        }

        // The overlay is hidden for good, whatever the run does: its files stay, the card is gone.
        _pageOutput.CancelRun();
        _pageOutputForced = true;
        try
        {
            if (Overlay == OverlayKind.PageOutput)
            {
                HideOverlay();
            }
        }
        finally
        {
            _pageOutputForced = false;
        }

        DisposePageOutput();
    }

    // True while the window itself ends the card (the tab went), which no running job may refuse.
    private bool _pageOutputForced;

    /// <summary>Lets go of the card and cancels its run (the overlay went, the tab went, the window closes).</summary>
    private void DisposePageOutput()
    {
        if (_pageOutputTab is { } tab && _pageOutputTabHandler is { } handler)
        {
            tab.PropertyChanged -= handler;
        }

        _pageOutputTabHandler = null;
        _pageOutputTab = null;
        var card = _pageOutput;
        _pageOutput = null;
        card?.Dispose();
    }
}
