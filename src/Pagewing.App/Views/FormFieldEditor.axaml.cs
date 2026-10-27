using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Forms;

namespace Pagewing.App.Views;

/// <summary>
/// The input of a form field on the page (milestone 11): opened by the document view over the field that has the focus in the
/// mode "Fill in form" (a click, Tab, the toolbar), placed exactly over the field's rectangle at the zoom of the view
/// (<see cref="Place"/>). It knows its field; the view decides what becomes of the input (<see cref="TextEdited"/>,
/// <see cref="ChoiceMade"/>, <see cref="CommitRequested"/>, <see cref="MoveRequested"/>, <see cref="EscapeRequested"/>,
/// <see cref="Left"/>). The text of a text field goes to the tab as its open value at every change; a choice is applied when
/// it is made.
/// </summary>
/// <remarks>
/// Keys (taken before the input sees them): Tab and Shift+Tab move to the next or previous field (the view says whether there
/// is one; after the last and before the first the key is left alone, so the focus leaves the document, WCAG 2.1.2); Enter in
/// a one-line field, a closed combo box or a list applies and moves on, in a multi-line field it breaks the line; Ctrl+Enter
/// applies; Esc drops the open value (a second Esc leaves the field), except in an open drop-down, which closes. Ctrl+Z in a
/// text field is the field's own (the window gives it to text fields). A text is never shortened here: the length and the
/// characters are checked when it is applied, and a refused text stays with the reason (<see cref="ShowProblem"/>).
/// </remarks>
public sealed partial class FormFieldEditor : UserControl
{
    /// <summary>DIPs a notch of the wheel over a closed combo box scrolls the document (as over the cards' lists).</summary>
    private const double WheelStep = 50;

    private static readonly IBrush Paper = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush Ink = new ImmutableSolidColorBrush(Colors.Black);

    // Set while the editor changes its own input (a field opened, a value shown): those changes are not the user's.
    private bool _updating;

    // The text last taken as the open value (shown again when the tab takes none).
    private string _acceptedText = string.Empty;

