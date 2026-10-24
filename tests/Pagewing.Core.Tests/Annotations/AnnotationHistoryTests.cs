using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationHistoryTests
{
    private static PdfAnnotation Annotation(int index) => new(
        0, index, null, AnnotationKind.Highlight, "Highlight", null, [], [], new PageRect(0, 0, 0, 0), null, null, null, true);

    private static AnnotationCommand.Create Command(int index) => new(Annotation(index));

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
}
