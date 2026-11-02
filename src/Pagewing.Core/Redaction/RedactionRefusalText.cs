using System.Globalization;

namespace Pagewing.Core.Redaction;

/// <summary>
/// What a failed check of a written redaction is called. Every <see cref="RedactionFindingKind"/> has one, and
/// <see cref="UnremovableContent"/> is the name of a <see cref="RedactionFindingKind.RawBytesStillPresent"/> finding: the removed text is
/// still in the file in a place that is not a page object (a Type 3 glyph procedure, a tiling pattern, a stream of a form), which PDFium
/// cannot remove. It is no refusal in advance (<see cref="RedactionRefusalKind"/>): it is found by the check after writing and ends the
/// apply with <see cref="Pagewing.Core.Saving.SaveFailureReason.RedactionFailed"/>.
/// </summary>
public enum RedactionFailureKind
{
    /// <summary>A removed text is still in the file outside the page objects.</summary>
    UnremovableContent,

    /// <summary>A removed text is still in the page text of the result.</summary>
    TextStillPresent,

    /// <summary>A character of the result lies inside a marked area.</summary>
    CharInsideArea,

    /// <summary>A pixel of an image under a marked area is not the colour of the area.</summary>
    ImagePixelNotOverwritten,

    /// <summary>Something in the file could not be read, so it cannot be shown that nothing is left in it.</summary>
    UnreadableStream,

    /// <summary>A page of the written file still carries a key PDFium cannot remove, or the keys could not be checked.</summary>
    PageKeysStillPresent,

    /// <summary>A page looks different outside the marked areas.</summary>
    PageChangedOutsideAreas,

    /// <summary>A removed text is not real text and could not be searched for.</summary>
    RemovedTextUnverifiable,

    /// <summary>Something the cleanup should have removed at the document level is still in the written file.</summary>
    CleanupItemStillPresent,

    /// <summary>A page that became an image is not only one image, or could not be counted.</summary>
    RasterPageInvalid,

    /// <summary>An image area of a plan that pixelates or blurs is neither the fill colour nor the block result worked out from the original.</summary>
    ImageTreatmentWrong,
}

/// <summary>
/// The texts of everything that stops a redaction, English and stable, one text for each kind, shared by the card and the command
/// line (the interface translates by kind and shows the pages from the refusal, never this text). A text never contains the removed text.
/// </summary>
public static class RedactionRefusalText
{
    /// <summary>The text of a failed check that left no <see cref="RedactionCheck"/> (the check itself could not be completed).</summary>
    public const string CheckNotCompleted = "The check of the written file could not be completed, so the file was not written.";

    /// <summary>Gets the text of one refusal.</summary>
    /// <param name="refusal">The refusal.</param>
    public static string Of(RedactionRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        switch (refusal.Kind)
        {
            case RedactionRefusalKind.Encrypted:
                return "The file is encrypted. Redacting it would write it without its protection, and Pagewing does not remove protection silently, so the file is not redacted.";

            case RedactionRefusalKind.UnremovablePageKeys when refusal.PageKeysUnverified:
                return "The page keys of the file could not be checked, so the file is not redacted: a page may carry a thumbnail, private data, metadata or actions that Pagewing cannot remove.";

            case RedactionRefusalKind.UnremovablePageKeys:
                return $"A thumbnail, private data, metadata or actions that Pagewing cannot remove are on {PageList(refusal.Pages)}, so the file is not redacted. {refusal.Reason}";

            case RedactionRefusalKind.PageNotRedactable:
                return $"Redacting {PageList(refusal.Pages)} would change content outside the marked areas, so the file is not redacted. {refusal.Reason}";

            case RedactionRefusalKind.OverlayRepeatsTerm:
                return $"The text on the areas of {PageList(refusal.Pages)} contains a text that is redacted, which would put it back on the page, so the file is not redacted. Change the text on the areas.";

            default:
                return $"The file is not redacted. {refusal.Reason}";
        }
    }

    /// <summary>Gets the texts of several refusals, one line each.</summary>
    /// <param name="refusals">The refusals.</param>
    public static string Of(IReadOnlyList<RedactionRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        return string.Join('\n', refusals.Select(Of));
    }

