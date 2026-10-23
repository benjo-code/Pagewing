using Pagewing.Core.Pdf;

namespace Pagewing.Cli.Commands;

/// <summary><c>pagewing text</c>: the text of the selected pages, each followed by a form feed.</summary>
internal static class TextCommand
{
    public static async Task RunAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct)
    {
        foreach (var page in CliApp.SelectPages(options.Pages, document.PageCount))
        {
            var text = await document.GetTextAsync(page, ct).ConfigureAwait(false);
            await output.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteAsync("\f".AsMemory(), ct).ConfigureAwait(false);
        }
    }
}
