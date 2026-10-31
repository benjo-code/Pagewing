using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The dialog "Insert pages from a file" (<see cref="OverlayKind.InsertFile"/>; its DataContext is the
/// <see cref="InsertFileViewModel"/> of the file). The window shows it with <see cref="Show"/> and hides it with
/// <see cref="Hide"/>. The keys go to the field the state asks for (the password, the pages); Enter inserts or opens with the
/// password, Esc closes the dialog and nothing behind it.
/// </summary>
public sealed partial class InsertFileCard : UserControl
{
    private InsertFileViewModel? _viewModel;

    public InsertFileCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus on the
        // card, Esc closes the dialog (not the plan behind it), Enter takes the field's text, and Page Up/Down stay with the card.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(OnEnter) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => { }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => { }) });
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Shows the card and puts the focus on the field the state asks for.</summary>
    internal void Show()
    {
        IsVisible = true;
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card.</summary>
    internal void Hide()
    {
        IsVisible = false;

        // A password typed here must not stay in the field of a card that is only hidden.
        InsertFilePassword.Text = string.Empty;
    }

    /// <summary>Puts the keys on the password field, the pages field, or (while opening or after a failure) the "Cancel" button.</summary>
    internal void FocusFirst()
    {
        if (!IsVisible)
        {
            return;
        }

        if (_viewModel is { NeedsPassword: true } && InsertFilePassword.IsEffectivelyVisible)
        {
            InsertFilePassword.Focus();
            InsertFilePassword.SelectAll();
        }
        else if (_viewModel is { IsReady: true } && InsertFileRange.IsEffectivelyVisible)
        {
            InsertFileRange.Focus();
            InsertFileRange.SelectAll();
        }
        else if (InsertFileCancelButton.IsEffectivelyVisible)
        {
            InsertFileCancelButton.Focus();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as InsertFileViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The state changed (the file opened, the password is asked, it failed) or the password was wrong: the keys follow.
        if (e.PropertyName is nameof(InsertFileViewModel.State) or nameof(InsertFileViewModel.IsPasswordIncorrect))
        {
            Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
        }
    }

    private void OnEscape()
    {
        if (DataContext is InsertFileViewModel viewModel)
        {
            viewModel.CancelCommand.Execute(null);
        }
    }

    private void OnEnter()
    {
        // Enter on a button runs that button; in a field it does what the dialog is waiting for.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button || DataContext is not InsertFileViewModel viewModel)
        {
            return;
        }

        var command = viewModel.NeedsPassword ? viewModel.SubmitPasswordCommand : viewModel.InsertCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
