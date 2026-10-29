using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Views;

/// <summary>
/// The dialog "New signature" (<see cref="OverlayKind.Signature"/>; its DataContext is the <see cref="SignatureCardViewModel"/> of
/// the showing). The window shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>. The card passes the strokes of the
/// pad to the view model, keeps the ink of the pad and the slider in step with it, and handles Esc and Enter like the other cards.
/// </summary>
public sealed partial class SignatureCard : UserControl
{
    private const double WheelStep = 50;

    private SignatureCardViewModel? _viewModel;

    public SignatureCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus on the
        // card, Esc closes an open list first and then the card, Enter saves while that is possible, and the page keys scroll the
        // card instead of turning the pages of the document behind it.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(OnEscape) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(OnEnter) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(SignatureScroller.PageUp) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(SignatureScroller.PageDown) });
        SignatureInkChoice.TemplateApplied += OnListTemplateApplied;
        SignatureInkChoice.AddHandler(PointerWheelChangedEvent, OnListWheel, RoutingStrategies.Tunnel);
        SignaturePad.StrokeCompleted += OnStrokeCompleted;
        SignatureThreshold.PropertyChanged += OnSliderChanged;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Shows the card and puts the focus on the first control.</summary>
    internal void Show()
    {
        SignatureScroller.Offset = default;
        IsVisible = true;
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card (and a list of it that is open).</summary>
    internal void Hide()
    {
        CloseOpenLists();
        IsVisible = false;
    }

    /// <summary>Puts the keys on the name while the card is shown.</summary>
    internal void FocusFirst()
    {
        if (IsVisible && SignatureName.IsEffectivelyVisible)
        {
            SignatureName.Focus();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as SignatureCardViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            SyncInk();
            SyncSlider();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SignatureCardViewModel.Color):
                SyncInk();
                break;
            case nameof(SignatureCardViewModel.Threshold):
                SyncSlider();
                break;
        }
    }

    private void SyncInk()
    {
        if (_viewModel is not null)
        {
            var color = DrawPalette.ColorOf(_viewModel.Color);
            SignaturePad.Ink = Color.FromRgb(color.R, color.G, color.B);
        }
    }

    private void SyncSlider()
    {
        if (_viewModel is not null && Math.Abs(SignatureThreshold.Value - _viewModel.Threshold) > 0.5)
        {
            SignatureThreshold.Value = _viewModel.Threshold;
        }
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty && _viewModel is not null)
        {
            _viewModel.Threshold = (int)Math.Round(SignatureThreshold.Value);
        }
    }

    private void OnStrokeCompleted(IReadOnlyList<DisplayPoint> points) => _viewModel?.AddStroke(points);

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is ComboBox list)
        {
            CardLists.OnTemplateApplied(this, list, e);
        }
    }

    private void OnListWheel(object? sender, PointerWheelEventArgs e) =>
        CardLists.OnListWheel(SignatureScroller, WheelStep, sender, e);

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
        if (!CloseOpenLists() && DataContext is SignatureCardViewModel viewModel)
        {
            viewModel.CancelCommand.Execute(null);
        }
    }

    private void OnEnter()
    {
        // Enter on a button runs that button; on a field or a choice it saves when that is possible.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button || DataContext is not SignatureCardViewModel viewModel)
        {
            return;
        }

        if (viewModel.SaveCommand.CanExecute(null))
        {
            viewModel.SaveCommand.Execute(null);
        }
    }
}
