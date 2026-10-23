using System.Globalization;

namespace Pagewing.Core.Pdf;

/// <summary>Describes a page size as a paper format and millimetres.</summary>
public static class PageFormat
{
    private const double PointsToMillimetres = 25.4 / 72;
    private const double ToleranceMillimetres = 1.5;

    // Short side first, in millimetres. Letter and Legal are 8.5 x 11 and 8.5 x 14 inches.
    private static readonly (string Name, double Short, double Long)[] Formats =
    [
        ("A3", 297, 420),
        ("A4", 210, 297),
        ("A5", 148, 210),
        ("A6", 105, 148),
        ("Letter", 215.9, 279.4),
        ("Legal", 215.9, 355.6),
    ];

    /// <summary>
    /// Gets <c>A4 · 210 × 297 mm</c> for a known format (A3 to A6, Letter, Legal; within 1.5 mm on both sides,
    /// portrait or landscape, in the orientation of the page) and <c>200 × 300 mm</c> otherwise. Millimetres are
    /// rounded to whole numbers and formatted with <paramref name="culture"/>.
    /// </summary>
    /// <returns>The description, or an empty string when the size is not finite and positive.</returns>
    public static string Describe(PdfPageSize size, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            return string.Empty;
        }

        var width = size.Width * PointsToMillimetres;
        var height = size.Height * PointsToMillimetres;
        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        var landscape = width > height;

        foreach (var (name, formatShort, formatLong) in Formats)
        {
            if (Math.Abs(shortSide - formatShort) <= ToleranceMillimetres && Math.Abs(longSide - formatLong) <= ToleranceMillimetres)
            {
                var (nominalWidth, nominalHeight) = landscape ? (formatLong, formatShort) : (formatShort, formatLong);
                return $"{name} · {Millimetres(nominalWidth, nominalHeight, culture)}";
            }
        }

        return Millimetres(width, height, culture);
    }

    private static string Millimetres(double width, double height, CultureInfo culture) =>
        string.Create(culture, $"{Round(width):N0} × {Round(height):N0} mm");

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
