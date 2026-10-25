using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The mode "Comment" of the tab (milestone 9, design system 7.7): whether it is on, the tool (highlight, underline,
/// strike-out or none, which only selects), its colour, and making a markup from a text selection. The mode belongs to the
/// tab: each tab has its own, a tab switch keeps it, another document in the tab ends it. Nothing of it is made or read
/// before it is first asked for: the permissions of the document are read when the mode is first requested for it (never
/// at open), and the commands do nothing before the first sharp page (<see cref="HasShownSharpPage"/>).
/// </summary>
public sealed partial class TabViewModel
{
    private bool _isAnnotating;
    private AnnotationKind? _activeTool;

    // The kind whose colour the colour buttons show without a tool: the last tool chosen (the highlight at first).
    private AnnotationKind _lastTool = AnnotationKind.Highlight;

    // The drawing tool whose style the style controls show without a tool: the last drawing tool chosen (the pen at first).
    private AnnotationKind _lastDrawTool = AnnotationKind.Ink;

    // Whether the open document allows annotations (its permissions), read on the first request of the mode for it, and
    // the document it was read for; a read that runs.
    private (PdfDocument Document, bool Allowed)? _permission;
    private Task<bool>? _permissionRead;

    // The markups whose geometry is still being read (from the release of the mouse until the editor took them): closing
    // asks about them as about changes (AsksBeforeClosing).
    private int _pendingEdits;
    private Task _editsSettled = Task.CompletedTask;
    private UserSettings? _annotationSettings;

    // Counts the documents of the tab (not the saved file that replaces one): a markup asked for on one is not made on the next.
    private int _documentGeneration;

    private RelayCommand? _beginAnnotateCommand;
    private RelayCommand? _endAnnotateCommand;
    private RelayCommand? _toggleAnnotateCommand;
    private RelayCommand<AnnotationKind>? _setToolCommand;
    private RelayCommand<MarkupColor>? _setColorCommand;
    private RelayCommand? _highlightCommand;
    private RelayCommand? _underlineCommand;
    private RelayCommand? _strikeOutCommand;

    /// <summary>
    /// Gets whether the mode "Comment" is on: the window shows its toolbar over the document, the "Comments" panel its edit
    /// controls, and with a tool the release of the mouse after a text selection makes a markup.
    /// </summary>
    public bool IsAnnotating
    {
        get => _isAnnotating;
        private set
        {
            if (SetProperty(ref _isAnnotating, value))
            {
                RaiseAnnotateCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets the tool of the mode: a text markup (<see cref="AnnotationKind.Highlight"/>, <see cref="AnnotationKind.Underline"/>,
    /// <see cref="AnnotationKind.StrikeOut"/>), a drawing tool (<see cref="AnnotationKind.Ink"/>, <see cref="AnnotationKind.Rectangle"/>,
    /// <see cref="AnnotationKind.Ellipse"/>, <see cref="AnnotationKind.Line"/>, <see cref="AnnotationKind.Arrow"/>; see
    /// <c>TabViewModel.Drawing.cs</c>), or <see langword="null"/> (a selection only selects, Ctrl+C copies it; the select
    /// tool, <see cref="IsSelectTool"/>, is none of these). A tool stays on until another is chosen or the mode ends.
    /// </summary>
    public AnnotationKind? ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                if (value is { } tool)
                {
                    RememberTool(tool);
                    IsSelectTool = false;
                }

                OnPropertyChanged(nameof(ActiveColor));
                OnActiveToolChangedForDrawing();
            }
        }
    }

    /// <summary>
    /// Gets the colour of the text markup tool (without one, of the last text markup tool chosen): the colour remembered for
    /// that kind in the settings (<see cref="AppSettings.HighlightColor"/>, <see cref="AppSettings.UnderlineColor"/>,
    /// <see cref="AppSettings.StrikeOutColor"/>; yellow, blue and pink at first). A drawing tool has its style instead
    /// (<see cref="ActiveStyle"/>).
    /// </summary>
    public MarkupColor ActiveColor => ColorOf(_activeTool is { } tool && IsMarkupKind(tool) ? tool : _lastTool);

    /// <summary>
    /// Gets why the mode cannot start now, for the "Comments" panel, or <see langword="null"/>: the document forbids
    /// annotations (known only after the first request), or it is not shown yet.
    /// </summary>
    public string? AnnotateUnavailableReason =>
        _permission is { Allowed: false } known && ReferenceEquals(known.Document, _document) ? Strings.AnnotateForbidden
        : !CanRequestAnnotate() ? Strings.AnnotateNotReady
        : null;

    /// <summary>Gets whether the "Comments" panel offers its button "Comment" (<see cref="AnnotateUnavailableReason"/> is <see langword="null"/>).</summary>
    public bool CanAnnotate => AnnotateUnavailableReason is null;

    /// <summary>The button "Comment" of the "Comments" panel: starts the mode without a tool (reads the permissions first, once per document).</summary>
    public ICommand BeginAnnotateCommand => _beginAnnotateCommand ??= new RelayCommand(() => _ = RequestAnnotateAsync(null, apply: false), CanBeginAnnotate);

    /// <summary>"Done" in the toolbar (and Esc without a text selection): ends the mode; the changes stay, unsaved.</summary>
    public ICommand EndAnnotateCommand => _endAnnotateCommand ??= new RelayCommand(EndAnnotate, () => _isAnnotating);

    /// <summary>The command "Comment" (catalog, Ctrl+Shift+K, the button of the panel): starts the mode or ends it.</summary>
    public ICommand ToggleAnnotateCommand => _toggleAnnotateCommand ??= new RelayCommand(
        () =>
        {
            if (_isAnnotating)
            {
                EndAnnotate();
            }
            else
            {
                _ = RequestAnnotateAsync(null, apply: false);
            }
        },
        () => _isAnnotating || CanBeginAnnotate());

    /// <summary>
    /// Chooses a tool of the mode (parameter: the <see cref="AnnotationKind"/>); choosing the tool that is on again turns
    /// it off (the selection only selects). Only in the mode, and only the kinds that have a tool: the three text markups
    /// and the drawing tools (<see cref="IsToolKind"/>). It takes the place of the select tool.
    /// </summary>
    public ICommand SetToolCommand => _setToolCommand ??= new RelayCommand<AnnotationKind>(
        kind => ActiveTool = _activeTool == kind ? null : kind,
        kind => _isAnnotating && IsToolKind(kind));

    /// <summary>
    /// Chooses the colour (parameter: the <see cref="MarkupColor"/>) of the tool, or without one of the last tool, which it
    /// turns on (choosing a colour means marking with it). The settings remember it for that kind.
    /// </summary>
    public ICommand SetColorCommand => _setColorCommand ??= new RelayCommand<MarkupColor>(color =>
    {
        if (!_isAnnotating || !Enum.IsDefined(color))
        {
            return;
        }

        var kind = _activeTool is { } tool && IsMarkupKind(tool) ? tool : _lastTool;
        AnnotationSettings.Update(current => kind switch
        {
            AnnotationKind.Underline => current with { UnderlineColor = color },
            AnnotationKind.StrikeOut => current with { StrikeOutColor = color },
            _ => current with { HighlightColor = color },
        });
        ActiveTool = kind;
        OnPropertyChanged(nameof(ActiveColor));
    });

    /// <summary>Ctrl+Shift+H: starts the mode with the highlight; a text selection becomes a highlight at once.</summary>
    public ICommand HighlightCommand => _highlightCommand ??= MarkCommand(AnnotationKind.Highlight);

    /// <summary>Ctrl+Shift+U: as <see cref="HighlightCommand"/>, with the underline.</summary>
    public ICommand UnderlineCommand => _underlineCommand ??= MarkCommand(AnnotationKind.Underline);

    /// <summary>Ctrl+Shift+X: as <see cref="HighlightCommand"/>, with the strike-out.</summary>
    public ICommand StrikeOutCommand => _strikeOutCommand ??= MarkCommand(AnnotationKind.StrikeOut);

    /// <summary>
    /// Gets or sets the settings that remember the colours and give the author (the window's, set when the tab joins it);
    /// in-memory settings without a window (tests).
    /// </summary>
    internal UserSettings AnnotationSettings
    {
        get => _annotationSettings ??= UserSettings.Detached();
        set => _annotationSettings = value;
    }

    /// <summary>Gets or sets how the permissions of a document are read: <see cref="PdfDocument.MayAnnotateAsync"/> (replaceable for tests).</summary>
    internal Func<PdfDocument, CancellationToken, Task<bool>> PermissionReader { get; set; } =
        static (document, cancellationToken) => document.MayAnnotateAsync(cancellationToken);

    /// <summary>
    /// Gets whether a change is in flight: a markup or an edit of the panel on its way to the editor, or an operation the
    /// editor took and has not ended (also a save). <see cref="WhenEditsSettledAsync"/> waits for them.
    /// </summary>
    internal bool HasEditsInFlight => !_disposed && (_pendingEdits > 0 || _editor?.IsBusy == true);

    /// <summary>
    /// Esc of the mode (DocumentView, the toolbar, the "Comments" list): drops a stroke of a drawing tool that is being
    /// dragged (<see cref="CancelDrawingGesture"/>), else a drag of the selected annotation (<see cref="CancelSelectionDrag"/>),
    /// else clears the selected annotation (<see cref="SelectedAnnotation"/>), else the text selection, else ends the mode.
    /// </summary>
    /// <returns>Whether Esc had something to end.</returns>
    internal bool EscapeAnnotate()
    {
        if (!_isAnnotating)
        {
            return false;
        }

        if (CancelDrawingGesture?.Invoke() == true)
        {
            return true;
        }

        if (CancelSelectionDrag?.Invoke() == true)
        {
            return true;
        }

        if (_selectedAnnotation is not null)
        {
            ClearSelection();
            return true;
        }

        if (HasTextSelection)
        {
            TextSelection = null;
        }
        else
        {
            EndAnnotate();
        }

        return true;
    }

    /// <summary>
    /// Makes a markup of the kind of the tool over <paramref name="selection"/>: one annotation for each page of it, from
    /// the rectangles of the selected characters of that page (one per line, blanks at its ends left out). A selection
    /// without such rectangles makes nothing and says nothing. Runs in the background through the editor; the UI never
    /// waits for it. From the call on the tab counts as having changes (<see cref="AsksBeforeClosing"/>).
    /// </summary>
    /// <param name="selection">The selected text.</param>
    /// <param name="kind">Highlight, underline or strike-out.</param>
    /// <returns>Completes when every markup was made (or failed, which changes nothing).</returns>
    internal Task ApplySelectionAsync(TextSelection selection, AnnotationKind kind)
    {
        if (_disposed || !IsMarkupKind(kind) || selection.IsEmpty || !CanRunMarkup() || PageTexts is null)
        {
            return Task.CompletedTask;
        }

        var color = AnnotationPalette.ColorOf(ColorOf(kind));
        var author = AnnotationSettings.Current.AuthorName;
        return Track(ApplyCoreAsync(_documentGeneration, selection, kind, color, author));
    }

    /// <summary>
    /// Runs a change of the "Comments" panel through the editor (the comment, the colour or deleting an entry): the tab
    /// counts as changed from the call on, a failure (the annotation went, PDFium refused) changes nothing and is
    /// swallowed; the list shows what the document has.
    /// </summary>
    /// <param name="edit">The operation of the editor.</param>
    /// <returns>Completes when the operation has ended; it never fails.</returns>
    internal Task RunEditAsync(Func<AnnotationEditor, Task> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!CanRunEdit)
        {
            return Task.CompletedTask;
        }

        return Track(RunEditCoreAsync(edit));
    }

    /// <summary>
    /// Gets whether <see cref="RunEditAsync"/> takes an edit of the "Comments" panel now: the mode is on, no card of the save
    /// flow asks about the tab (<see cref="IsAskedAbout"/>) and editing is not locked for an update. Otherwise it refuses
    /// silently, so a caller that must not lose what the user typed asks first.
    /// </summary>
    internal bool CanRunEdit => !_disposed && _isAnnotating && CanRunMarkup();

    /// <summary>
    /// Waits until the changes the tab started (markups whose geometry is read, edits of the panel) and every operation of
    /// the editor have ended, then mirrors the editor: the question before closing then sees what was really changed. It
    /// waits for the tasks of those changes, never on a counter alone: each tracked task ends only after its change left
    /// the count (<see cref="Track"/>), and every round yields once, so a continuation that is still posted runs first.
    /// </summary>
    internal async Task WhenEditsSettledAsync()
    {
        // Nudges that wait for the keys to rest are made now: they are changes like the others.
        FlushNudge();
        while (!_disposed && (_pendingEdits > 0 || _editor?.IsBusy == true))
        {
            try
            {
                await _editsSettled;
            }
            catch (Exception)
            {
                // The tracked tasks never fail; only their end matters here.
            }

            if (_editor is { } editor)
            {
                await editor.WhenIdleAsync();
            }

            // A safety net: never a round without giving the dispatcher a turn.
            await Task.Yield();
        }

        SyncFromEditor();
    }

    /// <summary>
    /// Takes a change of the tab into account: <paramref name="run"/> counts the change up before its first await and down
    /// in its own <c>finally</c>, so when it ends the count is down already; <see cref="WhenEditsSettledAsync"/> waits for it.
    /// </summary>
    private Task Track(Task run)
    {
        _editsSettled = _editsSettled.IsCompleted ? run : Task.WhenAll(_editsSettled, run);
        SyncFromEditor();
        return run;
    }

    /// <summary>An edit of the panel, counted from the call to its end; a failure (also a synchronous throw) changes nothing.</summary>
    private async Task RunEditCoreAsync(Func<AnnotationEditor, Task> edit)
    {
        try
        {
            _pendingEdits++;
            await edit(Editor);
        }
        catch (Exception)
        {
            // The annotation went meanwhile, or PDFium refused: the editor recorded nothing.
        }
        finally
        {
            _pendingEdits--;
            SyncFromEditor();
        }
    }

    /// <summary>The settings changed (any window): the colour of the tool may be another one now.</summary>
    internal void OnAnnotationSettingsChanged()
    {
        if (_isAnnotating)
        {
            OnPropertyChanged(nameof(ActiveColor));
            OnPropertyChanged(nameof(ActiveStyle));
        }
    }

    /// <summary>The rectangles a markup gets on a page: those of the selected characters, without blanks at the ends.</summary>
    internal static IReadOnlyList<PageRect> MarkupLinesOf(PageText text, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = text.Chars;
        start = Math.Clamp(start, 0, chars.Count);
        end = Math.Clamp(end, start, chars.Count);
        while (start < end && IsBlank(chars[start]))
        {
            start++;
        }

        while (end > start && IsBlank(chars[end - 1]))
        {
            end--;
        }

        return start < end ? text.RectsOf(start, end) : [];
    }

    private static bool IsBlank(TextChar c) => !Rune.IsValid(c.CodePoint) || Rune.IsWhiteSpace(new Rune(c.CodePoint));

    internal static bool IsMarkupKind(AnnotationKind kind) =>
        kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut;

    /// <summary>Whether the kind has a tool in the mode: a text markup or a drawing tool.</summary>
    private static bool IsToolKind(AnnotationKind kind) => IsMarkupKind(kind) || IsDrawKind(kind);

    private void RememberTool(AnnotationKind tool)
    {
        if (IsMarkupKind(tool))
        {
            _lastTool = tool;
        }
        else if (IsDrawKind(tool))
        {
            _lastDrawTool = tool;
        }
    }

    private MarkupColor ColorOf(AnnotationKind kind)
    {
        var settings = AnnotationSettings.Current;
        return kind switch
        {
            AnnotationKind.Underline => settings.UnderlineColor,
            AnnotationKind.StrikeOut => settings.StrikeOutColor,
            _ => settings.HighlightColor,
        };
    }

    private RelayCommand MarkCommand(AnnotationKind kind) => new(
        () =>
        {
            if (_isAnnotating && CanRunMarkup())
            {
                ActiveTool = kind;
                if (TextSelection is { IsEmpty: false } selection)
                {
                    TextSelection = null;
                    _ = ApplySelectionAsync(selection, kind);
                }

                return;
            }

            _ = RequestAnnotateAsync(kind, apply: true);
        },
        () => (_isAnnotating && CanRunMarkup()) || CanBeginAnnotate());

    /// <summary>Whether the mode may be asked for now: an open document whose first sharp page was shown.</summary>
    private bool CanRequestAnnotate() => !_disposed && IsReady && _document is not null && _hasShownSharpPage;

    /// <summary>Whether the mode can start now: it may be asked for, and the document is not known to forbid it.</summary>
    private bool CanBeginAnnotate() =>
        !_isAnnotating && CanRequestAnnotate() && !(_permission is { Allowed: false } known && ReferenceEquals(known.Document, _document));

    /// <summary>
    /// Whether a markup can be made now: the mode is on with an open document shown, no card asks about the tab, and editing
    /// is not locked for an update (<see cref="IsEditingFrozen"/>).
    /// </summary>
    private bool CanRunMarkup() => CanRequestAnnotate() && !_isAskedAbout && !_isEditingFrozen;

    /// <summary>
    /// Starts the mode, with <paramref name="tool"/> if given (or, with <paramref name="selectTool"/>, with the select tool),
    /// once the permissions of the document allow it (read now on the first request for this document); with
    /// <paramref name="apply"/> a text selection becomes a markup of that tool at once. A document that forbids annotations
    /// gets no mode: the "Comments" panel opens and says why.
    /// </summary>
    private async Task RequestAnnotateAsync(AnnotationKind? tool, bool apply, bool selectTool = false)
    {
        if (!CanBeginAnnotate() || _document is not { } document)
        {
            return;
        }

        var selection = apply ? TextSelection : null;
        bool allowed;
        if (_permission is { } known && ReferenceEquals(known.Document, document))
        {
            allowed = known.Allowed;
        }
        else
        {
            try
            {
                _permissionRead ??= PermissionReader(document, CancellationToken.None);
                allowed = await _permissionRead;
            }
            catch (Exception)
            {
                // The document went meanwhile, or PDFium could not tell: a later request reads again.
                _permissionRead = null;
                return;
            }

            _permissionRead = null;
            if (_disposed || !ReferenceEquals(document, _document))
            {
                return;
            }

            _permission = (document, allowed);
            OnAnnotateAvailabilityChanged();
        }

        if (!allowed)
        {
            // The reason stands in the "Comments" panel; a key or the palette shows it there.
            SidePanel = SidePanelKind.Comments;
            return;
        }

        if (_isAnnotating || !CanRequestAnnotate())
        {
            return;
        }

        IsAnnotating = true;
        if (selectTool)
        {
            IsSelectTool = true;
        }
        else if (tool is { } kind)
        {
            ActiveTool = kind;
            if (selection is { IsEmpty: false } selected && IsMarkupKind(kind) && TextSelection == selected && CanRunMarkup())
            {
                TextSelection = null;
                _ = ApplySelectionAsync(selected, kind);
            }
        }
    }

    private void EndAnnotate()
    {
        if (!_isAnnotating)
        {
            return;
        }

        ActiveTool = null;
        IsSelectTool = false;
        IsAnnotating = false;
    }

    private async Task ApplyCoreAsync(int generation, TextSelection selection, AnnotationKind kind, AnnotationColor color, string? author)
    {
        try
        {
            // Counted before the first await, down again in the finally: the task ends after the count is down (Track).
            _pendingEdits++;
            for (var page = selection.Start.Page; page <= selection.End.Page; page++)
            {
                if (await ReadTextAsync(generation, page) is not { } text)
                {
                    // Another file is open in the tab now, or a page is damaged: what was made stays; nothing more is made.
                    return;
                }

                var count = text.Chars.Count;
                var start = page == selection.Start.Page ? Math.Min(selection.Start.Caret, count) : 0;
                var end = page == selection.End.Page ? Math.Min(selection.End.Caret, count) : count;
                var lines = MarkupLinesOf(text, start, end);
                if (lines.Count == 0)
                {
                    continue;
                }

                PdfAnnotation? created;
                try
                {
                    created = await Editor.ApplyMarkupAsync(page, kind, lines, color, null, author);
                }
                catch (Exception)
                {
                    // The document went, or PDFium refused the markup: nothing changed (the editor recorded nothing). The
                    // task of the markup never fails, so nobody has to observe it.
                    return;
                }

                if (created is not null)
                {
                    _comments?.SelectWhenListed(created);
                }
            }
        }
        finally
        {
            _pendingEdits--;
            SyncFromEditor();
        }
    }

    /// <summary>
    /// The text of a page for a markup, from the text of the document the tab shows now, or <see langword="null"/> when
    /// another file is open in the tab since the markup was asked for (<paramref name="generation"/>) or the page cannot be
    /// read. A save that swaps the document for the written file meanwhile (the same pages) is no reason to stop: the text
    /// is read again from the new one.
    /// </summary>
    private async Task<PageText?> ReadTextAsync(int generation, int page)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_disposed || generation != _documentGeneration || PageTexts is not { } texts)
            {
                return null;
            }

            try
            {
                var text = await texts.GetAsync(page, PdfPriority.Interactive);
                return _disposed || generation != _documentGeneration ? null : text;
            }
            catch (ObjectDisposedException)
            {
                // The saved file replaced the document while its text was read: once more from the new one.
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Another document is open in the tab (not the saved file in place of the old one): the mode ends with the old one.</summary>
    private void ResetAnnotateMode()
    {
        _documentGeneration++;
        EndAnnotate();
        _permission = null;
        _permissionRead = null;
        OnAnnotateAvailabilityChanged();
    }

    /// <summary>The saved file replaced the document: the same content, the same permissions.</summary>
    private void RebindAnnotateMode(PdfDocument old, PdfDocument adopted)
    {
        if (_permission is { } known && ReferenceEquals(known.Document, old))
        {
            _permission = (adopted, known.Allowed);
        }
    }

    private void OnAnnotateAvailabilityChanged()
    {
        OnPropertyChanged(nameof(AnnotateUnavailableReason));
        OnPropertyChanged(nameof(CanAnnotate));
        RaiseAnnotateCanExecuteChanged();
    }

    private void RaiseAnnotateCanExecuteChanged()
    {
        _beginAnnotateCommand?.RaiseCanExecuteChanged();
        _endAnnotateCommand?.RaiseCanExecuteChanged();
        _toggleAnnotateCommand?.RaiseCanExecuteChanged();
        _highlightCommand?.RaiseCanExecuteChanged();
        _underlineCommand?.RaiseCanExecuteChanged();
        _strikeOutCommand?.RaiseCanExecuteChanged();
        RaiseDrawCanExecuteChanged();
    }
}
