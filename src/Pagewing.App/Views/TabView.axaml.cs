using Avalonia.Automation;
using Avalonia.Controls;
using Pagewing.App.Resources;

namespace Pagewing.App.Views;

/// <summary>
/// One tab of the tab strip; its <c>DataContext</c> is a <see cref="ViewModels.TabViewModel"/>. The strip keeps one
/// active view for whichever tab is active (with the element names of the M3 tab: <c>Tab</c>, <c>TabName</c>,
/// <c>CloseTabButton</c>, <c>TabDocumentIcon</c>, <c>TabLockIcon</c>, <c>TabErrorIcon</c>) and one inactive view per
/// other tab, which drops those names so they stay unique in the window.
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
        IsActiveView = isActive;
        Tab.Classes.Set("active", isActive);

        // Ctrl+W closes the active tab only, so only its close button names the shortcut.
        var closeText = isActive ? Strings.CloseTab : Strings.CloseTabTooltip;
        ToolTip.SetTip(CloseTabButton, closeText);
        AutomationProperties.SetName(CloseTabButton, closeText);

        if (!isActive)
        {
            // Not styled yet (the view is not in a tree), so the names may still change.
            foreach (var named in new Control[] { Tab, TabName, CloseTabButton, TabDocumentIcon, TabLockIcon, TabErrorIcon })
            {
                named.Name = null;
            }
        }
    }

    /// <summary>Gets whether this is the view of the active tab.</summary>
    public bool IsActiveView { get; }
}
