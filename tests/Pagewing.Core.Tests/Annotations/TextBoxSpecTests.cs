using System.Buffers.Binary;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The checks of a text box (<see cref="TextBoxSpec"/>) and its character set (<see cref="TextBoxCharacters"/>), milestone 10.</summary>
public sealed class TextBoxSpecTests
{
    private static readonly PageRect Box = new(20, 30, 180, 78);

    private static readonly AnnotationColor Blue = new(30, 80, 220);

    [Fact]
    public void NormalizeTurnsEveryLineBreakIntoALineFeed()
    {
        Assert.Equal("a\nb\nc\nd", TextBoxSpec.NormalizeText("a\r\nb\rc\nd"));
        Assert.Equal("a\nb\nc", TextBoxSpec.NormalizeText("a" + (char)0x2028 + "b" + (char)0x2029 + "c"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n \n")]
    public void EmptyOrBlankTextIsRefused(string text) =>
        Assert.Equal(TextBoxTextProblem.Empty, TextBoxSpec.CheckText(text).Problem);

    [Fact]
    public void TooLongTextIsRefused()
    {
        Assert.Equal(TextBoxTextProblem.None, TextBoxSpec.CheckText(new string('a', TextBoxSpec.MaxTextLength)).Problem);
        Assert.Equal(TextBoxTextProblem.TooLong, TextBoxSpec.CheckText(new string('a', TextBoxSpec.MaxTextLength + 1)).Problem);
    }

    [Fact]
    public void TooManyLinesAreRefused()
    {
        var lines = string.Join('\n', Enumerable.Repeat("a", TextBoxSpec.MaxLines));
        Assert.Equal(TextBoxTextProblem.None, TextBoxSpec.CheckText(lines).Problem);
        Assert.Equal(TextBoxTextProblem.TooManyLines, TextBoxSpec.CheckText(lines + "\na").Problem);
    }

    [Fact]
    public void GermanUmlautsEuroAndCentralEuropeanLettersAreSupported()
    {
        Assert.Equal(TextBoxTextProblem.None, TextBoxSpec.CheckText("Grüße aus Köln – 5 €\nŁódź, Plzeň, Győr; ẞ «» © ½ µ").Problem);
    }

    [Fact]
    public void UnsupportedCharactersAreNamedOnceEachInTheirOrder()
    {
        var check = TextBoxSpec.CheckText("Ωmega 😀 and Ω again\tand ĕ");

        Assert.Equal(TextBoxTextProblem.UnsupportedCharacters, check.Problem);
        Assert.Equal(["Ω", "😀", "\t", "ĕ"], check.Unsupported);
    }

    [Fact]
    public void ALoneSurrogateIsUnsupported()
    {
        var check = TextBoxSpec.CheckText("a\uD800b");

        Assert.Equal(TextBoxTextProblem.UnsupportedCharacters, check.Problem);
        Assert.Equal(["\uD800"], check.Unsupported);
    }

    [Fact]
    public void AtMostEightUnsupportedCharactersAreNamed()
    {
        var check = TextBoxSpec.CheckText("ΑΒΓΔΕΖΗΘΙΚ");

        Assert.Equal(8, check.Unsupported.Count);
    }

    [Fact]
    public void ValidateAcceptsAGoodSpec() =>
        new TextBoxSpec(Box, "Hello\nWorld", 12, Blue).Validate();

    [Theory]
    [InlineData(5.99)]
    [InlineData(96.01)]
    [InlineData(double.NaN)]
    public void ValidateRefusesAFontSizeOutsideTheRange(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextBoxSpec(Box, "a", size, Blue).Validate());

    [Fact]
    public void ValidateRefusesASmallOrDamagedBox()
    {
        Assert.Throws<ArgumentException>(() => new TextBoxSpec(new PageRect(0, 0, 3.9, 50), "a", 12, Blue).Validate());
        Assert.Throws<ArgumentException>(() => new TextBoxSpec(new PageRect(0, 0, 50, 3.9), "a", 12, Blue).Validate());
        Assert.Throws<ArgumentException>(() => new TextBoxSpec(new PageRect(0, double.NaN, 50, 50), "a", 12, Blue).Validate());
        new TextBoxSpec(new PageRect(0, 0, 4, 4), "a", 12, Blue).Validate();
    }

    [Fact]
    public void ValidateRefusesBadText()
    {
        Assert.Throws<ArgumentException>(() => new TextBoxSpec(Box, " ", 12, Blue).Validate());
        Assert.Throws<ArgumentException>(() => new TextBoxSpec(Box, "Ω", 12, Blue).Validate());
        Assert.Throws<ArgumentNullException>(() => new TextBoxSpec(Box, null!, 12, Blue).Validate());
    }

    [Fact]
    public void FontSizesOfTheToolAreInTheRange()
    {
        Assert.Equal([12d, 16d, 24d], TextBoxSpec.ToolFontSizes);
        Assert.All(TextBoxSpec.ToolFontSizes, size => Assert.InRange(size, TextBoxSpec.MinFontSize, TextBoxSpec.MaxFontSize));
    }

    [Fact]
    public void TheSupportedSetIsExactlyWhatTheShippedGeistHasOfLatinOneEuroAndCentralEurope()
    {
        // Every supported character must have its own glyph in the shipped font: a missing one would make PDFium fall back to
        // a system font, which differs between machines.
        var covered = CmapOf(Path.Combine(TestData.Root, "src", "Pagewing.App", "Assets", "Fonts", "Geist-Regular.ttf"));
        var wanted = new HashSet<int>();
        for (var c = 0x20; c <= 0x17F; c++)
        {
            if (c is < 0x7F or >= 0xA0)
            {
                wanted.Add(c);
            }
        }

        foreach (var c in (ReadOnlySpan<int>)[0x1E9E, 0x2013, 0x2014, 0x2018, 0x2019, 0x201A, 0x201C, 0x201D, 0x201E, 0x2026, 0x20AC])
        {
            wanted.Add(c);
        }

        wanted.IntersectWith(covered);
        Assert.Equal(wanted.Order(), TextBoxCharacters.All.Select(c => (int)c).Order());
        Assert.Contains('€', TextBoxCharacters.All);
        Assert.Contains('ü', TextBoxCharacters.All);
        Assert.DoesNotContain((char)0xAD, TextBoxCharacters.All);
        Assert.All(TextBoxCharacters.All, c => Assert.True(TextBoxCharacters.IsSupported(c)));
        Assert.False(TextBoxCharacters.IsSupported('\n'));
    }

    /// <summary>The code points the Unicode cmap (format 4) of a TrueType font maps to a glyph other than 0.</summary>
    private static HashSet<int> CmapOf(string path)
    {
        var data = File.ReadAllBytes(path);
        int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        var cmap = -1;
        for (var i = 0; i < U16(4); i++)
        {
            var record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(data, record, 4) == "cmap")
            {
                cmap = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
            }
        }

        Assert.True(cmap > 0, "The font has no cmap.");
        var result = new HashSet<int>();
        for (var i = 0; i < U16(cmap + 2); i++)
        {
            var (platform, encoding) = (U16(cmap + 4 + (i * 8)), U16(cmap + 6 + (i * 8)));
            var table = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cmap + 8 + (i * 8)));
            if (platform != 3 || encoding != 1 || U16(table) != 4)
            {
                continue;
            }

            var segments = U16(table + 6) / 2;
            var ends = table + 14;
            var starts = ends + (segments * 2) + 2;
            var deltas = starts + (segments * 2);
            var ranges = deltas + (segments * 2);
            for (var s = 0; s < segments; s++)
            {
                var (start, end, delta, range) = (U16(starts + (s * 2)), U16(ends + (s * 2)), (short)U16(deltas + (s * 2)), U16(ranges + (s * 2)));
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    var glyph = range == 0 ? (c + delta) & 0xFFFF : U16(ranges + (s * 2) + range + ((c - start) * 2));
                    if (range != 0 && glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }

                    if (glyph != 0)
                    {
                        result.Add(c);
                    }
                }
            }
        }

        return result;
    }
}
