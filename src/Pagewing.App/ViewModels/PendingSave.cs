using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
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

    /// <summary>
    /// The tab with unsaved changes is about to close, but its changes cannot be saved any more (another program changed
    /// the file, or an encryption the written file would lose: <see cref="PendingSave.Reason"/>): closing discards them.
    /// "Close and discard changes" and "Cancel" (default); no "Save" that could not work.
    /// </summary>
    DiscardChanges,
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

    /// <summary>Close and discard the changes that cannot be saved (only <see cref="PendingSaveKind.DiscardChanges"/>).</summary>
    Discard,
}

/// <summary>
/// A question of the save flow of a window (<see cref="MainViewModel.PendingSave"/>) that waits for the user: its kind, the
/// file, the reason or the signature level, the choices with the default, and the texts of its card (design system 7.8:
/// a card in the document area, not a modal dialog; the views come with the cards). The flow awaits <see cref="Choice"/>;
/// the card answers with <see cref="Choose"/> (its buttons, which keep to the lock of the dangerous choices), the flow and
/// tests with <see cref="ResolveCommand"/> or <see cref="Resolve"/>. Each question is answered once; the window answers
/// <see cref="PendingSaveChoice.Cancel"/> for it when its tab closes or the window goes. UI thread only.
/// </summary>
/// <remarks>
/// A choice that loses or overwrites something (<see cref="IsDangerous"/>: "Don't save", "Save anyway", "Overwrite",
/// "Close and discard changes") is never the default and does nothing for <see cref="ArmingDelay"/> after the card was laid
/// out (<see cref="OnCardShown"/>), like "Open" of the link card: a second Enter or the second click of a double click
/// never lands on it.
/// </remarks>
public sealed class PendingSave
{
    private readonly TaskCompletionSource<PendingSaveChoice> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The text of a question whose text is not given by its kind and reason (SaveNotPossible), or null.
    private readonly Func<string>? _message;

    // When the card was last laid out (Clock timestamp); none: no card shows the question now.
    private long? _shownAt;

    // Ends the lock of the dangerous choices visibly (raises ArmedChanged); made when the card is shown, never before.
    private ITimer? _armingTimer;

    private PendingSave(
        PendingSaveKind kind,
        TabViewModel tab,
        string path,
        PendingSaveChoice[] choices,
        PendingSaveChoice defaultChoice,
        SaveFailureReason? reason = null,
        DocMdpLevel level = DocMdpLevel.NotSet,
        Func<string>? message = null)
    {
        _message = message;
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

    /// <summary>How long after its card was laid out a dangerous choice stays without effect (the lock of the link card, 400 ms).</summary>
    public static TimeSpan ArmingDelay => MainViewModel.LinkArmingDelay;

    /// <summary>Gets or sets the clock (and timer) of <see cref="ArmingDelay"/>: the window's (<see cref="MainViewModel.Clock"/>).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Raised on the UI thread when <see cref="IsArmed"/> may have changed (the card was shown, the lock ended, the answer came).</summary>
    public event Action? ArmedChanged;

    /// <summary>Gets the title of the card in the language of the interface.</summary>
    public string Title => Kind switch
    {
        PendingSaveKind.SaveChanges => Format(Strings.SaveChangesTitle, FileName),
        PendingSaveKind.DiscardChanges => Format(Strings.DiscardChangesTitle, FileName),
        PendingSaveKind.SignatureWarning => Strings.SignatureWarningTitle,
        PendingSaveKind.SignatureNote => Strings.SignatureNoteTitle,
        PendingSaveKind.FileChanged => Format(Strings.FileChangedTitle, FileName),
        _ => Format(Strings.SaveErrorTitle, FileName),
    };

    /// <summary>Gets the text of the card in the language of the interface (never "valid" for a signature).</summary>
    public string Message => _message?.Invoke() ?? Kind switch
    {
        PendingSaveKind.SaveChanges => Strings.SaveChangesText,
        PendingSaveKind.SignatureWarning => Strings.SignatureWarningText,
        PendingSaveKind.SignatureNote => Strings.SignatureNoteText,
        PendingSaveKind.FileChanged => Strings.FileChangedText,
        PendingSaveKind.DiscardChanges => Reason == SaveFailureReason.EncryptedNotSupported
            ? Strings.DiscardChangesEncrypted
            : Strings.DiscardChangesSourceModified,
        _ => MessageOf(Reason),
    };

    /// <summary>
    /// Gets whether the card warns that something is lost or changed for good (the signature, another file, changes that
    /// cannot be saved, a failure): it shows the warning symbol with its text, and screen readers announce the text.
    /// </summary>
    public bool IsWarning => Kind is PendingSaveKind.SignatureWarning or PendingSaveKind.FileChanged or PendingSaveKind.SaveError
        or PendingSaveKind.DiscardChanges;

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
        PendingSaveChoice.Discard => Strings.SaveChoiceDiscard,
        _ => Strings.SaveChoiceCancel,
    };

    /// <summary>Gets whether <paramref name="choice"/> loses or overwrites something: never the default, locked at first (<see cref="ArmingDelay"/>).</summary>
    public static bool IsDangerous(PendingSaveChoice choice) =>
        choice is PendingSaveChoice.DontSave or PendingSaveChoice.SaveAnyway or PendingSaveChoice.Overwrite or PendingSaveChoice.Discard;

