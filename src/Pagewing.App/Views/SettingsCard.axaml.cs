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
        foreach (var list in new[] { ThemeChoice, DensityChoice, LayoutChoice, ZoomChoice, PrintScalingChoice, LanguageChoice })
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

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is ComboBox list)
        {
            CardLists.OnTemplateApplied(this, list, e);
        }
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

    private void OnListWheel(object? sender, PointerWheelEventArgs e) =>
        CardLists.OnListWheel(SettingsScroller, WheelStep, sender, e);

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
