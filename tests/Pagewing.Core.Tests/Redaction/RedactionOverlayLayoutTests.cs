using System.Diagnostics;
using Pagewing.Core.Annotations;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The layout of the overlay text on an area (milestone 14.1, task 14), with made-up glyph widths: every glyph 0.5 em wide, ascent 1.0
/// and descent 0.3 em (so a line is 1.3 em high). The layout never reaches outside the area it is given.
/// </summary>
public sealed class RedactionOverlayLayoutTests
{
    private static readonly FakeMetrics Metrics = new();

    private static RedactionOverlayStyle Fixed(bool repeat = false) => new(Repeat: repeat, AutoSize: false);

    private static RedactionOverlayStyle Auto(bool repeat = false) => new(Repeat: repeat, AutoSize: true);

    [Fact]
    public void FitCentersOnceWithoutRepeat()
    {
        // "ab" at 10 pt is 10 pt wide and a line is 13 pt high; the area is 100 x 50 and sits at (20, 30).
        var placement = RedactionOverlayLayout.Fit("ab", new PageRect(20, 30, 120, 80), Fixed(), 10, Metrics);

        Assert.True(placement.Fits);
        Assert.Equal(10, placement.FontSize);
        var line = Assert.Single(placement.Lines);
        Assert.Equal("ab", line.Text);
        Assert.Equal(10, line.Width, 9);

        // Offsets are from the top left of the area: centred across, and the 13 pt line centred down (top at 18.5, baseline 10 pt below it).
        Assert.Equal(45, line.X, 9);
        Assert.Equal(28.5, line.Baseline, 9);
    }

    [Fact]
    public void RepeatTilesWithoutOverflow()
    {
        // "abcd" at 10 pt is 20 pt wide, a line 13 pt high; 200 x 60 leaves 198 x 58 inside the margin of 1 pt.
        var area = new PageRect(0, 0, 200, 60);
        var placement = RedactionOverlayLayout.Fit("abcd", area, Fixed(repeat: true), 10, Metrics);

        Assert.True(placement.Fits);
        Assert.Equal(18, placement.Lines.Count);
        Assert.All(placement.Lines, line =>
        {
            Assert.Equal("abcd", line.Text);
            Assert.True(line.X >= 0 && line.X + line.Width <= 200, $"x {line.X} width {line.Width}");
            Assert.True(line.Baseline - 10 >= 0 && line.Baseline + 3 <= 60, $"baseline {line.Baseline}");
        });
        Assert.Equal(placement.Lines.Count, placement.Lines.Select(static l => (l.X, l.Baseline)).Distinct().Count());

        // The tiles do not touch: in a row they are a gap apart, and the rows are further apart than one line is high.
        var first = placement.Lines[0];
        var second = placement.Lines[1];
        Assert.True(second.X > first.X + first.Width);
        Assert.True(placement.Lines.Select(static l => l.Baseline).Distinct().Order().Zip(placement.Lines.Select(static l => l.Baseline).Distinct().Order().Skip(1), static (a, b) => b - a).All(static d => d > 13));
    }

    [Fact]
    public void RepeatNeverReachesOutsideAnAreaThatIsTightAcross()
    {
        // One tile fits across but not two: one column, as many rows as fit.
        var placement = RedactionOverlayLayout.Fit("abcd", new PageRect(0, 0, 30, 100), Fixed(repeat: true), 10, Metrics);

        Assert.True(placement.Fits);
        Assert.All(placement.Lines, line => Assert.Equal(5, line.X, 9));
        Assert.True(placement.Lines.Count >= 4);
        Assert.All(placement.Lines, line => Assert.True(line.Baseline + 3 <= 100));
    }

