using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli.Commands;

/// <summary><c>pagewing render</c>: renders pages as PNG files and prints the path of each file.</summary>
internal static class RenderCommand
{
    public static async Task RunAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct)
    {
        var pages = CliApp.SelectPages(options.Pages, document.PageCount);
        if (pages.Count == 0)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(options.File);
        Directory.CreateDirectory(options.OutputDirectory);

        // One page per call: each path is printed as soon as its file is complete, as before. Like before, an existing file is
        // replaced (the explicit choice of this command); the file that is open never is.
        foreach (var page in pages)
        {
            try
            {
                var written = await PageImageExporter.ExportAsync(
                    document,
                    stem,
                    [page],
                    options.Scale,
                    options.Annotations,
                    options.OutputDirectory,
                    overwrite: true,
                    progress: null,
                    ct,
                    sourcePath: options.File,
                    maxPixels: PageImageExporter.RenderLimitOnly).ConfigureAwait(false);

                // The path as the user wrote the folder (maybe relative), not the full path the exporter returns.
                var path = Path.Combine(options.OutputDirectory, Path.GetFileName(written[0]));
                await output.WriteLineAsync(path.AsMemory(), ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (PageExportException ex)
            {
                throw new CliException(ex.Message, ex);
            }
        }
    }
}
