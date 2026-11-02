using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>The rule of Spec 3.3: which font sets a new text, and when it is refused as a whole.</summary>
public sealed class TextEditCheckTests
{
    private sealed class Chars(string present) : IFontCoverage
    {
        public bool Covers(int codePoint) => present.Contains(char.ConvertFromUtf32(codePoint), StringComparison.Ordinal);
    }

    private static TextEditDecision Check(string oldText, string newText, string fontHas, TextEditMode mode = TextEditMode.KeepFont) =>
        TextEditCheck.Evaluate(oldText, newText, new Chars(fontHas), mode);

    [Fact]
    public void SameTextIsUnchanged()
    {
        var decision = Check("Total 12", "Total 12", string.Empty);

        Assert.Equal(TextEditOutcome.Unchanged, decision.Outcome);
        Assert.Empty(decision.Missing);
    }

    [Fact]
    public void AllNewCharactersInTheFontKeepTheFont()
    {
        var decision = Check("Total 12", "Total 17", "Total 127");

        Assert.Equal(TextEditOutcome.KeepFont, decision.Outcome);
        Assert.Equal([new TextEditSegment("Total 17", false)], decision.Segments);
        Assert.Empty(decision.Missing);
        Assert.Empty(decision.Unsupported);
    }

    [Fact]
    public void MissingCharactersAreSetInGeistAndNamed()
    {
        var decision = Check("Total 12", "Total 1äß2", "Total 12");

        Assert.Equal(TextEditOutcome.SubstituteMissing, decision.Outcome);
        Assert.Equal(["ä", "ß"], decision.Missing);
        Assert.Equal([new TextEditSegment("Total 1", false), new TextEditSegment("äß", true), new TextEditSegment("2", false)], decision.Segments);
    }

    [Fact]
    public void WholeRunInGeistIsOfferedOnlyWhenEveryCharacterIsInGeist()
    {
        var offered = Check("Total 12", "Total 17 ä", "Total 12");
        var notOffered = Check("Total 12", "Total →", "Total 12");

        Assert.True(offered.WholeRunInGeistPossible);
        Assert.False(notOffered.WholeRunInGeistPossible);
        Assert.Equal(TextEditOutcome.WholeRunInGeist, Check("Total 12", "Total 17 ä", "Total 12", TextEditMode.WholeRunInGeist).Outcome);
        Assert.Equal(TextEditOutcome.Rejected, Check("Total 12", "Total →", "Total 12", TextEditMode.WholeRunInGeist).Outcome);
    }

    [Fact]
    public void AnExistingCharacterGeistLacksStaysInItsFont()
    {
        // "→" is in the run and in its font but not in Geist: a typo elsewhere is a plain edit, the run stays in its font.
        var decision = Check("A→B 1", "A→B 2", "A→B 12");

        Assert.Equal(TextEditOutcome.KeepFont, decision.Outcome);
        Assert.False(decision.WholeRunInGeistPossible);
    }

    [Fact]
    public void ACharacterInNeitherFontRejectsTheWholeText()
    {
        var decision = Check("Total 12", "Total 1ж", "Total 12");

        Assert.Equal(TextEditOutcome.Rejected, decision.Outcome);
        Assert.Equal(TextEditRejection.UnsupportedCharacters, decision.Rejection);
        Assert.Equal(["ж"], decision.Unsupported);
        Assert.Empty(decision.Segments);
    }

    [Fact]
    public void NonLatinRunIsEditableWhileItsFontCoversTheNewCharacters()
    {
        var decision = Check("привет", "приветы", "приветы");

        Assert.Equal(TextEditOutcome.KeepFont, decision.Outcome);
    }

    [Fact]
    public void EmptyNewTextIsARemoval()
    {
        var decision = Check("Total 12", string.Empty, "Total 12");

        Assert.Equal(TextEditOutcome.Remove, decision.Outcome);
        Assert.Equal(TextEditRejection.None, decision.Rejection);
    }

    [Fact]
    public void SurrogatePairsAndCombiningMarksAreOneElement()
    {
        Assert.Equal(["a", "\U0001F600", "é", "b"], TextEditCheck.Elements("a\U0001F600éb"));

        var decision = Check("ab", "a\U0001F600b", "ab");
        Assert.Equal(TextEditOutcome.Rejected, decision.Outcome);
        Assert.Equal(["\U0001F600"], decision.Unsupported);
    }

    [Fact]
    public void MoreThan2000CharactersIsRejected()
    {
        var long2001 = new string('a', TextEditLimits.MaxCharsPerRun + 1);
        var fits = new string('a', TextEditLimits.MaxCharsPerRun);

        Assert.Equal(TextEditRejection.TooLong, Check("a", long2001, "a").Rejection);
        Assert.Equal(TextEditOutcome.KeepFont, Check("a", fits, "a").Outcome);
    }

    [Fact]
    public void ACharacterTheOldTextAlreadyShowsIsNeverNew()
    {
        // The font reports nothing, but "x" is drawn in the run: moving it around needs no replacement.
        var decision = Check("xyz", "zyx", string.Empty);

        Assert.Equal(TextEditOutcome.KeepFont, decision.Outcome);
    }

    [Fact]
    public void AControlCharacterIsRefusedEvenIfTheFontClaimsIt()
    {
        var decision = Check("ab", "a\nb", "ab\n");

        Assert.Equal(TextEditOutcome.Rejected, decision.Outcome);
        Assert.Equal(["\n"], decision.Unsupported);
    }
}

/// <summary>The hint of Spec 3.4: a new box that overlaps other text or leaves the page.</summary>
public sealed class TextEditOverlapTests
{
    private static readonly PageRect Page = new(0, 0, 300, 200);

    [Fact]
    public void LongerTextOverlappingANeighbourIsFlagged()
    {
        var flags = TextEditOverlap.Find(new PageRect(20, 100, 180, 114), [new PageRect(150, 98, 170, 116)], Page);

        Assert.Equal(TextEditOverflow.OverlapsText, flags);
    }

    [Fact]
    public void TextOverTheRightPageEdgeIsOutsidePage()
    {
        var flags = TextEditOverlap.Find(new PageRect(20, 100, 320, 114), [], Page);

        Assert.Equal(TextEditOverflow.OutsidePage, flags);
    }

    [Fact]
    public void ShorterTextIsNone()
    {
        var flags = TextEditOverlap.Find(new PageRect(20, 100, 60, 114), [new PageRect(150, 98, 170, 116)], Page);

        Assert.Equal(TextEditOverflow.None, flags);
    }

    [Fact]
    public void TouchingBoxesAreNotOverlapping()
    {
        var flags = TextEditOverlap.Find(new PageRect(20, 100, 150, 114), [new PageRect(150, 100, 170, 114), new PageRect(20, 114, 100, 128)], Page);

        Assert.Equal(TextEditOverflow.None, flags);
    }

    [Fact]
    public void BothFlagsCanComeTogether()
    {
        var flags = TextEditOverlap.Find(new PageRect(250, 100, 340, 114), [new PageRect(280, 100, 290, 114)], Page);

        Assert.Equal(TextEditOverflow.OverlapsText | TextEditOverflow.OutsidePage, flags);
    }
}
