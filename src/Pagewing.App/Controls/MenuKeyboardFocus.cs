using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Pagewing.App.Controls;

/// <summary>
/// Shows where the keyboard is in a menu (design system 5.3 and 7.9): Avalonia marks an entry reached with the arrow
/// keys only as selected, never as focus-visible, so a key pressed in a menu (<see cref="MenuFlyoutPresenter"/>,
/// <see cref="ContextMenu"/>) gives it the class <see cref="KeyboardClass"/>, which draws the focus ring around its
/// selected entry (Styles/Controls.axaml). The pointer moving over the menu and closing it take the class away again,
/// so a menu opened with the mouse never shows the ring. UI thread only.
/// </summary>
internal static class MenuKeyboardFocus
{
    /// <summary>The class of a menu that is used with the keyboard.</summary>
    public const string KeyboardClass = "keyboard";

    private static bool s_registered;

    /// <summary>
    /// Registers the class handlers, once. Called when a menu is about to open, so nothing of it is part of the startup
    /// path.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (s_registered)
        {
            return;
        }

        s_registered = true;
        InputElement.KeyDownEvent.AddClassHandler<MenuBase>(OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<MenuBase>(OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnKeyDown(MenuBase menu, KeyEventArgs e)
    {
        if (!menu.Classes.Contains(KeyboardClass))
        {
            menu.Classes.Add(KeyboardClass);
            menu.DetachedFromVisualTree += OnDetached;
        }
    }

    private static void OnPointerMoved(MenuBase menu, PointerEventArgs e) => Clear(menu);

    // The menu closed: the next time it opens, it starts without the ring.
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is MenuBase menu)
        {
            Clear(menu);
        }
    }

    private static void Clear(MenuBase menu)
    {
        if (menu.Classes.Remove(KeyboardClass))
        {
            menu.DetachedFromVisualTree -= OnDetached;
        }
    }
}
