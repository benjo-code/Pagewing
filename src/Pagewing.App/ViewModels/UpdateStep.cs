using Pagewing.App.Resources;

namespace Pagewing.App.ViewModels;

/// <summary>Where a step of the installation is.</summary>
public enum UpdateStepState
{
    /// <summary>Not started.</summary>
    Open,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Done.</summary>
    Done,

    /// <summary>Failed; the window says why.</summary>
    Failed,
}

/// <summary>
/// One of the four steps of the progress window (Download, Verify, Install, Restart). Its state shows in a mark of its
/// own shape (check mark, ring with a dot, empty ring, cross), in the weight of the label and in words for screen readers,
/// never only in a colour (design system 7.10).
/// </summary>
/// <param name="Label">The name of the step.</param>
/// <param name="State">Where it is.</param>
public sealed record UpdateStep(string Label, UpdateStepState State)
{
    /// <summary>Gets whether the step is done.</summary>
    public bool IsDone => State == UpdateStepState.Done;

    /// <summary>Gets whether the step runs.</summary>
    public bool IsRunning => State == UpdateStepState.Running;

    /// <summary>Gets whether the step has not started.</summary>
    public bool IsOpen => State == UpdateStepState.Open;

    /// <summary>Gets whether the step failed.</summary>
    public bool IsFailed => State == UpdateStepState.Failed;

    /// <summary>Gets whether the label is set in bold: the step that runs or failed.</summary>
    public bool IsCurrent => State is UpdateStepState.Running or UpdateStepState.Failed;

    /// <summary>Gets the state in words ("done", "in progress", "not started", "failed").</summary>
    public string StateText => State switch
    {
        UpdateStepState.Done => Strings.UpdateStepDone,
        UpdateStepState.Running => Strings.UpdateStepRunning,
        UpdateStepState.Failed => Strings.UpdateStepFailed,
        _ => Strings.UpdateStepOpen,
    };

    /// <summary>Gets the name for screen readers: the visible label, then the state ("Download: done").</summary>
    public string AutomationName => Label + ": " + StateText;
}
