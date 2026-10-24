using Avalonia.Automation;
using Avalonia.Controls;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>The action bar left of the panel and the document area; its <c>DataContext</c> is the window's view model.</summary>
public sealed partial class ActionBar : UserControl
{
    public ActionBar() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel window)
        {
            NameShortcuts(window.Commands);
        }
    }

    /// <summary>
    /// The tooltips of "Pages", "Contents" and "Comments" name their shortcuts as the catalog has them ("Seiten (Strg+Umschalt+1)"),
    /// so a changed gesture cannot leave a stale text; the names stay what the entries are, and every panel entry tells
    /// screen readers its shortcut as the accelerator key. The window built the catalog already, for its key bindings.
    /// </summary>
    private void NameShortcuts(CommandCatalog catalog)
    {
        Describe(PagesButton, Strings.PagesTooltip, catalog.ById("panel.pages"));
        Describe(ContentsButton, Strings.ContentsTooltip, catalog.ById("panel.contents"));
        Describe(CommentsButton, Strings.CommentsPanelTitle, catalog.ById("panel.comments"));
        Describe(PrintButton, Strings.PrintTitle, catalog.ById("file.print"));
        if (catalog.ById("search.open")?.Gesture is { } search)
        {
            AutomationProperties.SetAcceleratorKey(SearchButton, GestureText.Of(search));
        }

        static void Describe(Control entry, string label, AppCommand? command)
        {
            if (command?.Gesture is not { } gesture)
            {
                ToolTip.SetTip(entry, label);
                return;
            }

            var keys = GestureText.Of(gesture);
            ToolTip.SetTip(entry, $"{label} ({keys})");
            AutomationProperties.SetAcceleratorKey(entry, keys);
        }
    }
}
