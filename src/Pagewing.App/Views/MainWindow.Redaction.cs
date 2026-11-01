using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The toolbar of the mode "Redact" (milestone 14, design system 6 point 4 and 7.7): over the document area while the active tab is
/// in the mode, hidden otherwise, like the toolbars of "Comment" and "Fill in form". It is made when a tab of the window first
/// starts the mode, so nothing of it is built in the startup path (and never in a measurement run, which starts no mode). The
/// document view moves down by its height while it is shown (<see cref="PlaceDocument"/>, MainWindow.Forms.cs). Ending the mode with
/// the focus in the toolbar gives the keys back to the document.
/// </summary>
public sealed partial class MainWindow
{
    private RedactionToolbar? _redactionToolbar;
    private TabViewModel? _redactionTab;

    private void AttachRedaction(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnRedactionWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnRedactionWindowPropertyChanged;
        }

        FollowRedactionTab(next?.ActiveTab);
    }

    private void OnRedactionWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            FollowRedactionTab(_viewModel?.ActiveTab);
        }
    }

    private void FollowRedactionTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(tab, _redactionTab))
        {
            if (_redactionTab is not null)
            {
                _redactionTab.PropertyChanged -= OnRedactionTabPropertyChanged;
            }

            _redactionTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnRedactionTabPropertyChanged;
            }
        }

        UpdateRedactionToolbar();
    }

    private void OnRedactionTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsRedacting))
        {
            UpdateRedactionToolbar();
        }
    }

    private void UpdateRedactionToolbar()
    {
        if (_redactionTab is { IsRedacting: true } tab)
        {
            var toolbar = _redactionToolbar ??= CreateRedactionToolbar();
            toolbar.DataContext = tab;
            toolbar.IsVisible = true;
            PlaceDocument();
            return;
        }

        if (_redactionToolbar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        PlaceDocument();
        FocusDocumentAfter(hadFocus);
    }

    private RedactionToolbar CreateRedactionToolbar()
    {
        var toolbar = new RedactionToolbar
        {
            Name = "RedactionToolbar",
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        if (_viewModel is { } window)
        {
            toolbar.NameShortcuts(window.Commands);
        }

        // Its top left corner follows that of the document area (radius.overlay without a panel beside it).
        toolbar.ToolbarFrame.CornerRadius = new CornerRadius(DocumentArea.CornerRadius.TopLeft, 0, 0, 0);
        DocumentArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == Border.CornerRadiusProperty)
            {
                toolbar.ToolbarFrame.CornerRadius = new CornerRadius(DocumentArea.CornerRadius.TopLeft, 0, 0, 0);
            }
        };

        // A changed density changes its height: the document follows.
        toolbar.SizeChanged += (_, _) =>
        {
            if (toolbar.IsVisible)
            {
                PlaceDocument();
            }
        };

        // In the document area right after the document view, before the hint bar of a form and the cards (password, error,
        // link), which lie over it. The document moves below it (PlaceDocument, MainWindow.Forms.cs).
        DocumentAreaLayers.Children.Insert(DocumentAreaLayers.Children.IndexOf(DocumentView) + 1, toolbar);
        return toolbar;
    }
}
