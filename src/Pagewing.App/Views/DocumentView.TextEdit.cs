using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.TextEdit;

namespace Pagewing.App.Views;

/// <summary>
/// The mode "Edit text" on the pages (milestone 16): the layer outlines the line under the pointer or with the keyboard focus (a locked line gets a lock, its
/// reason is the tooltip), a press on a line opens the field over it (<see cref="TextRunEditor"/>), Enter applies the text, Esc drops it. The arrows move the
/// keyboard focus from line to line and Enter opens it; Tab is never taken. The field follows its line when the view scrolls or zooms. A press elsewhere
/// applies the open text first (one that cannot be applied stays in the field with the reason).
/// </summary>
public sealed partial class DocumentView
{
    // A line is hit within this many DIPs of its box.
    private const double RunHitTolerance = 3;

    private TextEditOverlay? _textEditOverlay;
    private TextRunEditor? _textRunEditor;
    private TabViewModel? _textEditTab;
    private string? _textEditTip;

    /// <summary>Gets the layer of the mode, or <see langword="null"/> before the mode first started (for tests).</summary>
    internal TextEditOverlay? TextEditOverlay => _textEditOverlay;

    /// <summary>Gets the open field over a line, or <see langword="null"/> (for tests).</summary>
    internal TextRunEditor? TextRunEditor => _textRunEditor is { IsVisible: true, Run: not null } editor ? editor : null;

    /// <summary>The tab the view follows in the mode, or stops following: called on a change of the mode and when a document is shown.</summary>
    private void SyncTextEditMode()
    {
        if (_viewModel is { IsTextEditing: true } tab && _layout is not null)
        {
            if (!ReferenceEquals(_textEditTab, tab))
            {
                DisconnectTextEdit();
                _textEditTab = tab;
                tab.TextRunsChanged += OnTextRunsChanged;
                tab.TextEditMarksChanged += OnTextEditMarksChanged;
                tab.TextEditFieldChanged += OnTextEditMarksChanged;
                tab.TextEditApplied += OnTextEditApplied;
                tab.VisibleTextEditPages = VisibleFormPages;
            }

            EnsureTextEditOverlay().IsVisible = true;
            tab.EnsureTextRuns(VisibleFormPages());
            RefreshTextEdit();
            return;
        }

        DisconnectTextEdit();
    }

    private void DisconnectTextEdit()
    {
        HideTextRunEditor();
        SetTextEditTip(null);
        if (_textEditTab is { } tab)
        {
            tab.TextRunsChanged -= OnTextRunsChanged;
            tab.TextEditMarksChanged -= OnTextEditMarksChanged;
            tab.TextEditFieldChanged -= OnTextEditMarksChanged;
            tab.TextEditApplied -= OnTextEditApplied;
            tab.VisibleTextEditPages = null;
            _textEditTab = null;
        }

        if (_textEditOverlay is { } overlay)
        {
            overlay.Show(null, null);
            overlay.IsVisible = false;
        }
    }

    private TextEditOverlay EnsureTextEditOverlay()
    {
        if (_textEditOverlay is null)
        {
            // Right above the pages, below the scroll viewer and its bars; it takes no input.
            _textEditOverlay = new TextEditOverlay(PageCanvas) { Name = "TextEditOverlay" };
            DocumentLayers.Children.Insert(DocumentLayers.Children.IndexOf(PageCanvas) + 1, _textEditOverlay);
        }

        return _textEditOverlay;
    }

    private void OnTextRunsChanged(int page) => RefreshTextEdit();

    private void OnTextEditMarksChanged() => RefreshTextEdit();

    // A line was edited: its page is looked at again (the runs are read again by the tab).
    private void OnTextEditApplied(int page)
    {
        if (_textEditTab is { } tab)
        {
            tab.EnsureTextRuns(VisibleFormPages());
        }

        RefreshTextEdit();
        Focus(NavigationMethod.Unspecified);
    }

    /// <summary>Gives the layer the lines to outline, and opens, moves or closes the field.</summary>
    private void RefreshTextEdit()
    {
        if (_textEditOverlay is not { } overlay || _viewModel is not { IsTextEditing: true } tab || _layout is null || !ReferenceEquals(tab, _textEditTab))
        {
            return;
        }

        overlay.Show(tab.HoverTextRun, tab.FocusedTextRun);
        SyncTextRunEditor(tab);
    }

