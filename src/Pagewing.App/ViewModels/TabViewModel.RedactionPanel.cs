using System.Windows.Input;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The panel "Redact" of the tab (milestone 14, task 12b): the list of the marks of the plan and "find and redact". The toolbar of the
/// mode shows it (<see cref="ToggleRedactionPanelCommand"/>); it exists only while the mode is on, and nothing of it is made before it
/// first opens.
/// </summary>
public sealed partial class TabViewModel
{
    // Made on the first access (only the panel gets here, never the startup path), dropped when the mode ends.
    private RedactionViewModel? _redactionPanel;

    // The panel shown before "Redact" opened, shown again when it closes (only "Pages" and "Contents" come back).
    private SidePanelKind _panelBeforeRedact;

    // The toolbar asked for the focus in the search field; the panel takes it once it is shown.
    private bool _redactionSearchFocusPending;
    private RelayCommand? _toggleRedactionPanelCommand;

    /// <summary>
    /// Gets the model of the panel: the rows of the marks, the search and its hits. Made on the first access, which only the panel makes
    /// (and only while the mode is on), and dropped, with a search that may still run, when the mode ends.
    /// </summary>
    public RedactionViewModel RedactionPanel => _redactionPanel ??= new RedactionViewModel(this);

    /// <summary>Gets whether the panel "Redact" is the one shown beside the document (a state of the toolbar button).</summary>
    public bool IsRedactionPanelOpen => _sidePanel == SidePanelKind.Redact;

    /// <summary>
    /// The command of the button "Find and redact" of the toolbar (and of the catalog): shows the panel with the focus in its search
    /// field, or closes it again. Runs only in the mode.
    /// </summary>
    public ICommand ToggleRedactionPanelCommand => _toggleRedactionPanelCommand ??= new RelayCommand(ToggleRedactionPanel, () => !_disposed && _isRedacting);

    /// <summary>Gets whether the model of the panel was made (for tests: nothing makes it before the panel opens).</summary>
    internal bool IsRedactionPanelCreated => _redactionPanel is not null;

    /// <summary>Raised when the search field of the panel should take the keyboard focus with its content selected.</summary>
    internal event Action? RedactionSearchFocusRequested;

    /// <summary>Gets whether the toolbar asked for the focus in the search field and the panel has not given it yet.</summary>
    internal bool IsRedactionSearchFocusPending => _redactionSearchFocusPending;

    /// <summary>The panel gave the focus to its field: the request is done.</summary>
    internal void ClearRedactionSearchFocusRequest() => _redactionSearchFocusPending = false;

    /// <summary>
    /// Adds marks that a search made, in one step of the history, and selects them. All or nothing: a plan that would hold more marks
    /// than allowed takes none and says so (<see cref="RedactionNotice"/>).
    /// </summary>
    /// <param name="marks">The marks.</param>
    /// <returns>Whether they were added.</returns>
    internal bool AddRedactionMarks(IReadOnlyList<RedactionMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        return marks.Count > 0 && StageRedaction(plan => plan.WithRange(marks), marks.Select(static mark => mark.Id));
    }

    private void ToggleRedactionPanel()
    {
        if (_sidePanel == SidePanelKind.Redact)
        {
            SidePanel = PanelAfterRedaction();
            return;
        }

        SidePanel = SidePanelKind.Redact;
        _redactionSearchFocusPending = true;
        RedactionSearchFocusRequested?.Invoke();
    }

    // Called by the setter of SidePanel before anything else: the panel became the shown one, or stopped being it.
    private void OnRedactionPanelChanged(SidePanelKind old, SidePanelKind value)
    {
        if (old != SidePanelKind.Redact && value != SidePanelKind.Redact)
        {
            return;
        }

        if (value == SidePanelKind.Redact)
        {
            _panelBeforeRedact = old;
        }
        else
        {
            // A search that still runs is stopped: nobody looks at its hits, and it must not go on reading the document.
            _redactionSearchFocusPending = false;
            _redactionPanel?.OnPanelHidden();
        }

        OnPropertyChanged(nameof(IsRedactionPanelOpen));
    }

    private SidePanelKind PanelAfterRedaction() =>
        _panelBeforeRedact is SidePanelKind.Pages or SidePanelKind.Contents ? _panelBeforeRedact : SidePanelKind.None;

    // The mode ended (or the document went): no panel of it any more.
    private void DiscardRedactionPanel()
    {
        var panel = _redactionPanel;
        _redactionPanel = null;
        panel?.Dispose();
        _redactionSearchFocusPending = false;
        if (_sidePanel == SidePanelKind.Redact && !_disposed)
        {
            SidePanel = PanelAfterRedaction();
        }
    }
}
