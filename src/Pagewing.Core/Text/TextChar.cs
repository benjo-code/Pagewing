using System.Text;

namespace Pagewing.Core.Text;

/// <summary>Properties of a <see cref="TextChar"/> (named "Traits" because CA1711 forbids the suffix "Flags" for an enum).</summary>
[Flags]
public enum TextCharTraits
{
    /// <summary>No property.</summary>
    None = 0,

    /// <summary>
    /// Inserted by the PDF engine (a space or a line break), not drawn by the page. A line break is a CR LF pair
    /// whose boxes have no area.
    /// </summary>
    Generated = 1,

    /// <summary>A hyphen at the end of a line; the next character starts the next line, with no line break between.</summary>
    Hyphen = 2,

    /// <summary>The font is bold.</summary>
    Bold = 4,

    /// <summary>The font is italic.</summary>
    Italic = 8,
}

/// <summary>A character of a page at the index the PDF engine reports it.</summary>
/// <param name="CodePoint">The Unicode scalar value; U+FFFD if the engine has none.</param>
/// <param name="Box">The glyph box in page points.</param>
/// <param name="Flags">Generated, hyphen, bold, italic.</param>
/// <param name="FontSize">The font size in points (the engine reports 1 for generated characters).</param>
public readonly record struct TextChar(int CodePoint, PageRect Box, TextCharTraits Flags, float FontSize)
{
    /// <summary>Gets whether the engine inserted the character (a space or a line break).</summary>
    public bool IsGenerated => (Flags & TextCharTraits.Generated) != 0;

    /// <summary>Gets whether the character is a generated carriage return or line feed.</summary>
    public bool IsLineBreak => IsGenerated && CodePoint is '\r' or '\n';

    /// <summary>Gets the character as a string; U+FFFD if <see cref="CodePoint"/> is not a Unicode scalar value.</summary>
    public string Text => Rune.IsValid(CodePoint) ? char.ConvertFromUtf32(CodePoint) : Rune.ReplacementChar.ToString();
}
