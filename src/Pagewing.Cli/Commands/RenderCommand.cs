using System.Globalization;
using Pagewing.Core.Imaging;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli.Commands;

/// <summary><c>pagewing render</c>: renders pages as PNG files and prints the path of each file.</summary>
internal static class RenderCommand
{
    private const int MinimumDigits = 3;

    public static async Task RunAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct)
    {
        var pages = CliApp.SelectPages(options.Pages, document.PageCount);
        if (pages.Count == 0)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(options.File);
        var digits = Math.Max(MinimumDigits, document.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        var format = "D" + digits.ToString(CultureInfo.InvariantCulture);
        var renderOptions = new RenderOptions { Annotations = options.Annotations };

        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var page in pages)
        {
            var image = await document.RenderPageAsync(page, options.Scale, renderOptions, ct).ConfigureAwait(false);
            var path = Path.Combine(options.OutputDirectory, $"{stem}-p{(page + 1).ToString(format, CultureInfo.InvariantCulture)}.png");
            await File.WriteAllBytesAsync(path, PngEncoder.Encode(image), ct).ConfigureAwait(false);
            await output.WriteLineAsync(path.AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
