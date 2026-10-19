using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The settings card (<see cref="OverlayKind.Settings"/>; its DataContext is the <see cref="SettingsViewModel"/>). The
/// window shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>; Esc, the close button and "Done" raise
/// <see cref="CloseRequested"/>, and the window hides the overlay.
/// </summary>
public sealed partial class SettingsCard : UserControl
{
    /// <summary>DIPs a wheel notch scrolls the card (Avalonia's step for content without logical scrolling).</summary>
    private const double WheelStep = 50;

    public SettingsCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus
        // on the card, Esc closes an open list first and then the card (it does not reach the link card or the search),
        // and the page keys scroll the card instead of turning the pages of the document behind it.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(SettingsScroller.PageUp) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(SettingsScroller.PageDown) });
        SettingsCloseButton.Click += (_, _) => CloseRequested?.Invoke();
        SettingsDoneButton.Click += (_, _) => CloseRequested?.Invoke();
        foreach (var list in new[] { ThemeChoice, DensityChoice, LayoutChoice, ZoomChoice, LanguageChoice })
        {
            list.TemplateApplied += OnListTemplateApplied;
            list.AddHandler(PointerWheelChangedEvent, OnListWheel, RoutingStrategies.Tunnel);
        }

        foreach (var toggle in new[] { DarkPagesSwitch, ReduceMotionSwitch })
        {
            toggle.TemplateApplied += OnSwitchTemplateApplied;
        }
    }

    /// <summary>Raised when the user closes the card (Esc, the close button, "Done").</summary>
    internal event Action? CloseRequested;

    /// <summary>Shows the card at its top and puts the focus on its first option.</summary>
    internal void Show()
    {
        SettingsScroller.Offset = default;
        IsVisible = true;

        // After the layout of this change, when the option can take the focus.
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card (and a list of it that is open).</summary>
    internal void Hide()
    {
        CloseOpenLists();
        IsVisible = false;
    }

    /// <summary>Puts the keys on the first option while the card is shown.</summary>
    internal void FocusFirst()
    {
        if (IsVisible && ThemeChoice.IsEffectivelyVisible)
        {
            ThemeChoice.Focus();
        }
    }

    /// <summary>
    /// The open list of a combo box lies in the overlay layer of the window, not on the card, so the keys in it reach the
    /// window's key bindings, not the card's: Esc would close the card with the list, the page keys turn the pages of the
    /// document. Its content claims them: Esc closes only the list (the focus goes back to the box), the page keys do
    /// nothing (the lists are short). It is drawn like the card (surface.raised, 1 px border.subtle) instead of Fluent's
    /// colours, which its template sets on the part and only a local value outranks. <c>PART_Popup</c> is the popup every
    /// combo box template has.
    /// </summary>
    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is not ComboBox list || e.NameScope.Find<Popup>("PART_Popup")?.Child is not InputElement content)
        {
            return;
        }

        if (content is Border border)
        {
            border.Bind(Border.BackgroundProperty, this.GetResourceObservable("Pw.Brush.Surface.Raised"));
            border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Pw.Brush.Border.Subtle"));
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
    /// Fluent draws the knob of a switch that is on in white, too faint on the light accent of the dark theme (2.3:1): it
    /// is accent.on here (4.5:1 on accent.default in every theme), following the theme. The template sets the colour on
    /// its part, which only a local value outranks; the part <c>SwitchKnobOn</c> is named in Fluent's template
    /// (SettingsCardTests.TheCardColoursMeetAA fails if an update renames it).
    /// </summary>
    private void OnSwitchTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Ellipse>("SwitchKnobOn") is { } knob)
        {
            knob.Bind(Shape.FillProperty, this.GetResourceObservable("Pw.Brush.Accent.On"));
        }
    }

    /// <summary>
    /// A focused, closed combo box takes the wheel to change its value, which changes a setting while the user only
    /// meant to scroll the card past it. The wheel over a closed box scrolls the card instead, by the step the scroll
    /// viewer uses for content that does not scroll logically (50 DIPs a notch); an open list keeps the wheel.
    /// </summary>
    private void OnListWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false })
        {
            return;
        }

        e.Handled = true;
        var room = Math.Max(0, SettingsScroller.Extent.Height - SettingsScroller.Viewport.Height);
        var offset = SettingsScroller.Offset;
        SettingsScroller.Offset = offset.WithY(Math.Clamp(offset.Y - (e.Delta.Y * WheelStep), 0, room));
    }

    private bool CloseOpenLists()
    {
        var closed = false;
        foreach (var list in this.GetVisualDescendants().OfType<ComboBox>())
        {
            if (list.IsDropDownOpen)
            {
                list.IsDropDownOpen = false;
                closed = true;
            }
        }

        return closed;
    }

    private void OnEscape()
    {
        if (!CloseOpenLists())
        {
            CloseRequested?.Invoke();
        }
    }
}
