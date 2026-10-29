using System.ComponentModel;
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
/// The toolbar of the mode "Fill in form" over the document (design system 6 point 4 and 7.7): "Highlight fields", previous
/// and next field, undo, redo and "Done". Its <c>DataContext</c> is the active tab; the switch follows
/// <see cref="TabViewModel.HighlightFields"/> (toggle state for screen readers, a frame and an accent label, so its state is
/// not told by colour alone). Made by the window when the mode is first started, never in the startup path.
/// </summary>
public sealed partial class FormToolbar : UserControl
{
    /// <summary>The last level of <see cref="Fit"/>: the label of the switch, the text and the icon of the mode name are gone.</summary>
    private const int MaxFitLevel = 3;

    private TabViewModel? _tab;
    private bool _fitting;

    public FormToolbar()
    {
        InitializeComponent();

        // Esc of the toolbar: the open value of a field, its focus, then the mode (the keys go back to the document then, see
        // MainWindow). An Esc of the window (a waiting address, a card) comes first: this binding lies nearer the focus, so it
        // lets the key go on then.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () => _tab?.EscapeFill(),
                () => _tab is { IsFilling: true } && TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { HasWindowEscape: true }),
        });
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>
    /// The tooltips of the switch, undo and redo name their shortcuts as the catalog has them ("Highlight fields
    /// (Ctrl+Shift+J)"), and screen readers get them as accelerator keys.
    /// </summary>
    internal void NameShortcuts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Describe(HighlightToggle, Strings.FormHighlightFields, catalog.ById("form.highlight"));
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

        UpdateState();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.HighlightFields) or nameof(TabViewModel.IsFilling))
        {
            UpdateState();
        }
    }

    /// <summary>Shows whether the fields are highlighted: on for screen readers and with the frame of a chosen tool.</summary>
    private void UpdateState()
    {
        var on = _tab?.HighlightFields == true;
        HighlightToggle.IsChecked = on;
        HighlightToggle.Classes.Set("active", on);
    }

    /// <summary>
    /// Too narrow for everything (design system 6, narrow window), step by step until it fits: first the label of the switch
    /// goes (its tooltip and name stay), then the text of the mode name, then its icon. The buttons always stay in the row.
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
                HighlightLabel.IsVisible = level < 1;
                ModeName.IsVisible = level < 2;
                ModeLabel.IsVisible = level < 3;
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
