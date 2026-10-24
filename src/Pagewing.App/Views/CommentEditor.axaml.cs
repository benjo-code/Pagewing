using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Views;

/// <summary>
/// The edit controls of the selected entry of the "Comments" panel in the mode "Comment" (see CommentEditor.axaml). The
/// comment field claims Ctrl+Enter and Esc with key bindings of its own (the window's bindings would come first otherwise);
/// Ctrl+Z stays the field's (the undo of the document yields to a text field). The menu "Change colour" is filled when it
/// opens.
/// </summary>
public sealed partial class CommentEditor : UserControl
{
    private readonly ObservableCollection<MenuItem> _colorEntries = [];
    private CommentsViewModel? _comments;

    public CommentEditor()
    {
        InitializeComponent();
        CommentBox.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Enter, KeyModifiers.Control),
            Command = new RelayCommand(() => _comments?.CommitCommentCommand.Execute(null)),
        });
        CommentBox.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(() =>
            {
                _comments?.DiscardCommentCommand.Execute(null);
                FocusReturnRequested?.Invoke();
            }),
        });

        var menu = new MenuFlyout { ItemsSource = _colorEntries };
        menu.Opening += OnColorMenuOpening;

        // The entries hold the command of the list: a closed menu drops them.
        menu.Closed += (_, _) => _colorEntries.Clear();
        CommentColorButton.Flyout = menu;
    }

    /// <summary>Raised when Esc in the comment field dropped what was typed: the keys go back to the entry in the list.</summary>
    internal event Action? FocusReturnRequested;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_comments is not null)
        {
            _comments.PropertyChanged -= OnCommentsPropertyChanged;
        }

        _comments = DataContext as CommentsViewModel;
        if (_comments is not null)
        {
            _comments.PropertyChanged += OnCommentsPropertyChanged;
        }

        UpdateColorSwatch();
    }

    /// <summary>A swatch of a palette colour (content of the PDF) with a border.strong edge, and a check mark when it is the chosen one.</summary>
    private static Panel Swatch(MarkupColor color, bool chosen)
    {
        var rgb = AnnotationPalette.ColorOf(color);
        var fill = new Border
        {
            Width = 16,
            Height = 16,
            BorderThickness = new Avalonia.Thickness(1),
            Background = new ImmutableSolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)),
        };
        fill.Bind(Border.BorderBrushProperty, fill.GetResourceObservable("Pw.Brush.Border.Strong"));
        fill.Bind(Border.CornerRadiusProperty, fill.GetResourceObservable("Pw.Radius.Control"));
        var panel = new Panel { Children = { fill } };
        if (chosen)
        {
            var check = new StrokeIcon { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            check.Bind(StrokeIcon.BrushProperty, check.GetResourceObservable("Pw.Brush.Search.On"));
            check.Bind(StrokeIcon.DataProperty, check.GetResourceObservable("Pw.Icon.Check"));
            panel.Children.Add(check);
        }

        return panel;
    }

    private void OnCommentsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommentsViewModel.SelectedColor) or nameof(CommentsViewModel.SelectedItem))
        {
            UpdateColorSwatch();
        }
    }

    /// <summary>The button shows the colour of the selected markup (none for a colour of another application).</summary>
    private void UpdateColorSwatch()
    {
        if (_comments?.SelectedItem?.Annotation.Color is { } color)
        {
            CommentColorSwatch.Background = new ImmutableSolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            CommentColorSwatch.IsVisible = true;
        }
        else
        {
            CommentColorSwatch.IsVisible = false;
        }
    }

    /// <summary>Fills the menu when it opens: the five colours with swatch and name, the colour of the markup with a check mark.</summary>
    private void OnColorMenuOpening(object? sender, EventArgs e)
    {
        MenuKeyboardFocus.EnsureRegistered();
        _colorEntries.Clear();
        if (_comments is not { SelectedIsEditable: true } comments)
        {
            return;
        }

        foreach (var color in AnnotationPalette.All)
        {
            var name = Strings.ColorName(color);
            var entry = new MenuItem
            {
                Name = "ColorEntry" + color,
                Header = name,
                Command = comments.SetItemColorCommand,
                CommandParameter = color,
                Icon = Swatch(color, comments.SelectedColor == color),
            };
            AutomationProperties.SetName(entry, name);
            _colorEntries.Add(entry);
        }
    }
}
