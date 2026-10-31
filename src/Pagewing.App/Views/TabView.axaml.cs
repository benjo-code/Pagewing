using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Pagewing.App.Resources;

namespace Pagewing.App.Views;

/// <summary>
/// One tab of the tab strip; its <c>DataContext</c> is a <see cref="ViewModels.TabViewModel"/>. The strip keeps one
/// active view for whichever tab is active (with the element names of the M3 tab: <c>Tab</c>, <c>TabName</c>,
/// <c>CloseTabButton</c>, <c>TabDocumentIcon</c>, <c>TabLockIcon</c>, <c>TabErrorIcon</c>, since M9 <c>TabUnsavedDot</c>) and one inactive view per
/// other tab, which drops those names so they stay unique in the window. To screen readers each view is a tab item
/// named after its file, selected when it is the active view (<see cref="TabViewAutomationPeer"/>).
/// </summary>
public sealed partial class TabView : Decorator
{
    /// <summary>Creates the view of an inactive tab (the parameterless constructor the XAML loader asks for).</summary>
    public TabView()
        : this(isActive: false)
    {
    }

    /// <param name="isActive">
    /// Whether this is the view of the active tab: the colour of the document area, bold text and a close button
    /// that names Ctrl+W.
    /// </param>
    public TabView(bool isActive)
    {
        InitializeComponent();
        IsActive = isActive;
        Tab.Classes.Set("active", isActive);

        // Ctrl+W closes the active tab only, so only its close button names the shortcut.
        var closeText = isActive ? Strings.CloseTab : Strings.CloseTabTooltip;
        ToolTip.SetTip(CloseTabButton, closeText);
        AutomationProperties.SetName(CloseTabButton, closeText);

        if (!isActive)
        {
            // Not styled yet (the view is not in a tree), so the names may still change.
            foreach (var named in new Control[] { Tab, TabName, CloseTabButton, TabDocumentIcon, TabLockIcon, TabErrorIcon, TabUnsavedDot })
            {
                named.Name = null;
            }
        }
    }

    /// <summary>Gets whether this is the view of the active tab (it stays so while the active tab changes).</summary>
    internal bool IsActive { get; }

    /// <summary>Gets the border of the tab, which carries its name for screen readers.</summary>
    internal Border TabBorder => Tab;

    /// <summary>Activates the tab of this view, as a press on it does (a screen reader's "select").</summary>
    internal void Activate() => this.FindAncestorOfType<TabStrip>()?.Activate(this);

    protected override AutomationPeer OnCreateAutomationPeer() => new TabViewAutomationPeer(this);
}
