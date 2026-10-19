using Avalonia.Automation;
using Avalonia.Controls;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

public sealed partial class TitleBar : UserControl
{
    public TitleBar() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel window)
        {
            NameShortcut(window.Commands);
        }
    }

    /// <summary>
    /// The button "Search or command" shows and names the shortcut of the command palette as the catalog has it
    /// ("Strg+K"), so a changed gesture cannot leave a stale text (as the action bar does, <see cref="ActionBar"/>). Its
    /// title stays "Search or command", not the catalog's "Command palette": the button says what it is for. The window
    /// built the catalog already, for its key bindings.
    /// </summary>
    private void NameShortcut(CommandCatalog catalog)
    {
        if (catalog.ById("palette.show")?.Gesture is not { } gesture)
        {
            ToolTip.SetTip(PaletteButton, Strings.PaletteButton);
            PaletteKbdText.Text = string.Empty;
            return;
        }

        var keys = GestureText.Of(gesture);
        ToolTip.SetTip(PaletteButton, $"{Strings.PaletteButton} ({keys})");
        PaletteKbdText.Text = keys;
        AutomationProperties.SetAcceleratorKey(PaletteButton, keys);
    }
}
