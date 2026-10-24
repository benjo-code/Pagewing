using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>What a <see cref="PendingSave"/> asks the user.</summary>
public enum PendingSaveKind
{
    /// <summary>The tab has unsaved changes and is about to close: "Save", "Don't save", "Cancel" (default "Save").</summary>
    SaveChanges,

    /// <summary>
    /// The signature of the document does not allow comments (DocMDP level 1 or 2), so saving would make programs such as
    /// Acrobat show it as invalid: "Cancel" (default), "Save as", "Save anyway". Asked before every save of such a file.
    /// </summary>
    SignatureWarning,

    /// <summary>
    /// The document is signed and allows comments (DocMDP level 3 or none): Pagewing only appends, the signed bytes stay.
    /// "Save" (default) and "Cancel"; asked once per tab and document.
    /// </summary>
    SignatureNote,

    /// <summary>
    /// Another file stands at the path of the original since it was opened (<see cref="SaveFailureReason.OriginalChanged"/>):
    /// "Overwrite", "Save as" (default), "Cancel".
    /// </summary>
    FileChanged,

    /// <summary>
    /// The save failed (<see cref="PendingSave.Reason"/>): "Save as" (default) and "Cancel", or "Cancel" alone where another
    /// name cannot help (<see cref="PendingSave.SaveError"/>). The tab keeps its document and its changes.
    /// </summary>
    SaveError,
}

/// <summary>An answer to a <see cref="PendingSave"/>.</summary>
public enum PendingSaveChoice
{
    /// <summary>Nothing is saved; the tab keeps its changes.</summary>
    Cancel,

    /// <summary>Save (the changes before closing, or go on after the note about the signature).</summary>
    Save,

    /// <summary>Close without saving (only <see cref="PendingSaveKind.SaveChanges"/>).</summary>
    DontSave,

    /// <summary>Save under another name (the file dialog).</summary>
    SaveAs,

    /// <summary>Save although the signature does not allow it (only <see cref="PendingSaveKind.SignatureWarning"/>).</summary>
    SaveAnyway,

    /// <summary>Replace the other file at the path of the original (only <see cref="PendingSaveKind.FileChanged"/>).</summary>
    Overwrite,
}

/// <summary>
/// A question of the save flow of a window (<see cref="MainViewModel.PendingSave"/>) that waits for the user: its kind, the
/// file, the reason or the signature level, the choices with the default, and the texts of its card (design system 7.8:
/// a card in the document area, not a modal dialog; the views come with the cards). The flow awaits <see cref="Choice"/>;
/// the card answers with <see cref="ResolveCommand"/> or <see cref="Resolve"/>. Each question is answered once; the window
/// answers <see cref="PendingSaveChoice.Cancel"/> for it when its tab closes or the window goes. UI thread only.
/// </summary>
public sealed class PendingSave
{
    private readonly TaskCompletionSource<PendingSaveChoice> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PendingSave(
        PendingSaveKind kind,
        TabViewModel tab,
        string path,
        PendingSaveChoice[] choices,
        PendingSaveChoice defaultChoice,
        SaveFailureReason? reason = null,
        DocMdpLevel level = DocMdpLevel.NotSet)
    {
        Kind = kind;
        Tab = tab;
        Path = path;
        Choices = choices;
        DefaultChoice = defaultChoice;
        Reason = reason;
        Level = level;
        ResolveCommand = new RelayCommand<PendingSaveChoice>(choice => Resolve(choice));
    }

    /// <summary>Gets what the question is about.</summary>
    public PendingSaveKind Kind { get; }

    /// <summary>Gets the tab whose document is to be saved.</summary>
    public TabViewModel Tab { get; }

    /// <summary>Gets the full path the save writes to (the file of the tab, or the target of "Save as").</summary>
    public string Path { get; }

    /// <summary>Gets the file name of <see cref="Path"/>, for the texts.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Gets why the save failed (<see cref="PendingSaveKind.SaveError"/> and <see cref="PendingSaveKind.FileChanged"/>), or <see langword="null"/>.</summary>
    public SaveFailureReason? Reason { get; }

    /// <summary>Gets the DocMDP level of the signature (<see cref="PendingSaveKind.SignatureWarning"/> and <see cref="PendingSaveKind.SignatureNote"/>).</summary>
    public DocMdpLevel Level { get; }

    /// <summary>Gets the answers the card offers, in the order of its buttons.</summary>
    public IReadOnlyList<PendingSaveChoice> Choices { get; }

    /// <summary>Gets the answer that has the focus and Enter (one of <see cref="Choices"/>).</summary>
    public PendingSaveChoice DefaultChoice { get; }

    /// <summary>Gets the title of the card in the language of the interface.</summary>
    public string Title => Kind switch
    {
        PendingSaveKind.SaveChanges => Format(Strings.SaveChangesTitle, FileName),
        PendingSaveKind.SignatureWarning => Strings.SignatureWarningTitle,
        PendingSaveKind.SignatureNote => Strings.SignatureNoteTitle,
        PendingSaveKind.FileChanged => Format(Strings.FileChangedTitle, FileName),
        _ => Format(Strings.SaveErrorTitle, FileName),
    };

