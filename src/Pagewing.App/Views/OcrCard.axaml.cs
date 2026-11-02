using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card of the text recognition (<see cref="OverlayKind.Ocr"/>; its DataContext is the <see cref="OcrViewModel"/>). The window shows it with
/// <see cref="Show"/> and hides it with <see cref="Hide"/>. The keys go to the first language while the choices are asked, to "Cancel" while a run goes
/// on, to "Discard" after a stop and to "Close" in the report. Enter starts (or closes), Esc stops a run, answers the question with "Discard" or closes the
/// card, and nothing behind it.
/// </summary>
public sealed partial class OcrCard : UserControl
{
    private OcrViewModel? _viewModel;

    public OcrCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus on the card, Esc ends the card
        // (not the plan or the search behind it) and the page keys stay with the card.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(OnEnter, EnterIsForTheCard) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => { }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => { }) });
        foreach (var list in new[] { OcrScopeList, OcrModeList, OcrResolutionList })
        {
            list.TemplateApplied += (sender, e) => CardLists.OnTemplateApplied(this, (ComboBox)sender!, e);
            list.PointerWheelChanged += (sender, e) => CardLists.OnListWheel(OcrScroller, 48, sender, e);
        }

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

    /// <summary>Puts the keys on the first thing the state asks for.</summary>
    internal void FocusFirst()
    {
        if (!IsVisible || _viewModel is not { } viewModel)
        {
            return;
        }

        Control target = viewModel.State switch
        {
            OcrCardState.Running => OcrCancelRunButton,
            OcrCardState.Choose => OcrDiscardButton,
            OcrCardState.Report => OcrReportCloseButton,
            OcrCardState.Failed => OcrFailedCloseButton,
            _ => viewModel.HasNoLanguages ? OcrAddLanguageButton : OcrStartButton,
        };
        if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.Focus();
        }
        else if (OcrCloseButton.IsEffectivelyVisible && OcrCloseButton.IsEffectivelyEnabled)
        {
            OcrCloseButton.Focus();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as OcrViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The state changed (the run started or ended): the keys follow.
        if (e.PropertyName == nameof(OcrViewModel.State))
        {
            Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
        }
    }

    private void OnEscape() => _viewModel?.HandleEscape();

    // Enter on a button is the button's (the key binding then leaves the key alone); elsewhere it does what the card is waiting for.
    private bool EnterIsForTheCard() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not (Button or ComboBox or CheckBox or TextBox);

    private void OnEnter()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var command = viewModel.State switch
        {
            OcrCardState.Options => viewModel.StartCommand,
            OcrCardState.Report or OcrCardState.Failed => viewModel.CloseCommand,
            _ => null,
        };
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
