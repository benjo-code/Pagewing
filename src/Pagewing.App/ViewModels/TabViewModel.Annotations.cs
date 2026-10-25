using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The changes of the tab's document (annotations): the editor with its history, whether something is unsaved (the dot in
/// the tab and "Unsaved" in the status bar, design system 7.7 and 7.10), undo and redo, and the save commands, which the
/// window carries out (<see cref="MainViewModel.SaveActiveTabAsync"/>: it owns the file dialog and the questions).
/// </summary>
public sealed partial class TabViewModel
{
    // Made on first use (an edit, undo, redo or a save), never in the startup path or in a measurement run.
    private AnnotationEditor? _editor;
    private bool _isDirty;
    private bool _canUndo;
    private bool _canRedo;
    private bool _isSaving;
    private bool _isPrinting;
    private bool _isAskedAbout;
    private bool _isEditingFrozen;
    private double _saveProgress;

    // The signatures of the document, read at its first save (never at open), and whether the note about a signature that
    // allows comments was shown for this document already.
    private (PdfDocument Document, PdfSignatureInfo Info)? _signature;
    private bool _signatureNoteShown;

    private RelayCommand? _saveCommand;
    private RelayCommand? _saveAsCommand;
    private RelayCommand? _undoCommand;
    private RelayCommand? _redoCommand;

    /// <summary>
    /// Raised when the tab asks its window to save it (<see langword="false"/>) or to save it under another name
    /// (<see langword="true"/>): <see cref="SaveCommand"/>, <see cref="SaveAsCommand"/>.
    /// </summary>
    internal event Action<bool>? SaveRequested;

    /// <summary>
    /// Gets the editor of the document's annotations, made on the first access (an edit, undo, redo or a save; nothing
    /// in the startup path or a measurement run gets here, see <see cref="HasEditor"/>). It belongs to the tab and follows
    /// its session, also into another window.
    /// </summary>
    public AnnotationEditor Editor
    {
        get
        {
            if (_editor is null)
            {
                _editor = new AnnotationEditor(_session);
                _editor.Changed += OnEditorChanged;
            }

            return _editor;
        }
    }

    /// <summary>Gets whether <see cref="Editor"/> was made (for tests: nothing makes it before it is needed).</summary>
    internal bool HasEditor => _editor is not null;

