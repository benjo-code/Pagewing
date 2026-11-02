using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.Cli.Commands;

/// <summary>
/// <c>pagewing rotate</c>: turns pages and writes the result as a new file. The same chain as the app's "Apply": the plan is
/// analysed first (a refused plan is never written), what the change loses is printed on stderr before anything is written,
/// and the file goes through the checked, atomic full rewrite of <see cref="PageEditApplier"/> (temporary file in the output
/// folder, reopened and checked, then moved into place). An encrypted input stays encrypted or the command fails.
/// </summary>
internal static class RotateCommand
{
    private static readonly IReadOnlyDictionary<PageSourceId, PdfDocument> NoSources = new Dictionary<PageSourceId, PdfDocument>();

    private static readonly IReadOnlyDictionary<PageSourceId, PageSourceFile> NoFiles = new Dictionary<PageSourceId, PageSourceFile>();

    public static async Task RunAsync(
        PdfDocument document,
        CliOptions options,
        string? password,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var target = options.OutputFile ?? throw new UsageException("The 'rotate' command needs --output.");
        var pages = CliApp.SelectPages(options.Pages, document.PageCount);
        try
        {
            var plan = PageEditPlan.Identity(document.PageCount).Rotate(pages, options.Angle);
            var report = await PageEditExecutor.AnalyzeAsync(plan, document, NoSources, ct).ConfigureAwait(false);
            if (report.IsRefused)
            {
                throw new CliException($"The page changes cannot be applied: {PageLossText.RefusalOf(report)}");
            }

            // The user is told what is lost before the first byte is written.
            PageLossText.WriteWarnings(report, error);
            await error.FlushAsync(ct).ConfigureAwait(false);

            // The output is never the input (OutputGuard), so there is no stamp of "the original" to compare at the target.
            var request = new PageApplyRequest(plan, Path.GetFullPath(target), NoFiles, ReplaceExisting: options.Force);
            var saved = await PageEditApplier.WriteAsync(new PdfEngine(), document, Path.GetFullPath(options.File), password, openedStamp: null, request, progress, ct).ConfigureAwait(false);
            await saved.Reopened.DisposeAsync().ConfigureAwait(false);
        }
        catch (PageEditException ex)
        {
            throw new CliException(ex.Message, ex);
        }
        catch (SaveFailureException ex)
        {
            throw new CliException(SaveFailureText.Describe(ex, target), ex);
        }

        await output.WriteLineAsync(target.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
