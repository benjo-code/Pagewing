using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationHistoryTests
{
    private static PdfAnnotation Annotation(int index) => new(
        0, index, null, AnnotationKind.Highlight, "Highlight", null, [], [], new PageRect(0, 0, 0, 0), null, null, null, true);

    private static AnnotationCommand.Create Command(int index) => new(Annotation(index));

    [Fact]
    public void PackedImageCountsTowardTheBudget()
    {
        var history = new AnnotationHistory();
        var small = new PackedSignatureImage(10, 10, new byte[1_000]);
        var large = new PackedSignatureImage(10, 10, new byte[2_500]);
        Assert.Equal(0, history.ImageBytes);
        Assert.True(history.Fits(SignatureLimits.MaxHistoryImageBytes));
        Assert.False(history.Fits(SignatureLimits.MaxHistoryImageBytes + 1));

        history.Push(new AnnotationCommand.Create(Annotation(1), small));
        history.Push(new AnnotationCommand.Delete(Annotation(2), large));
        history.Push(Command(3));

        // Both stacks count in ImageBytes; what a new step may add is measured against the steps that can still be undone.
        Assert.Equal(3_500, history.ImageBytes);
        Assert.True(history.Fits(SignatureLimits.MaxHistoryImageBytes - 3_500));
        Assert.False(history.Fits(SignatureLimits.MaxHistoryImageBytes - 3_499));
        history.TakeUndo();
        history.TakeUndo();
        Assert.Equal(3_500, history.ImageBytes);
        Assert.True(history.Fits(SignatureLimits.MaxHistoryImageBytes - 1_000));

        // A new step forgets the redo stack and its images.
        history.Push(Command(4));
        Assert.Equal(1_000, history.ImageBytes);
        history.Clear();
        Assert.Equal(0, history.ImageBytes);
    }

    [Fact]
    public void PushThenUndoReturnsTheCommand()
    {
        var history = new AnnotationHistory();
        var command = Command(1);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        history.Push(command);

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Same(command, history.TakeUndo());
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Null(history.TakeUndo());
    }

    [Fact]
    public void UndoThenRedoReturnsTheSameCommand()
    {
        var history = new AnnotationHistory();
        var first = Command(1);
        var second = new AnnotationCommand.Edit(Annotation(2), Annotation(3));
        history.Push(first);
        history.Push(second);

        Assert.Same(second, history.TakeUndo());
        Assert.Same(first, history.TakeUndo());
        Assert.Same(first, history.TakeRedo());
        Assert.Same(second, history.TakeRedo());
        Assert.Null(history.TakeRedo());
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Same(second, history.TakeUndo());
    }

    [Fact]
    public void PushAfterUndoClearsRedo()
    {
        var history = new AnnotationHistory();
        history.Push(Command(1));
        history.TakeUndo();
        Assert.True(history.CanRedo);

        var next = new AnnotationCommand.Delete(Annotation(2));
        history.Push(next);

        Assert.False(history.CanRedo);
        Assert.Null(history.TakeRedo());
        Assert.Same(next, history.TakeUndo());
        Assert.Null(history.TakeUndo());
    }

    [Fact]
    public void HistoryKeepsAtMostMaxCommandsAndDropsTheOldest()
    {
        var history = new AnnotationHistory();
        var pushed = new List<AnnotationCommand>();
        for (var i = 0; i < AnnotationHistory.MaxCommands + 3; i++)
        {
            var command = Command(i);
            pushed.Add(command);
            history.Push(command);
        }

        var undone = new List<AnnotationCommand>();
        while (history.TakeUndo() is { } command)
        {
            undone.Add(command);
        }

        Assert.Equal(AnnotationHistory.MaxCommands, undone.Count);
        Assert.Same(pushed[^1], undone[0]);
        Assert.Same(pushed[3], undone[^1]);
    }

    [Fact]
    public void DiscardForgetsOnlyTheNewestCommandOfAStack()
    {
        var history = new AnnotationHistory();
        var first = Command(1);
        var second = Command(2);
        history.Push(first);
        history.Push(second);

        // Undone, the second one is the newest of the redo stack; forgetting it leaves the first one to undo. A command
        // that is the newest of neither stack is not forgotten.
        Assert.Same(second, history.TakeUndo());
        var version = history.Version;
        Assert.False(history.Discard(Command(9)));
        Assert.Equal(version, history.Version);
        Assert.True(history.Discard(second));
        Assert.True(history.Version > version);
        Assert.False(history.CanRedo);
        Assert.Same(first, history.TakeUndo());

        // Redone, it is the newest of the undo stack.
        Assert.Same(first, history.TakeRedo());
        Assert.True(history.Discard(first));
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.Discard(first));
    }

    [Fact]
    public void ClearEmptiesBothStacksButKeepsVersionIncreasing()
    {
        var history = new AnnotationHistory();
        history.Push(Command(1));
        history.Push(Command(2));
        history.TakeUndo();
        var before = history.Version;

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.TakeUndo());
        Assert.Null(history.TakeRedo());
        Assert.True(history.Version > before);

        var afterClear = history.Version;
        history.Push(Command(3));
        Assert.True(history.Version > afterClear);
    }

    [Fact]
    public void MoveCountsAsOneVersionStep()
    {
        var history = new AnnotationHistory();
        var before = Annotation(1) with { Drawing = DrawingGeometry.Rectangle(new PageRect(10, 10, 60, 40), 2), GeometryEditable = true };
        var after = before with { Drawing = DrawingGeometry.Rectangle(new PageRect(30, 20, 80, 50), 2) };
        history.Push(new AnnotationCommand.Create(before));
        var version = history.Version;

        // A move is one command on the stack and one step of the version, however many mouse events led to it.
        var move = new AnnotationCommand.Edit(before, after);
        history.Push(move);

        Assert.Equal(version + 1, history.Version);
        Assert.Same(move, history.TakeUndo());
        Assert.Equal(version + 2, history.Version);
        Assert.Same(move, history.TakeRedo());
        Assert.Equal(version + 3, history.Version);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void VersionChangesWithEveryStep()
    {
        var history = new AnnotationHistory();
        var seen = new HashSet<int> { history.Version };

        history.Push(Command(1));
        Assert.True(seen.Add(history.Version));
        history.Push(Command(2));
        Assert.True(seen.Add(history.Version));
        history.TakeUndo();
        Assert.True(seen.Add(history.Version));
        history.TakeRedo();
        Assert.True(seen.Add(history.Version));

        // Steps that do nothing leave the version alone.
        var version = history.Version;
        Assert.Null(history.TakeRedo());
        Assert.Equal(version, history.Version);
    }

    [Fact]
    public void AFormValueIsAStepLikeAnyOther()
    {
        var history = new AnnotationHistory();
        var annotation = Command(1);
        var form = new AnnotationCommand.FormValue("Name", FormFieldState.OfText("Alt"), FormFieldState.OfText("Neu"), new HashSet<int> { 0, 1 });
        history.Push(annotation);
        var version = history.Version;
        history.Push(form);

        Assert.Equal(version + 1, history.Version);
        Assert.Equal("Name", form.FieldName);
        Assert.Equal([0, 1], form.Pages.Order());
        Assert.Same(form, history.TakeUndo());
        Assert.True(history.CanRedo);
        Assert.Same(form, history.TakeRedo());
        Assert.True(history.Discard(form));
        Assert.Same(annotation, history.TakeUndo());
    }

    [Fact]
    public void OnlyFormChangesSinceTheLastClearAreTold()
    {
        var history = new AnnotationHistory();
        var form = new AnnotationCommand.FormValue("Name", FormFieldState.OfText("Alt"), FormFieldState.OfText("Neu"), new HashSet<int> { 0 });

        // Nothing yet: no form change either.
        Assert.False(history.HasOnlyFormChanges);
        history.Push(form);
        Assert.True(history.HasOnlyFormChanges);
        history.Push(form with { After = FormFieldState.OfText("Neuer") });
        Assert.True(history.HasOnlyFormChanges);

        // An annotation step counts even after it was undone, dropped or forgotten: the document was touched by it.
        history.Push(Command(1));
        Assert.False(history.HasOnlyFormChanges);
        Assert.NotNull(history.TakeUndo());
        Assert.False(history.HasOnlyFormChanges);
        history.Push(form);
        Assert.False(history.HasOnlyFormChanges);

        // Only a clear (a save, another document) starts afresh.
        history.Clear();
        Assert.False(history.HasOnlyFormChanges);
        history.Push(form);
        Assert.True(history.HasOnlyFormChanges);

        // A change the history cannot undo (a failed form write) is no annotation step.
        history.MarkChanged();
        Assert.True(history.HasOnlyFormChanges);
        history.Clear();
        history.MarkChanged();
        Assert.False(history.HasOnlyFormChanges);
    }

    [Fact]
    public void MarkChangedMovesTheVersionWithoutAStep()
    {
        var history = new AnnotationHistory();
        history.Push(Command(1));
        var version = history.Version;

        history.MarkChanged();

        Assert.True(history.Version > version);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.NotNull(history.TakeUndo());
        Assert.Null(history.TakeUndo());
    }
}
