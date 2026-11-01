using System.Globalization;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli.Commands;

/// <summary>
/// <c>pagewing export</c>: pages as PNG images. The same implementation, file names (<c>&lt;name&gt;-p&lt;NNN&gt;.png</c>) and
/// output as <c>render</c>; the differences are the rules of a command that writes new files: an existing image is replaced
/// only with <c>--force</c> (all files in the way are named before the first image is rendered), the file that is open never,
/// and a page above 64 megapixels at the chosen resolution is refused (naming the page) before anything is written. The path
/// of each image is printed on stdout, one per line, also of the images that were finished when a later one failed or the
/// command was canceled. Like <c>render</c>, a missing output folder is created.
/// </summary>
internal static class ExportCommand
{
    public static async Task RunAsync(
        PdfDocument document,
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var pages = CliApp.SelectPages(options.Pages, document.PageCount);
        if (pages.Count == 0)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(options.File);
        Directory.CreateDirectory(options.OutputDirectory);
        var written = new List<string>();
        try
        {
            await PageImageExporter.ExportAsync(
                document,
                stem,
                pages,
                options.Scale,
                options.Annotations,
                options.OutputDirectory,
                overwrite: options.Force,
                progress,
                ct,
                written,
                sourcePath: options.File).ConfigureAwait(false);
        }
        catch (PageExportException ex)
        {
            throw new CliException(Describe(ex, written.Count, pages.Count), ex);
        }
        catch (OperationCanceledException)
        {
            if (written.Count > 0)
            {
                await error.WriteLineAsync($"warning: {Written(written.Count, pages.Count)} before the command was canceled; they were kept.").ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            // The path as the user wrote the folder (maybe relative), not the full path the exporter returns.
            foreach (var path in written)
            {
                await output.WriteLineAsync(Path.Combine(options.OutputDirectory, Path.GetFileName(path))).ConfigureAwait(false);
            }

            await output.FlushAsync().ConfigureAwait(false);
        }
    }

    private static string Describe(PageExportException ex, int written, int total)
    {
        var tail = written > 0 ? $" {Written(written, total)} before this; they were kept." : string.Empty;
        var names = string.Join(", ", ex.Paths.Select(static path => $"'{Path.GetFileName(path)}'"));
        return ex.Error switch
        {
            PageExportError.TargetExists when ex.Page < 0 => $"These files already exist: {names}. Use --force to replace them. Nothing was written.",
            PageExportError.TargetExists => $"{names} appeared while the command ran; it was not replaced. Use --force to replace existing files.{tail}",
            PageExportError.TargetIsSource => $"An image must not replace the input file ({names}), with or without --force. Nothing was written.",
            PageExportError.WriteFailed => $"{ex.Message}{tail}",
            _ => ex.Message,
        };
    }

    private static string Written(int written, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"{written} of {total} files were written");
}
