using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Pagewing.App.Views;

/// <summary>
/// A tab of the tab strip as screen readers see it (design system 7.2 and 7.10): a tab item (UI Automation "TabItem",
/// AT-SPI "page tab") named after its file, selected when it is the view of the active tab, in the tab list of the
/// strip. The strip keeps one view for the active tab whichever tab that is, so that view stays selected and its name
/// changes with the active tab; the peer reports the change. Created only when an assistive technology asks.
/// </summary>
internal sealed class TabViewAutomationPeer : ControlAutomationPeer, ISelectionItemProvider
{
    public TabViewAutomationPeer(TabView owner)
        : base(owner)
    {
        owner.TabBorder.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationProperties.NameProperty)
            {
                RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, e.OldValue, e.NewValue);
            }
        };
    }

    public bool IsSelected => View.IsActive;

    public ISelectionProvider? SelectionContainer =>
        View.GetVisualParent() is Control strip ? GetOrCreate(strip).GetProvider<ISelectionProvider>() : null;

    private TabView View => (TabView)Owner;

    public void Select()
    {
        EnsureEnabled();
        if (!View.IsActive)
        {
            View.Activate();
        }
    }

    // One tab is always selected: adding a tab selects it, and the selected tab cannot be taken out of the selection.
    void ISelectionItemProvider.AddToSelection() => Select();

    void ISelectionItemProvider.RemoveFromSelection()
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

    protected override string? GetNameCore()
    {
        var name = AutomationProperties.GetName(View);
        return string.IsNullOrWhiteSpace(name) ? AutomationProperties.GetName(View.TabBorder) : name;
    }
}
