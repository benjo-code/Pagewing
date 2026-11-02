using System.Globalization;
using Pagewing.Core.Ocr;

namespace Pagewing.Cli.Commands;

/// <summary>
/// The report of <c>pagewing ocr</c> on stderr (English, stable wording for scripts): how many pages and words, the engine's own confidence, every
/// page that was not recognized with its reason, and the pages that need a second look. It never contains recognized text and never says the result
/// is correct or complete: it only counts.
/// </summary>
internal static class OcrReportText
{
    /// <summary>Writes the report; pages count from 1.</summary>
    public static void Write(OcrReport report, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Recognized {report.PagesRecognized} {Noun(report.PagesRecognized, "page", "pages")}, {report.Words} {Noun(report.Words, "word", "words")}, mean confidence {report.MeanConfidence:0} %."));
        foreach (var page in report.LowConfidencePages)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  page {page + 1}: low confidence; the page may be turned or hard to read."));
        }

        foreach (var page in report.PagesWithoutWords)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  page {page + 1}: no words found."));
        }

        foreach (var page in report.LittleTextPages)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  page {page + 1}: has a little text of its own, which stays; the page was recognized."));
        }

        foreach (var page in report.LoweredPages)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  page {page + 1}: recognized at a lowered resolution (pixel limit)."));
        }

        if (report.WordsLeftOut > 0)
        {
            writer.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{report.WordsLeftOut} {Noun(report.WordsLeftOut, "word", "words")} left out of the text layer: a character of them is not in its font."));
        }

        if (report.Skipped.Count > 0)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Skipped {report.Skipped.Count} {Noun(report.Skipped.Count, "page", "pages")}:"));
            foreach (var skipped in report.Skipped)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  page {skipped.Page + 1}: {ReasonOf(skipped)}"));
            }
        }
    }

    /// <summary>The reason a page was skipped, in English.</summary>
    public static string ReasonOf(OcrSkippedPage skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        var detail = skipped.Detail is { Length: > 0 } text ? $" ({text})" : string.Empty;
        return skipped.Reason switch
        {
            OcrSkipReason.HasText => "has text of its own",
            OcrSkipReason.AlreadyRecognized => "already has an OCR text layer",
            OcrSkipReason.TooLarge => $"too large to recognize{detail}",
            OcrSkipReason.ResolutionTooLow => $"resolution too low{detail}",
            OcrSkipReason.Timeout => "took longer than the time limit",
            OcrSkipReason.Failed => "could not be recognized",
            OcrSkipReason.NoSupportedCharacters => "no word has only characters the text layer can hold",
            _ => "not recognized",
        };
    }

    private static string Noun(int count, string one, string many) => count == 1 ? one : many;
}
