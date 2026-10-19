using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// What the cards that hold combo boxes (the settings card, the print card) do alike. The open list of a combo box lies in
/// the overlay layer of the window, not on the card, so the keys in it reach the window's key bindings, not the card's:
/// Esc would close the card with the list, the page keys turn the pages of the document.
/// </summary>
internal static class CardLists
{
    /// <summary>
    /// Makes the content of the open list of <paramref name="list"/> claim the keys: Esc closes only the list (the focus goes
    /// back to the box), the page keys do nothing (the lists are short). The list is drawn like the card (surface.raised,
    /// 1 px border.subtle) instead of Fluent's colours, which its template sets on the part and only a local value outranks.
    /// <c>PART_Popup</c> is the popup every combo box template has.
    /// </summary>
    public static void OnTemplateApplied(Control card, ComboBox list, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Popup>("PART_Popup")?.Child is not InputElement content)
        {
            return;
        }

        if (content is Border border)
        {
            border.Bind(Border.BackgroundProperty, card.GetResourceObservable("Pw.Brush.Surface.Raised"));
            border.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Pw.Brush.Border.Subtle"));
        }

        var close = new RelayCommand(() =>
        {
            list.IsDropDownOpen = false;
            list.Focus();
        });
        var nothing = new RelayCommand(static () => { });
        content.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = close });
        content.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = nothing });
        content.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = nothing });
    }

    /// <summary>
    /// A focused, closed combo box takes the wheel to change its value, which changes a choice while the user only meant to
    /// scroll the card past it. The wheel over a closed box scrolls <paramref name="scroller"/> instead, by
    /// <paramref name="step"/> DIPs a notch; an open list keeps the wheel.
    /// </summary>
    public static void OnListWheel(ScrollViewer scroller, double step, object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false })
        {
            return;
        }

        e.Handled = true;
        var room = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var offset = scroller.Offset;
        scroller.Offset = offset.WithY(Math.Clamp(offset.Y - (e.Delta.Y * step), 0, room));
    }
}
