using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// What the four cards of the save flow share (<see cref="SaveChangesCard"/>, <see cref="SignatureWarningCard"/>,
/// <see cref="FileChangedCard"/>, <see cref="SaveErrorCard"/>; design system 7.14): a card in the document area of the tab
/// that asks a <see cref="PendingSave"/> (its <c>DataContext</c>), not a modal dialog. One button per choice the question
/// offers, in its order; the default one is the primary button and gets the focus. A dangerous choice
/// (<see cref="PendingSave.IsDangerous"/>) is disabled and ignores presses, Enter and Space until the lock after the first
/// layout ends (<see cref="PendingSave.ArmingDelay"/>). The transparent background covers the document area, so clicks
/// and document keys do not act behind the card; Esc answers "Cancel" (catalog entry <c>save.cancel</c>). The window
/// shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>; each card is made when first needed.
/// </summary>
public abstract class SaveCard : UserControl
{
    // Does nothing: a key the card takes away from the window while it is up.
    private static readonly RelayCommand Ignore = new(static () => { });

    private readonly List<(Button Button, PendingSaveChoice Choice)> _buttons = [];

    // The question shown whose layout has not been seen yet (Show); its lock starts with the first layout.
    private PendingSave? _awaitingLayout;

    protected SaveCard()
    {
        // Its own question only, never the window's view model it would inherit (the bindings are compiled for PendingSave).
        DataContext = null;
        IsVisible = false;
        Background = Avalonia.Media.Brushes.Transparent;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus
        // on the card, these keys of the document (page turning, history, search, page field) do nothing behind it.
        foreach (var gesture in new[]
        {
            new KeyGesture(Key.PageUp),
            new KeyGesture(Key.PageDown),
            new KeyGesture(Key.Left, KeyModifiers.Alt),
            new KeyGesture(Key.Right, KeyModifiers.Alt),
            new KeyGesture(Key.F3),
            new KeyGesture(Key.F3, KeyModifiers.Shift),
            new KeyGesture(Key.F, KeyModifiers.Control),
            new KeyGesture(Key.G, KeyModifiers.Control),
        })
        {
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = Ignore });
        }

        // The second press of a multiple click never reaches a button: it belongs to a click that began elsewhere. A press
        // on a locked button never reaches it either, so its release after the lock does nothing; nor do Enter and Space.
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Gets the question the card shows, or <see langword="null"/> while it is hidden.</summary>
    internal PendingSave? Pending { get; private set; }

    /// <summary>Gets the button of <paramref name="choice"/>, or <see langword="null"/> when the card has none.</summary>
    internal Button? ButtonOf(PendingSaveChoice choice) => _buttons.FirstOrDefault(entry => entry.Choice == choice).Button;

    /// <summary>
    /// Shows <paramref name="question"/>: its texts (bound to it), its buttons in their order with the default as primary
    /// and focused, the dangerous ones locked until the lock after the first layout ends.
    /// </summary>
    internal void Show(PendingSave question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!ReferenceEquals(Pending, question))
        {
            Detach();
            Pending = question;
            question.ArmedChanged += UpdateButtons;
        }

        DataContext = question;
        OnShowing(question);
        var order = question.Choices.ToList();
        foreach (var (button, choice) in _buttons)
        {
            button.IsVisible = order.Contains(choice);
            button.Classes.Set("primary", choice == question.DefaultChoice);
        }

        // The buttons stand in the order of the choices.
        if (_buttons.Count > 0 && _buttons[0].Button.Parent is Panel row)
        {
            foreach (var (button, choice) in _buttons.Where(entry => order.Contains(entry.Choice)).OrderBy(entry => order.IndexOf(entry.Choice)))
            {
                row.Children.Remove(button);
                row.Children.Add(button);
            }
        }

        UpdateButtons();
        if (_awaitingLayout is null)
        {
            LayoutUpdated += OnLayoutUpdated;
        }

        _awaitingLayout = question;
        IsVisible = true;

        // After the layout of this change, when the button can take the focus.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(Pending, question) && ButtonOf(question.DefaultChoice) is { IsEffectivelyVisible: true } button)
                {
                    button.Focus(NavigationMethod.Unspecified);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card; its question keeps waiting (another tab is shown) or was answered.</summary>
    internal void Hide()
    {
        StopAwaitingLayout();
        Pending?.OnCardHidden();
        Detach();
        IsVisible = false;
        DataContext = null;
    }

    /// <summary>Makes the button of <paramref name="choice"/> answer the question (in the order the subclass knows them).</summary>
    protected void Register(Button button, PendingSaveChoice choice)
    {
        ArgumentNullException.ThrowIfNull(button);
        _buttons.Add((button, choice));
        button.Content = PendingSave.LabelOf(choice);
        button.Click += (_, _) => Pending?.Choose(choice);
    }

    /// <summary>What a card sets for <paramref name="question"/> beyond its texts (which variant shows).</summary>
    protected virtual void OnShowing(PendingSave question)
    {
    }

    private void Detach()
    {
        if (Pending is { } old)
        {
            old.ArmedChanged -= UpdateButtons;
        }

        Pending = null;
    }

    private void UpdateButtons()
    {
        // An answered question keeps its buttons as they are until the card goes: a button that is disabled under the focus
        // would drop it, and the keys could not go back to the document.
        if (Pending is not { IsResolved: false } question)
        {
            return;
        }

        foreach (var (button, choice) in _buttons)
        {
            button.IsEnabled = question.IsArmed(choice);
        }
    }

    private bool IsOnLockedButton(object? source)
    {
        if (source is not Visual visual || Pending is not { } question)
        {
            return false;
        }

        foreach (var (button, choice) in _buttons)
        {
            if ((ReferenceEquals(visual, button) || button.IsVisualAncestorOf(visual)) && !question.IsArmed(choice))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1 || IsOnLockedButton(e.Source))
        {
            e.Handled = true;
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && IsOnLockedButton(e.Source))
        {
            e.Handled = true;
        }
    }

    /// <summary>The first layout after <see cref="Show"/> with the card on screen: the lock of the dangerous choices starts now.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_awaitingLayout is not { } question || !IsEffectivelyVisible || Bounds.Width <= 0)
        {
            return;
        }

        StopAwaitingLayout();
        if (ReferenceEquals(Pending, question))
        {
            question.OnCardShown();
        }
    }

    private void StopAwaitingLayout()
    {
        if (_awaitingLayout is not null)
        {
            _awaitingLayout = null;
            LayoutUpdated -= OnLayoutUpdated;
        }
    }
}
