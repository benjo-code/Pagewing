using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

public sealed class RedactionPlanTests
{
    private static readonly PageRect Box = new(10, 20, 110, 40);

    private static RedactionMark Area(int page = 0, params PageRect[] areas) =>
        new(Guid.NewGuid(), RedactionKind.Area, page, areas.Length == 0 ? [Box] : areas, null, false);

    private static RedactionMark Text(string term, int page = 0, bool everywhere = false, RedactionKind kind = RedactionKind.Text) =>
        new(Guid.NewGuid(), kind, page, [Box], term, everywhere);

    private static RedactionError ErrorOf(Action action) => Assert.Throws<RedactionException>(action).Error;

    [Fact]
    public void LimitsArePinned()
    {
        Assert.Equal(10_000, RedactionLimits.MaxMarks);
        Assert.Equal(500, RedactionLimits.MaxAreasPerMark);
        Assert.Equal(1_000, RedactionLimits.MaxTermLength);
        Assert.Equal(4, RedactionLimits.MinRawCheckLength);
    }

    [Fact]
    public void EmptyPlanHasNoMarksAndBlackFill()
    {
        var plan = RedactionPlan.Empty;
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Marks);
        Assert.Empty(plan.Pages);
        Assert.Equal(RedactionFill.Black, plan.Fill);
    }

    [Fact]
    public void AddingAMarkKeepsTheOthers()
    {
        var first = Area(0);
        var second = Text("Müller", 2);
        var one = RedactionPlan.Empty.With(first);
        var two = one.With(second);

        Assert.True(RedactionPlan.Empty.IsEmpty);
        Assert.Single(one.Marks);
        Assert.Equal([first.Id, second.Id], two.Marks.Select(m => m.Id));
        Assert.False(two.IsEmpty);
        Assert.Equal([0, 2], two.Pages.Order());
    }

    [Fact]
    public void AddingTheSameIdTwiceIsRefused()
    {
        var mark = Area();
        var plan = RedactionPlan.Empty.With(mark);
        Assert.Throws<ArgumentException>(() => plan.With(mark));
    }

    [Fact]
    public void WithRangeAddsManyMarksAtOnceAndRespectsTheLimit()
    {
        var first = Area(0);
        var plan = RedactionPlan.Empty.With(first);
        var many = Enumerable.Range(0, RedactionLimits.MaxMarks - 1).Select(i => Text("term", i % 7)).ToList();

        var full = plan.WithRange(many);

        Assert.Equal(RedactionLimits.MaxMarks, full.Marks.Count);
        Assert.Equal(first.Id, full.Marks[0].Id);
        Assert.Equal(many.Select(static m => m.Id), full.Marks.Skip(1).Select(static m => m.Id));
        Assert.Single(plan.Marks);
        Assert.Equal(Enumerable.Range(0, 7), full.Pages.Order());
        Assert.Equal(full.Fill, plan.Fill);
        Assert.Same(plan, plan.WithRange([]));
        Assert.Equal(RedactionFill.White, plan.WithFill(RedactionFill.White).WithRange([Area(1)]).Fill);

        // One too many: nothing is added.
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => plan.WithRange([.. many, Text("more")])));
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => full.WithRange([Area()])));
        Assert.Single(plan.Marks);

        // A sequence that never ends is refused at the limit, not read to its end.
        var read = 0;
        IEnumerable<RedactionMark> Endless()
        {
            while (true)
            {
                read++;
                yield return Text("x");
            }
        }

        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => RedactionPlan.Empty.WithRange(Endless())));
        Assert.True(read <= RedactionLimits.MaxMarks + 1);

        // One refused mark, or a repeated id, leaves the plan as it was.
        var bad = new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [new PageRect(1, 1, 1, 1)], null, false);
        Assert.Equal(RedactionError.InvalidArea, ErrorOf(() => plan.WithRange([Area(2), bad])));
        Assert.Throws<ArgumentException>(() => plan.WithRange([first]));
        var twice = Area(3);
        Assert.Throws<ArgumentException>(() => plan.WithRange([twice, twice]));
        Assert.Single(plan.Marks);
        Assert.Throws<ArgumentNullException>(() => plan.WithRange(null!));
    }

    [Fact]
    public void AreaWithNaNInfinityOrNoSizeIsRefused()
    {
        PageRect[] bad =
        [
            new(double.NaN, 0, 10, 10),
            new(0, double.NaN, 10, 10),
            new(0, 0, double.PositiveInfinity, 10),
            new(0, 0, 10, double.NegativeInfinity),
            new(5, 0, 5, 10),
            new(0, 5, 10, 5),
            new(10, 0, 0, 10),
            new(0, 10, 10, 0),
        ];
        foreach (var rect in bad)
        {
            Assert.Equal(RedactionError.InvalidArea, ErrorOf(() => RedactionPlan.Empty.With(Area(0, rect))));
        }

        Assert.Equal(RedactionError.InvalidArea, ErrorOf(() => RedactionPlan.Empty.With(Area(0, Box, new PageRect(double.NaN, 0, 1, 1)))));
        Assert.Equal(RedactionError.InvalidArea, ErrorOf(() => RedactionPlan.Empty.With(Area(-1))));
        Assert.Equal(RedactionError.InvalidArea, ErrorOf(() => RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [], null, false))));
    }

    [Fact]
    public void NegativeAndOutOfPageAreasAreAcceptedAndClippedLater()
    {
        var plan = RedactionPlan.Empty
            .With(Area(0, new PageRect(-50, -30, -10, -5)))
            .With(Area(0, new PageRect(10_000, 10_000, 20_000, 20_000)))
            .With(Area(0, new PageRect(-1e9, -1e9, 1e9, 1e9)));
        Assert.Equal(3, plan.AreasOn(0).Count);
        Assert.Equal(new PageRect(-50, -30, -10, -5), plan.AreasOn(0)[0]);
    }

    [Fact]
    public void EmptyOrWhitespaceTermIsRefused()
    {
        foreach (var term in new[] { string.Empty, " ", "\t\r\n", " ", "  " })
        {
            Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => RedactionPlan.Empty.With(Text(term))));
            Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => RedactionPlan.Empty.With(Text(term, kind: RedactionKind.Hit))));
        }

        Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 0, [Box], null, false))));
        Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [Box], " ", false))));
        Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [Box], null, true))));
    }

    [Fact]
    public void TermLongerThanTheLimitIsRefusedButTheLimitItselfIsKept()
    {
        var atLimit = Text(new string('a', RedactionLimits.MaxTermLength));
        Assert.Single(RedactionPlan.Empty.With(atLimit).Marks);
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => RedactionPlan.Empty.With(Text(new string('a', RedactionLimits.MaxTermLength + 1)))));
    }

    [Fact]
    public void MoreThanMaxMarksFails()
    {
        var plan = RedactionPlan.Empty;
        for (var i = 0; i < RedactionLimits.MaxMarks; i++)
        {
            plan = plan.With(Area(i % 5));
        }

        Assert.Equal(RedactionLimits.MaxMarks, plan.Marks.Count);
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => plan.With(Area())));
        Assert.Equal(RedactionLimits.MaxMarks, plan.Marks.Count);
    }

    [Fact]
    public void MoreThanMaxAreasPerMarkFails()
    {
        var atLimit = Enumerable.Repeat(Box, RedactionLimits.MaxAreasPerMark).ToArray();
        Assert.Single(RedactionPlan.Empty.With(Area(0, atLimit)).Marks);

        var tooMany = Enumerable.Repeat(Box, RedactionLimits.MaxAreasPerMark + 1).ToArray();
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => RedactionPlan.Empty.With(Area(0, tooMany))));
    }

    [Fact]
    public void AreaListIsRefusedBySizeBeforeItIsRead()
    {
        var mark = new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, new HostileAreas(int.MaxValue), null, false);
        Assert.Equal(RedactionError.LimitExceeded, ErrorOf(() => RedactionPlan.Empty.With(mark)));
    }

    [Fact]
    public void ReplaceKeepsTheIdAndPosition()
    {
        var a = Area(0);
        var b = Text("Meier", 1);
        var c = Area(2);
        var plan = RedactionPlan.Empty.With(a).With(b).With(c);

        var changed = b with { Term = "Meyer", Everywhere = true };
        var result = plan.Replace(changed);

        Assert.Equal([a.Id, b.Id, c.Id], result.Marks.Select(m => m.Id));
        Assert.Equal("Meyer", result.Marks[1].Term);
        Assert.True(result.Marks[1].Everywhere);
        Assert.Equal("Meier", plan.Marks[1].Term);
        Assert.Throws<ArgumentException>(() => plan.Replace(Area()));
        Assert.Equal(RedactionError.EmptyTerm, ErrorOf(() => plan.Replace(b with { Term = " " })));
    }

    [Fact]
    public void WithoutRemovesOnlyTheNamedMarks()
    {
        var a = Area(0);
        var b = Area(1);
        var c = Area(1);
        var plan = RedactionPlan.Empty.With(a).With(b).With(c);

        var result = plan.Without([b.Id, Guid.NewGuid()]);
        Assert.Equal([a.Id, c.Id], result.Marks.Select(m => m.Id));
        Assert.Equal([0, 1], result.Pages.Order());
        Assert.Equal(3, plan.Marks.Count);
        Assert.Same(plan, plan.Without([Guid.NewGuid()]));
        Assert.Same(plan, plan.Without([]));
        Assert.Equal([0], result.Without([c.Id]).Pages.Order());
    }

    [Fact]
    public void FillChangesOnlyTheFill()
    {
        var plan = RedactionPlan.Empty.With(Area());
        var white = plan.WithFill(RedactionFill.White);
        Assert.Equal(RedactionFill.White, white.Fill);
        Assert.Equal(RedactionFill.Black, plan.Fill);
        Assert.Same(plan, plan.WithFill(RedactionFill.Black));
        Assert.Equal(RedactionFill.White, white.With(Area(3)).Fill);
        Assert.Equal(RedactionFill.White, white.Without([white.Marks[0].Id]).Fill);
    }

    [Fact]
    public void AreasOnReturnsOnlyThatPage()
    {
        var r1 = new PageRect(0, 0, 10, 10);
        var r2 = new PageRect(20, 20, 30, 30);
        var r3 = new PageRect(40, 40, 50, 50);
        var plan = RedactionPlan.Empty.With(Area(0, r1)).With(Area(1, r2)).With(Area(0, r2, r3));

        Assert.Equal([r1, r2, r3], plan.AreasOn(0));
        Assert.Equal([r2], plan.AreasOn(1));
        Assert.Empty(plan.AreasOn(7));
    }

    [Fact]
    public void ThePlanKeepsItsOwnCopyOfTheAreas()
    {
        var areas = new List<PageRect> { Box };
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, areas, null, false));
        areas.Add(new PageRect(0, 0, 1, 1));
        areas[0] = new PageRect(0, 0, 2, 2);

        Assert.Equal([Box], plan.Marks[0].Areas);
    }

    [Fact]
    public void TermWithRegexCharactersIsKeptLiterally()
    {
        const string Term = @"^(a+)+$ [x]* \d .+ ?|";
        var plan = RedactionPlan.Empty.With(Text(Term, everywhere: true));
        Assert.Equal(Term, plan.Marks[0].Term);
        Assert.True(plan.Marks[0].Everywhere);
    }

    [Fact]
    public void UnicodeTermsAreKeptAsGiven()
    {
        // Composed and decomposed u-umlaut, sharp s, an emoji, right-to-left text and a leading space: nothing is normalised
        // or trimmed here, the search decides what counts as equal.
        string[] terms = ["Müller", "Müller", "MÜLLER", "ẞ", "Straẞe", "😀", "שלום", " Meier "];
        foreach (var term in terms)
        {
            var plan = RedactionPlan.Empty.With(Text(term));
            Assert.Equal(term, plan.Marks[0].Term);
            Assert.Equal(term.Length, plan.Marks[0].Term!.Length);
        }

        var plan2 = RedactionPlan.Empty.With(Text("Müller")).With(Text("Müller"));
        Assert.NotEqual(plan2.Marks[0].Term, plan2.Marks[1].Term);
    }

    [Fact]
    public void HistoryUndoRedoAndVersion()
    {
        var history = new RedactionHistory(RedactionPlan.Empty);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(0, history.Version);
        history.Undo();
        history.Redo();
        Assert.Equal(0, history.Version);

        var a = RedactionPlan.Empty.With(Area());
        var ab = a.With(Area());
        history.Do(a);
        Assert.Equal(1, history.Version);
        history.Do(ab);
        Assert.Equal(2, history.Version);
        Assert.Same(ab, history.Current);
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Equal(3, history.Version);
        Assert.Same(a, history.Current);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Equal(4, history.Version);
        Assert.Same(ab, history.Current);
        Assert.False(history.CanRedo);

        history.Undo();
        Assert.True(history.CanRedo);
        var other = a.With(Text("Meier"));
        history.Do(other);
        Assert.Equal(6, history.Version);
        Assert.False(history.CanRedo);
        history.Redo();
        Assert.Equal(6, history.Version);
        Assert.Same(other, history.Current);

        history.Undo();
        history.Undo();
        Assert.Same(RedactionPlan.Empty, history.Current);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void HistoryKeepsAtMostOneHundredSteps()
    {
        var history = new RedactionHistory(RedactionPlan.Empty);
        var plan = RedactionPlan.Empty;
        for (var i = 0; i < 150; i++)
        {
            plan = plan.With(Area());
            history.Do(plan);
        }

        var steps = 0;
        while (history.CanUndo)
        {
            history.Undo();
            steps++;
        }

        Assert.Equal(100, steps);
        Assert.Equal(50, history.Current.Marks.Count);
    }

    /// <summary>A list that claims a huge size and fails if anyone reads it.</summary>
    private sealed class HostileAreas(int count) : IReadOnlyList<PageRect>
    {
        public int Count => count;

        public PageRect this[int index] => throw new InvalidOperationException("The list must not be read.");

        public IEnumerator<PageRect> GetEnumerator() => throw new InvalidOperationException("The list must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
