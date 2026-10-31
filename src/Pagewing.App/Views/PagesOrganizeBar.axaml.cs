using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The tools of the mode "Organize pages" above the list of the "Pages" panel (design system 7.7, spec 5.3): the count, the
/// note while changes wait, the notice of a refused change, the tool buttons, "Apply" and "Discard". Its <c>DataContext</c> is
/// the active tab. "Discard" with changes pending asks first (the question replaces the buttons, "Keep" has the focus, Esc
/// keeps); without changes the same button is "Done" and ends the mode at once. Made by <see cref="PagesPanel"/> when the mode
/// starts, never in the startup path.
/// </summary>
public sealed partial class PagesOrganizeBar : UserControl
{
    /// <summary>The buttons of the tools with their name and the catalog entry whose shortcut the tooltip names.</summary>
    private readonly (Button Button, string Name, string CommandId)[] _tools;

    private TabViewModel? _tab;

    public PagesOrganizeBar()
    {
        InitializeComponent();
        _tools =
        [
            (RotateLeftButton, Strings.CommandRotatePagesLeft, "pages.rotateLeft"),
            (RotateRightButton, Strings.CommandRotatePagesRight, "pages.rotateRight"),
            (DeleteButton, Strings.CommandDeletePages, "pages.delete"),
            (DuplicateButton, Strings.CommandDuplicatePages, "pages.duplicate"),
            (BlankButton, Strings.CommandInsertBlankPage, "pages.insertBlank"),
            (InsertFileToolButton, Strings.CommandInsertFile, "pages.insertFile"),
            (MoveEarlierButton, Strings.CommandMovePagesEarlier, "pages.moveEarlier"),
            (MoveLaterButton, Strings.CommandMovePagesLater, "pages.moveLater"),
            (UndoButton, Strings.CommandUndo, "edit.undo"),
            (RedoButton, Strings.CommandRedo, "edit.redo"),
        ];
        foreach (var (button, name, _) in _tools)
        {
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }

        DiscardButton.Click += OnDiscardClick;
        KeepButton.Click += (_, _) => CloseQuestion(focusDiscard: true);
        ConfirmDiscardButton.Click += (_, _) => _tab?.DiscardPageEditsCommand.Execute(null);

        // Esc keeps the changes when the question is open. A binding of the bar lies nearer the focus than the window's, so
        // an Esc of the window (a card, an overlay) comes first; without a question the key goes on.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () => CloseQuestion(focusDiscard: true),
                () => DiscardConfirm.IsVisible && TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { HasWindowEscape: true }),
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel { } window)
        {
            NameShortcuts(window.Commands);
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

        UpdateDiscard();
    }

    /// <summary>The tooltips of the tools name their shortcuts as the catalog has them ("Rotate selected pages left (Ctrl+Shift+Left)"), and screen readers get them as accelerator keys.</summary>
    internal void NameShortcuts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var (button, name, id) in _tools)
        {
            Describe(button, name, catalog.ById(id));
        }

        Describe(ApplyButton, Strings.CommandApplyPageEdits, catalog.ById("pages.apply"));

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

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.HasPendingPageEdits))
        {
            UpdateDiscard();
        }
    }

    /// <summary>The button that ends the mode: "Discard" while changes wait, "Done" when nothing is staged. A question about changes that are gone is closed.</summary>
    private void UpdateDiscard()
    {
        var pending = _tab?.HasPendingPageEdits == true;
        var label = pending ? Strings.PagesDiscard : Strings.PagesDone;
        DiscardText.Text = label;
        DiscardIcon.Data = pending ? this.FindResource("Pw.Icon.X") as Avalonia.Media.Geometry : this.FindResource("Pw.Icon.Check") as Avalonia.Media.Geometry;
        AutomationProperties.SetName(DiscardButton, label);
        ToolTip.SetTip(DiscardButton, label);
        if (!pending && DiscardConfirm.IsVisible)
        {
            CloseQuestion(focusDiscard: false);
        }
    }

    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        if (_tab is not { } tab)
        {
            return;
        }

        if (!tab.HasPendingPageEdits)
        {
            // Nothing is staged, so nothing is lost: the mode just ends.
            tab.DiscardPageEditsCommand.Execute(null);
            return;
        }

        // Staged changes are dropped only after the question; "Keep" is the default.
        DiscardConfirm.IsVisible = true;
        ActionRow.IsVisible = false;
        Dispatcher.UIThread.Post(() => KeepButton.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }

    private void CloseQuestion(bool focusDiscard)
    {
        DiscardConfirm.IsVisible = false;
        ActionRow.IsVisible = true;
        if (focusDiscard)
        {
            Dispatcher.UIThread.Post(() => DiscardButton.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }
    }
}
