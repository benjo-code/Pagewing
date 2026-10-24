using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationExcerptTests
{
    private const double PageHeight = 100;

    /// <summary>Lays out lines of text: 10 points per character, 12 points per line, the first line from y = 10 downwards.</summary>
    private static PageText Page(params string[] lines)
    {
        var chars = new List<TextChar>();
        var top = 10.0;
        foreach (var line in lines)
        {
            var left = 10.0;
            foreach (var ch in line)
            {
                var generated = ch == ' ' ? TextCharTraits.Generated : TextCharTraits.None;
                chars.Add(new TextChar(ch, new PageRect(left, top, left + 10, top + 12), generated, 12));
                left += 10;
            }

            chars.Add(new TextChar('\r', new PageRect(left, top, left, top), TextCharTraits.Generated, 0));
            chars.Add(new TextChar('\n', new PageRect(left, top, left, top), TextCharTraits.Generated, 0));
            top += 12;
        }

        return new PageText(0, 300, PageHeight, chars, []);
    }

    /// <summary>The quad of a rectangle given in page points (origin top left), as the file stores it (origin bottom left).</summary>
    private static Quad QuadOf(double left, double top, double right, double bottom) => MarkupGeometry.ToQuad(
        new PagePoint(left, PageHeight - top),
        new PagePoint(right, PageHeight - top),
        new PagePoint(right, PageHeight - bottom),
        new PagePoint(left, PageHeight - bottom));

    private static PdfAnnotation Annotation(params Quad[] quads) => new(
        0, 0, null, AnnotationKind.Highlight, "Highlight", null, quads, new PageRect(0, 0, 0, 0), null, null, null, true);

    [Fact]
    public void ExcerptReadsTheTextUnderTheQuadsAndShortens()
    {
        // "Hello big world" on line one, "second line" on line two.
        var text = Page("Hello big world", "second line");
        var lineOne = QuadOf(10, 10, 160, 22);
        var lineTwo = QuadOf(10, 22, 70, 34);

        Assert.Equal("Hello big world second", AnnotationExcerpt.For(Annotation(lineOne, lineTwo), text));
        Assert.Equal("big wo", AnnotationExcerpt.For(Annotation(QuadOf(60, 10, 130, 22)), text));
        Assert.Equal("Hello big…", AnnotationExcerpt.For(Annotation(lineOne, lineTwo), text, 11));
        Assert.Equal("…", AnnotationExcerpt.For(Annotation(lineOne), text, 1));
    }

    [Fact]
    public void ExcerptOfAnotherLineIsNotPulledIn()
    {
        var text = Page("Hello big world", "second line");

        Assert.Equal("second", AnnotationExcerpt.For(Annotation(QuadOf(10, 22, 70, 34)), text));
    }

    [Fact]
    public void ExcerptFallsBackToTheBoundsWithoutQuads()
    {
        var text = Page("Hello big world");
        var annotation = Annotation() with { Bounds = new PageRect(10, 10, 60, 22) };

        Assert.Equal("Hello", AnnotationExcerpt.For(annotation, text));
    }

    [Fact]
    public void ExcerptIsEmptyWhereThereIsNoText()
    {
        var text = Page("Hello");

        Assert.Equal(string.Empty, AnnotationExcerpt.For(Annotation(QuadOf(200, 50, 250, 62)), text));
        Assert.Equal(string.Empty, AnnotationExcerpt.For(Annotation(), text));
    }

    [Fact]
    public void ExcerptNeverSplitsASurrogatePair()
    {
        var text = Page("ab");
        var chars = text.Chars.ToList();
        chars.Insert(1, new TextChar(0x1F600, new PageRect(20, 10, 30, 22), TextCharTraits.None, 12));
        var withEmoji = new PageText(0, 300, PageHeight, chars, []);

        // "a😀b" is four UTF-16 units; a maximum of 3 leaves room for "a" plus the ellipsis only.
        Assert.Equal("a…", AnnotationExcerpt.For(Annotation(QuadOf(10, 10, 160, 22)), withEmoji, 3));
    }
}
