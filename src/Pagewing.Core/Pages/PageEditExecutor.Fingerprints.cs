using System.Buffers.Binary;
using System.Security.Cryptography;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Pages;

/// <summary>
/// What the check after a rewrite compares per page: the size after rotation (rounded to 0.5 pt), the absolute rotation and two
/// hashes of the text (<see cref="PageEditExecutor.FingerprintsAsync(PdfDocument, CancellationToken)"/>). A wrong page order, a
/// wrong rotation or a page that lost its content shows as a different fingerprint.
/// </summary>
/// <remarks>
/// <see cref="TextHash"/> depends on the order of the text and is the strong one: it tells two pages apart that have the same
/// characters in another sequence. PDFium reads a page that is turned in another sequence (the lines of a turned page come out
/// in a different order, the spaces differ), so for a slot the plan turns the expected <see cref="TextHash"/> cannot be known;
/// such an expected fingerprint has <see cref="CompareTextOrder"/> <see langword="false"/> and only <see cref="TextSetHash"/>
/// (which characters, how often) is compared. Everywhere else, where PDFium reads the page as it did in the source, both are.
/// </remarks>
/// <param name="Width">The width in points after rotation, rounded to 0.5.</param>
/// <param name="Height">The height in points after rotation, rounded to 0.5.</param>
/// <param name="Rotation">The absolute rotation: 0, 90, 180 or 270.</param>
/// <param name="TextHash">A stable hash (not <see cref="string.GetHashCode()"/>, which differs from run to run) of the first <see cref="PageEditExecutor.FingerprintTextLength"/> characters of the text, in reading order.</param>
/// <param name="TextSetHash">A stable hash of the characters of the whole text and their counts, without white space and control characters, in no particular order.</param>
/// <param name="CompareTextOrder">Whether the check compares <see cref="TextHash"/> (an expected fingerprint of a turned slot says <see langword="false"/>).</param>
/// <param name="CompareText">
/// Whether the check compares the text at all. <see langword="false"/> for a page whose text is changed on purpose (a redacted page):
/// only the size and the rotation are compared and both text hashes are ignored. The default keeps the comparison of every
/// rewrite that does not change text.
/// </param>
public sealed record PageFingerprint(double Width, double Height, int Rotation, int TextHash, int TextSetHash = 0, bool CompareTextOrder = true, bool CompareText = true);

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
            result[i] = new PageFingerprint(RoundToHalfPoint(size.Width), RoundToHalfPoint(size.Height), rotation, HashOf(text), SetHashOf(text));
        }

        return result;
    }

    /// <summary>
    /// The fingerprints a rewrite of <paramref name="plan"/> must give, worked out from the <em>source</em> documents and the
    /// plan, never from the changed working copy (whose page caches would show the state before the edit): the fingerprint of
    /// the source page with the added rotation (sides swapped at 90 and 270 degrees), the size of a blank page and the hash of
    /// no text. The check after the rewrite reads only the positions of <see cref="SampledPages(int)"/>, so only those are
    /// computed (a plan of 20,000 pages costs at most <see cref="MaxCheckedPages"/> page reads); every other position holds
    /// <see cref="Unchecked"/>. One read per distinct source page.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="primary">The open document the plan was made for.</param>
    /// <param name="inserted">The open documents of the inserted sources the plan uses.</param>
    /// <param name="cancellationToken">Cancels between two pages.</param>
    /// <returns>One entry per slot of the plan.</returns>
    /// <exception cref="PageEditException">A source has no document, or a page of a source does not exist.</exception>
    /// <exception cref="InvalidDataException">A page is damaged and cannot be read.</exception>
    public static async Task<IReadOnlyList<PageFingerprint>> ExpectedFingerprintsAsync(
        PageEditPlan plan,
        PdfDocument primary,
        IReadOnlyDictionary<PageSourceId, PdfDocument> inserted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(inserted);
        var usage = PlanUsage.Of(plan);
        usage.EnsureFilesAreGiven(inserted.Keys, inserted.Count);
        var expected = new PageFingerprint[plan.Slots.Count];
        Array.Fill(expected, Unchecked);
        var known = new Dictionary<(int Source, int Index), PageFingerprint>();
        foreach (var position in SampledPages(plan.Slots.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = plan.Slots[position];
            if (slot.Blank is { } blank)
            {
                var swap = slot.ExtraRotation % 180 != 0;
                expected[position] = new PageFingerprint(
                    RoundToHalfPoint(swap ? blank.Height : blank.Width),
                    RoundToHalfPoint(swap ? blank.Width : blank.Height),
                    slot.ExtraRotation,
                    HashOf(string.Empty),
                    SetHashOf(string.Empty));
                continue;
            }

            var key = (slot.Source.Value, slot.SourceIndex);
            if (!known.TryGetValue(key, out var page))
            {
                var document = slot.Source.Value == 0 ? primary : inserted[slot.Source];
                if (slot.SourceIndex >= document.PageCount)
                {
                    throw new PageEditException(PageEditError.OutOfRange, "A page of the plan is not a page of its source.");
                }

                page = (await FingerprintsAsync(document, [slot.SourceIndex], cancellationToken).ConfigureAwait(false))[0];
                known[key] = page;
            }

            var turned = slot.ExtraRotation % 180 != 0;
            expected[position] = page with
            {
                Width = turned ? page.Height : page.Width,
                Height = turned ? page.Width : page.Height,
                Rotation = (page.Rotation + slot.ExtraRotation) % 360,

                // PDFium reads a turned page in another order: only which characters it has can be known beforehand.
                CompareTextOrder = slot.ExtraRotation == 0,
            };
        }

        return expected;
    }

    /// <summary>The entry of <see cref="ExpectedFingerprintsAsync"/> for a position the check does not read.</summary>
    public static PageFingerprint Unchecked { get; } = new(0, 0, 0, 0);

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

    // The text in reading order: the first characters, UTF-8, SHA-256 (a surrogate pair is not split).
    private static int HashOf(string text)
    {
        var length = Math.Min(text.Length, FingerprintTextLength);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.AsSpan(0, length).ToString()));
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }

    // Which characters a page has and how often, whatever the order (see PageFingerprint). White space and control characters are
    // left out: they are what differs most between two readings of the same page.
    private static int SetHashOf(string text)
    {
        var counts = new SortedDictionary<char, int>();
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character) && !char.IsControl(character))
            {
                counts[character] = counts.GetValueOrDefault(character) + 1;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> entry = stackalloc byte[6];
        foreach (var (character, count) in counts)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, character);
            BinaryPrimitives.WriteInt32LittleEndian(entry[2..], count);
            hash.AppendData(entry);
        }

        return BinaryPrimitives.ReadInt32LittleEndian(hash.GetHashAndReset());
    }
}
