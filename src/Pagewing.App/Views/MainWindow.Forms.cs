using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The window side of the mode "Fill in form" (milestone 11): its toolbar over the document area while the active tab is in
/// the mode (like the toolbar of "Comment", MainWindow.Annotate.cs; only one of the two modes is on), and the hint bar of a
/// form below the toolbars while the active tab has a message for it (the offer of the mode after the first sharp page, a
/// hint, the notice of a refused value; <see cref="FormHintBar"/>). Both are made when they are first needed, so nothing of
/// them is built in the startup path, and never for a document without a form. The document view moves down by the height
/// of what is shown over it (<see cref="PlaceDocument"/>). When the toolbar or the bar goes with the keyboard focus, the
/// focus goes to the document.
/// </summary>
public sealed partial class MainWindow
{
    private FormToolbar? _formToolbar;
    private FormHintBar? _formHintBar;
    private TabViewModel? _formTab;

    private void AttachForms(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnFormsWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnFormsWindowPropertyChanged;
        }

        FollowFormTab(next?.ActiveTab);
    }

    private void OnFormsWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            FollowFormTab(_viewModel?.ActiveTab);
        }
    }

    private void FollowFormTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(tab, _formTab))
        {
            if (_formTab is not null)
            {
                _formTab.PropertyChanged -= OnFormTabPropertyChanged;
            }

            _formTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnFormTabPropertyChanged;
            }
        }

        UpdateFormToolbar();
        UpdateFormHintBar();
    }

    private void OnFormTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsFilling))
        {
            UpdateFormToolbar();
        }

        if (e.PropertyName is nameof(TabViewModel.IsFilling) or nameof(TabViewModel.OffersFill) or nameof(TabViewModel.PendingHints)
            or nameof(TabViewModel.FormNotice))
        {
            UpdateFormHintBar();
        }
    }

    private void UpdateFormToolbar()
    {
        if (_formTab is { IsFilling: true } tab)
        {
            var toolbar = _formToolbar ??= CreateFormToolbar();
            toolbar.DataContext = tab;
            toolbar.IsVisible = true;
            PlaceDocument();
            return;
        }

        if (_formToolbar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        PlaceDocument();
        FocusDocumentAfter(hadFocus);
    }

    private void UpdateFormHintBar()
    {
        if (_formTab is { } tab && FormHintBar.HasMessage(tab))
        {
            var bar = _formHintBar ??= CreateFormHintBar();
            bar.DataContext = tab;
            bar.IsVisible = true;
            PlaceDocument();
            return;
        }

        if (_formHintBar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        PlaceDocument();
        FocusDocumentAfter(hadFocus);
    }

    private void FocusDocumentAfter(bool hadFocus)
    {
        if (hadFocus && DocumentView.Layout is not null)
        {
            DocumentView.Focus(NavigationMethod.Unspecified);
        }
    }

    private FormToolbar CreateFormToolbar()
    {
        var toolbar = new FormToolbar
        {
            Name = "FormToolbar",
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

        // A changed density changes its height: the document (and the hint bar) follow.
        toolbar.SizeChanged += (_, _) =>
        {
            if (toolbar.IsVisible)
            {
                PlaceDocument();
            }
        };

        // In the document area right after the document view, before the hint bar and the cards (password, error, link).
        DocumentAreaLayers.Children.Insert(DocumentAreaLayers.Children.IndexOf(DocumentView) + 1, toolbar);
        return toolbar;
    }

    private FormHintBar CreateFormHintBar()
    {
        var bar = new FormHintBar { Name = "FormHintBar", IsVisible = false };

        // Its top left corner follows that of the document area while no toolbar is above it (PlaceDocument).
        DocumentArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == Border.CornerRadiusProperty && bar.IsVisible)
            {
                PlaceDocument();
            }
        };

        // Its height follows its text (wrapped in a narrow window): the document follows.
        bar.SizeChanged += (_, _) =>
        {
            if (bar.IsVisible)
            {
                PlaceDocument();
            }
        };

        // After the toolbars in the tab order, before the cards (password, error, link) that lie over the document area.
        var after = new Control?[] { DocumentView, _annotationToolbar, _formToolbar }
            .Select(control => control is null ? -1 : DocumentAreaLayers.Children.IndexOf(control))
            .Max();
        DocumentAreaLayers.Children.Insert(after + 1, bar);
        return bar;
    }

    /// <summary>
    /// Puts the document view below what is shown over the top of the document area: the toolbar of a mode, then the hint bar
    /// of a form; each by its height as laid out, or as measured before the first layout.
    /// </summary>
    private void PlaceDocument()
    {
        double top = 0;
        if (_annotationToolbar is { IsVisible: true } annotation)
        {
            top += ToolbarHeightOf(annotation);
        }

        if (_formToolbar is { IsVisible: true } form)
        {
            top += ToolbarHeightOf(form);
        }

        if (_formHintBar is { IsVisible: true } bar)
        {
            bar.Margin = new Thickness(0, top, 0, 0);

            // At the top of the document area it takes the rounded corner of the area, below a toolbar none.
            bar.HintFrame.CornerRadius = top > 0 ? default : new CornerRadius(DocumentArea.CornerRadius.TopLeft, 0, 0, 0);
            top += HintBarHeightOf(bar);
        }

        DocumentView.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>The height of a toolbar: as laid out, or as the density gives it before the first layout.</summary>
    private double ToolbarHeightOf(Control toolbar) =>
        toolbar.Bounds.Height > 0 ? toolbar.Bounds.Height
        : this.TryFindResource("Pw.Size.ToolbarHeight", ActualThemeVariant, out var value) && value is double size ? size
        : 44;

    /// <summary>The height of the hint bar: as laid out, or measured for the width of the document area before its first layout.</summary>
    private double HintBarHeightOf(FormHintBar bar)
    {
        if (bar.Bounds.Height > 0)
        {
            return bar.Bounds.Height;
        }

        bar.Measure(new Size(DocumentAreaLayers.Bounds.Width, double.PositiveInfinity));
        return Math.Max(0, bar.DesiredSize.Height - bar.Margin.Top - bar.Margin.Bottom);
    }
}
