using Pagewing.Core.Pdf;
using Xunit;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// The name index of the form scan (milestone 11, task 7, fix round 2): its limits apply while the pages arrive, so memory
/// stays bounded by the limits plus one page, and pages whose names were cut short are recorded.
/// </summary>
public sealed class FormNameIndexTests
{
    [Fact]
    public void NamesOfThePagesAreIndexedWithTheirPagesAndIncompletePages()
    {
        var builder = new FormNameIndexBuilder(characterLimit: 100, entryLimit: 100, incompletePageLimit: 2);
        builder.Add(0, ["A", "B"], complete: true);
        builder.Add(2, ["A"], complete: false);
        builder.Add(3, [], complete: true);

        var index = builder.Build();

        Assert.NotNull(index.Pages);
        Assert.Equal([0, 2], index.Pages["A"]);
        Assert.Equal([0], index.Pages["B"]);
        Assert.Equal([2], index.IncompletePages);
        Assert.Equal(3, builder.RetainedEntries);
        Assert.Equal(2, builder.RetainedCharacters);
    }

    [Fact]
    public void CharacterLimitDropsEverythingAsSoonAsItIsCrossed()
    {
        var builder = new FormNameIndexBuilder(characterLimit: 5, entryLimit: 100, incompletePageLimit: 10);
        builder.Add(0, ["abc"], complete: true);
        Assert.False(builder.Overflowed);

        builder.Add(1, ["abc", "def"], complete: true);

        // Nothing is kept once the limit is crossed, and later pages add nothing.
        Assert.True(builder.Overflowed);
        Assert.Equal(0, builder.RetainedCharacters);
        Assert.Equal(0, builder.RetainedEntries);
        builder.Add(2, ["x"], complete: false);
        Assert.Equal(0, builder.RetainedEntries);
        var index = builder.Build();
        Assert.Null(index.Pages);
        Assert.Empty(index.IncompletePages);
    }

    [Fact]
    public void EntryLimitDropsEverythingAsSoonAsItIsCrossed()
    {
        var builder = new FormNameIndexBuilder(characterLimit: 1_000, entryLimit: 3, incompletePageLimit: 10);
        builder.Add(0, ["a", "b"], complete: true);
        builder.Add(1, ["a"], complete: true);
        Assert.False(builder.Overflowed);

        // The same name on another page is one more entry.
        builder.Add(2, ["a"], complete: true);

        Assert.True(builder.Overflowed);
        Assert.Equal(0, builder.RetainedEntries);
        Assert.Null(builder.Build().Pages);
    }

    [Fact]
    public void TooManyIncompletePagesMakeTheIndexUnusable()
    {
        var builder = new FormNameIndexBuilder(characterLimit: 1_000, entryLimit: 1_000, incompletePageLimit: 2);
        builder.Add(0, ["a"], complete: false);
        builder.Add(1, ["a"], complete: false);
        Assert.False(builder.Overflowed);

        builder.Add(2, ["a"], complete: false);

        Assert.True(builder.Overflowed);
        Assert.Null(builder.Build().Pages);
    }
}
