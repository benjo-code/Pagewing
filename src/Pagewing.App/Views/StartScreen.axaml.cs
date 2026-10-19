using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The start screen of a window without tabs (design system 7.1); its DataContext is the <see cref="MainViewModel"/>.
/// When it is shown it asks the window to read the history (<see cref="MainViewModel.OnStartScreenShown"/>, after the
/// frame), and it builds "Recently opened" (<see cref="RecentFilesSection"/>) the first time the history has entries.
/// </summary>
public sealed partial class StartScreen : UserControl
{
    private MainViewModel? _viewModel;
    private RecentFilesSection? _recent;

    public StartScreen() => InitializeComponent();

    /// <summary>Gets the section "Recently opened", or <see langword="null"/> before the history had entries.</summary>
    internal RecentFilesSection? RecentSection => _recent;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEmpty) or nameof(MainViewModel.HasStartRecentFiles))
        {
            Update();
        }
    }

    /// <summary>
    /// Shown (the window has no tab, by the view model, not by IsVisible, whose binding may lag behind): the history is
    /// read after the frame; entries build the section once.
    /// </summary>
    private void Update()
    {
        if (_viewModel is not { } viewModel || VisualRoot is null)
        {
            return;
        }

        viewModel.OnStartScreenShown();
        if (viewModel.HasStartRecentFiles && _recent is null)
        {
            _recent = new RecentFilesSection { FocusFallback = () => ChooseFileButton.Focus(NavigationMethod.Tab) };
            RecentHost.Children.Add(_recent);
        }
    }
}
