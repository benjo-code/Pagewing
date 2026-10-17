using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The main window; its <c>DataContext</c> is a <see cref="MainViewModel"/>. Opens dropped files and comes to the
/// front when a later launch hands its files over.
/// </summary>
public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Gets the local path of the item a drop opens: the first file or folder (a folder fails to open with
    /// "cannot be read"), or <see langword="null"/> when the data holds no local storage item (text, links, ...).
    /// </summary>
    internal static string? DroppedPath(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = data.TryGetFiles();
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ActivationRequested -= BringToFront;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ActivationRequested += BringToFront;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() is var state && state != WindowState.Minimized)
        {
            _restoreState = state;
        }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _restoreState;
        }

        Activate();
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is not null && DroppedPath(e.DataTransfer) is { } path)
        {
            // Opens in this window whatever it shows (design system 7.1); the one document is replaced.
            _ = _viewModel.OpenAsync(path);
        }
    }
}
