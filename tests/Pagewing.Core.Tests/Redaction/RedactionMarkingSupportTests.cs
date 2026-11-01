using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>What the page view needs of the core for marking (milestone 14, task 12): merging a nudge into the last step, and the hit of a text selection.</summary>
public sealed class RedactionMarkingSupportTests
{
    private static RedactionMark Area(PageRect rect) => new(Guid.NewGuid(), RedactionKind.Area, 0, [rect], null, false);

    [Fact]
    public void AmendReplacesTheCurrentPlanWithoutANewStep()
    {
        var history = new RedactionHistory(RedactionPlan.Empty);
        var mark = Area(new PageRect(10, 10, 50, 30));
        var moved = mark with { Areas = [new PageRect(11, 10, 51, 30)] };
        history.Do(RedactionPlan.Empty.With(mark));
        var version = history.Version;

        var amended = RedactionPlan.Empty.With(moved);
        history.Amend(amended);

        Assert.Same(amended, history.Current);
        Assert.True(history.Version > version, "The version still rises, so the unsaved state notices the change.");

        // One undo goes back past both: the amended step is the one step.
        history.Undo();
        Assert.True(history.Current.IsEmpty);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void AmendWithoutAStepIsAStepAndForgetsTheRedoList()
    {
        var history = new RedactionHistory(RedactionPlan.Empty);
        var one = RedactionPlan.Empty.With(Area(new PageRect(1, 1, 5, 5)));
        history.Do(one);
        history.Undo();
        Assert.True(history.CanRedo);

        // Nothing to merge into (no earlier step): it does what Do does.
        var other = RedactionPlan.Empty.With(Area(new PageRect(2, 2, 6, 6)));
        history.Amend(other);

        Assert.Same(other, history.Current);
        Assert.False(history.CanRedo);
        Assert.True(history.CanUndo);
    }

    private static PageText TextOf(string text, double top = 100)
    {
        var chars = new List<TextChar>();
        var x = 10.0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                chars.Add(new TextChar('\r', default, TextCharTraits.Generated, 10));
                chars.Add(new TextChar('\n', default, TextCharTraits.Generated, 10));
                top += 14;
                x = 10;
                continue;
            }

            chars.Add(new TextChar(c, new PageRect(x, top, x + 6, top + 12), TextCharTraits.None, 12));
            x += 6;
        }

        return new PageText(0, 400, 400, chars, []);
    }

    [Fact]
    public void ARangeBecomesAHitWithOneRectanglePerLineAndItsText()
    {
        var text = TextOf("Hello\nWorld");

        // "llo", the line end, "Wor" (character indexes: H e l l o CR LF W o r l d).
        var hit = RedactionSearch.HitOfRange(text, page: 0, start: 2, end: 10);

        Assert.NotNull(hit);
        Assert.True(hit.Markable);
        Assert.Equal(2, hit.Areas.Count);
        Assert.Equal("llo Wor", hit.Text);
        Assert.Equal(0, hit.Page);
        Assert.All(hit.Areas, area => Assert.True(area.Right > area.Left && area.Bottom > area.Top));
    }

    [Fact]
    public void ABlankRangeIsNoHitAndBlanksAtTheEdgesAreLeftOut()
    {
        var text = TextOf("  ab  ");

        Assert.Null(RedactionSearch.HitOfRange(text, 0, 0, 2));
        Assert.Null(RedactionSearch.HitOfRange(text, 0, 3, 3));

        var hit = RedactionSearch.HitOfRange(text, 0, 0, 6);
        Assert.NotNull(hit);
        Assert.Equal("ab", hit.Text);
        Assert.Equal(new PageRect(22, 100, 34, 112), Assert.Single(hit.Areas));
    }

    [Fact]
    public void ARangeOutsideThePageIsClampedAndNeverThrows()
    {
        var text = TextOf("abc");

        Assert.Null(RedactionSearch.HitOfRange(text, 0, 5, 9));
        Assert.Equal("abc", RedactionSearch.HitOfRange(text, 0, -3, 99)!.Text);
        Assert.Null(RedactionSearch.HitOfRange(text, 0, 2, 1));
    }
}