    [Fact]
    public void AutoSizeShrinksDownToSixPoints()
    {
        // "abcdefghij" is 5 em wide. 100 x 40 leaves 98 x 38: the width allows 19.6 pt, the height 29.2 pt, so the width decides.
        var wide = RedactionOverlayLayout.Fit("abcdefghij", new PageRect(0, 0, 100, 40), Auto(), 12, Metrics);

        Assert.True(wide.Fits);
        Assert.InRange(wide.FontSize, 19.5, 19.6);
        var line = Assert.Single(wide.Lines);
        Assert.True(line.Width <= 98);

        // Narrower and narrower: the size follows down to exactly 6 pt (30.01 pt across leaves room for 6.002 pt) ...
        var atTheLimit = RedactionOverlayLayout.Fit("abcdefghij", new PageRect(0, 0, 32.01, 40), Auto(), 12, Metrics);
        Assert.True(atTheLimit.Fits);
        Assert.Equal(RedactionLimits.MinOverlayFontSize, atTheLimit.FontSize);
        Assert.True(Assert.Single(atTheLimit.Lines).Width <= 30.01);

        // ... and below it nothing is drawn.
        var below = RedactionOverlayLayout.Fit("abcdefghij", new PageRect(0, 0, 31, 40), Auto(), 12, Metrics);
        Assert.False(below.Fits);
        Assert.Empty(below.Lines);
    }

    [Fact]
    public void AutoSizeFollowsTheHeightOfAFlatArea()
    {
        // 300 x 12 leaves 10 pt of height: 10 / 1.3 = 7.69 pt, whatever the width would allow.
        var placement = RedactionOverlayLayout.Fit("ab", new PageRect(0, 0, 300, 12), Auto(), 12, Metrics);

        Assert.True(placement.Fits);
        Assert.InRange(placement.FontSize, 7.6, 7.7);
        Assert.True(placement.FontSize * 1.3 <= 10);
    }

    [Fact]
    public void AutoSizeNeverGrowsAboveTheCapAndRepeatNeverAboveTheBaseSize()
    {
        var single = RedactionOverlayLayout.Fit("ab", new PageRect(0, 0, 2000, 2000), Auto(), 12, Metrics);
        Assert.True(single.Fits);
        Assert.Equal(RedactionOverlayLayout.MaxAutoFontSize, single.FontSize);

        var repeated = RedactionOverlayLayout.Fit("ab", new PageRect(0, 0, 2000, 2000), Auto(repeat: true), 12, Metrics);
        Assert.True(repeated.Fits);
        Assert.Equal(12, repeated.FontSize);
    }

    [Fact]
    public void TooSmallAreaDoesNotFitAndSaysSo()
    {
        // A fixed size that does not fit across, one that does not fit down, and an area with nothing inside the margin.
        var across = RedactionOverlayLayout.Fit("abcdefghij", new PageRect(0, 0, 40, 40), Fixed(), 12, Metrics);
        var down = RedactionOverlayLayout.Fit("ab", new PageRect(0, 0, 100, 10), Fixed(), 12, Metrics);
        var none = RedactionOverlayLayout.Fit("ab", new PageRect(0, 0, 2, 2), Auto(), 12, Metrics);
        var repeated = RedactionOverlayLayout.Fit("abcdefghij", new PageRect(0, 0, 40, 40), Fixed(repeat: true), 12, Metrics);

        foreach (var placement in new[] { across, down, none, repeated })
        {
            Assert.False(placement.Fits);
            Assert.Empty(placement.Lines);
        }
    }

    [Fact]
    public void AnAreaWithoutSizeOrWithBrokenEdgesDoesNotFit()
    {
        foreach (var area in new[]
        {
            new PageRect(5, 5, 5, 50),
            new PageRect(5, 5, 50, 5),
            new PageRect(50, 5, 5, 50),
            new PageRect(double.NaN, 0, 10, 10),
            new PageRect(0, 0, double.PositiveInfinity, 10),
        })
        {
            Assert.False(RedactionOverlayLayout.Fit("ab", area, Auto(), 12, Metrics).Fits);
        }
    }

