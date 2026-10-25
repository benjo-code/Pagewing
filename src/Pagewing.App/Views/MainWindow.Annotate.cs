using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The toolbar of the mode "Comment" (design system 6 point 4 and 7.7): over the document area while the active tab is in
/// the mode, hidden otherwise. It is made when a tab of the window first starts the mode, so nothing of it is built in the
/// startup path (and never in a measurement run, which starts no mode). The document view moves down by the height of
/// the toolbar while it is shown. Ending the mode with the focus in the toolbar gives the keys back to the document.
/// </summary>
public sealed partial class MainWindow
{
    private AnnotationToolbar? _annotationToolbar;
    private TabViewModel? _annotateTab;

    private void AttachAnnotate(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnAnnotateWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnAnnotateWindowPropertyChanged;
        }

        FollowAnnotateTab(next?.ActiveTab);
    }

    private void OnAnnotateWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            FollowAnnotateTab(_viewModel?.ActiveTab);
        }
    }

    private void FollowAnnotateTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(tab, _annotateTab))
        {
            if (_annotateTab is not null)
            {
                _annotateTab.PropertyChanged -= OnAnnotateTabPropertyChanged;
            }

            _annotateTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnAnnotateTabPropertyChanged;
            }
        }

        UpdateAnnotationToolbar();
    }

    private void OnAnnotateTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsAnnotating))
        {
            UpdateAnnotationToolbar();
        }
    }

    private void UpdateAnnotationToolbar()
    {
        var tab = _annotateTab;
        if (tab is { IsAnnotating: true })
        {
            var toolbar = _annotationToolbar ??= CreateAnnotationToolbar();
            toolbar.DataContext = tab;
            toolbar.IsVisible = true;
            PlaceDocumentBelowToolbar(toolbar);
            return;
        }

        if (_annotationToolbar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        DocumentView.Margin = default;
        if (hadFocus && DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    private AnnotationToolbar CreateAnnotationToolbar()
    {
        var toolbar = new AnnotationToolbar
        {
            Name = "AnnotationToolbar",
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
                PlaceDocumentBelowToolbar(toolbar);
            }
        };

        // In the document area, before the cards (password, error, link), which lie over it.
        DocumentAreaLayers.Children.Insert(DocumentAreaLayers.Children.IndexOf(DocumentView) + 1, toolbar);
        return toolbar;
    }

    /// <summary>Moves the document view below the toolbar: by its height as laid out, or as the density gives it before the first layout.</summary>
    private void PlaceDocumentBelowToolbar(AnnotationToolbar toolbar)
    {
        var height = toolbar.Bounds.Height > 0 ? toolbar.Bounds.Height
            : this.TryFindResource("Pw.Size.ToolbarHeight", ActualThemeVariant, out var value) && value is double size ? size
            : 44;
        DocumentView.Margin = new Thickness(0, height, 0, 0);
    }
}
