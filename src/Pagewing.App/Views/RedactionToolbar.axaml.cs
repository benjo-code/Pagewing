using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

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

    private TabViewModel? _tab;
    private bool _fitting;

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
        _tab = DataContext as TabViewModel;
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
