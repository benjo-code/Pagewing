using System.Globalization;
using System.Text;
using Pagewing.Core.Pages;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary><see cref="OutputNames"/>: file names made from document content (bookmark titles) are safe and unique.</summary>
public sealed class OutputNamesTests
{
    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("What? <is> \"this\": a|b*c", "What_ _is_ _this__ a_b_c")]
    [InlineData("", "part")]
    [InlineData("   ", "part")]
    [InlineData("...", "part")]
    [InlineData("..", "part")]
    [InlineData(".", "part")]
    [InlineData("name...", "name")]
    [InlineData("name   ", "name")]
    [InlineData("name. . .", "name")]
    [InlineData(" .hidden", "hidden")]
    [InlineData("a\tb\r\nc", "a b c")]
    [InlineData("a  \u00A0 b", "a b")]
    [InlineData("Kapitel 1", "Kapitel 1")]
    [InlineData("Übersicht – Größe", "Übersicht – Größe")]
    public void SanitizeRemovesSeparatorsReservedNamesAndControlCharacters(string title, string expected)
    {
        Assert.Equal(expected, OutputNames.Sanitize(title));
    }

    [Theory]
    [InlineData("..\\..\\x")]
    [InlineData("../../x")]
    [InlineData("C:\\Windows\\System32\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("x:stream")]
    public void SanitizeNeverLeavesAPathBehind(string title)
    {
        var name = OutputNames.Sanitize(title);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.NotEqual("..", name);
        Assert.NotEqual(".", name);
        Assert.NotEmpty(name);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("NUL.pdf")]
    [InlineData("nul.tar.gz")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9.txt")]
    [InlineData("COM\u00B9")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("NUL   ")]
    [InlineData("NUL . ")]
    public void SanitizeAvoidsReservedWindowsNames(string title)
    {
        var name = OutputNames.Sanitize(title);

        Assert.False(IsReservedOnWindows(name + ".pdf"), $"'{name}.pdf' is a reserved name.");
        Assert.NotEmpty(name);
    }

    [Fact]
    public void SanitizeRemovesControlAndBidiCharacters()
    {
        // NUL, other C0 controls, DEL, a C1 control, bidi overrides and isolates, zero width characters, the BOM, a soft hyphen.
        var title = "a\u0000b\u0001c\u001Fd\u007Fe\u0085f\u202Eg\u202Ah\u2066i\u2069j\u200Bk\u200Fl\uFEFFm\u00ADn\u2028o\u2029p";

        var name = OutputNames.Sanitize(title);

        foreach (var c in name)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            Assert.NotEqual(UnicodeCategory.Control, category);
            Assert.NotEqual(UnicodeCategory.Format, category);
            Assert.NotEqual(UnicodeCategory.LineSeparator, category);
            Assert.NotEqual(UnicodeCategory.ParagraphSeparator, category);
        }

        // Controls and line separators become one space, format characters (bidi, zero width, BOM, soft hyphen) vanish.
        Assert.Equal("a b c d e fghijklmn o p", name);
    }

    [Fact]
    public void SanitizeReplacesLoneSurrogates()
    {
        var name = OutputNames.Sanitize("a\uD800b\uDC00c");

        Assert.Equal("a_b_c", name);
        Assert.True(IsWellFormed(name));
    }

    [Fact]
    public void SanitizeLimitsTheLengthByTextElements()
    {
        // 100 emoji (surrogate pairs), each one text element.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        var cut = OutputNames.Sanitize(emoji, 10);
        Assert.Equal(10, TextElements(cut));
        Assert.True(IsWellFormed(cut));

        // A letter with combining marks is one element and never split.
        var accented = string.Concat(Enumerable.Repeat("e\u0301\u0302", 50));
        var shortened = OutputNames.Sanitize(accented, 7);
        Assert.Equal(7, TextElements(shortened));
        Assert.EndsWith("\u0302", shortened, StringComparison.Ordinal);
        Assert.True(IsWellFormed(shortened));

        // The default is 80, and a plain string is cut at the limit.
        Assert.Equal(80, OutputNames.Sanitize(new string('x', 5000)).Length);
        Assert.Equal("abc", OutputNames.Sanitize("abcdef", 3));
    }

    [Fact]
    public void SanitizeBoundsTheBytesOfOneHugeTextElement()
    {
        // A single letter followed by thousands of combining marks is one text element: it must not become a huge name.
        var hostile = "e" + string.Concat(Enumerable.Repeat("\u0301", 3000));

        var name = OutputNames.Sanitize(hostile);

        Assert.True(Encoding.UTF8.GetByteCount(name) <= 200, "The name stays far below the 255 bytes of a file system.");
        Assert.NotEmpty(name);
    }

    [Fact]
    public void SanitizeKeepsWideTextAtTheDefaultLimitUnder160Bytes()
    {
        var cjk = OutputNames.Sanitize(string.Concat(Enumerable.Repeat("\u6F22", 80)));
        var emoji = OutputNames.Sanitize(string.Concat(Enumerable.Repeat("\U0001F600", 80)));

        Assert.InRange(Encoding.UTF8.GetByteCount(cjk), 1, 160);
        Assert.InRange(Encoding.UTF8.GetByteCount(emoji), 1, 160);
        Assert.Equal(53, TextElements(cjk));
        Assert.Equal(40, TextElements(emoji));
        Assert.True(IsWellFormed(emoji));
    }

    [Fact]
    public void SanitizeHonoursASmallerLimitForNamesWithASuffix()
    {
        // The base name of a generated stem leaves room for its suffix.
        Assert.Equal(64, OutputNames.Sanitize(new string('a', 100), 64, 144).Length);
        Assert.Equal(48, TextElements(OutputNames.Sanitize(string.Concat(Enumerable.Repeat("\u6F22", 100)), 64, 144)));
    }

    [Fact]
    public void SanitizeKeepsTheLimitEvenWhenAReservedNameNeedsAPrefix()
    {
        var name = OutputNames.Sanitize("NUL", 3);

        Assert.True(TextElements(name) <= 3);
        Assert.False(IsReservedOnWindows(name + ".pdf"));
    }

    [Fact]
    public void SanitizeRejectsNullAndAnEmptyLimit()
    {
        Assert.Throws<ArgumentNullException>(() => OutputNames.Sanitize(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputNames.Sanitize("a", 0));
    }

    [Fact]
    public void UniqueNumbersEqualAndExistingNamesAndNeverOverwrites()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kapitel.pdf", "Kapitel (2).pdf", "other.pdf" };

        var names = OutputNames.Unique(["Kapitel", "kapitel", "Intro", "INTRO", "Intro", "other", "x"], ".pdf", existing.Contains);

        Assert.Equal(7, names.Count);
        Assert.Equal(["Kapitel (3).pdf", "kapitel (4).pdf", "Intro.pdf", "INTRO (2).pdf", "Intro (3).pdf", "other (2).pdf", "x.pdf"], names);
        Assert.Equal(names.Count, names.Select(n => n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.DoesNotContain(name, existing));
    }

    [Fact]
    public void UniqueSanitizesEveryStemAndKeepsTheExtension()
    {
        var names = OutputNames.Unique(["..\\..\\x", "a/b", "CON", "NUL.pdf", "aux", "", "name..."], ".pdf", _ => false);

        Assert.Equal(7, names.Count);
        foreach (var name in names)
        {
            Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain(':', name);
            Assert.False(IsReservedOnWindows(name), $"'{name}' is a reserved name.");
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UniqueMakesEqualTitlesDifferentEvenWhenTheyOnlyDifferInCase()
    {
        var names = OutputNames.Unique(["Same", "same", "SAME", "Same"], ".pdf", _ => false);

        Assert.Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UniqueRefusesAnExtensionThatIsNoExtension()
    {
        Assert.Throws<ArgumentException>(() => OutputNames.Unique(["a"], "pdf\\..\\x", _ => false));
        Assert.Throws<ArgumentException>(() => OutputNames.Unique(["a"], ".p/df", _ => false));
        Assert.Throws<ArgumentException>(() => OutputNames.Unique(["a"], "..", _ => false));
    }

    [Fact]
    public void UniqueGivesUpWhenEverythingExists()
    {
        Assert.Throws<InvalidOperationException>(() => OutputNames.Unique(["a"], ".pdf", _ => true));
    }

    [Fact]
    public void UniqueOfNothingIsEmpty()
    {
        Assert.Empty(OutputNames.Unique([], ".pdf", _ => false));
    }

    private static int TextElements(string text) => new StringInfo(text).LengthInTextElements;

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    // The documented rule of Windows: the part before the first dot, without trailing spaces, names a device.
    private static bool IsReservedOnWindows(string fileName)
    {
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        string[] fixedNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"];
        if (fixedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3');
    }
}
