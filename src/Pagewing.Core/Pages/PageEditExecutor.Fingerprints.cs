using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

/// <summary>
/// What the check after a rewrite compares per page: the size after rotation (rounded to 0.5 pt), the absolute rotation and a
/// hash of the first <see cref="PageEditExecutor.FingerprintTextLength"/> characters of the text. A wrong page order, a wrong
/// rotation or a page that lost its content shows as a different fingerprint.
/// </summary>
/// <param name="Width">The width in points after rotation, rounded to 0.5.</param>
/// <param name="Height">The height in points after rotation, rounded to 0.5.</param>
/// <param name="Rotation">The absolute rotation: 0, 90, 180 or 270.</param>
/// <param name="TextHash">A stable hash (not <see cref="string.GetHashCode()"/>, which differs from run to run) of the first characters of the text.</param>
public sealed record PageFingerprint(double Width, double Height, int Rotation, int TextHash);

public sealed partial class PageEditExecutor
{
    /// <summary>The number of characters of a page's text that go into its <see cref="PageFingerprint.TextHash"/>.</summary>
    public const int FingerprintTextLength = 256;

    /// <summary>The most pages whose fingerprint the check after a rewrite compares; a longer document is sampled (see <see cref="SampledPages"/>).</summary>
    internal const int MaxCheckedPages = 50;

    /// <summary>Gets the fingerprints of all pages of <paramref name="document"/>, in order (one short job per page).</summary>
    /// <param name="document">A document that may be read: not a working copy that was changed by page operations.</param>
    /// <param name="cancellationToken">Cancels between two pages.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="InvalidDataException">A page is damaged and cannot be read.</exception>
    public static Task<IReadOnlyList<PageFingerprint>> FingerprintsAsync(PdfDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FingerprintsAsync(document, [.. Enumerable.Range(0, document.PageCount)], cancellationToken);
    }

    /// <summary>Gets the fingerprints of the given pages of <paramref name="document"/>, in the order of <paramref name="pages"/>.</summary>
    /// <param name="document">A document that may be read.</param>
    /// <param name="pages">The 0-based pages.</param>
    /// <param name="cancellationToken">Cancels between two pages.</param>
    /// <exception cref="ArgumentOutOfRangeException">A page is not a page of the document.</exception>
    public static async Task<IReadOnlyList<PageFingerprint>> FingerprintsAsync(PdfDocument document, IReadOnlyList<int> pages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        var result = new PageFingerprint[pages.Count];
        for (var i = 0; i < result.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[i];
            var size = await document.GetPageSizeAsync(page, cancellationToken).ConfigureAwait(false);
            var rotation = await document.GetPageRotationAsync(page, cancellationToken).ConfigureAwait(false);
            var text = await document.GetTextAsync(page, cancellationToken).ConfigureAwait(false);
            result[i] = new PageFingerprint(RoundToHalfPoint(size.Width), RoundToHalfPoint(size.Height), rotation, HashOf(text));
        }

        return result;
    }

    /// <summary>
    /// The pages whose fingerprint (and rendering) is checked after a rewrite of a document with <paramref name="pageCount"/>
    /// pages: all up to <see cref="MaxCheckedPages"/>, then the first ten, the last ten and evenly spread pages in between, at
    /// most <see cref="MaxCheckedPages"/> in all. Deterministic, ascending.
    /// </summary>
    internal static IReadOnlyList<int> SampledPages(int pageCount)
    {
        if (pageCount <= MaxCheckedPages)
        {
            return [.. Enumerable.Range(0, Math.Max(0, pageCount))];
        }

        const int Edge = 10;
        const int Spread = MaxCheckedPages - (2 * Edge);
        var pages = new SortedSet<int>();
        for (var i = 0; i < Edge; i++)
        {
            pages.Add(i);
            pages.Add(pageCount - 1 - i);
        }

        for (var i = 0; i < Spread; i++)
        {
            pages.Add((int)((long)i * (pageCount - 1) / (Spread - 1)));
        }

        return [.. pages];
    }

    private static double RoundToHalfPoint(double points) => Math.Round(points * 2, MidpointRounding.AwayFromZero) / 2;

    private static int HashOf(string text)
    {
        var length = Math.Min(text.Length, FingerprintTextLength);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.AsSpan(0, length).ToString()));
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }
}
