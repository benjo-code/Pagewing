using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Forms;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// Filling in a form on the page (milestone 11, spec "Ausfüllen"): in the mode "Fill in form" the form layer
/// (<see cref="Controls.FormOverlay"/>) marks the fields of the visible pages, a press on a field gives it the focus
/// (<see cref="TabViewModel.FocusedField"/>): a text field, combo box or list opens its input over the field
/// (<see cref="Views.FormFieldEditor"/>), a check box or radio button switches on the release inside it, a field Pagewing cannot
/// change only says why (<see cref="TabViewModel.ShowFormNotice"/>, the lock also as tooltip). A press anywhere else on a page
/// leaves the field (its value is applied) and selects text or follows a link as usual. Tab and Shift+Tab walk the fields in
/// the tab order (<see cref="TabViewModel.MoveToFieldAsync"/>), Space switches a check box or radio button with the focus; the
/// field that gets the focus is scrolled into view.
/// </summary>
/// <remarks>
/// <para>
/// Tab never traps the focus (WCAG 2.1.2): after the last field and before the first the key is not taken, the focus moves on
/// out of the document and the field lets go of it. The value of a text field is the tab's open value from the first change
/// (<see cref="TabViewModel.EditDraft"/>) and is applied when the field is left (another field, a press elsewhere, Enter,
/// Ctrl+Enter, the focus going elsewhere, the field scrolled out of view), one step of the history; a field entered and left
/// without a change makes none. A value that cannot be applied stays in the field with the reason.
/// </para>
/// <para>
/// The highlight of the fields is PDFium's: the tab tells the session first (<see cref="TabViewModel.ShowsFieldHighlight"/>),
/// then the view the canvas (<see cref="DocumentCanvas.FormHighlight"/>), so the canvas drops only the results of the old
/// setting. Nothing of this exists before the mode: the layer is made when it first starts, the input at the first field
/// that has one.
/// </para>
/// </remarks>
public sealed partial class DocumentView
{
    /// <summary>How far (DIPs) outside its rectangle a press still hits a field.</summary>
    internal const double FieldHitTolerance = 2;

    private FormOverlay? _formOverlay;
    private Panel? _formEditorHost;
    private FormFieldEditor? _formEditor;

    // The tab whose fields the view follows (FormFieldsChanged), while it is shown in the mode.
    private TabViewModel? _formTab;

    // A press on a check box or radio button: it switches on the release inside the same field.
    private FormPress? _formPress;

    // Set by a press on a text field: its input opens with the caret at the end instead of the text selected (the keyboard).
    private bool _formPressedText;

    // The tooltip shown over a locked field, and the fields last given to the layer (reused while nothing changed).
    private string? _formTip;
    private IReadOnlyList<FormField> _formFields = [];
    private List<IReadOnlyList<FormField>> _formFieldSources = [];

    /// <summary>Gets the form layer, or <see langword="null"/> before the mode first started (for tests).</summary>
    internal FormOverlay? FormOverlay => _formOverlay;

    /// <summary>Gets the open input of a form field, or <see langword="null"/> (for tests).</summary>
    internal FormFieldEditor? FormFieldEditor => _formEditor is { IsVisible: true, Field: not null } editor ? editor : null;

    /// <summary>Whether a field has an input over it (a text field, combo box or list that can be changed).</summary>
    private static bool HasInput(FormField field) =>
        TabViewModel.IsEditable(field) && field.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox;

    /// <summary>The field the view follows in the mode or stops following: called on a change of the mode and when a document is shown.</summary>
    private void SyncFormMode()
    {
        if (_viewModel is { IsFilling: true } tab && _layout is not null)
        {
            if (!ReferenceEquals(_formTab, tab))
            {
                DisconnectForms();
                _formTab = tab;
                tab.FormFieldsChanged += OnFormFieldsChanged;
                tab.VisibleFormPages = VisibleFormPages;
            }

            EnsureFormOverlay().IsVisible = true;
            RefreshForms(focus: false);
            return;
        }

        DisconnectForms();
    }

    /// <summary>The view leaves the tab, its document or the mode: the input closes (an open value stays with the tab), the layer empties.</summary>
    private void DisconnectForms()
    {
        HideFormEditor();
        _formPress = null;
        SetFormTip(null);
        if (_formTab is { } tab)
        {
            tab.FormFieldsChanged -= OnFormFieldsChanged;
            tab.VisibleFormPages = null;
            _formTab = null;
        }

        if (_formOverlay is { } overlay)
        {
            _formFields = [];
            _formFieldSources = [];
            overlay.Show([], null, outline: false);
            overlay.IsVisible = false;
        }
    }

