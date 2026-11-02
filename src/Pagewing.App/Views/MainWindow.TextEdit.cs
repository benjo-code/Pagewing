using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The mode "Edit text" in the window (milestone 16): the bar (<see cref="TextEditHintBar"/>) over the document area while the mode is on. It is made when the
/// mode is first entered, so nothing of it is built in the startup path. The document view moves down by the height of what is shown over it
/// (<see cref="PlaceDocument"/>).
/// </summary>
public sealed partial class MainWindow
{
    private TextEditHintBar? _textEditHintBar;
    private TabViewModel? _textEditTab;

    /// <summary>Gets the bar of the mode, or <see langword="null"/> before the mode was first entered (for tests).</summary>
    internal TextEditHintBar? TextEditHintBar => _textEditHintBar;

    private void AttachTextEdit(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnTextEditWindowPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnTextEditWindowPropertyChanged;
        }

        FollowTextEditTab(next?.ActiveTab);
    }

    private void OnTextEditWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            FollowTextEditTab(_viewModel?.ActiveTab);
        }
    }

    private void FollowTextEditTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(tab, _textEditTab))
        {
            if (_textEditTab is not null)
            {
                _textEditTab.PropertyChanged -= OnTextEditTabPropertyChanged;
            }

            _textEditTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnTextEditTabPropertyChanged;
            }
        }

        UpdateTextEditHintBar();
    }

    private void OnTextEditTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.IsTextEditing))
        {
            UpdateTextEditHintBar();
        }
    }

    private void UpdateTextEditHintBar()
    {
        if (_textEditTab is { IsTextEditing: true } tab)
        {
            var bar = _textEditHintBar ??= CreateTextEditHintBar();
            bar.DataContext = tab;
            bar.IsVisible = true;
            PlaceDocument();
            return;
        }

        if (_textEditHintBar is not { IsVisible: true } shown)
        {
            return;
        }

        var hadFocus = shown.IsKeyboardFocusWithin;
        shown.IsVisible = false;
        shown.DataContext = null;
        PlaceDocument();
        FocusDocumentAfter(hadFocus);
    }

    private TextEditHintBar CreateTextEditHintBar()
    {
        var bar = new TextEditHintBar { Name = "TextEditHintBar", IsVisible = false };

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

        var after = new Control?[] { DocumentView, _annotationToolbar, _formToolbar, _redactionToolbar, _formHintBar, _ocrHintBar }
            .Select(control => control is null ? -1 : DocumentAreaLayers.Children.IndexOf(control))
            .Max();
        DocumentAreaLayers.Children.Insert(after + 1, bar);
        return bar;
    }

    /// <summary>The height of the bar of the mode: as laid out, or measured for the width of the document area before its first layout.</summary>
    private double TextEditHintBarHeightOf(TextEditHintBar bar)
    {
        if (bar.Bounds.Height > 0)
        {
            return bar.Bounds.Height;
        }

        bar.Measure(new Size(DocumentAreaLayers.Bounds.Width, double.PositiveInfinity));
        return Math.Max(0, bar.DesiredSize.Height - bar.Margin.Top - bar.Margin.Bottom);
    }
}
