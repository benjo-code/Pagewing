using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The password card; keeps the keyboard focus in the password field when the card appears and after a wrong
/// password, with the wrong password selected so typing replaces it (design system 7.8).
/// </summary>
public sealed partial class PasswordCard : UserControl
{
    private MainViewModel? _viewModel;

    public PasswordCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PasswordRejected -= OnPasswordRejected;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PasswordRejected += OnPasswordRejected;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            FocusFieldLater(selectAll: false);
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            FocusFieldLater(selectAll: false);
        }
    }

    private void OnPasswordRejected() => FocusFieldLater(selectAll: true);

    // After the bindings of the same change have run, so the card (and its parents) are visible and can take focus.
    private void FocusFieldLater(bool selectAll) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!PasswordField.IsEffectivelyVisible)
                {
                    return;
                }

                PasswordField.Focus();
                if (selectAll)
                {
                    PasswordField.SelectAll();
                }
            },
            DispatcherPriority.Loaded);
}
