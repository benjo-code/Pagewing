using Pagewing.Core.Forms;
using Pagewing.Core.Signatures;

namespace Pagewing.Core.Annotations;

/// <summary>
/// A step of the undo history. It is pure data: undoing a <see cref="Delete"/> recreates the annotation from the stored
/// copy, so a restored annotation may get a new index.
/// </summary>
public abstract record AnnotationCommand
{
    /// <summary>An annotation was created; undo deletes it, redo creates it again.</summary>
    /// <param name="Created">The annotation as it was after creating.</param>
    /// <param name="Image">The pixels of an image signature (packed), which redo needs; otherwise <see langword="null"/>.</param>
    public sealed record Create(PdfAnnotation Created, PackedSignatureImage? Image = null) : AnnotationCommand;

    /// <summary>An annotation was deleted; undo recreates it.</summary>
    /// <param name="Removed">The annotation as it was before deleting.</param>
    /// <param name="Image">The pixels of an image signature (packed, read before deleting), which undo needs; otherwise <see langword="null"/>.</param>
    public sealed record Delete(PdfAnnotation Removed, PackedSignatureImage? Image = null) : AnnotationCommand;

    /// <summary>An annotation was changed; undo restores the earlier state.</summary>
    /// <param name="Before">The annotation before the change.</param>
    /// <param name="After">The annotation after the change.</param>
    public sealed record Edit(PdfAnnotation Before, PdfAnnotation After) : AnnotationCommand;

    /// <summary>
    /// The value of a form field was changed; undo writes <paramref name="Before"/> again, redo <paramref name="After"/>. The
    /// field is found by its full name, never by an annotation index, which shifts when annotations come and go.
    /// </summary>
    /// <param name="FieldName">The fully qualified name of the field.</param>
    /// <param name="Before">The state before the change.</param>
    /// <param name="After">The state after the change, as read back.</param>
    /// <param name="Pages">Every page with a widget of the field.</param>
    public sealed record FormValue(string FieldName, FormFieldState Before, FormFieldState After, IReadOnlySet<int> Pages) : AnnotationCommand;
}

/// <summary>The packed images of the signatures in the history would be more than <see cref="SignatureLimits.MaxHistoryImageBytes"/>: save first.</summary>
public sealed class AnnotationHistoryFullException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public AnnotationHistoryFullException()
        : base("The history holds the images of too many signatures; save first.")
    {
    }
}

/// <summary>
/// The undo and redo stacks of one document. The editor pushes a command after each change; <see cref="TakeUndo"/>
/// returns the command to reverse, <see cref="TakeRedo"/> the command to apply again. Not thread-safe; used on the UI
/// thread.
/// </summary>
public sealed class AnnotationHistory
{
    /// <summary>The most commands kept; on overflow the oldest one is dropped.</summary>
    public const int MaxCommands = 500;

    private readonly List<AnnotationCommand> _undo = [];
    private readonly List<AnnotationCommand> _redo = [];

    // What was pushed since the last Clear: form values, and any other step (also one undone, dropped or forgotten since).
    private bool _formSteps;
    private bool _otherSteps;

    /// <summary>
    /// Gets the bytes of the packed images in both stacks (the memory the history holds for them). Empty after <see cref="Clear"/>,
    /// so after a save.
    /// </summary>
    public long ImageBytes => ImageBytesOf(_undo) + ImageBytesOf(_redo);

    /// <summary>
    /// Whether a step that adds <paramref name="bytes"/> of packed image fits into <see cref="SignatureLimits.MaxHistoryImageBytes"/>:
    /// measured against the steps that can still be undone, because a new step forgets the redo stack and its images.
    /// </summary>
    /// <param name="bytes">The packed size of the image the step would keep.</param>
    public bool Fits(long bytes) => Fits(bytes, SignatureLimits.MaxHistoryImageBytes);

    /// <summary>Like <see cref="Fits(long)"/> against another budget (tests make it small).</summary>
    internal bool Fits(long bytes, long budget) => ImageBytesOf(_undo) + bytes <= budget;

    /// <summary>Whether <see cref="TakeUndo"/> returns a command.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether <see cref="TakeRedo"/> returns a command.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Changes with every push, undo and redo that did something, and with <see cref="Clear"/>; it never repeats
    /// (never goes back), so "changed since the last save" is <c>Version != VersionAtLastSave</c>. An undo followed by
    /// a redo therefore counts as a change, which errs on the side of offering to save.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Gets whether every step pushed since the last <see cref="Clear"/> changed a form value
    /// (<see cref="AnnotationCommand.FormValue"/>) and there was at least one. An annotation step counts for good until the next
    /// clear, also when it was undone, dropped or forgotten since: the document was touched by it. <see cref="MarkChanged"/> is
    /// no step of its own (it follows a failed form write).
    /// </summary>
    public bool HasOnlyFormChanges => _formSteps && !_otherSteps;

    private static long ImageBytesOf(List<AnnotationCommand> commands)
    {
        long bytes = 0;
        foreach (var command in commands)
        {
            bytes += command switch
            {
                AnnotationCommand.Create create => create.Image?.Deflated.Length ?? 0,
                AnnotationCommand.Delete delete => delete.Image?.Deflated.Length ?? 0,
                _ => 0,
            };
        }

        return bytes;
    }

    /// <summary>Records a new change and forgets everything that could be redone.</summary>
    /// <param name="command">The change that was just made.</param>
    public void Push(AnnotationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is AnnotationCommand.FormValue)
        {
            _formSteps = true;
        }
        else
        {
            _otherSteps = true;
        }

        _redo.Clear();
        if (_undo.Count >= MaxCommands)
        {
            _undo.RemoveAt(0);
        }

        _undo.Add(command);
        Version++;
    }

    /// <summary>Takes the newest change off the undo stack and keeps it for redo.</summary>
    /// <returns>The command to reverse, or <see langword="null"/> if there is nothing to undo.</returns>
    public AnnotationCommand? TakeUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(command);
        Version++;
        return command;
    }

    /// <summary>Takes the newest undone change off the redo stack and puts it back on the undo stack.</summary>
    /// <returns>The command to apply again, or <see langword="null"/> if there is nothing to redo.</returns>
    public AnnotationCommand? TakeRedo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(command);
        Version++;
        return command;
    }

    /// <summary>
    /// Forgets <paramref name="command"/> if it is the newest command of either stack, for example a step that
    /// <see cref="TakeUndo"/> or <see cref="TakeRedo"/> just moved and that cannot be applied: the other commands stay
    /// usable. Changes <see cref="Version"/> when it forgets something.
    /// </summary>
    /// <param name="command">The command to forget.</param>
    /// <returns>Whether it was the newest command of a stack and is forgotten.</returns>
    public bool Discard(AnnotationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var stack in (ReadOnlySpan<List<AnnotationCommand>>)[_redo, _undo])
        {
            if (stack.Count > 0 && ReferenceEquals(stack[^1], command))
            {
                stack.RemoveAt(stack.Count - 1);
                Version++;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Moves <see cref="Version"/> without a step: something may have changed in the document that the history cannot undo (a
    /// form write that failed and could not be taken back), so the document counts as changed since the last save.
    /// </summary>
    public void MarkChanged() => Version++;

    /// <summary>Empties both stacks, for example after the document was reloaded; <see cref="Version"/> keeps increasing.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _formSteps = false;
        _otherSteps = false;
        Version++;
    }
}
