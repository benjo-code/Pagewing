using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Pagewing.App.Views;

/// <summary>
/// A line of the command palette as screen readers see it: an entry is a list item (selected while it is the chosen
/// one, which the input keeps when the arrows move the choice), a group heading is a text with its heading level and
/// cannot be selected. Its name is the line's automation name. Created only when an assistive technology asks.
/// </summary>
internal sealed class PaletteRowAutomationPeer(PaletteRow owner) : ControlAutomationPeer(owner), ISelectionItemProvider
{
    public bool IsSelected => Row.IsItem && Row.IsChosen;

    // The list of the palette has no element of its own; the input around which it is built keeps the focus.
    public ISelectionProvider? SelectionContainer => null;

    private PaletteRow Row => (PaletteRow)Owner;

    public void Select()
    {
        EnsureEnabled();
        if (Row.IsItem)
        {
            Row.Choose?.Invoke();
        }
    }

    // One entry is chosen at a time.
    void ISelectionItemProvider.AddToSelection() => Select();

    void ISelectionItemProvider.RemoveFromSelection()
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        Row.IsItem ? AutomationControlType.ListItem : AutomationControlType.Text;

    protected override object? GetProviderCore(Type providerType) =>
        providerType == typeof(ISelectionItemProvider) && !Row.IsItem ? null : base.GetProviderCore(providerType);
}
