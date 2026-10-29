using Pagewing.Core.Text;

namespace Pagewing.Core.Signatures;

/// <summary>The boxes of image signatures: the aspect ratio is fixed and the signature lies wholly on the page.</summary>
internal static class SignatureBoxes
{
    /// <summary>
    /// The largest box of the aspect ratio <paramref name="aspect"/> (width divided by height) inside <paramref name="box"/>, around
    /// its centre, then made to fit the page: scaled down if it is larger than the page, moved in if it reaches beyond an edge.
    /// </summary>
    /// <param name="box">The wanted box in page points from the top left; finite, with a width and a height.</param>
    /// <param name="aspect">Width divided by height, above 0.</param>
    /// <param name="pageWidth">The width of the displayed page in points.</param>
    /// <param name="pageHeight">The height of the displayed page in points.</param>
    public static PageRect Fit(PageRect box, double aspect, double pageWidth, double pageHeight)
    {
        var width = Math.Min(box.Width, box.Height * aspect);
        var centreX = (box.Left + box.Right) / 2;
        var centreY = (box.Top + box.Bottom) / 2;
        if (width > pageWidth)
        {
            width = pageWidth;
        }

        if (width / aspect > pageHeight)
        {
            width = pageHeight * aspect;
        }

        var height = width / aspect;
        var left = Math.Clamp(centreX - (width / 2), 0, Math.Max(0, pageWidth - width));
        var top = Math.Clamp(centreY - (height / 2), 0, Math.Max(0, pageHeight - height));
        return new PageRect(left, top, left + width, top + height);
    }
}