    /// <summary>The 0-based pages the view shows now.</summary>
    private IEnumerable<int> VisibleFormPages()
    {
        if (_layout is not { PageCount: > 0 } layout)
        {
            return [];
        }

        var viewport = PageCanvas.Viewport;
        var (first, last) = layout.GetVisiblePages(viewport.Y, viewport.Bottom);
        return first > last ? [] : Enumerable.Range(first, last - first + 1);
    }

    // UI thread: fields of a page were read, or the history moved on.
    private void OnFormFieldsChanged(int page) => RefreshForms(focus: false);

    /// <summary>
    /// Gives the layer the fields of the visible pages and the focus ring, and opens, moves or closes the input of the field
    /// with the focus; <paramref name="focus"/> puts the keyboard focus into it (the focus moved to it), else it keeps the
    /// focus where it is.
    /// </summary>
    private void RefreshForms(bool focus)
    {
        if (_formOverlay is not { } overlay || _viewModel is not { IsFilling: true } tab || _layout is null || !ReferenceEquals(tab, _formTab))
        {
            return;
        }

        overlay.Show(VisibleFields(tab), tab.FocusedField, tab.ShowsFieldHighlight);
        SyncFormEditor(tab, focus);
    }

    /// <summary>The fields of the visible pages in the order of their pages; the same list as before while none changed.</summary>
    private IReadOnlyList<FormField> VisibleFields(TabViewModel tab)
    {
        var sources = VisibleFormPages().Select(tab.FieldsOfPage).ToList();
        if (sources.Count == _formFieldSources.Count && sources.Zip(_formFieldSources).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return _formFields;
        }

        _formFieldSources = sources;
        _formFields = [.. sources.SelectMany(fields => fields)];
        return _formFields;
    }

    /// <summary>The field with the focus has changed (a press, Tab, the toolbar, a refused value): into view, its input opens.</summary>
    private void OnFocusedFieldChanged()
    {
        if (_viewModel is not { IsFilling: true } tab || !ReferenceEquals(tab, _formTab))
        {
            return;
        }

        if (tab.FocusedField is { } key && tab.FieldOf(key) is { } field)
        {
            // Into view first (only when it is not fully visible), so the input opens where the field is now.
            OnRevealRequested(key.Page, field.Bounds);
        }

        RefreshForms(focus: true);
    }

