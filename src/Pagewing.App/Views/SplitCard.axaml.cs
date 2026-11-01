using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card "Split document" or "Extract selected pages" (<see cref="OverlayKind.PageOutput"/>; its DataContext is the
/// <see cref="SplitViewModel"/>). The window shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>. The keys go
/// to what the state waits for: the field of the chosen way while the options are asked, the primary button in the review, the
/// button "Close" when done. Enter does what the card waits for, Esc cancels a run or closes the card and nothing behind it.
/// </summary>
public sealed partial class SplitCard : UserControl
{
    private SplitViewModel? _viewModel;

    public SplitCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus on the
        // card, Esc ends the card (not the plan or the search behind it), Enter takes the field's text, and Page Up/Down stay with the card.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(OnEnter, EnterIsForTheCard) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => { }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => { }) });
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Shows the card and puts the focus on what the state asks for.</summary>
    internal void Show()
    {
        IsVisible = true;
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card.</summary>
    internal void Hide() => IsVisible = false;

    /// <summary>Puts the keys on the field of the chosen way, the primary button, the button "Close", or "Cancel".</summary>
    internal void FocusFirst()
    {
        if (!IsVisible || _viewModel is not { } viewModel)
        {
            return;
        }

        Control target = viewModel.State switch
        {
            PageOutputState.Setup => viewModel.IsRanges ? SplitRangesField : viewModel.IsEvery ? SplitEveryField : SplitBookmarksChoice,
            PageOutputState.Review => SplitStartButton,
            PageOutputState.Finished => SplitDoneButton,
            _ => SplitCancelButton,
        };
        if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.Focus();
            if (target is TextBox box)
            {
                box.SelectAll();
            }
        }
        else if (SplitCancelButton.IsEffectivelyVisible)
        {
            SplitCancelButton.Focus();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as SplitViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The state changed (the review is there, the run ended): the keys follow.
        if (e.PropertyName == nameof(SplitViewModel.State))
        {
            Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
        }
    }

    private void OnEscape()
    {
        if (DataContext is SplitViewModel viewModel)
        {
            viewModel.CancelCommand.Execute(null);
        }
    }

    // Enter on a button is the button's (the key binding then leaves the key alone); in a field it does what the card is waiting for.
    private bool EnterIsForTheCard() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Button;

    private void OnEnter()
    {
        if (DataContext is not SplitViewModel viewModel)
        {
            return;
        }

        var command = viewModel.State switch
        {
            PageOutputState.Setup => viewModel.ReviewCommand,
            PageOutputState.Review => viewModel.StartCommand,
            PageOutputState.Finished => viewModel.CloseCommand,
            _ => null,
        };
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
