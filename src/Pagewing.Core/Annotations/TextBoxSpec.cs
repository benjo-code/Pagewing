using System.Globalization;
using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>What is wrong with the text of a text box (<see cref="TextBoxSpec.CheckText"/>).</summary>
public enum TextBoxTextProblem
{
    /// <summary>Nothing: the text can be shown.</summary>
    None,

    /// <summary>The text is empty or only white space; such a text box is never created.</summary>
    Empty,

    /// <summary>The text has more than <see cref="TextBoxSpec.MaxTextLength"/> characters.</summary>
    TooLong,

    /// <summary>The text has more than <see cref="TextBoxSpec.MaxLines"/> lines.</summary>
    TooManyLines,

    /// <summary>The text has characters outside <see cref="TextBoxCharacters"/>.</summary>
    UnsupportedCharacters,
}

/// <summary>The result of <see cref="TextBoxSpec.CheckText"/>.</summary>
/// <param name="Problem">What is wrong, or <see cref="TextBoxTextProblem.None"/>.</param>
/// <param name="Unsupported">The unsupported characters (<see cref="TextBoxCharacters.UnsupportedIn"/>); empty for the other problems.</param>
public sealed record TextBoxTextCheck(TextBoxTextProblem Problem, IReadOnlyList<string> Unsupported);

/// <summary>
/// A text box as Pagewing creates it: a FreeText annotation whose appearance Pagewing draws from the glyph outlines of
/// Geist Regular, left aligned and wrapped to the width of the box (<see cref="TextBoxLayout"/>). See
/// <c>Docs/DECISIONS.md</c>, entries "Textfeld: Erscheinungsbild und Entscheidung (M10)" and "Textfeld: Umsetzung und
/// Grenzen (M10)".
/// </summary>
/// <param name="Box">The box in page points from the top left of the displayed page (after rotation and CropBox).</param>
/// <param name="Text">The text, with line feeds as line breaks (see <see cref="NormalizeText"/>); it is also the comment (<c>/Contents</c>).</param>
/// <param name="FontSize">The font size in points.</param>
/// <param name="Color">The colour of the text.</param>
public sealed record TextBoxSpec(PageRect Box, string Text, double FontSize, AnnotationColor Color)
{
    /// <summary>The smallest font size, in points.</summary>
    public const double MinFontSize = 6;

    /// <summary>The largest font size, in points.</summary>
    public const double MaxFontSize = 96;

    /// <summary>The font size a text box of another program gets when its <c>/DA</c> names none (the PDF default of a text field is 12 pt).</summary>
    public const double DefaultFontSize = 12;

    /// <summary>The most characters of a text (line feeds included); about 200 bytes of appearance each, written again on every save.</summary>
    public const int MaxTextLength = 2_000;

    /// <summary>The most lines (line feeds plus one); wrapping may make more of them.</summary>
    public const int MaxLines = 200;

    /// <summary>The smallest side of the box, in points.</summary>
    public const double MinSide = 4;

    /// <summary>The width of a text box made with a click instead of a drag, in points.</summary>
    public const double DefaultWidth = 160;

    /// <summary>The height of a text box made with a click instead of a drag, in points.</summary>
    public const double DefaultHeight = 48;

    /// <summary>Gets the three font sizes the text box tool offers (small, medium, large), in points.</summary>
    public static IReadOnlyList<double> ToolFontSizes { get; } = [12, 16, 24];

    /// <summary>
    /// Gets the font size of a size step of the text box tool: its style keeps the step in the place of a stroke width
    /// (<see cref="ToolStyle.Width"/>), thin for small (12 pt), medium (16 pt) and thick for large (24 pt).
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The font size in points.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not a defined step.</exception>
    public static double FontSizeOf(StrokeWidth step) => step switch
    {
        StrokeWidth.Thin => ToolFontSizes[0],
        StrokeWidth.Medium => ToolFontSizes[1],
        StrokeWidth.Thick => ToolFontSizes[2],
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>Gets the size step of a font size, or <see langword="null"/> for a size the tool does not offer.</summary>
    /// <param name="fontSize">The font size in points.</param>
    /// <returns>The step.</returns>
    public static StrokeWidth? StepOf(double fontSize) =>
        fontSize == ToolFontSizes[0] ? StrokeWidth.Thin : fontSize == ToolFontSizes[1] ? StrokeWidth.Medium : fontSize == ToolFontSizes[2] ? StrokeWidth.Thick : null;

    /// <summary>Turns every line break (CR LF, CR, U+2028, U+2029) into a line feed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with line feeds only.</returns>
    public static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace((char)0x2028, '\n')
            .Replace((char)0x2029, '\n');
    }

    /// <summary>Checks whether a text can be a text box: not blank, not too long, not too many lines, only supported characters. Line breaks are normalized first.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The first problem found, with the unsupported characters if that is it.</returns>
    public static TextBoxTextCheck CheckText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new TextBoxTextCheck(TextBoxTextProblem.Empty, []);
        }

        if (normalized.Length > MaxTextLength)
        {
            return new TextBoxTextCheck(TextBoxTextProblem.TooLong, []);
        }

        if (normalized.AsSpan().Count('\n') + 1 > MaxLines)
        {
            return new TextBoxTextCheck(TextBoxTextProblem.TooManyLines, []);
        }

        var unsupported = TextBoxCharacters.UnsupportedIn(normalized);
        return unsupported.Count > 0
            ? new TextBoxTextCheck(TextBoxTextProblem.UnsupportedCharacters, unsupported)
            : new TextBoxTextCheck(TextBoxTextProblem.None, []);
    }

    /// <summary>Checks the whole text box: a finite box of at least <see cref="MinSide"/> per side, a font size from <see cref="MinFontSize"/> to <see cref="MaxFontSize"/>, and a text <see cref="CheckText"/> accepts.</summary>
    /// <exception cref="ArgumentNullException">The text is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The font size is outside the range or not a number.</exception>
    /// <exception cref="ArgumentException">The box is too small or not finite, or the text is refused.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Text, nameof(Text));
        if (!(FontSize >= MinFontSize && FontSize <= MaxFontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(FontSize), FontSize, string.Create(CultureInfo.InvariantCulture, $"The font size must be from {MinFontSize} to {MaxFontSize} points."));
        }

        if (!double.IsFinite(Box.Left) || !double.IsFinite(Box.Top) || !double.IsFinite(Box.Right) || !double.IsFinite(Box.Bottom)
            || !(Box.Width >= MinSide) || !(Box.Height >= MinSide))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The box of a text box must be finite and at least {MinSide} points on each side."), nameof(Box));
        }

        var check = CheckText(Text);
        if (check.Problem != TextBoxTextProblem.None)
        {
            var detail = check.Problem == TextBoxTextProblem.UnsupportedCharacters
                ? ": " + string.Join(", ", check.Unsupported.Select(c => string.Create(
                    CultureInfo.InvariantCulture, $"U+{(c.Length > 1 && char.IsSurrogatePair(c, 0) ? char.ConvertToUtf32(c, 0) : c[0]):X4}")))
                : string.Empty;
            throw new ArgumentException($"The text of the text box is refused ({check.Problem}){detail}.", nameof(Text));
        }
    }
}
