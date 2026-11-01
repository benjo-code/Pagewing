using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The content of the "Pages" panel: the page thumbnails of the active tab (<see cref="Controls.ThumbnailList"/>, which
/// follows the tab in its <c>DataContext</c>). While the tab is in the mode "Organize pages" the tools
/// (<see cref="PagesOrganizeBar"/>) stand above the list, made when the mode starts and taken away when it ends. Built when the
/// panel opens, released when it closes.
/// </summary>
public sealed partial class PagesPanel : UserControl
{
    private TabViewModel? _tab;
    private PagesOrganizeBar? _bar;
    private PageOutputMenu? _outputMenu;

    public PagesPanel() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = DataContext as TabViewModel;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        SyncBar();
        (_outputMenu ??= new PageOutputMenu(PageOutputButton)).Attach(_tab);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab = null;
        }

        _outputMenu?.Detach();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is TabViewModel tab && !ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _tab.PropertyChanged += OnTabPropertyChanged;
            SyncBar();
            (_outputMenu ??= new PageOutputMenu(PageOutputButton)).Attach(_tab);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsOrganizing))
        {
            SyncBar();
        }
    }

    /// <summary>Puts the tools above the list while the mode is on and takes them away when it ends.</summary>
    private void SyncBar()
    {
        var on = _tab?.IsOrganizing == true;
        if (on == (_bar is not null))
        {
            return;
        }

        // The focus was on the button that went (the way in, "Done"): it goes to the list, not nowhere.
        var focusWasHere = _bar?.IsKeyboardFocusWithin == true || OrganizeButton.IsKeyboardFocusWithin;
        if (on)
        {
            _bar = new PagesOrganizeBar { Name = "OrganizeBar" };
            OrganizeHost.Content = _bar;
        }
        else
        {
            OrganizeHost.Content = null;
            _bar = null;
        }

        if (focusWasHere)
        {
            Dispatcher.UIThread.Post(() => ThumbnailList.Focus(NavigationMethod.Unspecified), DispatcherPriority.Loaded);
        }
    }
}
