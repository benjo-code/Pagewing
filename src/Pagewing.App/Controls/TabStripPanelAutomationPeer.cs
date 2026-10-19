using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Pagewing.App.Controls;

/// <summary>
/// The tab strip as screen readers see it: a tab list (UI Automation "Tab", AT-SPI "page tab list") whose children are
/// the tabs and whose one selected tab is the active one (<see cref="TabStripPanel.ActiveItem"/>). Created only when an
/// assistive technology asks, never in the startup path.
/// </summary>
internal sealed class TabStripPanelAutomationPeer(TabStripPanel owner) : ControlAutomationPeer(owner), ISelectionProvider
{
    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => true;

    public IReadOnlyList<AutomationPeer> GetSelection()
    {
        var panel = (TabStripPanel)Owner;
        return panel.ActiveItem is { } active && panel.Children.Contains(active) ? [GetOrCreate(active)] : [];
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
}
