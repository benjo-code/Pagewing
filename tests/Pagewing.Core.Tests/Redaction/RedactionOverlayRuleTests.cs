using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The rule that an overlay text must not contain a text that is redacted (milestone 14.1, task 14): one decision for the analysis and
/// the apply. The files are copies in a temp folder; nothing is written next to the inputs.
/// </summary>
public sealed class RedactionOverlayRuleTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    private static RedactionMark Mark(int page, string? term, string? overlay = null) =>
        new(Guid.NewGuid(), term is null ? RedactionKind.Area : RedactionKind.Text, page, [new PageRect(20, 20, 80, 40)], term, false, overlay);

    private static RedactionPlan PlanOf(params RedactionMark[] marks) => RedactionPlan.Empty.WithRange(marks);

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    [Fact]
    public async Task OverlayContainingATermRefusesTheApply()
    {
        var path = Copy("text-rich.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        var plan = PlanOf(Mark(0, "Pagewing", "Akte Pagewing 4711"), Mark(1, null));

        // The analysis lists it, with the page of the mark that carries the text (and no other) ...
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, path, null, Ct);
        Assert.True(analysis.IsRefused);
        var refusal = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.OverlayRepeatsTerm, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        Assert.DoesNotContain("Pagewing", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pagewing", RedactionRefusalText.Of(refusal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page 1", RedactionRefusalText.Of(refusal), StringComparison.Ordinal);

        // ... and a caller that did not look is refused all the same, with nothing written.
        var refused = await Assert.ThrowsAsync<RedactionRefusedException>(() => new RedactionExecutor(engine).BuildAsync(plan, path, null, null, Ct));
        Assert.Contains(refused.Refusals, static r => r.Kind == RedactionRefusalKind.OverlayRepeatsTerm && r.Pages.SequenceEqual([0]));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public async Task ApplyRefusesAnOverlayWithATermAndWritesNothing()
    {
        var path = Copy("text-rich.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var target = Path.Combine(folder.Path, "redacted.pdf");
        var plan = PlanOf(Mark(0, "Pagewing", "Geschwärzt: Pagewing"));

        var refused = await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct));

        Assert.Equal(RedactionRefusalKind.OverlayRepeatsTerm, Assert.Single(refused.Refusals).Kind);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void ThePagesAreThoseOfTheMarksThatCarryTheText()
    {
        var plan = PlanOf(
            Mark(3, "Mustermann", "Mustermann"),
            Mark(1, "Beta", "Fall Mustermann"),
            Mark(1, "Gamma", "Fall Mustermann zwei"),
            Mark(0, "Delta", "geschwärzt"),
            Mark(2, null, null));

        var refusal = RedactionOverlayRule.Refusal(plan);

        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.OverlayRepeatsTerm, refusal.Kind);
        Assert.Equal([1, 3], refusal.Pages);
    }

    [Fact]
    public void ShortTermIsComparedOnlyForEquality()
    {
        // A term of 3 characters is a piece of the overlay text "geschwärzt" only by chance: not refused.
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "ab", "geschwärzt"))));
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "sch", "geschwärzt"))));
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "ab", "ab c"), Mark(1, "xyz", "ab c xyz"))));

        // The whole text equal to a short term is the one case: that text would put the term back on the page.
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "ab", "ab"))));
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "abc", "ABC"))));

        // Four characters is the first length that is searched as a piece.
        Assert.Equal(4, RedactionLimits.MinRawCheckLength);
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "gesc", "geschwärzt"))));
    }

    [Fact]
    public void TermsAreTrimmedLikeTheCheckTrimsThem()
    {
        // A selection may include white space next to the word; the check trims it, so does the rule.
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "  Pagewing\t", "Akte Pagewing"))));
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "  ab ", "geschwärzt"))));
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, " ab ", "ab"))));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "Pagewing", "AKTE PAGEWING"))));
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "PAGEWING", "akte pagewing"))));
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "Müller-Lüdenscheid", "Fall MÜLLER-lüdenscheid"))));
    }

    [Fact]
    public void LetterFormsAreComparedAsOneText()
    {
        // The term as it was read from the page may use a letter and a combining mark; the overlay text has the single letter.
        Assert.NotNull(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "Müller", "Akte Müller"))));
    }

    [Fact]
    public void MarksAddedAfterTheTextAreChecked()
    {
        var plan = PlanOf(Mark(0, null, "Fall Mustermann"), Mark(1, "Beta"));
        Assert.Null(RedactionOverlayRule.Refusal(plan));

        // The term comes later, from another page and another mark.
        var later = plan.With(Mark(4, "Mustermann"));
        var refusal = RedactionOverlayRule.Refusal(later);

        Assert.NotNull(refusal);
        Assert.Equal([0], refusal.Pages);
        Assert.Null(RedactionOverlayRule.Refusal(plan.Without([later.Marks[^1].Id])));
    }

    [Fact]
    public void CliStyleBatchMarksAreChecked()
    {
        // The command line gives every mark of the call the same overlay text, and the terms stand only after the search.
        const string Text = "Akte Mustermann";
        var batch = new[]
        {
            Mark(0, "Mustermann", Text),
            Mark(1, "Beta", Text),
            Mark(1, "Gamma", Text),
        };

        var refusal = RedactionOverlayRule.Refusal(RedactionPlan.Empty.WithRange(batch));
        Assert.NotNull(refusal);
        Assert.Equal([0, 1], refusal.Pages);

        // Without a term inside the text nothing is refused.
        Assert.Null(RedactionOverlayRule.Refusal(RedactionPlan.Empty.WithRange(batch.Select(static m => m with { Id = Guid.NewGuid(), OverlayText = "geschwärzt" }))));
    }

    [Fact]
    public void PlansWithoutOverlayTextOrWithoutTermsAreNeverRefused()
    {
        Assert.Null(RedactionOverlayRule.Refusal(RedactionPlan.Empty));
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, "Pagewing"))));
        Assert.Null(RedactionOverlayRule.Refusal(PlanOf(Mark(0, null, "Pagewing"))));
        Assert.Throws<ArgumentNullException>(() => RedactionOverlayRule.Refusal(null!));
    }

    [Fact]
    public void ManyMarksAreCheckedInReasonableTime()
    {
        // The largest plan with one text on every mark and a different term on each: the check does not square.
        var marks = Enumerable.Range(0, RedactionLimits.MaxMarks)
            .Select(static i => Mark(i % 50, "Begriff" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "geschwärzt"))
            .ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var refusal = RedactionOverlayRule.Refusal(RedactionPlan.Empty.WithRange(marks));

        Assert.Null(refusal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }
}
