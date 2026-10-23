using Pagewing.Core.Pdf;

namespace Pagewing.Core.Printing;

/// <summary>The printable area of the paper, in points (1/72 inch), as the printer reports it.</summary>
/// <param name="Width">Width of the printable area.</param>
/// <param name="Height">Height of the printable area.</param>
public readonly record struct PaperArea(double Width, double Height);

/// <summary>
/// Where and how large a page lands on the paper. Coordinates are points from the top left of the printable area
/// (the origin of a printer device context); <see cref="X"/> and <see cref="Y"/> are negative when the page is larger
/// than the area and gets cut off.
/// </summary>
/// <param name="X">Left edge of the page.</param>
/// <param name="Y">Top edge of the page.</param>
/// <param name="Width">Width of the page on the paper, after the turn.</param>
/// <param name="Height">Height of the page on the paper, after the turn.</param>
/// <param name="QuarterTurns">0, or 1 when the page is turned by 90° clockwise because it fits the paper better that way.</param>
/// <param name="Clipped">Whether part of the page lies outside the printable area.</param>
public readonly record struct PrintPlacement(double X, double Y, double Width, double Height, int QuarterTurns, bool Clipped)
{
    private const double ClipTolerance = 0.01;
    private const double PointsPerInch = 72;

    /// <summary>Converts the placement to device units of a printer.</summary>
    /// <param name="dpiX">Horizontal resolution of the printer in dots per inch.</param>
    /// <param name="dpiY">Vertical resolution of the printer in dots per inch.</param>
    /// <exception cref="ArgumentOutOfRangeException">A resolution is not positive and finite.</exception>
    public DevicePlacement ToDevice(double dpiX, double dpiY)
    {
        if (!(double.IsFinite(dpiX) && dpiX > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(dpiX), dpiX, "The resolution must be positive.");
        }

        if (!(double.IsFinite(dpiY) && dpiY > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(dpiY), dpiY, "The resolution must be positive.");
        }

        var scaleX = dpiX / PointsPerInch;
        var scaleY = dpiY / PointsPerInch;
        return new DevicePlacement(
            (int)Math.Round(X * scaleX),
            (int)Math.Round(Y * scaleY),
            Math.Max(1, (int)Math.Round(Width * scaleX)),
            Math.Max(1, (int)Math.Round(Height * scaleY)),
            QuarterTurns);
    }

    /// <summary>Places <paramref name="page"/> on <paramref name="paper"/>.</summary>
    /// <param name="page">The page as shown (already rotated by its own <c>/Rotate</c>).</param>
    /// <param name="paper">The printable area.</param>
    /// <param name="scaling">Fit to the area, or 100 %.</param>
    /// <param name="autoRotate">Whether to turn a landscape page onto portrait paper and the other way round.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page or the area has no positive, finite size.</exception>
    public static PrintPlacement Plan(PdfPageSize page, PaperArea paper, PrintScaling scaling, bool autoRotate = true)
    {
        if (!(double.IsFinite(page.Width) && page.Width > 0 && double.IsFinite(page.Height) && page.Height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "The page needs a positive size.");
        }

        if (!(double.IsFinite(paper.Width) && paper.Width > 0 && double.IsFinite(paper.Height) && paper.Height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(paper), paper, "The printable area needs a positive size.");
        }

        var pageLandscape = page.Width > page.Height;
        var pagePortrait = page.Height > page.Width;
        var paperLandscape = paper.Width > paper.Height;
        var paperPortrait = paper.Height > paper.Width;
        var turn = autoRotate && ((pageLandscape && paperPortrait) || (pagePortrait && paperLandscape));

        var width = turn ? page.Height : page.Width;
        var height = turn ? page.Width : page.Height;
        if (scaling == PrintScaling.FitToPaper)
        {
            var scale = Math.Min(paper.Width / width, paper.Height / height);
            width *= scale;
            height *= scale;
        }

        var clipped = width > paper.Width + ClipTolerance || height > paper.Height + ClipTolerance;
        return new PrintPlacement((paper.Width - width) / 2, (paper.Height - height) / 2, width, height, turn ? 1 : 0, clipped);
    }
}

/// <summary>A <see cref="PrintPlacement"/> in device units (pixels of the printer), as <c>FPDF_RenderPage</c> takes it.</summary>
/// <param name="X">Left edge of the display area.</param>
/// <param name="Y">Top edge of the display area.</param>
/// <param name="Width">Width of the display area, after the turn.</param>
/// <param name="Height">Height of the display area, after the turn.</param>
/// <param name="QuarterTurns">0, or 1 for a turn by 90° clockwise.</param>
public readonly record struct DevicePlacement(int X, int Y, int Width, int Height, int QuarterTurns);
