using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The text recognition in the window (milestone 15): the card (<see cref="OcrCard"/>, the overlay <see cref="OverlayKind.Ocr"/>) and the hint bar
/// "This looks like a scan" over the document area (<see cref="OcrHintBar"/>). Both are made when they are first needed, so nothing of them is built in the
/// startup path, and never for a document that does not look like a scan. The document view moves down by the height of what is shown over it
/// (<see cref="PlaceDocument"/>).
/// </summary>
public sealed partial class MainWindow
{
    private OcrCard? _ocrCard;
    private OcrHintBar? _ocrHintBar;
    private TabViewModel? _ocrTab;

    /// <summary>Shows or hides the card for the overlay kind; called by the overlays.</summary>
    private void UpdateOcrCard(OverlayKind kind)
    {
        if (kind == OverlayKind.Ocr && _viewModel?.Ocr is { } card)
        {
            var view = _ocrCard ??= CreateOcrCard();
            view.DataContext = card;
            view.Show();
        }
        else
        {
            _ocrCard?.Hide();
        }
    }

    private OcrCard CreateOcrCard()
    {
        // Like the signature card: over the work area and the status bar, below the title bar.
        var card = new OcrCard { Name = "OcrCard" };
        Grid.SetRow(card, 1);
        Grid.SetRowSpan(card, 2);
        card.ZIndex = 10;
        RootGrid.Children.Add(card);
        return card;
    }

    private void AttachOcr(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnOcrWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnOcrWindowPropertyChanged;
        }

        FollowOcrTab(next?.ActiveTab);
    }

    private void OnOcrWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            FollowOcrTab(_viewModel?.ActiveTab);
        }
    }

    private void FollowOcrTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(tab, _ocrTab))
        {
            if (_ocrTab is not null)
            {
                _ocrTab.PropertyChanged -= OnOcrTabPropertyChanged;
            }

            _ocrTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnOcrTabPropertyChanged;
            }
        }

        UpdateOcrHintBar();
    }

    private void OnOcrTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.ShowsOcrHint))
        {
            UpdateOcrHintBar();
        }
    }

    private void UpdateOcrHintBar()
    {
        if (_ocrTab is { ShowsOcrHint: true } tab)
        {
            var bar = _ocrHintBar ??= CreateOcrHintBar();
            bar.DataContext = tab;
            bar.IsVisible = true;
            PlaceDocument();
            return;
        }

        if (_ocrHintBar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        PlaceDocument();
        FocusDocumentAfter(hadFocus);
    }

    private OcrHintBar CreateOcrHintBar()
    {
        var bar = new OcrHintBar { Name = "OcrHintBar", IsVisible = false };

        // Its top left corner follows that of the document area while nothing is above it (PlaceDocument).
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

        // After the toolbars and the hint bar of a form in the tab order, before the cards that lie over the document area.
        var after = new Control?[] { DocumentView, _annotationToolbar, _formToolbar, _redactionToolbar, _formHintBar }
            .Select(control => control is null ? -1 : DocumentAreaLayers.Children.IndexOf(control))
            .Max();
        DocumentAreaLayers.Children.Insert(after + 1, bar);
        return bar;
    }

    /// <summary>The height of the hint bar of the text recognition: as laid out, or measured for the width of the document area before its first layout.</summary>
    private double OcrHintBarHeightOf(OcrHintBar bar)
    {
        if (bar.Bounds.Height > 0)
        {
            return bar.Bounds.Height;
        }

        bar.Measure(new Size(DocumentAreaLayers.Bounds.Width, double.PositiveInfinity));
        return Math.Max(0, bar.DesiredSize.Height - bar.Margin.Top - bar.Margin.Bottom);
    }
}
