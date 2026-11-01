using System.Text.RegularExpressions;
using Pagewing.Core.Redaction;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The causes of a page that cannot be redacted, and the kinds of what the check did not check (milestone 14, task 13, fix round 1):
/// the interface says both in its own language from a kind and numbers, so every sentence the engine can produce must have a kind. The
/// sentences of the engine are read from its source files; a new one without a group fails here instead of showing English in a German
/// card.
/// </summary>
public sealed partial class RedactionPageCausesTests
{
    [Fact]
    public void EveryReasonTheEngineCanGiveIsInAGroup()
    {
        var literals = new List<string>();
        foreach (var file in new[] { "PdfiumRedactionRun.cs", "PdfiumRedactionRun.Objects.cs" })
        {
            var source = File.ReadAllText(Path.Combine(TestData.Root, "src", "Pagewing.Pdfium", file));
            foreach (Match match in RefusalAssignment().Matches(source))
            {
                // An interpolated sentence: its numbers stand in as one digit.
                literals.Add(InterpolatedPart().Replace(match.Groups["text"].Value, "1"));
            }
        }

        // The sentences were found (a renamed field would make this test look at nothing).
        Assert.True(literals.Count >= 28, $"{literals.Count} sentences found");
        foreach (var reason in literals)
        {
            Assert.NotEqual(RedactionPageCause.Unknown, RedactionPageCauses.Classify(reason));
        }
    }

    [Theory]
    [InlineData("The page looks different outside the marked areas after it is written again (12 pixels, around x 1 to 2 pt and y 3 to 4 pt from the top left): PDFium cannot write this page without changing it.", RedactionPageCause.ChangesOutsideAreas)]
    [InlineData("The page changed its size when it was written again.", RedactionPageCause.SizeChanged)]
    [InlineData("The page could not be rendered for the check, so it cannot be shown that nothing outside the marked areas changed.", RedactionPageCause.RenderFailed)]
    [InlineData("The rewritten document could not be opened again for the check (NotAPdf).", RedactionPageCause.ReopenFailed)]
    [InlineData("The page has 3 annotations; at most 2 are handled.", RedactionPageCause.AnnotationsTooComplex)]
    [InlineData("The page has 3 objects; at most 2 are handled.", RedactionPageCause.TooLarge)]
    [InlineData("A shape that covers more than half of the page and is not a plain filled rectangle is under a rectangle; it cannot be cut and is never removed as a whole.", RedactionPageCause.BigShape)]
    [InlineData("Something nobody wrote a group for.", RedactionPageCause.Unknown)]
    [InlineData("", RedactionPageCause.Unknown)]
    public void TheReasonsOfThePdfDocumentCheckHaveTheirGroup(string reason, RedactionPageCause expected) =>
        Assert.Equal(expected, RedactionPageCauses.Classify(reason));

    [Fact]
    public void TheRenderCheckAndTheReopenCheckSayWhatTheClassifierReads()
    {
        // The three sentences of PdfDocument.Redaction.cs (the check of the rewritten document) are read by the classifier; the source
        // is the witness that their start has not changed.
        var source = File.ReadAllText(Path.Combine(TestData.Root, "src", "Pagewing.Core", "Pdf", "PdfDocument.Redaction.cs"));
        foreach (var start in new[]
        {
            "The rewritten document could not be opened again for the check",
            "The page changed its size when it was written again.",
            "The page looks different outside the marked areas after it is written again",
            "The page could not be rendered for the check",
        })
        {
            Assert.Contains(start, source, StringComparison.Ordinal);
            Assert.NotEqual(RedactionPageCause.Unknown, RedactionPageCauses.Classify(start));
        }
    }

    [Fact]
    public void TheApplyLinesOfWhatIsNotCheckedComeAsDataWithTheSameText()
    {
        Assert.Equal(RedactionReportText.ApplyNotChecked, RedactionReportText.ApplyNotCheckedItems.Select(static item => item.Text));
        Assert.All(RedactionReportText.ApplyNotCheckedItems, static item => Assert.NotEqual(RedactionNotCheckedKind.Other, item.Kind));
    }

    [GeneratedRegex(@"\.Refusal\s*(\?\?)?=\s*\$?""(?<text>(?:[^""\\]|\\.)*)""\s*;")]
    private static partial Regex RefusalAssignment();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex InterpolatedPart();
}