    /// <summary>Opens, moves or closes the input for the field with the focus (see <see cref="RefreshForms"/>).</summary>
    private void SyncFormEditor(TabViewModel tab, bool focus)
    {
        var field = tab.FocusedField is { } key ? tab.FieldOf(key) : null;
        if (field is null || !HasInput(field))
        {
            HideFormEditor();
            if (field is { Kind: FormFieldKind.CheckBox or FormFieldKind.RadioButton } && _formOverlay?.ElementOf(field.Key) is { Focusable: true } element)
            {
                // A check box or radio button: its element of the layer takes the keys (Space switches it, the view gets the
                // rest), so screen readers follow the focus to it; its ring is on the layer.
                if ((focus || IsFocused) && !element.IsFocused)
                {
                    element.Focus(NavigationMethod.Unspecified);
                }
            }
            else if (_formOverlay is { IsKeyboardFocusWithin: true })
            {
                // The field let go of the focus: the keys go back to the view.
                Focus(NavigationMethod.Unspecified);
            }

            return;
        }

        var rect = _formOverlay?.ViewRectOf(field);
        var viewport = new Rect(Scroller.Viewport);
        if (rect is not { } shown || !shown.Intersects(viewport))
        {
            // Scrolled out of view: the value is applied (or stays with the tab with its reason), and the input closes until the
            // field is back.
            if (_formEditor is { } open && open.Shows(field.Key))
            {
                var hadFocus = open.IsKeyboardFocusWithin;
                HideFormEditor();
                tab.FlushFormDraft();
                if (hadFocus)
                {
                    Focus(NavigationMethod.Unspecified);
                }
            }

            return;
        }

        var editor = EnsureFormEditor();
        var text = DraftTextOf(tab, field);
        if (!editor.Shows(field.Key))
        {
            editor.Start(tab, field, text);
            editor.IsVisible = true;
            focus |= IsKeyboardFocusWithin;
        }
        else if (tab.CurrentDraft is not { } draft || draft.Key != field.Key)
        {
            // No open value of its own: it shows the value the field has now (after undo, redo or a write).
            editor.ShowValue(field, text);
        }

        ShowDraftReason(tab, editor);
        _formEditorHost!.Width = viewport.Width;
        _formEditorHost.Height = viewport.Height;
        editor.Place(shown, PageCanvas.PagePlacement(field.Key.Page)?.DipsPerPoint ?? DocumentLayout.DipsPerPoint);
        if (focus)
        {
            var selectAll = !_formPressedText;
            _formPressedText = false;

            // After the input of the press or key that opened it, and laid out.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(FormFieldEditor, editor) && editor.Shows(field.Key))
                    {
                        editor.FocusInput(selectAll);
                    }
                },
                DispatcherPriority.Background);
        }
    }

    /// <summary>The text an input shows: the open value of the field, else its value.</summary>
    private static string DraftTextOf(TabViewModel tab, FormField field) =>
        tab.CurrentDraft is { } draft && draft.Key == field.Key ? draft.Text : field.State.Text ?? string.Empty;

    /// <summary>The reason of the open value of the field shown (the input stays with it), or none.</summary>
    private static void ShowDraftReason(TabViewModel tab, FormFieldEditor editor)
    {
        if (editor.Field is { } field && tab.CurrentDraft is { } draft && draft.Key == field.Key)
        {
            editor.ShowProblem(draft.Reason);
        }
        else if (editor.HasProblem)
        {
            editor.ShowProblem(null);
        }
    }

    /// <summary>The open value of the tab changed: the input shows its text (a refused one that came back) and its reason.</summary>
    private void OnFormDraftChanged()
    {
        if (_viewModel is not { } tab || FormFieldEditor is not { Field: { } field } editor)
        {
            return;
        }

        if (tab.CurrentDraft is { } draft && draft.Key == field.Key && !string.Equals(draft.Text, editor.Text, StringComparison.Ordinal))
        {
            editor.ShowValue(field, draft.Text);
        }

        ShowDraftReason(tab, editor);
    }

    private FormOverlay EnsureFormOverlay()
    {
        if (_formOverlay is null)
        {
            // Right above the pages, below the scroll viewer and its bars; it takes no input.
            _formOverlay = new FormOverlay(PageCanvas) { Name = "FormOverlay" };
            _formOverlay.ToggleRequested += field =>
            {
                if (_viewModel is { IsFilling: true } tab)
                {
                    ToggleField(tab, field);
                }
            };
            _formOverlay.FocusedElementRemoved += () =>
            {
                if (_layout is not null)
                {
                    Focus(NavigationMethod.Unspecified);
                }
            };
            _formOverlay.ValueRequested += (field, value) =>
            {
                if (_viewModel is { IsFilling: true } tab && field.Kind == FormFieldKind.Text)
                {
                    _ = tab.CommitFieldAsync(field.Key.FieldName, FormFieldState.OfText(value));
                }
            };
            DocumentLayers.Children.Insert(DocumentLayers.Children.IndexOf(PageCanvas) + 1, _formOverlay);
        }

        return _formOverlay;
    }

    private FormFieldEditor EnsureFormEditor()
    {
        if (_formEditor is null)
        {
            _formEditor = new FormFieldEditor { Name = "FormFieldEditor", IsVisible = false, DocumentScroller = Scroller };
            _formEditor.TextEdited += OnFormTextEdited;
            _formEditor.ChoiceMade += OnFormChoiceMade;
            _formEditor.CommitRequested += OnFormCommitRequested;
            _formEditor.MoveRequested += OnFormMoveRequested;
            _formEditor.EscapeRequested += OnFormEscapeRequested;
            _formEditor.Left += OnFormEditorLeft;

            // Above the pages and the scroll viewer (the input takes the keys and the pointer), clipped to the viewport so it
            // never covers the scroll bars, the toolbar or the status bar. The host has no background: a press beside the input
            // reaches the scroll viewer.
            _formEditorHost = new Panel { Name = "FormEditorHost", ClipToBounds = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            _formEditorHost.Children.Add(_formEditor);
            DocumentLayers.Children.Add(_formEditorHost);
        }

        return _formEditor;
    }

    /// <summary>Hides the input (its field's open value stays with the tab); the keys go back to the view when it had them.</summary>
    private void HideFormEditor()
    {
        if (_formEditor is not { } editor || !editor.IsVisible)
        {
            return;
        }

        var hadFocus = editor.IsKeyboardFocusWithin;

        // First, so the focus it loses below reports nothing.
        editor.Stop();
        editor.IsVisible = false;
        if (hadFocus && _layout is not null)
        {
            Focus(NavigationMethod.Unspecified);
        }
    }

    private bool OnFormTextEdited(FormFieldEditor editor, string text)
    {
        if (editor.Tab is not { } tab || editor.Field is not { } field || !ReferenceEquals(tab, _viewModel))
        {
            return false;
        }

        if (tab.EditDraft(field.Key, text))
        {
            return true;
        }

        editor.ShowProblem(tab.CurrentDraft is { Reason: { } reason } draft && draft.Key != field.Key ? reason : Strings.FormValueNotNow);
        return false;
    }

    private void OnFormChoiceMade(FormFieldEditor editor, FormFieldState state)
    {
        if (editor.Tab is not { } tab || editor.Field is not { } field || !ReferenceEquals(tab, _viewModel))
        {
            return;
        }

        if (tab.CurrentDraft is { } draft && draft.Key == field.Key)
        {
            // An option of an editable combo box replaces the text typed into it.
            tab.DiscardDraft();
        }

        _ = CommitChoiceAsync(tab, editor, field.Key, state);
    }

    /// <summary>
    /// Applies a choice. One the tab refuses (it takes no edit now, PDFium did not take it; the tab tells why) never stays on
    /// screen as if it were applied: the input shows the value the field has in the document again.
    /// </summary>
    private async Task CommitChoiceAsync(TabViewModel tab, FormFieldEditor editor, FormWidgetKey key, FormFieldState state)
    {
        bool taken;
        try
        {
            taken = await tab.CommitFieldAsync(key.FieldName, state);
        }
        catch (Exception)
        {
            // Never fails; if it did, the field shows what the document has.
            taken = false;
        }

        if (taken)
        {
            return;
        }

        // After the selection change that asked for it has ended (an input must not change its selection inside its own event).
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(tab, _viewModel) && editor.Shows(key) && tab.FieldOf(key) is { } now)
                {
                    editor.ShowValue(now, DraftTextOf(tab, now));
                }
            },
            DispatcherPriority.Background);
    }

    private void OnFormCommitRequested(FormFieldEditor editor, bool moveOn)
    {
        if (editor.Tab is not { } tab || !ReferenceEquals(tab, _viewModel))
        {
            return;
        }

        if (moveOn)
        {
            // Applied first (MoveToFieldAsync); after the last field the input stays on it.
            MoveFormFocus(tab, forward: true);
        }
        else
        {
            tab.FlushFormDraft();
        }

        ShowDraftReason(tab, editor);
    }

    /// <summary>Tab or Shift+Tab in an input (see <see cref="MoveFormFocus"/>).</summary>
    /// <returns>Whether the key was taken.</returns>
    private bool OnFormMoveRequested(FormFieldEditor editor, bool forward) =>
        editor.Tab is { } tab && ReferenceEquals(tab, _viewModel) && MoveFormFocus(tab, forward);

    private void OnFormEscapeRequested(FormFieldEditor editor)
    {
        if (editor.Tab is not { } tab || editor.Field is not { } field || !ReferenceEquals(tab, _viewModel))
        {
            return;
        }

        // The open value goes first (the field shows its value again), then the field lets go of the focus.
        var hadDraft = tab.CurrentDraft is { } draft && draft.Key == field.Key;
        tab.EscapeFill();
        if (hadDraft && FormFieldEditor is { } open && open.Shows(field.Key) && tab.FieldOf(field.Key) is { } now)
        {
            open.ShowValue(now, now.State.Text ?? string.Empty);
            open.ShowProblem(null);
        }
    }

    private void OnFormEditorLeft(FormFieldEditor editor)
    {
        if (editor.Tab is not { } tab || editor.Field is not { } field || !ReferenceEquals(tab, _viewModel) || tab.FocusedField != field.Key)
        {
            return;
        }

        // Leaving the field applies its value; one it cannot take stays with the reason.
        tab.FlushFormDraft();
        ShowDraftReason(tab, editor);
    }

    /// <summary>
    /// Tab or Shift+Tab in the mode (from the view, an input or a check box): the next or previous field gets the focus. After
    /// the last field (before the first) the key is not taken and the focus moves on out of the view, also while the field keeps
    /// a value it cannot apply (WCAG 2.1.2); the field lets go of the focus then. Shift+Tab from an input or a check box would
    /// land on the view itself (it comes before them): the focus moves on from the view instead, so one key leaves the document
    /// in both directions.
    /// </summary>
    /// <returns>Whether the key was taken.</returns>
    private bool MoveFormFocus(TabViewModel tab, bool forward)
    {
        var move = tab.MoveToFieldAsync(forward);
        if (!move.IsCompletedSuccessfully)
        {
            // A page is read first: the key is taken; when no field follows, the focus moves on as the key would have moved it.
            _ = MoveFormFocusWhenReadAsync(tab, move, forward);
            return true;
        }

        if (move.Result)
        {
            return true;
        }

        var left = tab.FocusedField;
        if (!forward && IsKeyboardFocusWithin && !IsFocused)
        {
            Focus(NavigationMethod.Tab);
            TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(NavigationDirection.Previous);
            ClearLeftField(tab, left);
            return true;
        }

        ClearLeftField(tab, left);
        return false;
    }

    /// <summary>After the key that left the fields: the field lets go of the focus once the keys are outside the view.</summary>
    private void ClearLeftField(TabViewModel tab, FormWidgetKey? left)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(tab, _viewModel) && tab.FocusedField == left && left is not null && !IsKeyboardFocusWithin)
                {
                    tab.FocusedField = null;
                }
            },
            DispatcherPriority.Background);
    }

    private async Task MoveFormFocusWhenReadAsync(TabViewModel tab, Task<bool> move, bool forward)
    {
        bool moved;
        try
        {
            moved = await move;
        }
        catch (Exception)
        {
            // Never fails; nothing to do if it did.
            return;
        }

        if (!moved && ReferenceEquals(tab, _viewModel) && IsKeyboardFocusWithin)
        {
            tab.FocusedField = null;
            TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(forward ? NavigationDirection.Next : NavigationDirection.Previous);
        }
    }

    /// <summary>The keys of the mode while the view has the focus: Tab and Shift+Tab walk the fields, Space switches the check box or radio button with the focus, Enter opens the input of the field with the focus.</summary>
    /// <returns>Whether the key was taken.</returns>
    private bool TryFormKey(Key key, KeyModifiers modifiers)
    {
        if (_viewModel is not { IsFilling: true } tab || !ReferenceEquals(tab, _formTab))
        {
            return false;
        }

        if (key == Key.Tab && modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            return MoveFormFocus(tab, modifiers == KeyModifiers.None);
        }

        if (modifiers != KeyModifiers.None || tab.FocusedField is not { } focused || tab.FieldOf(focused) is not { } field || !TabViewModel.IsEditable(field))
        {
            return false;
        }

        if (key == Key.Space && field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
        {
            ToggleField(tab, field);
            return true;
        }

        if (key == Key.Enter && HasInput(field))
        {
            RefreshForms(focus: true);
            return true;
        }

        return false;
    }

    /// <summary>A click or Space on a check box or radio button: the box switches, the button is chosen (one step).</summary>
    private static void ToggleField(TabViewModel tab, FormField field)
    {
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
                _ = tab.CommitFieldAsync(field.Key.FieldName, FormFieldState.OfChecked(field.State.Checked != true));
                break;
            case FormFieldKind.RadioButton when field.ExportValue is { } export && !string.Equals(field.State.Text, export, StringComparison.Ordinal):
                // A chosen button stays chosen: a radio group is never set to no choice (the form engine cannot undo a choice).
                _ = tab.CommitFieldAsync(field.Key.FieldName, FormFieldState.OfText(export));
                break;
        }
    }

    /// <summary>The topmost field under a point of the viewport (the last in <c>/Annots</c>), within <see cref="FieldHitTolerance"/>.</summary>
    private FormField? FieldAt(TabViewModel tab, Point viewportPoint)
    {
        if (_layout is not { } layout || PageCanvas.PagePointAt(viewportPoint) is not { } hit)
        {
            return null;
        }

        var fields = tab.FieldsOfPage(hit.Page);
        var tolerance = FieldHitTolerance / (layout.Zoom * DocumentLayout.DipsPerPoint);
        for (var i = fields.Count - 1; i >= 0; i--)
        {
            if (fields[i].Bounds.Inflate(tolerance).Contains(hit.Position.X, hit.Position.Y))
            {
                return fields[i];
            }
        }

        return null;
    }

    /// <summary>
    /// A press in the mode: on a field it is the field's (no text selection, no link): it gets the focus, a check box or radio
    /// button switches on the release, a locked field only says why. Anywhere else the field with the focus is left (its value
    /// applied) and the press goes on as usual.
    /// </summary>
    /// <returns>Whether the field took the press.</returns>
    private bool PressForForm(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { IsFilling: true } tab || !ReferenceEquals(tab, _formTab) || _layout is null
            || properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || IsInScrollBar(e.Source))
        {
            return false;
        }

        var field = FieldAt(tab, e.GetPosition(PageCanvas));
        if (field is null)
        {
            // Elsewhere: the field lets go of the focus once its value is applied (one that cannot be applied keeps it).
            if (tab.FocusedField is not null && tab.FlushFormDraft())
            {
                tab.FocusedField = null;
            }

            return false;
        }

        if (!TabViewModel.IsEditable(field))
        {
            // Read-only, a signature, a button: nothing runs; the hint says why, neutrally.
            tab.ShowFormNotice(TabViewModel.LockTextOf(field)!);
            return true;
        }

        if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
        {
            if (tab.FocusedField == field.Key)
            {
                // The press gave the keys to the view: they go back to the field's element.
                RefreshForms(focus: true);
            }
            else
            {
                tab.FocusedField = field.Key;
            }

            _formPress = new FormPress(tab, e.Pointer, field.Key);
            e.Pointer.Capture(this);
            return true;
        }

        if (HasInput(field))
        {
            _formPressedText = true;
            if (tab.FocusedField == field.Key)
            {
                // The field has the focus already (its input was closed or is out of the keys): it opens again.
                RefreshForms(focus: true);
            }
            else
            {
                tab.FocusedField = field.Key;
            }

            _formPressedText = false;
        }

        return true;
    }

    /// <summary>A move during a press on a check box or radio button is the field's.</summary>
    private bool MoveForForm(PointerEventArgs e) => _formPress is not null;

    /// <summary>The release of a press on a check box or radio button: inside the same field it switches it.</summary>
    /// <returns>Whether the press was the field's.</returns>
    private bool ReleaseForForm(PointerReleasedEventArgs e)
    {
        if (_formPress is not { } press)
        {
            return false;
        }

        if (!ReferenceEquals(e.Pointer, press.Pointer))
        {
            return true;
        }

        _formPress = null;
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }

        if (ReferenceEquals(press.Tab, _viewModel) && press.Tab.IsFilling && FieldAt(press.Tab, e.GetPosition(PageCanvas)) is { } field && field.Key == press.Key)
        {
            ToggleField(press.Tab, field);
        }

        return true;
    }

    /// <summary>The cursor in the mode over a field: the I-beam over a text field, the hand over another one that can be changed.</summary>
    /// <returns>Whether the point lies on a field (the cursor is chosen).</returns>
    private bool UpdateFormCursor(Point position)
    {
        if (_viewModel is not { IsFilling: true } tab || !ReferenceEquals(tab, _formTab) || FieldAt(tab, position) is not { } field)
        {
            SetFormTip(null);
            return false;
        }

        SetFormTip(TabViewModel.LockTextOf(field));
        SetCursor(!TabViewModel.IsEditable(field) ? CursorKind.Arrow : field.Kind == FormFieldKind.Text ? CursorKind.Text : CursorKind.Link);
        return true;
    }

    /// <summary>The tooltip of the document: why the field under the pointer is locked, or none.</summary>
    private void SetFormTip(string? tip)
    {
        if (string.Equals(tip, _formTip, StringComparison.Ordinal))
        {
            return;
        }

        _formTip = tip;
        ToolTip.SetTip(Scroller, tip);
    }

    /// <summary>Whether an input event comes from the input of a form field (it handles its own keys and pointer).</summary>
    private bool IsInFormEditor(object? source)
    {
        if (_formEditor is null)
        {
            return false;
        }

        for (var visual = source as Visual; visual is not null and not DocumentView; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, _formEditor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A press of the pointer on a check box or radio button, until it is released.</summary>
    private sealed record FormPress(TabViewModel Tab, IPointer Pointer, FormWidgetKey Key);
}
