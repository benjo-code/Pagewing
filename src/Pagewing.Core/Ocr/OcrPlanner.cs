namespace Pagewing.Core.Ocr;

/// <summary>What a page is made of, as far as the decision to recognize it needs.</summary>
/// <param name="TextCharacters">The characters of text the page has, without white space and without Pagewing's own OCR layer.</param>
/// <param name="HasOwnLayer">Whether Pagewing's own layer is on the page.</param>
/// <param name="ImageDpi">The effective resolution of the largest image on the page, or <see langword="null"/> without an image.</param>
/// <param name="ImageCoverage">The share of the page the largest image covers, 0 to 1.</param>
public sealed record PageOcrInfo(int TextCharacters, bool HasOwnLayer, double? ImageDpi, double ImageCoverage);

/// <summary>The decision about one page.</summary>
/// <param name="Run">Whether the page is recognized.</param>
/// <param name="Reason">Why it is not, when it is not.</param>
/// <param name="LittleText">Whether the page has some text of its own, but fewer than <see cref="OcrPlanner.TextThreshold"/> characters (a page number, a stamp): it is recognized and the report says so.</param>
public sealed record OcrDecision(bool Run, OcrSkipReason? Reason, bool LittleText);

/// <summary>Decides, before anything is rendered, whether a page needs recognizing.</summary>
public static class OcrPlanner
{
    /// <summary>A page "has text" from this many characters without white space: scans often carry a page number, a stamp or a Bates number, and such pages are to be recognized.</summary>
    public const int TextThreshold = 50;

    /// <summary>Decides for one page.</summary>
    public static OcrDecision Decide(PageOcrInfo info, OcrMode mode)
    {
        ArgumentNullException.ThrowIfNull(info);
        var little = info.TextCharacters is > 0 and < TextThreshold;
        var hasText = info.TextCharacters >= TextThreshold;
        switch (mode)
        {
            case OcrMode.Force:
                return new OcrDecision(true, null, little);
            case OcrMode.Redo:
                // Redo replaces the own layer; a page with text of its own and no layer of ours stays as it is.
                return hasText && !info.HasOwnLayer
                    ? new OcrDecision(false, OcrSkipReason.HasText, false)
                    : new OcrDecision(true, null, little);
            default:
                if (info.HasOwnLayer)
                {
                    return new OcrDecision(false, OcrSkipReason.AlreadyRecognized, false);
                }

                return hasText ? new OcrDecision(false, OcrSkipReason.HasText, false) : new OcrDecision(true, null, little);
        }
    }
}
