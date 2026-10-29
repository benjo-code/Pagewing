using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.Core.Signatures;

/// <summary>Where and how large a signature lands on a page when it is placed.</summary>
public static class SignaturePlacement
{
    /// <summary>
    /// The box of a signature placed with its centre at <paramref name="centre"/>: <see cref="SignatureLimits.StartWidthPoints"/>
    /// wide, at most <see cref="SignatureLimits.MaxStartWidthFraction"/> of the page width, the aspect ratio fixed, and wholly on
    /// the page (scaled down if even that does not fit, moved in at an edge). A centre that is not finite is the page's.
    /// </summary>
    /// <param name="aspect">Width divided by height of the signature, above 0.</param>
    /// <param name="centre">The wanted centre in page points from the top left.</param>
    /// <param name="pageWidth">The width of the displayed page in points, above 0.</param>
    /// <param name="pageHeight">The height of the displayed page in points, above 0.</param>
    /// <returns>The box in page points from the top left.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The aspect ratio or a side of the page is not a positive number.</exception>
    public static PageRect Fit(double aspect, DisplayPoint centre, double pageWidth, double pageHeight)
    {
        if (!double.IsFinite(aspect) || aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspect), aspect, "The aspect ratio must be a positive number.");
        }

        if (!double.IsFinite(pageWidth) || pageWidth <= 0 || !double.IsFinite(pageHeight) || pageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidth), pageWidth, "The page needs a width and a height.");
        }

        var width = Math.Min(SignatureLimits.StartWidthPoints, pageWidth * SignatureLimits.MaxStartWidthFraction);
        var height = width / aspect;
        var x = double.IsFinite(centre.X) ? centre.X : pageWidth / 2;
        var y = double.IsFinite(centre.Y) ? centre.Y : pageHeight / 2;
        return SignatureBoxes.Fit(new PageRect(x - (width / 2), y - (height / 2), x + (width / 2), y + (height / 2)), aspect, pageWidth, pageHeight);
    }
}