    private void SyncTextRunEditor(TabViewModel tab)
    {
        if (tab.OpenTextRun is not { } run)
        {
            HideTextRunEditor();
            return;
        }

        var editor = EnsureTextRunEditor();
        var opened = !ReferenceEquals(editor.Run, run) || !editor.IsVisible;
        if (opened)
        {
            editor.Start(tab, run);
            editor.IsVisible = true;
        }

        PlaceTextRunEditor();
        editor.ShowProblem(tab.TextEditProblem);
        if (opened)
        {
            // After the input of the press or key that opened it (the view takes the focus at a release), and laid out.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(TextRunEditor, editor))
                    {
                        editor.FocusField();
                    }
                },
                DispatcherPriority.Background);
        }
    }

    private TextRunEditor EnsureTextRunEditor()
    {
        if (_textRunEditor is null)
        {
            _textRunEditor = new TextRunEditor { Name = "TextRunEditor", IsVisible = false };
            _textRunEditor.CommitRequested += OnTextRunCommitRequested;
            _textRunEditor.DiscardRequested += OnTextRunDiscardRequested;
            _textRunEditor.TextEdited += editor => editor.Tab?.SetTextEditDraft(editor.Text);

            // Above the pages and the scroll viewer: the field takes the keys and the pointer.
            DocumentLayers.Children.Add(_textRunEditor);
        }

        return _textRunEditor;
    }

    private void HideTextRunEditor()
    {
        if (_textRunEditor is { } editor)
        {
            editor.IsVisible = false;
        }
    }

    /// <summary>The field follows its line when the view scrolls or zooms.</summary>
    private void PlaceTextRunEditor()
    {
        if (TextRunEditor is not { Run: { } run } editor || _textEditOverlay?.ViewRectOf(run) is not { } rect)
        {
            return;
        }

        var scale = PageCanvas.PagePlacement(run.Page) is { } placement ? placement.DipsPerPoint : 1;
        editor.Place(rect, scale);
    }

    private void OnTextRunCommitRequested(TextRunEditor editor, bool focusLeft)
    {
        if (!ReferenceEquals(editor, TextRunEditor) || editor.Tab is not { } tab)
        {
            return;
        }

        // The tab keeps what is typed; a text that cannot be applied stays in the field with the reason.
        tab.SetTextEditDraft(editor.Text);
        _ = tab.CommitTextEditAsync();
        if (!focusLeft)
        {
            Focus();
        }
    }

    private void OnTextRunDiscardRequested(TextRunEditor editor)
    {
        if (!ReferenceEquals(editor, TextRunEditor) || editor.Tab is not { } tab)
        {
            return;
        }

        tab.DiscardTextEditField();
        Focus();
    }

    /// <summary>The line under a point of the viewport (the last in the order of the page), within <see cref="RunHitTolerance"/> DIPs.</summary>
    private TextRun? RunAt(TabViewModel tab, Point viewportPoint)
    {
        if (_layout is not { } layout || PageCanvas.PagePointAt(viewportPoint) is not { } hit)
        {
            return null;
        }

        var runs = tab.TextRunsOf(hit.Page);
        var tolerance = RunHitTolerance / (layout.Zoom * Core.Viewing.DocumentLayout.DipsPerPoint);
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].Bounds.Inflate(tolerance).Contains(hit.Position.X, hit.Position.Y))
            {
                return runs[i];
            }
        }

        return null;
    }

    /// <summary>The pointer moved in the mode: the line under it is outlined, a locked one says why in the tooltip.</summary>
    private void MoveForTextEdit(PointerEventArgs e)
    {
        if (_viewModel is not { IsTextEditing: true } tab || !ReferenceEquals(tab, _textEditTab) || _layout is null || IsInScrollBar(e.Source))
        {
            return;
        }

        var run = RunAt(tab, e.GetPosition(PageCanvas));
        tab.HoverTextRun = run;
        SetTextEditTip(run is { Lock: { } reason } ? TabViewModel.LockTextOf(reason) : null);
    }

    private void SetTextEditTip(string? tip)
    {
        if (string.Equals(tip, _textEditTip, StringComparison.Ordinal))
        {
            return;
        }

        _textEditTip = tip;
        ToolTip.SetTip(Scroller, tip);
    }

    /// <summary>
    /// A press in the mode: on a line it opens the field (a locked line only says why); anywhere else the open text is applied first and the press goes on as
    /// usual.
    /// </summary>
    /// <returns>Whether the line took the press.</returns>
    private bool PressForTextEdit(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { IsTextEditing: true } tab || !ReferenceEquals(tab, _textEditTab) || _layout is null
            || properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || IsInScrollBar(e.Source) || IsInTextRunEditor(e.Source))
        {
            return false;
        }

        var run = RunAt(tab, e.GetPosition(PageCanvas));
        if (run is null)
        {
            tab.FlushTextEditDraft();
            return false;
        }

        if (run.IsLocked)
        {
            tab.FocusedTextRun = run;
            tab.ShowTextEditNotice(TabViewModel.LockTextOf(run.Lock!.Value));
            return true;
        }

        tab.OpenTextRunField(run);
        return true;
    }

    /// <summary>The keys of the mode when no field is open: the arrows move from line to line, Enter opens the line with the focus. Tab is never taken.</summary>
    private bool KeyForTextEdit(KeyEventArgs e)
    {
        if (_viewModel is not { IsTextEditing: true } tab || !ReferenceEquals(tab, _textEditTab) || tab.OpenTextRun is not null
            || IsInTextRunEditor(e.Source) || e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.Space when tab.FocusedTextRun is { } focused:
                if (focused.IsLocked)
                {
                    tab.ShowTextEditNotice(TabViewModel.LockTextOf(focused.Lock!.Value));
                }
                else
                {
                    tab.OpenTextRunField(focused);
                }

                return true;
            case Key.Down or Key.Right:
                return MoveTextEditFocus(tab, forward: true);
            case Key.Up or Key.Left:
                return MoveTextEditFocus(tab, forward: false);
            default:
                return false;
        }
    }

    private bool MoveTextEditFocus(TabViewModel tab, bool forward)
    {
        var runs = VisibleFormPages()
            .SelectMany(tab.TextRunsOf)
            .OrderBy(static r => r.Page)
            .ThenBy(static r => Math.Round(r.Bounds.Top / 3))
            .ThenBy(static r => r.Bounds.Left)
            .ToList();
        if (runs.Count == 0)
        {
            return false;
        }

        var current = tab.FocusedTextRun is { } focused ? runs.IndexOf(focused) : -1;
        var next = current < 0 ? (forward ? 0 : runs.Count - 1) : Math.Clamp(current + (forward ? 1 : -1), 0, runs.Count - 1);
        tab.FocusedTextRun = runs[next];
        OnRevealRequested(runs[next].Page, runs[next].Bounds);
        return true;
    }

    private bool IsInTextRunEditor(object? source)
    {
        if (_textRunEditor is null)
        {
            return false;
        }

        for (var visual = source as Visual; visual is not null and not DocumentView; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, _textRunEditor))
            {
                return true;
            }
        }

        return false;
    }
}