    /// <summary>Gets the kind a finding of the check is reported as.</summary>
    /// <param name="finding">The finding.</param>
    public static RedactionFailureKind KindOf(RedactionFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Kind switch
        {
            RedactionFindingKind.RawBytesStillPresent => RedactionFailureKind.UnremovableContent,
            RedactionFindingKind.TextStillPresent => RedactionFailureKind.TextStillPresent,
            RedactionFindingKind.CharInsideArea => RedactionFailureKind.CharInsideArea,
            RedactionFindingKind.ImagePixelNotOverwritten => RedactionFailureKind.ImagePixelNotOverwritten,
            RedactionFindingKind.UnreadableStream => RedactionFailureKind.UnreadableStream,
            RedactionFindingKind.PageKeysStillPresent => RedactionFailureKind.PageKeysStillPresent,
            RedactionFindingKind.PageChangedOutsideAreas => RedactionFailureKind.PageChangedOutsideAreas,
            RedactionFindingKind.CleanupItemStillPresent => RedactionFailureKind.CleanupItemStillPresent,
            RedactionFindingKind.RasterPageInvalid => RedactionFailureKind.RasterPageInvalid,
            RedactionFindingKind.ImageTreatmentWrong => RedactionFailureKind.ImageTreatmentWrong,
            _ => RedactionFailureKind.RemovedTextUnverifiable,
        };
    }

    /// <summary>Gets the 0-based pages a finding concerns: its page, or for an unremovable content the pages the text was removed from.</summary>
    /// <param name="finding">The finding.</param>
    public static IReadOnlyList<int> PagesOf(RedactionFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Page is { } page ? [page] : finding.RelatedPages ?? [];
    }

    /// <summary>Gets the text of a finding: what is wrong and where, never the removed text.</summary>
    /// <param name="finding">The finding.</param>
    public static string Of(RedactionFinding finding)
    {
        var pages = PagesOf(finding);
        var where = pages.Count == 0 ? string.Empty : $" ({PageList(pages)})";
        return KindOf(finding) switch
        {
            RedactionFailureKind.UnremovableContent =>
                $"Text that was removed{(pages.Count == 0 ? string.Empty : $" from {PageList(pages)}")} is still in the file in a place that is not a page object (for example a Type 3 font, a pattern or a stream of a form), and Pagewing cannot remove it, so the file was not written.",
            RedactionFailureKind.CleanupItemStillPresent =>
                $"{finding.Detail} The cleanup should have removed it, so the file was not written.",
            RedactionFailureKind.TextStillPresent =>
                $"A removed text is still in the page text of the result{where}, so the file was not written.",
            RedactionFailureKind.CharInsideArea =>
                $"Characters of the result lie inside a marked area{where}, so the file was not written.",
            RedactionFailureKind.ImagePixelNotOverwritten =>
                $"Pixels of an image under a marked area were not overwritten{where}, so the file was not written.",
            RedactionFailureKind.UnreadableStream =>
                $"Part of the file could not be read{where}, so it cannot be shown that nothing of the removed text is left in it, and the file was not written.",
            RedactionFailureKind.PageKeysStillPresent =>
                $"A page still carries a thumbnail, private data, metadata or actions that Pagewing cannot remove, or the keys could not be checked{where}, so the file was not written.",
            RedactionFailureKind.PageChangedOutsideAreas =>
                $"A page looks different outside the marked areas{where}, so the file was not written.",
            RedactionFailureKind.RasterPageInvalid =>
                $"A page that was turned into an image is not only one image, or could not be checked{where}, so the file was not written.",
            RedactionFailureKind.ImageTreatmentWrong =>
                $"An image area that should be pixelated or blurred is neither filled solid nor the block averages of the original{where}, so the file was not written.",
            _ =>
                $"A removed text could not be searched for{where}, so the result cannot be checked and the file was not written.",
        };
    }

    /// <summary>Gets the text of a failed check, one line per finding; <see cref="CheckNotCompleted"/> when there is no check (the check itself failed).</summary>
    /// <param name="check">The check from <see cref="Pagewing.Core.Saving.SaveFailureException.Details"/>, or <see langword="null"/>.</param>
    public static string Of(RedactionCheck? check) =>
        check is null || check.Findings.Count == 0 ? CheckNotCompleted : string.Join('\n', check.Findings.Select(static f => Of(f)));

    /// <summary>
    /// Gets the text of a failed save of a redaction: the findings when the failure is <see cref="Pagewing.Core.Saving.SaveFailureReason.RedactionFailed"/>
    /// and carries a <see cref="RedactionCheck"/>; <see cref="CheckNotCompleted"/> when it carries something else or nothing (a check that
    /// could not be completed is a failure without details, never a pass); the message of the failure for every other reason.
    /// </summary>
    /// <param name="failure">The failure of <see cref="RedactionApplier"/>.</param>
    public static string OfFailure(Pagewing.Core.Saving.SaveFailureException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Reason == Pagewing.Core.Saving.SaveFailureReason.RedactionFailed ? Of(failure.Details as RedactionCheck) : failure.Message;
    }

    private static string PageList(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0)
        {
            return "the file";
        }

        var numbers = string.Join(", ", pages.Select(static p => (p + 1).ToString(CultureInfo.InvariantCulture)));
        return pages.Count == 1 ? $"page {numbers}" : $"pages {numbers}";
    }
}
