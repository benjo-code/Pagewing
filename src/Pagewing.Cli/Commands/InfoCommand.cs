using System.Globalization;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli.Commands;

/// <summary><c>pagewing info</c>: page count, PDF version, metadata and page sizes.</summary>
internal static class InfoCommand
{
    public static async Task RunAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct)
    {
        var metadata = await document.GetMetadataAsync(ct).ConfigureAwait(false);
        var version = document.PdfVersion.Length == 0 ? "unknown" : document.PdfVersion;

        output.WriteLine($"File: {options.File}");
        output.WriteLine($"Pages: {document.PageCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"PDF version: {version}");
        WriteField(output, "Title", metadata.Title);
        WriteField(output, "Author", metadata.Author);
        WriteField(output, "Subject", metadata.Subject);
        WriteField(output, "Keywords", metadata.Keywords);
        WriteField(output, "Creator", metadata.Creator);
        WriteField(output, "Producer", metadata.Producer);
        WriteField(output, "CreationDate", metadata.CreationDate);
        WriteField(output, "ModDate", metadata.ModDate);

        output.WriteLine("Page sizes:");
        var groupStart = 0;
        PdfPageSize groupSize = default;
        for (var page = 0; page < document.PageCount; page++)
        {
            var size = await document.GetPageSizeAsync(page, ct).ConfigureAwait(false);
            if (page == 0)
            {
                groupSize = size;
            }
            else if (size != groupSize)
            {
                WriteGroup(output, groupStart, page - 1, groupSize);
                groupStart = page;
                groupSize = size;
            }
        }

        if (document.PageCount > 0)
        {
            WriteGroup(output, groupStart, document.PageCount - 1, groupSize);
        }
    }

    private static void WriteField(TextWriter output, string label, string? value)
    {
        if (value is not null)
        {
            // Keep one field per line, even if the value contains line breaks.
            output.WriteLine($"{label}: {value.ReplaceLineEndings(" ")}");
        }
    }

    private static void WriteGroup(TextWriter output, int firstIndex, int lastIndex, PdfPageSize size)
    {
        var first = (firstIndex + 1).ToString(CultureInfo.InvariantCulture);
        var pages = firstIndex == lastIndex ? first : $"{first}-{(lastIndex + 1).ToString(CultureInfo.InvariantCulture)}";
        output.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"  {pages}: {size.Width:F2} x {size.Height:F2} pt"));
    }
}
