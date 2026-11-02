using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Redaction;

namespace Pagewing.App.Views;

/// <summary>
/// The toolbar of the mode "Redact" over the document (milestone 14, design system 6 point 4 and 7.7): the tools of the pointer,
/// "Mark selection", "Find and redact" (which shows the panel), the fill, undo and redo, deleting marks, the counter of the marks, "Discard" and "Done". Its
/// <c>DataContext</c> is the active tab. Made by the window when the mode is first started, never in the startup path. The button
/// that applies the marks is here with the marks (task 13), and so is "Preview", whose numbers fill a second row.
/// </summary>
public sealed partial class RedactionToolbar : UserControl
{
    /// <summary>The last level of <see cref="Fit"/>: the hint, the labels, the mode name, its icon, the text of the counter and the words of "Done", "Discard" and "Apply" are gone.</summary>
    private const int MaxFitLevel = 8;

    /// <summary>From this level of <see cref="Fit"/> on the buttons of the tools have less padding.</summary>
    private const int TightLevel = 7;

    private TabViewModel? _tab;
    private bool _fitting;
    private readonly List<(Button Button, RedactionFill Fill, Control Check)> _presets = [];

    public RedactionToolbar()
    {
        InitializeComponent();

        // Esc of the toolbar: a drag, the selected marks, the text selection, then the mode (the keys go back to the document
        // then, see MainWindow). An Esc of the window (a waiting address, a card) comes first: this binding lies nearer the
        // focus, so it lets the key go on then.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () => _tab?.EscapeRedact(),
                () => _tab is { IsRedacting: true } && TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { HasWindowEscape: true }),
        });
        SizeChanged += (_, _) => Fit();

        // The colour field: Enter takes the colour typed, leaving the field takes it when it is one, else puts the colour of the plan back.
        FillHexBox.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(() => CommitFillText(showError: true)) });
        FillHexBox.LostFocus += (_, _) => CommitFillText(showError: false);
        BuildPresets();

        // "Apply …" comes and goes with the first and the last mark, and the counter changes its words: the row is fitted again.
        ApplyButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                Fit();
            }
        };
        CounterText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                Fit();
            }
        };
    }

    /// <summary>
    /// The tooltips of "Mark selection", undo and redo name their shortcuts as the catalog has them ("Mark selection
    /// (Ctrl+Enter)"), and screen readers get them as accelerator keys.
    /// </summary>
    internal void NameShortcuts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Describe(MarkSelectionButton, Strings.RedactMarkSelection, catalog.ById("redact.markSelection"));
        Describe(UndoButton, Strings.CommandUndo, catalog.ById("edit.undo"));
        Describe(RedoButton, Strings.CommandRedo, catalog.ById("edit.redo"));

        static void Describe(Control control, string label, AppCommand? command)
        {
            if (command?.Gesture is not { } gesture)
            {
                ToolTip.SetTip(control, label);
                return;
            }

            var keys = GestureText.Of(gesture);
            ToolTip.SetTip(control, $"{label} ({keys})");
            AutomationProperties.SetAcceleratorKey(control, keys);
        }
    }

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

        ShowFill();
    }

    /// <summary>
    /// Takes the colour typed in the colour field as the fill when it is written as <c>#RRGGBB</c>. Otherwise the field keeps what
    /// was typed and the error is shown (<paramref name="showError"/>, after Enter), or the colour of the plan is put back.
    /// </summary>
    /// <param name="showError">Whether text that is no colour is told, rather than undone.</param>
    /// <returns>Whether the fill of the plan is the typed colour now.</returns>
    internal bool CommitFillText(bool showError)
    {
        if (_tab is null)
        {
            return false;
        }

        var text = FillHexBox.Text?.Trim();
        if (RedactionFill.TryParse(text, out var fill))
        {
            FillHexError.IsVisible = false;
            if (_tab.SetRedactionFillCommand.CanExecute(fill))
            {
                _tab.SetRedactionFillCommand.Execute(fill);
            }

            ShowFill();
            return _tab.RedactionFill == fill;
        }

        if (showError)
        {
            FillHexError.IsVisible = true;
        }
        else
        {
            ShowFill();
        }

        return false;
    }

    private void BuildPresets()
    {
        for (var i = 0; i < RedactionFill.Presets.Count; i++)
        {
            var fill = RedactionFill.Presets[i];
            var name = Strings.RedactFillPresetName(i, fill.ToHex());
            var check = new StrokeIcon { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
            check.Bind(StrokeIcon.DataProperty, this.GetResourceObservable("Pw.Icon.Check"));
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(fill.Red, fill.Green, fill.Blue)),
                Child = check,
            };
            swatch.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Pw.Brush.Border.Strong"));
            var button = new Button
            {
                Name = $"FillPreset{i}",
                Classes = { "icon", "tool" },
                Content = swatch,
                CommandParameter = fill,
                Padding = new Thickness(2),
            };
            ToolTip.SetTip(button, name);
            AutomationProperties.SetName(button, name);
            _presets.Add((button, fill, check));
            FillPresets.Children.Add(button);

            // The check mark sits on the swatch in the colour that stands out from it (black or white), so it shows on every preset.
            check.Brush = new SolidColorBrush(RedactionOverlay.EdgeAgainst(Colors.White, Color.FromRgb(fill.Red, fill.Green, fill.Blue)));
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.RedactionFill) or nameof(TabViewModel.RedactionFillHex) or nameof(TabViewModel.IsRedactionFillCustom))
        {
            ShowFill();
        }
    }

    /// <summary>Shows the fill of the plan: the text of the colour field, the swatch of the button and the chosen preset.</summary>
    private void ShowFill()
    {
        var fill = _tab?.RedactionFill ?? RedactionFill.Black;
        if (FillHexBox.Text != fill.ToHex())
        {
            FillHexBox.Text = fill.ToHex();
        }

        FillHexError.IsVisible = false;
        FillSwatch.Background = new SolidColorBrush(Color.FromRgb(fill.Red, fill.Green, fill.Blue));
        foreach (var (button, presetFill, check) in _presets)
        {
            var chosen = presetFill == fill;
            check.IsVisible = chosen;
            button.Classes.Set("active", chosen);
            button.Command = _tab?.SetRedactionFillCommand;
        }
    }

    /// <summary>
    /// Too narrow for everything (design system 6, narrow window), step by step until it fits: first the hint goes, then the
    /// labels of the tools, the preview and the fill (their tooltips and names stay), then the text of the mode name, then its icon,
    /// then the text of the counter and the separators between the groups, then the words of "Done" and "Discard", last those of
    /// "Apply". The buttons
    /// always stay in the row.
    /// </summary>
    private void Fit()
    {
        if (_fitting)
        {
            return;
        }

        _fitting = true;
        try
        {
            var width = ToolbarContent.Bounds.Width;
            if (!(width > 0))
            {
                return;
            }

            for (var level = 0; level <= MaxFitLevel; level++)
            {
                PlanHint.IsVisible = level < 1;
                AreaToolLabel.IsVisible = level < 2;
                TextToolLabel.IsVisible = level < 2;
                MarkSelectionLabel.IsVisible = level < 2;
                FindLabel.IsVisible = level < 2;
                PreviewLabel.IsVisible = level < 2;
                FillLabel.IsVisible = level < 2;
                FillCustomDetail.IsVisible = level < 2;
                ModeName.IsVisible = level < 3;
                ModeLabel.IsVisible = level < 4;
                CounterText.IsVisible = level < 5;
                DoneLabel.IsVisible = level < 6;
                DiscardLabel.IsVisible = level < 7;
                ApplyLabel.IsVisible = level < 8;
                foreach (var separator in ToolsArea.Children.OfType<Border>())
                {
                    separator.IsVisible = level < 5;
                }

                // At the end the buttons of the tools give up part of their padding (they stay wider than 24 DIPs, WCAG 2.5.8).
                foreach (var tool in ToolsArea.Children.OfType<Button>().Where(static b => b.Classes.Contains("tool")))
                {
                    if (level >= TightLevel)
                    {
                        tool.Padding = new Thickness(4, 0);
                    }
                    else
                    {
                        tool.ClearValue(PaddingProperty);
                    }
                }

                if (level == MaxFitLevel || Needs() <= width)
                {
                    break;
                }
            }
        }
        finally
        {
            // The measures above ran without a limit: the next layout pass measures the content for its real width again.
            ToolbarContent.InvalidateMeasure();
            ToolbarFrame.InvalidateMeasure();
            _fitting = false;
        }
    }

    private double Needs()
    {
        // Everything is measured again: a part whose measure is still valid would keep the old size of what came or went.
        ToolbarContent.InvalidateMeasure();
        foreach (var part in ToolbarContent.GetVisualDescendants().OfType<Layoutable>())
        {
            part.InvalidateMeasure();
        }

        ToolbarContent.Measure(Size.Infinity);
        return ToolbarContent.DesiredSize.Width - ToolbarContent.Margin.Left - ToolbarContent.Margin.Right;
    }
}
