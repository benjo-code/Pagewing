using System.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Text;

/// <summary>
/// The characters and links of one page, in the order and at the indexes the PDF engine reports them.
/// The data is immutable once created.
/// </summary>
public sealed class PageText
{
    /// <summary>The granularity <see cref="BodyFontSize"/> is rounded to, in points.</summary>
    private const double FontSizeStep = 0.5;

    /// <summary>Creates the text of a page.</summary>
    /// <param name="pageIndex">The 0-based page index.</param>
    /// <param name="width">The page width in points.</param>
    /// <param name="height">The page height in points.</param>
    /// <param name="chars">The characters.</param>
    /// <param name="links">The links.</param>
    public PageText(int pageIndex, double width, double height, IReadOnlyList<TextChar> chars, IReadOnlyList<PageLink> links)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(links);
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Chars = chars;
        Links = links;

        var sizes = new Dictionary<double, int>();
        foreach (var c in chars)
        {
            if (!c.IsGenerated)
            {
                HasText = true;
                var size = Math.Round(c.FontSize / FontSizeStep, MidpointRounding.AwayFromZero) * FontSizeStep;
                sizes[size] = sizes.GetValueOrDefault(size) + 1;
            }
        }

        // The most frequent size; on a tie the smaller one, so the result does not depend on the order.
        var best = 0.0;
        var bestCount = 0;
        foreach (var (size, count) in sizes)
        {
            if (count > bestCount || (count == bestCount && size < best))
            {
                best = size;
                bestCount = count;
            }
        }

        BodyFontSize = (float)best;
    }

    /// <summary>Gets the 0-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>Gets the page width in points, after rotation and CropBox.</summary>
    public double Width { get; }

    /// <summary>Gets the page height in points, after rotation and CropBox.</summary>
    public double Height { get; }

    /// <summary>Gets the characters; empty for a page without text.</summary>
    public IReadOnlyList<TextChar> Chars { get; }

    /// <summary>Gets the links: link annotations first, then web addresses found in the text.</summary>
    public IReadOnlyList<PageLink> Links { get; }

    /// <summary>Gets whether any character is not generated.</summary>
    public bool HasText { get; }

    /// <summary>
    /// Gets the most frequent font size of the characters that are not generated, rounded to 0.5 points; 0 for a
    /// page without text.
    /// </summary>
    public float BodyFontSize { get; }

    /// <summary>Gets the characters <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive) as a string, without any processing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within <see cref="Chars"/>.</exception>
    public string GetText(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Chars.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, end);
        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            builder.Append(Chars[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>Maps what the PDFium layer read for a page to the Core model; links the engine cannot follow are dropped.</summary>
    internal static PageText FromPdfium(int pageIndex, double width, double height, PdfiumChar[] chars, PdfiumLink[] links)
    {
        var mapped = new TextChar[chars.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var flags = TextCharTraits.None;
            if (c.Generated)
            {
                flags |= TextCharTraits.Generated;
            }

            if (c.Hyphen)
            {
                flags |= TextCharTraits.Hyphen;
            }

            if (c.Bold)
            {
                flags |= TextCharTraits.Bold;
            }

            if (c.Italic)
            {
                flags |= TextCharTraits.Italic;
            }

            mapped[i] = new TextChar(c.CodePoint, new PageRect(c.Left, c.Top, c.Right, c.Bottom), flags, c.FontSize);
        }

        var mappedLinks = new List<PageLink>(links.Length);
        foreach (var link in links)
        {
            LinkTarget? target = link.Kind switch
            {
                PdfiumLinkKind.Page => new PageTarget(link.PageIndex, link.Top),
                PdfiumLinkKind.Uri when link.Uri is not null => new UriTarget(link.Uri),
                _ => null,
            };
            if (target is null)
            {
                continue;
            }

            var rects = new PageRect[link.Rects.Count];
            for (var i = 0; i < rects.Length; i++)
            {
                var (left, top, right, bottom) = link.Rects[i];
                rects[i] = new PageRect(left, top, right, bottom);
            }

            mappedLinks.Add(new PageLink(rects, target));
        }

        return new PageText(pageIndex, width, height, mapped, mappedLinks);
    }
}
