using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The main window; its <c>DataContext</c> is a <see cref="MainViewModel"/>. Opens dropped files in tabs, comes to the
/// front when a later launch hands its files over and closes when its view model asks for it.
/// </summary>
public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private TabStrip? _tabStrip;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        InitializePanel();
        InitializeOverlays();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Gets the local paths of the items a drop opens, in their order: files and folders (a folder fails to open with
    /// "cannot be read", in its own tab); empty when the data holds no local storage item (text, links, ...).
    /// </summary>
    internal static IReadOnlyList<string> DroppedPaths(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = data.TryGetFiles();
        if (items is null)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Gets the position a tab dropped at <paramref name="screen"/> takes in this window's tab strip, or
    /// <see langword="null"/> when the point is not over the strip or the window is hidden or minimized (see
    /// <see cref="TabStrip.DropIndexAt"/>).
    /// </summary>
    internal int? TabDropIndexAt(PixelPoint screen)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return null;
        }

        _tabStrip ??= this.GetVisualDescendants().OfType<TabStrip>().SingleOrDefault();
        return _tabStrip?.DropIndexAt(screen);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var old = _viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ActivationRequested -= BringToFront;
            _viewModel.CloseWindowRequested -= Close;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ActivationRequested += BringToFront;
            _viewModel.CloseWindowRequested += Close;
        }

        AttachPanel(old, _viewModel);
        AttachLinkCard(old, _viewModel);
        AttachSaveCards(old, _viewModel);
        AttachShortcuts(_viewModel);
        AttachOverlays(old, _viewModel);
        AttachPrint(old, _viewModel);
        AttachDefaultApp(old, _viewModel);
        AttachUpdate(old, _viewModel);
        AttachAnnotate(old, _viewModel);
        AttachForms(old, _viewModel);
    }

    /// <summary>
    /// The window closes (the user, the end of the app, its last tab gone): tabs with unsaved changes are asked about first
    /// (<see cref="LetCloseGoOn"/>; the close waits for the answers). Then the document view saves where it shows the active
    /// tab now, and every tab of the window with an open document goes into the history with its position, once.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && _viewModel is { } viewModel && LetCloseGoOn(e, viewModel))
        {
            DocumentView.SaveView();
            viewModel.RememberOpenTabs();
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
        if (_viewModel is not null && DroppedPaths(e.DataTransfer) is { Count: > 0 } paths)
        {
            // Opens in this window whatever it shows (design system 7.1), each file in a tab of its own.
            _ = _viewModel.OpenManyAsync(paths);
        }
    }
}
