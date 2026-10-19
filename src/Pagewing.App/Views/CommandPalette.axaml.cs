using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The view of the command palette (<see cref="PaletteViewModel"/>, its DataContext). The window shows it with
/// <see cref="Show"/> and hides it with <see cref="Hide"/>; Esc, Enter and a click on an entry close it through the view
/// model, and so does the focus leaving it (<see cref="FocusLeft"/>, the window decides).
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    // One slot per shown line (heading or entry), reused from one query to the next; the extra ones are hidden.
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<Control, Slot> _slotByRoot = [];
    private readonly List<Slot> _slotOfItem = [];
    private PaletteViewModel? _viewModel;
    private int _pressedItem = -1;

    public CommandPalette()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: the input claims
        // the keys of the list (the arrows would otherwise move the caret, the page keys turn the pages of the document
        // behind it, Esc reach the link card or the search).
        void Bind(Key key, Action<PaletteViewModel> action) =>
            PaletteInput.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key),
                Command = new RelayCommand(() =>
                {
                    if (_viewModel is not null)
                    {
                        action(_viewModel);
                    }
                }),
            });

        Bind(Key.Down, palette => palette.MoveNext());
        Bind(Key.Up, palette => palette.MovePrevious());
        Bind(Key.PageDown, palette => palette.PageDown());
        Bind(Key.PageUp, palette => palette.PageUp());
        Bind(Key.Enter, palette => palette.Run());
        Bind(Key.Escape, palette => palette.Close());

        // A press on an entry chooses it and its release runs it; a press anywhere on the card but the input keeps the
        // focus in the input (handled before the focus moves to the window).
        PaletteBox.AddHandler(PointerPressedEvent, OnBoxPointerPressed, RoutingStrategies.Tunnel);
        PaletteBox.AddHandler(PointerReleasedEvent, OnBoxPointerReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the keyboard focus leaves the shown palette (for another element, another window or nothing).</summary>
    internal event Action? FocusLeft;

    /// <summary>Gets the shown entries in their order, for tests.</summary>
    internal IReadOnlyList<Control> ItemRows => _slotOfItem.Select(slot => (Control)slot.Root).ToArray();

    /// <summary>Gets the shown group headings in their order, for tests.</summary>
    internal IReadOnlyList<TextBlock> Headings =>
        _slots.Where(slot => slot.Root.IsVisible && slot.ItemIndex < 0).Select(slot => slot.Heading).ToArray();

    /// <summary>Gets the entry a row of <see cref="ItemRows"/> shows.</summary>
    internal PaletteItem? ItemOf(Control row) =>
        _slotByRoot.TryGetValue(row, out var slot) && slot.ItemIndex >= 0 && _viewModel is { } palette && slot.ItemIndex < palette.Items.Count
            ? palette.Items[slot.ItemIndex]
            : null;

    /// <summary>Shows the palette with its entries and puts the focus into the input.</summary>
    internal void Show()
    {
        Rebuild();
        IsVisible = true;

        // After the layout of this change, when the input can take the focus.
        Dispatcher.UIThread.Post(FocusInputIfShown, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the palette.</summary>
    internal void Hide() => IsVisible = false;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnPalettePropertyChanged;
        }

        _viewModel = DataContext as PaletteViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnPalettePropertyChanged;
        }

        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty && !change.GetNewValue<bool>() && IsVisible)
        {
            // Asked once the press or key that moved the focus is done: a press on the scroll bar gives it back.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (IsVisible && !IsKeyboardFocusWithin)
                    {
                        FocusLeft?.Invoke();
                    }
                },
                DispatcherPriority.Input);
        }
    }

    /// <summary>Puts the keys into the input while the palette is shown (the window was activated with nothing focused).</summary>
    internal void FocusInput() => FocusInputIfShown();

    private void FocusInputIfShown()
    {
        if (IsVisible && PaletteInput.IsEffectivelyVisible)
        {
            PaletteInput.Focus();
        }
    }

    private void OnPalettePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaletteViewModel.Groups))
        {
            Rebuild();
        }
        else if (e.PropertyName == nameof(PaletteViewModel.SelectedIndex))
        {
            // A refresh in the background keeps the list where the user scrolled it.
            UpdateSelection(bringIntoView: _viewModel?.KeepsPosition != true);
        }
    }

    private void Rebuild()
    {
        _slotOfItem.Clear();
        var used = 0;
        var itemIndex = 0;
        foreach (var group in _viewModel?.Groups ?? [])
        {
            SlotAt(used++).ShowHeading(group.Title);
            foreach (var item in group.Items)
            {
                var slot = SlotAt(used++);
                slot.ShowItem(item, itemIndex++);
                _slotOfItem.Add(slot);
            }
        }

        for (var i = used; i < _slots.Count; i++)
        {
            _slots[i].Hide();
        }

        PaletteEmpty.IsVisible = itemIndex == 0;
        PaletteScroller.IsVisible = itemIndex > 0;
        if (_viewModel?.KeepsPosition != true)
        {
            PaletteScroller.Offset = default;
        }

        UpdateSelection(bringIntoView: false);
    }

    private Slot SlotAt(int index)
    {
        while (_slots.Count <= index)
        {
            var slot = new Slot();
            slot.Root.Choose = () =>
            {
                if (_viewModel is not null && slot.ItemIndex >= 0 && slot.ItemIndex < _viewModel.Items.Count)
                {
                    _viewModel.SelectedIndex = slot.ItemIndex;
                }
            };
            _slots.Add(slot);
            _slotByRoot.Add(slot.Root, slot);
            PaletteRows.Children.Add(slot.Root);
        }

        return _slots[index];
    }

    private void UpdateSelection(bool bringIntoView)
    {
        var selected = _viewModel?.SelectedIndex ?? -1;
        foreach (var slot in _slots)
        {
            slot.Root.SetChosen(slot.ItemIndex >= 0 && slot.ItemIndex == selected);
        }

        // The focus stays in the input: its help text names the chosen entry. Setting the attached property tells no
        // assistive technology (the control's peer forwards only a few properties), so the change is raised on the peer
        // of the input, when one exists: UI Automation gets a HelpText change, AT-SPI an accessible-description change.
        // The selection events of the rows (PaletteRow.SetChosen) reach UI Automation only; the AT-SPI bridge ignores them.
        var help = selected >= 0 && selected < _slotOfItem.Count ? AutomationProperties.GetName(_slotOfItem[selected].Root) : null;
        var previous = AutomationProperties.GetHelpText(PaletteInput);
        if (!string.Equals(previous, help, StringComparison.Ordinal))
        {
            AutomationProperties.SetHelpText(PaletteInput, help);
            ControlAutomationPeer.FromElement(PaletteInput)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty, previous, help);
        }

        if (bringIntoView && selected >= 0 && selected < _slotOfItem.Count)
        {
            _slotOfItem[selected].Root.BringIntoView();
        }
    }

    private void OnBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (ReferenceEquals(source, PaletteInput) || PaletteInput.IsVisualAncestorOf(source)))
        {
            return;
        }

        // The scroll bar of the list takes its press (its focus moves away for a moment); then the input gets it back.
        if (e.Source is Visual inBar && inBar.GetSelfAndVisualAncestors().OfType<ScrollBar>().Any())
        {
            Dispatcher.UIThread.Post(FocusInputIfShown, DispatcherPriority.Input);
            return;
        }

        e.Handled = true;
        _pressedItem = -1;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && SlotOf(e.Source) is { ItemIndex: >= 0 } slot && _viewModel is not null)
        {
            _viewModel.SelectedIndex = slot.ItemIndex;
            _pressedItem = slot.ItemIndex;
        }
    }

    private void OnBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _pressedItem;
        _pressedItem = -1;
        if (pressed < 0 || pressed >= _slotOfItem.Count || _viewModel is not { } palette || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        // Released over the entry it was pressed on: run it.
        var root = _slotOfItem[pressed].Root;
        var point = e.GetPosition(root);
        if (new Rect(root.Bounds.Size).Contains(point) && pressed < palette.Items.Count)
        {
            e.Handled = true;
            palette.Run(palette.Items[pressed]);
        }
    }

    private Slot? SlotOf(object? source)
    {
        for (var visual = source as Visual; visual is not null && !ReferenceEquals(visual, PaletteBox); visual = visual.GetVisualParent())
        {
            if (visual is Control control && _slotByRoot.TryGetValue(control, out var slot))
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>A line of the list: a group heading, or an entry with title, detail and shortcut (<see cref="PaletteRow"/>).</summary>
    private sealed class Slot
    {
        public Slot()
        {
            Heading = new TextBlock();
            Heading.Classes.AddRange(["caption", "paletteHeading"]);
            Title = new TextBlock { MaxWidth = 360 };
            Title.Classes.AddRange(["body", "paletteTitle"]);
            Detail = new TextBlock();
            Detail.Classes.AddRange(["caption", "paletteDetail"]);
            Key = new TextBlock();
            Key.Classes.Add("mono");
            Kbd = new Border { Child = Key };
            Kbd.Classes.Add("kbd");
            DockPanel.SetDock(Kbd, Dock.Right);
            DockPanel.SetDock(Title, Dock.Left);
            Row = new DockPanel { Children = { Kbd, Title, Detail } };
            Root = new PaletteRow { Child = new Panel { Children = { Heading, Row } } };
            Root.Classes.Add("paletteSlot");
        }

        public PaletteRow Root { get; }

        public TextBlock Heading { get; }

        public int ItemIndex { get; private set; } = -1;

        private DockPanel Row { get; }

        private TextBlock Title { get; }

        private TextBlock Detail { get; }

        private Border Kbd { get; }

        private TextBlock Key { get; }

        public void ShowHeading(string title)
        {
            ItemIndex = -1;
            Heading.Text = title;
            Heading.IsVisible = true;
            Row.IsVisible = false;
            Root.Classes.Set("header", true);
            Root.Classes.Set("item", false);
            Root.IsItem = false;
            Root.IsVisible = true;
            AutomationProperties.SetName(Root, title);
            AutomationProperties.SetHeadingLevel(Root, 2);
        }

        public void ShowItem(PaletteItem item, int index)
        {
            ItemIndex = index;
            Title.Text = item.Title;
            Detail.Text = item.Detail;
            Detail.IsVisible = item.Detail is not null;
            Key.Text = item.Gesture;
            Kbd.IsVisible = item.Gesture is not null;
            Heading.IsVisible = false;
            Row.IsVisible = true;
            Root.Classes.Set("header", false);
            Root.Classes.Set("item", true);
            Root.IsItem = true;
            Root.IsVisible = true;
            AutomationProperties.SetName(Root, item.AutomationName);
            AutomationProperties.SetHeadingLevel(Root, 0);
        }

        public void Hide()
        {
            ItemIndex = -1;
            Root.IsVisible = false;
        }
    }
}
