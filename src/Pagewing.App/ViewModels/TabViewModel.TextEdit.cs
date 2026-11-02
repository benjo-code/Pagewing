using Pagewing.App.Resources;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Editing a line of text (milestone 16): what the save flow needs of it. The mode, the runs and the field belong to the later parts of this class; nothing
/// here is made before the first text edit.
/// </summary>
public sealed partial class TabViewModel
{
    private bool _textEditNoticeShown;
    private string _textEditSaveNote = string.Empty;

    /// <summary>Gets whether a text edit was made since the last save (<see cref="Core.Annotations.AnnotationEditor.HasTextEdits"/>), also one undone since.</summary>
    internal bool HasTextEdits => _editor?.HasTextEdits == true;

    /// <summary>Gets or sets whether the note about the old text was shown for the open document (once per tab and document).</summary>
    internal bool TextEditNoticeShown
    {
        get => _textEditNoticeShown;
        set => _textEditNoticeShown = value;
    }

    /// <summary>Gets the note of the status bar after the first save of edited text ("The old text stays in earlier versions of the file …"), empty otherwise.</summary>
    public string TextEditSaveNote
    {
        get => _textEditSaveNote;
        private set
        {
            if (_textEditSaveNote != value)
            {
                _textEditSaveNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTextEditSaveNote));
            }
        }
    }

    /// <summary>Gets whether the status bar shows <see cref="TextEditSaveNote"/>.</summary>
    public bool HasTextEditSaveNote => _textEditSaveNote.Length > 0;

    /// <summary>Edited text was saved for the first time: the status bar says what stays in the file, once per tab and document.</summary>
    internal void NoteTextEditSaved()
    {
        _textEditNoticeShown = true;
        TextEditSaveNote = Strings.TextEditSaveNote;
    }

    /// <summary>Another file is open in the tab: the note may come again.</summary>
    private void ResetTextEdit()
    {
        _textEditNoticeShown = false;
        TextEditSaveNote = string.Empty;
    }
}