    /// <summary>
    /// Gets whether the document has changes that are not saved (<see cref="AnnotationEditor.IsDirty"/>, as last seen on
    /// the UI thread): the tab shows a dot before its close button and the status bar "Unsaved".
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(TabAutomationName));
                RaiseSaveCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether <see cref="UndoCommand"/> has something to undo.</summary>
    public bool CanUndo
    {
        get => _canUndo;
        private set
        {
            if (SetProperty(ref _canUndo, value))
            {
                _undoCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether <see cref="RedoCommand"/> has something to redo.</summary>
    public bool CanRedo
    {
        get => _canRedo;
        private set
        {
            if (SetProperty(ref _canRedo, value))
            {
                _redoCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether a save of this tab runs (set by the window); the save commands, undo and redo wait for it.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        internal set
        {
            if (SetProperty(ref _isSaving, value))
            {
                RaiseSaveCanExecuteChanged();
                _undoCommand?.RaiseCanExecuteChanged();
                _redoCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets how far the running save has written its file, 0 to 1 (set by the window from the progress of the save).
    /// </summary>
    public double SaveProgress
    {
        get => _saveProgress;
        internal set => SetProperty(ref _saveProgress, value);
    }

    /// <summary>
    /// Gets whether a print job of this tab runs (set by the window). The job prints the document the tab has now, which a
    /// save would replace, so the tab cannot be saved meanwhile (<see cref="SaveBlockedReason"/>).
    /// </summary>
    public bool IsPrinting
    {
        get => _isPrinting;
        internal set
        {
            if (SetProperty(ref _isPrinting, value))
            {
                OnPropertyChanged(nameof(SaveBlockedReason));
                RaiseSaveCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets why the tab cannot be saved now although it could otherwise (a print job runs), or <see langword="null"/>.</summary>
    public string? SaveBlockedReason => _isPrinting ? Strings.SaveBlockedPrinting : null;

    /// <summary>
    /// Gets the name of the tab for screen readers: the file name, with "unsaved changes" after it while
    /// <see cref="IsDirty"/> (the dot is not the only sign of the state, design system 7.10).
    /// </summary>
    public string? TabAutomationName => FileName is { } name && _isDirty
        ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.TabUnsavedName), name)
        : FileName;

    /// <summary>Ctrl+S: saves the changes to the file of the tab; only while there are some (<see cref="IsDirty"/>).</summary>
    public ICommand SaveCommand => _saveCommand ??= new RelayCommand(() => SaveRequested?.Invoke(false), CanSave);

    /// <summary>Ctrl+Shift+S: saves the document under another name, once it is open; the tab then belongs to that file.</summary>
    public ICommand SaveAsCommand => _saveAsCommand ??= new RelayCommand(() => SaveRequested?.Invoke(true), CanSaveAs);

    /// <summary>Ctrl+Z: undoes the newest change of the annotations.</summary>
    public ICommand UndoCommand => _undoCommand ??= new RelayCommand(UndoAfterNudge, () => IsReady && (_canUndo || HasDrawingInFlight) && !_isSaving && !_isAskedAbout && !_isEditingFrozen);

    /// <summary>Ctrl+Shift+Z or Ctrl+Y: applies the newest undone change again.</summary>
    public ICommand RedoCommand => _redoCommand ??= new RelayCommand(RedoAfterNudge, () => IsReady && (_canRedo || HasDrawingInFlight) && !_isSaving && !_isAskedAbout && !_isEditingFrozen);

    /// <summary>
    /// Gets why the changes of the tab cannot be saved now, or <see langword="null"/>: set by the window when a save failed with
    /// <see cref="SaveFailureReason.SourceModified"/> or <see cref="SaveFailureReason.EncryptedNotSupported"/>
    /// (every further save would fail the same way), cleared by a save that succeeds and when another file opens in the
    /// tab. Closing the tab then warns that its changes are lost instead of offering "Save" (<see cref="PendingSaveKind.DiscardChanges"/>).
    /// </summary>
    public SaveFailureReason? UnsaveableReason { get; internal set; }

    /// <summary>
    /// Gets whether closing the tab asks first: it has unsaved changes (as last mirrored, or as the editor has them now, so
    /// a change whose mirror is still on its way counts too), or a change is in flight (a markup whose geometry is read, an
    /// operation the editor accepted and has not recorded yet). The question waits for those first
    /// (<see cref="WhenEditsSettledAsync"/>), so a change made a moment before closing is never dropped silently.
    /// </summary>
    internal bool AsksBeforeClosing => !_disposed && (_isDirty || _pendingEdits > 0 || _nudge is not null || _editor is { IsDirty: true } or { IsBusy: true });

    /// <summary>
    /// Gets the version of the editor's history (<see cref="AnnotationEditor.Version"/>; 0 without an editor): a "Don't
    /// save" before closing counts only for this version, any later change asks again.
    /// </summary>
    internal int EditVersion => _editor?.Version ?? 0;

    /// <summary>
    /// Gets or sets whether a card of the save flow asks about this tab (set by the window with
    /// <see cref="MainViewModel.PendingSave"/>): meanwhile undo, redo, save, "Save as" and printing do nothing, so the
    /// changes the card is about stay as they are.
    /// </summary>
    internal bool IsAskedAbout
    {
        get => _isAskedAbout;
        set
        {
            if (_isAskedAbout != value)
            {
                _isAskedAbout = value;
                RaiseSaveCanExecuteChanged();
                RaiseAnnotateCanExecuteChanged();
                _undoCommand?.RaiseCanExecuteChanged();
                _redoCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets whether editing is locked because the installer of an update has it or is about to get it (set by the window
    /// for all its tabs, <see cref="Services.Workspace"/>): from the user's last answer about unsaved changes until the
    /// installation ends without a restart. Meanwhile no markup, no edit of the "Comments" panel, no undo, redo, save or
    /// "Save as" runs (the app ends without asking again), and the status bar says so.
    /// </summary>
    public bool IsEditingFrozen
    {
        get => _isEditingFrozen;
        internal set
        {
            if (_isEditingFrozen != value)
            {
                _isEditingFrozen = value;
                RaiseSaveCanExecuteChanged();
                RaiseAnnotateCanExecuteChanged();
                _undoCommand?.RaiseCanExecuteChanged();
                _redoCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Gets whether the note about a signature that allows comments was shown for the open document (window, once per document).</summary>
    internal bool SignatureNoteShown
    {
        get => _signatureNoteShown;
        set => _signatureNoteShown = value;
    }

    /// <summary>Whether "Save" can run: an open document with unsaved changes, no save and no print job of the tab running.</summary>
    internal bool CanSave() => CanSaveAs() && _isDirty;

    /// <summary>Whether "Save as" can run: an open document, no save and no print job of the tab running.</summary>
    internal bool CanSaveAs() => !_disposed && IsReady && _document is not null && !_isSaving && !_isPrinting && !_isAskedAbout && !_isEditingFrozen;

    /// <summary>
    /// Reads the signatures of the open document once per document (at its first save, never at open) with
    /// <paramref name="reader"/>; a saved document that the session adopted keeps what was read (the signatures did not
    /// change: the save only appends).
    /// </summary>
    internal async Task<PdfSignatureInfo> GetSignatureInfoAsync(
        PdfDocument document,
        Func<PdfDocument, CancellationToken, Task<PdfSignatureInfo>> reader,
        CancellationToken cancellationToken)
    {
        if (_signature is { } known && ReferenceEquals(known.Document, document))
        {
            return known.Info;
        }

        var info = await reader(document, cancellationToken);
        if (ReferenceEquals(document, _document))
        {
            _signature = (document, info);
        }

        return info;
    }

    /// <summary>
    /// Reads the state of the editor (UI thread): dirty (also while a markup of the tab is on its way to it), undo and redo.
    /// </summary>
    internal void SyncFromEditor()
    {
        if (_disposed)
        {
            return;
        }

        var editor = _editor;
        IsDirty = editor?.IsDirty == true || _pendingEdits > 0 || _nudge is not null;
        CanUndo = editor?.CanUndo == true;
        CanRedo = editor?.CanRedo == true;
    }

    /// <summary>The session swapped its document for the saved file (<see cref="Refresh"/>): what was read of it stays.</summary>
    private void RebindAnnotationState(PdfDocument old, PdfDocument adopted)
    {
        if (_signature is { } known && ReferenceEquals(known.Document, old))
        {
            _signature = (adopted, known.Info);
        }

        RebindAnnotateMode(old, adopted);
    }

    /// <summary>Another file is open in the tab: its signatures are read again at its first save, and the note may come again.</summary>
    private void ResetAnnotationState()
    {
        _signature = null;
        _signatureNoteShown = false;
        UnsaveableReason = null;
        ResetAnnotateMode();
        SyncFromEditor();
    }

    private void RaiseSaveCanExecuteChanged()
    {
        _saveCommand?.RaiseCanExecuteChanged();
        _saveAsCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>A waiting nudge is made first, so undo undoes it (the editor runs the operations in this order).</summary>
    private void UndoAfterNudge()
    {
        FlushNudge();
        _ = StepAsync(undo: true);
    }

    private void RedoAfterNudge()
    {
        FlushNudge();
        _ = StepAsync(undo: false);
    }

    /// <summary>
    /// Undo or redo in the background; a step that fails is dropped by the editor (or put back after a cancel) and changes
    /// nothing here. Every failure is observed here, also an unexpected one: the command starts this without awaiting it.
    /// </summary>
    private async Task StepAsync(bool undo)
    {
        // The drawings released before the key are made first (read now, before the first await): Ctrl+Z right after a
        // release undoes that stroke, not the change before it. The tail never fails.
        var drawings = DrawingTail;
        try
        {
            await drawings;
            await (undo ? Editor.UndoAsync() : Editor.RedoAsync());
        }
        catch (Exception)
        {
            // The document went, PDFium refused the step, or something nobody expected: the editor forgot the step (or
            // kept it after a cancel), the history shows what is left.
        }

        SyncFromEditor();
    }

    // Any thread (the editor's): must not wait and must never throw.
    private void OnEditorChanged()
    {
        try
        {
            Dispatcher.UIThread.Post(SyncFromEditor);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to show.
        }
    }

    /// <summary>Stops listening to the editor (the tab is disposed).</summary>
    private void DisposeAnnotations()
    {
        if (_editor is not null)
        {
            _editor.Changed -= OnEditorChanged;
        }

        DisposeSelection();
    }
}