    [Fact]
    public void NoTextHasNothingToDrawAndIsNotAMiss()
    {
        foreach (var text in new[] { string.Empty, "   " })
        {
            var placement = RedactionOverlayLayout.Fit(text, new PageRect(0, 0, 100, 40), Auto(), 12, Metrics);

            Assert.True(placement.Fits);
            Assert.Empty(placement.Lines);
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        var area = new PageRect(0, 0, 100, 40);

        Assert.Throws<ArgumentNullException>(() => RedactionOverlayLayout.Fit(null!, area, Auto(), 12, Metrics));
        Assert.Throws<ArgumentNullException>(() => RedactionOverlayLayout.Fit("a", area, null!, 12, Metrics));
        Assert.Throws<ArgumentNullException>(() => RedactionOverlayLayout.Fit("a", area, Auto(), 12, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionOverlayLayout.Fit("a", area, Auto(), 0, Metrics));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionOverlayLayout.Fit("a", area, Auto(), double.NaN, Metrics));
    }

    [Fact]
    public void HostileSizesAreCappedAndStillInsideTheArea()
    {
        // A one-character text, repeated over the largest page: hundreds of thousands of tiles would fit; the layout holds at most the cap.
        var area = new PageRect(0, 0, 14_400, 14_400);
        var clock = Stopwatch.StartNew();
        var placement = RedactionOverlayLayout.Fit("a", area, Fixed(repeat: true), 6, Metrics);
        clock.Stop();

        Assert.True(placement.Fits);
        Assert.InRange(placement.Lines.Count, RedactionOverlayLayout.MaxOverlayTiles / 2, RedactionOverlayLayout.MaxOverlayTiles);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        foreach (var line in placement.Lines)
        {
            Assert.True(line.X >= 0 && line.X + line.Width <= 14_400);
            Assert.True(line.Baseline - 6 >= 0 && line.Baseline + 1.8 <= 14_400);
        }

        // Reading the same list twice gives the same tiles (the list is computed, not filled).
        var counted = 0;
        OverlayLine? last = null;
        foreach (var line in placement.Lines)
        {
            counted++;
            last = line;
        }

        Assert.Equal(placement.Lines.Count, counted);
        Assert.Equal(last, placement.Lines[placement.Lines.Count - 1]);
    }

    [Fact]
    public void FiveHundredAreasWithRepeatedTextStayCheap()
    {
        var clock = Stopwatch.StartNew();
        var tiles = 0L;
        for (var i = 0; i < 500; i++)
        {
            var placement = RedactionOverlayLayout.Fit(new string('x', 40), new PageRect(0, 0, 600 + i, 400 + i), Auto(repeat: true), 9, Metrics);
            Assert.True(placement.Fits);
            tiles += placement.Lines.Count;
        }

        clock.Stop();
        Assert.True(tiles > 0);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");

        // Even 500 areas of the hostile size cost no memory per tile.
        var hostile = RedactionOverlayLayout.Fit("a", new PageRect(0, 0, 14_400, 14_400), Fixed(repeat: true), 6, Metrics);
        var before = GC.GetTotalAllocatedBytes(precise: false);
        for (var i = 0; i < 500; i++)
        {
            _ = RedactionOverlayLayout.Fit("a", new PageRect(0, 0, 14_400, 14_400), Fixed(repeat: true), 6, Metrics).Lines.Count;
        }

        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - before < 50_000_000);
        Assert.True(hostile.Fits);
    }

    [Fact]
    public void TextOutsideTheGlyphSetIsRefusedAsAWhole()
    {
        // The set of the layout is the one of the text box: an overlay text with any other character never reaches the layout.
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [new PageRect(0, 0, 10, 10)], null, false));
        var bad = new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [new PageRect(0, 0, 10, 10)], null, false, "geschwärzt ☃");

        var error = Assert.Throws<RedactionException>(() => plan.With(bad));

        Assert.Equal(RedactionError.OverlayInvalid, error.Error);
        Assert.Single(plan.Marks);
        Assert.Equal("geschwärzt", RedactionPlan.Empty.With(bad with { OverlayText = "geschwärzt" }).Marks[0].OverlayText);
    }

    private sealed class FakeMetrics : IGlyphMetrics
    {
        public double Ascent => 1.0;

        public double Descent => 0.3;

        public double AdvanceOf(char character) => 0.5;
    }
}