    /// <summary>Gets the text of the card in the language of the interface (never "valid" for a signature).</summary>
    public string Message => Kind switch
    {
        PendingSaveKind.SaveChanges => Strings.SaveChangesText,
        PendingSaveKind.SignatureWarning => Strings.SignatureWarningText,
        PendingSaveKind.SignatureNote => Strings.SignatureNoteText,
        PendingSaveKind.FileChanged => Strings.FileChangedText,
        _ => MessageOf(Reason),
    };

    /// <summary>Gets whether the question was answered.</summary>
    public bool IsResolved => _choice.Task.IsCompleted;

    /// <summary>Gets the answer, once it is given.</summary>
    public Task<PendingSaveChoice> Choice => _choice.Task;

    /// <summary>Answers the question (parameter: a <see cref="PendingSaveChoice"/> of <see cref="Choices"/>), for the buttons of the card.</summary>
    public ICommand ResolveCommand { get; }

    /// <summary>Gets the label of the button of an answer in the language of the interface.</summary>
    public static string LabelOf(PendingSaveChoice choice) => choice switch
    {
        PendingSaveChoice.Save => Strings.SaveChoiceSave,
        PendingSaveChoice.DontSave => Strings.SaveChoiceDontSave,
        PendingSaveChoice.SaveAs => Strings.SaveChoiceSaveAs,
        PendingSaveChoice.SaveAnyway => Strings.SaveChoiceSaveAnyway,
        PendingSaveChoice.Overwrite => Strings.SaveChoiceOverwrite,
        _ => Strings.SaveChoiceCancel,
    };

    /// <summary>Answers the question with <paramref name="choice"/>.</summary>
    /// <returns><see langword="false"/> when the question was answered already or does not offer <paramref name="choice"/>.</returns>
    public bool Resolve(PendingSaveChoice choice) => Choices.Contains(choice) && _choice.TrySetResult(choice);

    /// <summary>Answers <see cref="PendingSaveChoice.Cancel"/> (the tab or the window goes), whatever the card offers.</summary>
    internal void Abandon() => _choice.TrySetResult(PendingSaveChoice.Cancel);

    /// <summary>The question before a tab with unsaved changes closes (the cards and the closing come with a later task).</summary>
    internal static PendingSave SaveChanges(TabViewModel tab, string path) =>
        new(PendingSaveKind.SaveChanges, tab, path, [PendingSaveChoice.Save, PendingSaveChoice.DontSave, PendingSaveChoice.Cancel], PendingSaveChoice.Save);

    internal static PendingSave SignatureWarning(TabViewModel tab, string path, DocMdpLevel level) =>
        new(PendingSaveKind.SignatureWarning, tab, path, [PendingSaveChoice.Cancel, PendingSaveChoice.SaveAs, PendingSaveChoice.SaveAnyway], PendingSaveChoice.Cancel, level: level);

    internal static PendingSave SignatureNote(TabViewModel tab, string path, DocMdpLevel level) =>
        new(PendingSaveKind.SignatureNote, tab, path, [PendingSaveChoice.Save, PendingSaveChoice.Cancel], PendingSaveChoice.Save, level: level);

    internal static PendingSave FileChanged(TabViewModel tab, string path) =>
        new(PendingSaveKind.FileChanged, tab, path, [PendingSaveChoice.Overwrite, PendingSaveChoice.SaveAs, PendingSaveChoice.Cancel], PendingSaveChoice.SaveAs, SaveFailureReason.OriginalChanged);

    /// <summary>
    /// A failed save. "Save as" is offered unless another target cannot help: a file rewritten in place by another program
    /// (<see cref="SaveFailureReason.SourceModified"/>; every save reads the opened file, whatever the target) and an
    /// encryption the written file would lose. The tab keeps its document and its changes in every case.
    /// </summary>
    internal static PendingSave SaveError(TabViewModel tab, string path, SaveFailureReason reason) =>
        reason is SaveFailureReason.SourceModified or SaveFailureReason.EncryptedNotSupported
            ? new(PendingSaveKind.SaveError, tab, path, [PendingSaveChoice.Cancel], PendingSaveChoice.Cancel, reason)
            : new(PendingSaveKind.SaveError, tab, path, [PendingSaveChoice.SaveAs, PendingSaveChoice.Cancel], PendingSaveChoice.SaveAs, reason);

    /// <summary>The cause on the error card in everyday words; <see cref="SaveFailureReason.SourceModified"/> says that the changes stay.</summary>
    private static string MessageOf(SaveFailureReason? reason) => reason switch
    {
        SaveFailureReason.NotWritable => Strings.SaveErrorNotWritable,
        SaveFailureReason.DiskFull => Strings.SaveErrorDiskFull,
        SaveFailureReason.WriteFailed => Strings.SaveErrorWriteFailed,
        SaveFailureReason.VerificationFailed => Strings.SaveErrorVerificationFailed,
        SaveFailureReason.SourceModified => Strings.SaveErrorSourceModified,
        SaveFailureReason.EncryptedNotSupported => Strings.SaveErrorEncrypted,
        _ => Strings.SaveErrorUnknown,
    };

    private static string Format(string format, string name) => string.Format(Strings.Culture, CompositeFormat.Parse(format), name);
}
