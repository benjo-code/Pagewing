using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The rescue of pages that cannot be redacted (milestone 14.1, spec 4.3): when the analysis refuses the file for pages alone, the card
/// offers each of those pages as an image, by the user's choice only. Encrypted files and page keys Pagewing cannot remove are
/// refusals of the file that no page choice helps, so they get the plain card of a refusal. A page the analysis refuses even after it
/// was turned into an image (too large, unreadable) is never offered again: the card names it and says why.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Gets or sets how the size of a refused page is read for the offer (<see cref="PdfDocument.GetPageSizeAsync"/> when <see langword="null"/>; tests give a page that cannot be read or is huge).</summary>
    internal Func<PdfDocument, int, CancellationToken, Task<PdfPageSize>>? RescuePageSizeReader { get; set; }

    /// <summary>
    /// Works out how the refusals of <paramref name="analysis"/> are told: the card with the offer when pages can be rescued, else the plain
    /// card of the refusal (<c>Offer</c> is <see langword="null"/>).
    /// </summary>
    private async Task<(RedactionApplyInfo Info, RedactionRescueOffer? Offer)> BuildRescueAsync(
        RedactionPlan plan, RedactionAnalysis analysis, PdfDocument document, CancellationToken token)
    {
        var info = RedactionApplyInfo.ForRefusals(analysis.Refusals);
        if (analysis.Refusals.Any(static refusal => refusal.Kind != RedactionRefusalKind.PageNotRedactable || refusal.Pages.Count == 0))
        {
            return (info, null);
        }

        var offered = new List<(int Page, double Width, double Height)>();
        var stuck = new List<string>();
        foreach (var page in analysis.Refusals.SelectMany(static refusal => refusal.Pages).Distinct().Order())
        {
            var number = (page + 1).ToString(Strings.Culture);
            if (plan.RasterPages.Contains(page))
            {
                // Turned into an image already and refused all the same: offering it again would never end.
                stuck.Add(Format(Strings.RedactRescueStuck, number));
                continue;
            }

            PdfPageSize size;
            try
            {
                size = await (RescuePageSizeReader?.Invoke(document, page, token) ?? document.GetPageSizeAsync(page, token));
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ObjectDisposedException or ArgumentException)
            {
                // Never chosen, so nothing to undo: the page cannot be read, and what cannot be read cannot become an image.
                stuck.Add(Format(Strings.RedactRescueUnreadable, number));
                continue;
            }

            if (RedactionApplyInfo.RasterEstimate(size.Width, size.Height, RedactionLimits.DefaultRasterDpi) is null)
            {
                stuck.Add(Format(Strings.RedactRescueTooLarge, number));
                continue;
            }

            offered.Add((page, size.Width, size.Height));
        }

        // The pages that cannot be turned into images are named whether or not others can.
        if (stuck.Count > 0)
        {
            info = RedactionApplyInfo.WithSections([.. info.Sections, new RedactionSection(Strings.RedactRescueHeadingStuck, stuck)]);
        }

        return (info, offered.Count == 0 ? null : new RedactionRescueOffer(offered, plan.RasterDpi));
    }

    private static string Format(string format, string argument) =>
        string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(format), argument);

    // The analysis of the plan, or null after the card that told why it could not be made (the user went back).
    private async Task<RedactionAnalysis?> AnalyzeOrExplainAsync(TabViewModel tab, RedactionPlan plan, PdfDocument document, string current, CancellationToken token)
    {
        try
        {
            return await AnalyzeRedactionsAsync(plan, document, current, token);
        }
        catch (SaveFailureException ex) when (ex.Reason is SaveFailureReason.SourceModified or SaveFailureReason.OriginalChanged)
        {
            await AskIfShownAsync(tab, PendingSave.PageApplyError(tab, current, static () => Strings.RedactErrorFileChanged), token);
            return null;
        }
        catch (Exception ex) when (ex is RedactionException or PdfOpenException or InvalidDataException or IOException or InvalidOperationException or ObjectDisposedException or SaveFailureException)
        {
            // A mark on a page the document does not have, a page that cannot be read: nothing is written.
            await AskIfShownAsync(tab, PendingSave.PageApplyError(tab, current, static () => Strings.RedactErrorNotPrepared), CancellationToken.None);
            return null;
        }
    }
}