    public FormFieldEditor()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        // The text property, not TextChanged: that event comes later (posted), when the editor may show another field.
        TextField.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTextChanged(TextField.Text ?? string.Empty);
            }
        };
        ChoiceField.SelectionChanged += OnChoiceSelectionChanged;
        ChoiceField.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty && ChoiceField.IsEditable && !IsSelectedLabel(ChoiceField.Text))
            {
                OnTextChanged(ChoiceField.Text ?? string.Empty);
            }
        };
        ListField.SelectionChanged += OnListSelectionChanged;

        // The page keys are key bindings of the window, matched before any key event: the inputs that use them claim them
        // (a text field moves its caret or pages its lines, a list pages its options without choosing one, a closed combo box
        // keeps them), so they never turn the page under an open field. The open list of the combo box lies in the overlay
        // layer of the window and claims them as the cards' lists do (CardLists).
        ClaimPageKeys(TextField, PageText);
        ClaimPageKeys(ListField, forward => PageScroller(ListField, forward));
        ClaimPageKeys(ChoiceField, static _ => { });
        ChoiceField.TemplateApplied += (_, e) => CardLists.OnTemplateApplied(this, ChoiceField, e);

        // A focused, closed combo box would take the wheel to change its choice (one step each notch): the wheel scrolls the
        // document instead.
        ChoiceField.AddHandler(PointerWheelChangedEvent, OnChoiceWheel, RoutingStrategies.Tunnel);

        // The paper behind the text in every state of the field (Fluent paints its own surface on focus and hover).
        foreach (var key in (ReadOnlySpan<string>)["TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver"])
        {
            TextField.Resources[key] = Paper;
        }

        foreach (var key in (ReadOnlySpan<string>)["TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver"])
        {
            TextField.Resources[key] = Ink;
        }

        TextField.Background = Paper;
        TextField.Foreground = Ink;
    }

    /// <summary>Raised when the user changed the text (the open value of the field); returns whether the tab took it.</summary>
    internal event Func<FormFieldEditor, string, bool>? TextEdited;

    /// <summary>Raised when the user chose an option of a combo box or list (the value to apply).</summary>
    internal event Action<FormFieldEditor, FormFieldState>? ChoiceMade;

    /// <summary>Raised for Enter (<see langword="true"/>: apply and move on) or Ctrl+Enter (<see langword="false"/>: apply).</summary>
    internal event Action<FormFieldEditor, bool>? CommitRequested;

    /// <summary>Raised for Tab (<see langword="true"/>) or Shift+Tab; returns whether the key was taken (another field has the focus).</summary>
    internal event Func<FormFieldEditor, bool, bool>? MoveRequested;

    /// <summary>Raised for Esc outside an open drop-down.</summary>
    internal event Action<FormFieldEditor>? EscapeRequested;

    /// <summary>Raised when the keyboard focus left the editor.</summary>
    internal event Action<FormFieldEditor>? Left;

    /// <summary>Gets the tab the field belongs to.</summary>
    internal TabViewModel? Tab { get; private set; }

    /// <summary>Gets the field the editor shows, or <see langword="null"/> while it is closed.</summary>
    internal FormField? Field { get; private set; }

    /// <summary>Gets the input that is shown (text box, combo box or list), or <see langword="null"/>.</summary>
    internal Control? Input => Field?.Kind switch
    {
        FormFieldKind.Text => TextField,
        FormFieldKind.ComboBox => ChoiceField,
        FormFieldKind.ListBox => ListField,
        _ => null,
    };

    /// <summary>Gets the text of a text field or of an editable combo box.</summary>
    internal string Text => Field?.Kind == FormFieldKind.ComboBox ? ChoiceField.Text ?? string.Empty : TextField.Text ?? string.Empty;

    /// <summary>Gets whether a reason why the value cannot be applied is shown.</summary>
    internal bool HasProblem => ProblemFrame.IsVisible;

    /// <summary>Gets the reason shown, or <see langword="null"/>.</summary>
    internal string? Problem => HasProblem ? ProblemText.Text : null;

    /// <summary>Whether the editor shows <paramref name="key"/>.</summary>
    internal bool Shows(FormWidgetKey key) => IsVisible && Field is { } field && field.Key == key;

    /// <summary>
    /// Opens the editor for <paramref name="field"/> of <paramref name="tab"/> with <paramref name="text"/> (the open value of a
    /// text field, or its value) and its options and choice.
    /// </summary>
    internal void Start(TabViewModel tab, FormField field, string text)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(field);
        Tab = tab;
        Field = field;
        _updating = true;
        try
        {
            TextField.IsVisible = field.Kind == FormFieldKind.Text;
            ChoiceField.IsVisible = field.Kind == FormFieldKind.ComboBox;
            ListField.IsVisible = field.Kind == FormFieldKind.ListBox;
            switch (field.Kind)
            {
                case FormFieldKind.Text:
                    var multiline = field.Traits.HasFlag(FormFieldTraits.Multiline);
                    TextField.AcceptsReturn = multiline;
                    TextField.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    TextField.VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
                    TextField.PasswordChar = field.Traits.HasFlag(FormFieldTraits.Password) ? '•' : default;

                    // A new field starts a new history of its own: Ctrl+Z never brings back the text of another field.
                    TextField.IsUndoEnabled = false;
                    TextField.Text = text;
                    TextField.IsUndoEnabled = true;
                    TextField.CaretIndex = text.Length;
                    break;
                case FormFieldKind.ComboBox:
                    ChoiceField.IsEditable = field.Traits.HasFlag(FormFieldTraits.Edit);
                    ChoiceField.ItemsSource = field.Options.Select(option => option.Label).ToList();
                    ChoiceField.SelectedIndex = SelectedIndexOf(field);
                    if (ChoiceField.IsEditable)
                    {
                        ChoiceField.Text = text;
                    }

                    break;
                case FormFieldKind.ListBox:
                    ListField.SelectionMode = field.Traits.HasFlag(FormFieldTraits.MultiSelect)
                        ? SelectionMode.Multiple | SelectionMode.Toggle
                        : SelectionMode.Single;
                    ListField.ItemsSource = field.Options.Select(option => option.Label).ToList();
                    SelectOptions(field.State.SelectedOptions);
                    break;
            }

            _acceptedText = text;
        }
        finally
        {
            _updating = false;
        }

        if (Input is { } input)
        {
            AutomationProperties.SetName(input, field.DisplayName);
        }

        ShowProblem(null);
    }

    /// <summary>
    /// Shows a newer value of the field (read again after undo, redo or a write), unless the user has an open value in it:
    /// <paramref name="text"/> is the text to show for a text field or editable combo box.
    /// </summary>
    internal void ShowValue(FormField field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (Field is not { } shown || shown.Key != field.Key)
        {
            return;
        }

        Field = field;
        _updating = true;
        try
        {
            switch (field.Kind)
            {
                case FormFieldKind.Text when !string.Equals(TextField.Text, text, StringComparison.Ordinal):
                    TextField.Text = text;
                    TextField.CaretIndex = text.Length;
                    break;
                case FormFieldKind.ComboBox:
                    var index = SelectedIndexOf(field);
                    if (ChoiceField.SelectedIndex != index)
                    {
                        ChoiceField.SelectedIndex = index;
                    }

                    if (ChoiceField.IsEditable && !string.Equals(ChoiceField.Text, text, StringComparison.Ordinal))
                    {
                        ChoiceField.Text = text;
                    }

                    break;
                case FormFieldKind.ListBox when !ListField.Selection.SelectedIndexes.Order().SequenceEqual(field.State.SelectedOptions.Order()):
                    SelectOptions(field.State.SelectedOptions);
                    break;
            }

            _acceptedText = text;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Lets the field go (the editor is hidden); its changes are no longer reported.</summary>
    internal void Stop()
    {
        Field = null;
        Tab = null;
        ShowProblem(null);
    }

    /// <summary>
    /// Places the input over the field: <paramref name="rect"/> is the field in the coordinates of the editor's parent,
    /// <paramref name="dipsPerPoint"/> the zoom of the page. The input takes exactly that rectangle.
    /// </summary>
    internal void Place(Rect rect, double dipsPerPoint)
    {
        Margin = new Thickness(rect.X, rect.Y, 0, 0);
        var width = Math.Max(1, rect.Width);
        var height = Math.Max(1, rect.Height);
        var oneLine = (Field is { Kind: FormFieldKind.Text } field && !field.Traits.HasFlag(FormFieldTraits.Multiline))
            || Field?.Kind == FormFieldKind.ComboBox;

        // Readable at every zoom: a one-line input fits its text to the height of the field, a multi-line one and a list use the
        // 10-point text of a form at the zoom of the view.
        var fontSize = Math.Clamp(oneLine ? height * 0.6 : 10 * dipsPerPoint, 6, 32);
        var padding = Math.Clamp(2 * dipsPerPoint, 1, 6);
        foreach (var input in (ReadOnlySpan<Control>)[TextField, ChoiceField, ListField])
        {
            input.Width = width;
            input.Height = height;
        }

        TextField.FontSize = fontSize;
        TextField.Padding = new Thickness(padding, oneLine ? 0 : padding / 2, padding, 0);
        ChoiceField.FontSize = fontSize;
        ChoiceField.Padding = new Thickness(padding, 0, 0, 0);
        ListField.FontSize = fontSize;
        ProblemFrame.MaxWidth = Math.Max(width, 240);
    }

    /// <summary>Puts the keyboard focus into the input; <paramref name="selectAll"/> selects the text of a text field (keyboard).</summary>
    internal void FocusInput(bool selectAll)
    {
        if (Input is not { } input)
        {
            return;
        }

        var method = selectAll ? NavigationMethod.Tab : NavigationMethod.Pointer;
        if (!input.Focus(method) && input == ListField
            && ListField.ContainerFromIndex(Math.Max(0, ListField.SelectedIndex)) is { } item)
        {
            // The list itself takes no focus: its chosen (or first) option does, inside the list, which claims the keys.
            item.Focus(method);
        }

        if (selectAll && input == TextField)
        {
            TextField.SelectAll();
        }
    }

    /// <summary>Shows why the value cannot be applied (the editor stays), or hides it.</summary>
    internal void ShowProblem(string? problem)
    {
        ProblemText.Text = problem;
        ProblemFrame.IsVisible = problem is not null;
        if (Input is { } input && Field is { } field)
        {
            var hint = field.Kind == FormFieldKind.Text && field.Traits.HasFlag(FormFieldTraits.Multiline) ? Strings.FormEditorHintMultiline : Strings.FormEditorHint;
            var state = FormFieldElement.HelpTextOf(field);
            // With a reason the keys still follow it, so a screen reader hears the way out (Esc) as well.
            AutomationProperties.SetHelpText(input, problem is not null ? problem + " " + hint : state is null ? hint : state + ". " + hint);
        }
    }

    /// <summary>Gets or sets the scroll viewer of the document, which the wheel over a closed combo box scrolls.</summary>
    internal ScrollViewer? DocumentScroller { get; set; }

    private static void ClaimPageKeys(InputElement input, Action<bool> page)
    {
        input.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => page(true)) });
        input.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => page(false)) });
    }

    private static void PageScroller(Control input, bool forward)
    {
        if (input.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
        {
            if (forward)
            {
                scroller.PageDown();
            }
            else
            {
                scroller.PageUp();
            }
        }
    }

    /// <summary>Page Up/Page Down in a text field: a multi-line field pages its lines, a one-line field goes to its start or end.</summary>
    private void PageText(bool forward)
    {
        if (TextField.AcceptsReturn)
        {
            PageScroller(TextField, forward);
            return;
        }

        TextField.ClearSelection();
        TextField.CaretIndex = forward ? (TextField.Text ?? string.Empty).Length : 0;
    }

    private void OnChoiceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DocumentScroller is { } scroller)
        {
            CardLists.OnListWheel(scroller, WheelStep, sender, e);
        }
        else if (sender is ComboBox { IsDropDownOpen: false })
        {
            e.Handled = true;
        }
    }

    private static int SelectedIndexOf(FormField field) =>
        field.State.SelectedOptions is [var index, ..] && index >= 0 && index < field.Options.Count ? index : -1;

    private void SelectOptions(IReadOnlyList<int> options)
    {
        ListField.Selection.Clear();
        foreach (var index in options)
        {
            if (index >= 0 && index < (Field?.Options.Count ?? 0))
            {
                ListField.Selection.Select(index);
            }
        }
    }

    private bool IsSelectedLabel(string? text) =>
        ChoiceField.SelectedItem is string label && string.Equals(label, text, StringComparison.Ordinal);

    private void OnTextChanged(string text)
    {
        if (_updating || Field is null)
        {
            return;
        }

        if (TextEdited?.Invoke(this, text) == true)
        {
            _acceptedText = text;
            if (HasProblem && Tab?.CurrentDraft is not { Reason: not null })
            {
                ShowProblem(null);
            }

            return;
        }

        // The tab took no open value (editing is locked, or another field keeps a value it cannot apply): the text shown is
        // the one it has, never one it does not.
        _updating = true;
        try
        {
            if (Field?.Kind == FormFieldKind.ComboBox)
            {
                ChoiceField.Text = _acceptedText;
            }
            else
            {
                TextField.Text = _acceptedText;
                TextField.CaretIndex = _acceptedText.Length;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnChoiceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || Field is not { Kind: FormFieldKind.ComboBox } || ChoiceField.SelectedIndex < 0)
        {
            return;
        }

        ChoiceMade?.Invoke(this, FormFieldState.OfSelection([ChoiceField.SelectedIndex]));
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || Field is not { Kind: FormFieldKind.ListBox })
        {
            return;
        }

        ChoiceMade?.Invoke(this, FormFieldState.OfSelection([.. ListField.Selection.SelectedIndexes.Order()]));
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (Field is not { } field)
        {
            return;
        }

        var dropDownOpen = field.Kind == FormFieldKind.ComboBox && ChoiceField.IsDropDownOpen;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Tab, KeyModifiers.None or KeyModifiers.Shift):
                e.Handled = MoveRequested?.Invoke(this, e.KeyModifiers == KeyModifiers.None) == true;
                break;
            case (Key.Enter, KeyModifiers.None) when !dropDownOpen && !(field.Kind == FormFieldKind.Text && field.Traits.HasFlag(FormFieldTraits.Multiline)):
                e.Handled = true;
                CommitRequested?.Invoke(this, true);
                break;
            case (Key.Enter, KeyModifiers.Control) when !dropDownOpen:
                e.Handled = true;
                CommitRequested?.Invoke(this, false);
                break;
            case (Key.Escape, KeyModifiers.None) when !dropDownOpen:
                e.Handled = true;
                EscapeRequested?.Invoke(this);
                break;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // The focus went to a part of the editor itself, into the open drop-down of its combo box, or nowhere yet: not leaving.
        if (IsKeyboardFocusWithin || Field is null || (Field.Kind == FormFieldKind.ComboBox && ChoiceField.IsDropDownOpen))
        {
            return;
        }

        Left?.Invoke(this);
    }
}
