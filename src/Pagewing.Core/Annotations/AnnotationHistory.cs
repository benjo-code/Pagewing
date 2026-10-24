namespace Pagewing.Core.Annotations;

/// <summary>
/// A step of the undo history. It is pure data: undoing a <see cref="Delete"/> recreates the annotation from the stored
/// copy, so a restored annotation may get a new index.
/// </summary>
public abstract record AnnotationCommand
{
    /// <summary>An annotation was created; undo deletes it.</summary>
    /// <param name="Created">The annotation as it was after creating.</param>
    public sealed record Create(PdfAnnotation Created) : AnnotationCommand;

    /// <summary>An annotation was deleted; undo recreates it.</summary>
    /// <param name="Removed">The annotation as it was before deleting.</param>
    public sealed record Delete(PdfAnnotation Removed) : AnnotationCommand;

    /// <summary>An annotation was changed; undo restores the earlier state.</summary>
    /// <param name="Before">The annotation before the change.</param>
    /// <param name="After">The annotation after the change.</param>
    public sealed record Edit(PdfAnnotation Before, PdfAnnotation After) : AnnotationCommand;
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

    /// <summary>Records a new change and forgets everything that could be redone.</summary>
    /// <param name="command">The change that was just made.</param>
    public void Push(AnnotationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
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

    /// <summary>Empties both stacks, for example after the document was reloaded; <see cref="Version"/> keeps increasing.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Version++;
    }
}
