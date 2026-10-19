using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Pagewing.App.Views;

/// <summary>
/// A line of the command palette: a group heading or an entry (<see cref="CommandPalette"/> fills and reuses it). On
/// the screen a plain border (it takes the styles of <see cref="Border"/>; the chosen entry has the class "selected").
/// To screen readers an entry is a list item that is selected while it is the chosen one, a heading is a heading
/// (<see cref="PaletteRowAutomationPeer"/>).
/// </summary>
internal sealed class PaletteRow : Border
{
    /// <summary>Gets or sets whether the line shows an entry (not a heading).</summary>
    public bool IsItem { get; set; }

    /// <summary>Gets whether the line is the chosen entry.</summary>
    public bool IsChosen { get; private set; }

    /// <summary>Gets or sets what makes this line the chosen entry (a screen reader's "select").</summary>
    public Action? Choose { get; set; }

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>Marks the line as the chosen entry or not, and tells a screen reader that listens.</summary>
    public void SetChosen(bool chosen)
    {
        Classes.Set("selected", chosen);
        if (chosen == IsChosen)
        {
            return;
        }

        IsChosen = chosen;
        ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !chosen, chosen);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PaletteRowAutomationPeer(this);
}