    /// <summary>
    /// Gets whether the card's button of <paramref name="choice"/> acts now: the question offers it and is not answered yet,
    /// and for a dangerous choice <see cref="ArmingDelay"/> has passed since the card was laid out.
    /// </summary>
    public bool IsArmed(PendingSaveChoice choice) =>
        !IsResolved && Choices.Contains(choice)
        && (!IsDangerous(choice) || (_shownAt is { } shown && Clock.GetElapsedTime(shown) >= ArmingDelay));

    /// <summary>A button of the card: answers with <paramref name="choice"/> while it is armed (<see cref="IsArmed"/>).</summary>
    /// <returns><see langword="false"/> when the choice is locked, not offered, or the question was answered.</returns>
    public bool Choose(PendingSaveChoice choice) => IsArmed(choice) && Resolve(choice);

    /// <summary>
    /// The card of this question has been laid out (UI thread): the lock of the dangerous choices starts now (again when the
    /// card comes back after another tab was shown), and a one-shot timer ends it.
    /// </summary>
    internal void OnCardShown()
    {
        if (IsResolved)
        {
            return;
        }

        _shownAt = Clock.GetTimestamp();
        StartArmingTimer();
        ArmedChanged?.Invoke();
    }

    /// <summary>The card went (another tab is shown): the dangerous choices stay locked until it is laid out again.</summary>
    internal void OnCardHidden()
    {
        _shownAt = null;
        StopArmingTimer();
    }

    /// <summary>Answers the question with <paramref name="choice"/>.</summary>
    /// <returns><see langword="false"/> when the question was answered already or does not offer <paramref name="choice"/>.</returns>
    public bool Resolve(PendingSaveChoice choice)
    {
        if (!Choices.Contains(choice) || !_choice.TrySetResult(choice))
        {
            return false;
        }

        Answered();
        return true;
    }

    /// <summary>Answers <see cref="PendingSaveChoice.Cancel"/> (the tab or the window goes), whatever the card offers.</summary>
    internal void Abandon()
    {
        if (_choice.TrySetResult(PendingSaveChoice.Cancel))
        {
            Answered();
        }
    }

    /// <summary>The question before a tab with unsaved changes closes (<see cref="MainViewModel.ResolveUnsavedChangesAsync()"/>).</summary>
    internal static PendingSave SaveChanges(TabViewModel tab, string path) =>
        new(PendingSaveKind.SaveChanges, tab, path, [PendingSaveChoice.Save, PendingSaveChoice.DontSave, PendingSaveChoice.Cancel], PendingSaveChoice.Save);

    /// <summary>
    /// The question before a tab closes whose changes cannot be saved (<see cref="TabViewModel.UnsaveableReason"/>): closing
    /// discards them, so "Cancel" is the default and there is no "Save".
    /// </summary>
    internal static PendingSave DiscardChanges(TabViewModel tab, string path, SaveFailureReason reason) =>
        new(PendingSaveKind.DiscardChanges, tab, path, [PendingSaveChoice.Discard, PendingSaveChoice.Cancel], PendingSaveChoice.Cancel, reason);

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

    /// <summary>
    /// "Save" before closing cannot start now (the tab prints, or the document cannot be saved at the moment): the error card
    /// says so, with "Cancel" alone; the tab stays with its changes.
    /// </summary>
    internal static PendingSave SaveNotPossible(TabViewModel tab, string path, bool printing) =>
        new(PendingSaveKind.SaveError, tab, path, [PendingSaveChoice.Cancel], PendingSaveChoice.Cancel,
            message: printing ? static () => Strings.SaveNotPossiblePrinting : static () => Strings.SaveNotPossible);

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

    private void Answered()
    {
        StopArmingTimer();
        ArmedChanged?.Invoke();
    }

    private void StartArmingTimer()
    {
        StopArmingTimer();
        if (_shownAt is not { } shown || !Choices.Any(IsDangerous))
        {
            return;
        }

        var remaining = ArmingDelay - Clock.GetElapsedTime(shown);
        if (remaining > TimeSpan.Zero)
        {
            _armingTimer = Clock.CreateTimer(_ => PostArmed(), null, remaining, Timeout.InfiniteTimeSpan);
        }
    }

    // The timer's thread (any): must never throw.
    private void PostArmed()
    {
        try
        {
            Dispatcher.UIThread.Post(OnArmed);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nobody sees the buttons any more.
        }
    }

    private void OnArmed()
    {
        if (IsResolved || _shownAt is not { } shown)
        {
            return;
        }

        if (Clock.GetElapsedTime(shown) < ArmingDelay)
        {
            // The timer fired a little before the clock says the lock is over: wait for the rest.
            StartArmingTimer();
            return;
        }

        StopArmingTimer();
        ArmedChanged?.Invoke();
    }

    private void StopArmingTimer()
    {
        _armingTimer?.Dispose();
        _armingTimer = null;
    }

    private static string Format(string format, string name) => string.Format(Strings.Culture, CompositeFormat.Parse(format), name);
}
