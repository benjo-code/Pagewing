using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The print dialog (<see cref="OverlayKind.Print"/>; its DataContext is the <see cref="PrintViewModel"/> of the showing).
/// The window shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>; Esc and the buttons run the commands of
/// the view model, whose events the window listens to.
/// </summary>
public sealed partial class PrintCard : UserControl
{
    /// <summary>DIPs a wheel notch scrolls the card (Avalonia's step for content without logical scrolling).</summary>
    private const double WheelStep = 50;

    public PrintCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus on
        // the card, Esc closes an open list first and then the card, Enter prints, and the page keys scroll the card
        // instead of turning the pages of the document behind it.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(OnEnter) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(PrintScroller.PageUp) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(PrintScroller.PageDown) });
        foreach (var list in new[] { PrinterChoice, ScalingChoice, DuplexChoice, OrientationChoice })
        {
            list.TemplateApplied += OnListTemplateApplied;
            list.AddHandler(PointerWheelChangedEvent, OnListWheel, RoutingStrategies.Tunnel);
        }
    }

    /// <summary>Shows the card at its top and puts the focus on the printer.</summary>
    internal void Show()
    {
        PrintScroller.Offset = default;
        IsVisible = true;

        // After the layout of this change, when the printer list can take the focus.
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card (and a list of it that is open).</summary>
    internal void Hide()
    {
        CloseOpenLists();
        IsVisible = false;
    }

    /// <summary>Puts the keys on the printer while the card is shown.</summary>
    internal void FocusFirst()
    {
        if (IsVisible && PrinterChoice.IsEffectivelyVisible)
        {
            PrinterChoice.Focus();
        }
    }

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is ComboBox list)
        {
            CardLists.OnTemplateApplied(this, list, e);
        }
    }

    private void OnListWheel(object? sender, PointerWheelEventArgs e) =>
        CardLists.OnListWheel(PrintScroller, WheelStep, sender, e);

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
        if (!CloseOpenLists() && DataContext is PrintViewModel viewModel)
        {
            viewModel.CancelCommand.Execute(null);
        }
    }

    private void OnEnter()
    {
        // Enter on a button runs that button; on a field or a choice it prints when the choices are valid.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button || DataContext is not PrintViewModel viewModel)
        {
            return;
        }

        if (viewModel.PrintCommand.CanExecute(null))
        {
            viewModel.PrintCommand.Execute(null);
        }
    }
}
