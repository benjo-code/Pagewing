using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Pagewing.App.Controls;

/// <summary>
/// A panel entry of the action bar: to screen readers a toggle button (UI Automation toggle pattern, AT-SPI "toggle
/// button") that is on while its panel is open; on the screen exactly an icon button. It takes the theme and the styles
/// of <see cref="Button"/> (<see cref="StyleKeyOverride"/>), so the checked state of the Fluent toggle button never fills
/// it with the accent colour, and the "active" class marks the open panel as before (design system 7.3 and 7.10).
/// A click does not flip <see cref="ToggleButton.IsChecked"/> itself: only the command changes the panel, and the bound
/// <c>IsChecked</c> follows the panel however it is opened or closed, so the state can never drift from it.
/// </summary>
public sealed class ActionToggleButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override void Toggle()
    {
        // The command toggles the panel; IsChecked is bound to its state.
    }
}
