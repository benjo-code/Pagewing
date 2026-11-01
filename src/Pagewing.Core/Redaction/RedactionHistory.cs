namespace Pagewing.Core.Redaction;

/// <summary>
/// The undo and redo history of a <see cref="RedactionPlan"/>. Plans are immutable and share their marks, so keeping a plan per
/// step is cheap. Not thread-safe; used on the UI thread.
/// </summary>
public sealed class RedactionHistory
{
    // Each step keeps one list of at most RedactionLimits.MaxMarks references; the oldest step is dropped beyond this.
    private const int MaxSteps = 100;

    private readonly List<RedactionPlan> _undo = [];
    private readonly List<RedactionPlan> _redo = [];

    /// <summary>Creates a history that starts at <paramref name="start"/>.</summary>
    /// <param name="start">The plan before the first step.</param>
    public RedactionHistory(RedactionPlan start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Current = start;
    }

    /// <summary>Gets the plan as it is now.</summary>
    public RedactionPlan Current { get; private set; }

    /// <summary>Changes with every <see cref="Do"/>, <see cref="Undo"/> and <see cref="Redo"/> that did something and never goes back.</summary>
    public int Version { get; private set; }

    /// <summary>Gets whether <see cref="Undo"/> does something.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets whether <see cref="Redo"/> does something.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Makes <paramref name="plan"/> the current plan and forgets everything that could be redone.</summary>
    /// <param name="plan">The plan after the change.</param>
    public void Do(RedactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _redo.Clear();
        if (_undo.Count >= MaxSteps)
        {
            _undo.RemoveAt(0);
        }

        _undo.Add(Current);
        Current = plan;
        Version++;
    }

    /// <summary>
    /// Replaces the current plan by <paramref name="plan"/> without a step of its own: a change that belongs to the last step (a
    /// run of arrow keys that moves a mark again and again is one step). Whoever calls it knows that the last step is the one the
    /// change belongs to (the <see cref="Version"/> is what it was after that step). With no earlier step it is <see cref="Do"/>.
    /// The version still rises, and what could be redone is forgotten.
    /// </summary>
    /// <param name="plan">The plan after the change.</param>
    public void Amend(RedactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_undo.Count == 0)
        {
            Do(plan);
            return;
        }

        _redo.Clear();
        Current = plan;
        Version++;
    }

    /// <summary>Goes back one step; does nothing if there is none.</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Version++;
    }

    /// <summary>Goes forward one undone step; does nothing if there is none.</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Version++;
    }
}
