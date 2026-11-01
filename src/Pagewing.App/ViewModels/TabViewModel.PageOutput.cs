using System.Windows.Input;
using Pagewing.App.Resources;

namespace Pagewing.App.ViewModels;

/// <summary>What a card of the pages panel writes: new files of the open document, never the document itself.</summary>
public enum PageOutputKind
{
    /// <summary>The selected pages as one new PDF.</summary>
    Extract,

    /// <summary>The document cut into several new PDFs.</summary>
    Split,

    /// <summary>The pages as PNG images.</summary>
    ExportImages,
}

/// <summary>
/// The commands "Extract selected pages as a new PDF", "Split document" and "Export pages as images" of the tab (milestone 13,
/// spec 5.3). They read the open document <em>without</em> the plan and write only new files, so they are locked while a plan
/// changes anything or the document has unsaved changes (<see cref="PageOutputBlockedReason"/>, shown in the pages panel).
/// The selection is the one of the list in the mode "Organize pages"; page numbers are those of the document, which a plan that
/// equals the document does not change. What the commands do is the window's: it shows the card (<see cref="PageOutputRequested"/>).
/// Nothing of it is made before the first use.
/// </summary>
public sealed partial class TabViewModel
{
    private RelayCommand? _extractSelectionCommand;
    private RelayCommand? _splitCommand;
    private RelayCommand? _exportImagesCommand;

    /// <summary>The command "Extract selected pages as a new PDF …" (catalog <c>pages.extract</c>): runs with pages selected and nothing locking.</summary>
    public ICommand ExtractSelectionCommand => _extractSelectionCommand ??= new RelayCommand(
        () => PageOutputRequested?.Invoke(PageOutputKind.Extract),
        () => CanRunPageOutput && OutputSelection.Count > 0);

    /// <summary>The command "Split document into files …" (catalog <c>pages.split</c>).</summary>
    public ICommand SplitCommand => _splitCommand ??= new RelayCommand(
        () => PageOutputRequested?.Invoke(PageOutputKind.Split),
        () => CanRunPageOutput);

    /// <summary>The command "Export pages as images …" (catalog <c>pages.exportImages</c>).</summary>
    public ICommand ExportImagesCommand => _exportImagesCommand ??= new RelayCommand(
        () => PageOutputRequested?.Invoke(PageOutputKind.ExportImages),
        () => CanRunPageOutput);

    /// <summary>Raised by the three commands: the user wants the card of that kind.</summary>
    internal event Action<PageOutputKind>? PageOutputRequested;

    /// <summary>
    /// Gets the pages (0-based, ascending) the cards treat as selected: the selection of the list while the mode "Organize pages"
    /// is on and the plan changes nothing (then a position is a page of the document), else none.
    /// </summary>
    internal IReadOnlyList<int> OutputSelection =>
        _isOrganizing && !HasPendingPageEdits && _selectedPages.Count > 0
            ? [.. _selectedPages.Where(position => position >= 0 && position < PageCount).Order()]
            : [];

    /// <summary>Gets why extracting the selection cannot run now (the lock, else the missing selection), or <see langword="null"/>.</summary>
    public string? ExtractUnavailableReason =>
        PageOutputBlockedReason ?? (OutputSelection.Count == 0 ? Strings.ExtractNeedsSelection : null);

    /// <summary>
    /// Gets the reason to show in words under the buttons of the pages panel, or <see langword="null"/>: <see cref="PageOutputBlockedReason"/>
    /// except while a plan waits in the mode "Organize pages", whose own note ("Page changes are waiting to be applied.") and the
    /// buttons "Apply" and "Discard" beside it already say that (the help text of the menu button still names it).
    /// </summary>
    public string? PageOutputReasonText => _isOrganizing && HasPendingPageEdits ? null : PageOutputBlockedReason;

    // Raised with every change of the lock and of the selection.
    private void RaisePageOutputChanged()
    {
        OnPropertyChanged(nameof(PageOutputBlockedReason));
        OnPropertyChanged(nameof(CanRunPageOutput));
        OnPropertyChanged(nameof(PageOutputReasonText));
        OnPropertyChanged(nameof(ExtractUnavailableReason));
        _extractSelectionCommand?.RaiseCanExecuteChanged();
        _splitCommand?.RaiseCanExecuteChanged();
        _exportImagesCommand?.RaiseCanExecuteChanged();
    }
}
