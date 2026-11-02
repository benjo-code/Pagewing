using System.Globalization;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Settings;
using Pagewing.Ocr;

namespace Pagewing.Cli.Commands;

/// <summary>
/// <c>pagewing ocr</c>: recognizes the text of scanned pages and writes the result as a new file with Pagewing's invisible text layer behind the
/// unchanged picture. The file is written <b>incrementally</b> (the bytes of the input stay the exact beginning of the output, so signed areas and
/// the size are kept), through the checked, atomic save of the app (<see cref="DocumentSaver"/>); it never replaces its input and replaces another
/// file only with <c>--force</c>. The report goes to stderr and never holds recognized text. <c>--dry-run</c> loads no library: it lists the pages that
/// would be recognized and at what resolution.
/// </summary>
internal static class OcrCommand
{
    /// <summary>Makes the engine factory for a folder of language files; <see langword="null"/> means Tesseract (tests put a fake here).</summary>
    internal static Func<string, IOcrEngineFactory>? FactoryOverride { get; set; }

    public static async Task RunAsync(
        PdfDocument document,
        CliOptions options,
        string? password,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var store = StoreOf(options);
        var installed = store.List();
        var languages = options.Languages
            ?? OcrLanguages.DefaultFor(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, [.. installed.Select(static l => l.Name)]);
        if (languages.Length == 0)
        {
            throw new CliException("No language data found. Put <language>.traineddata files next to pagewing (a tessdata folder) or give --tessdata <folder>.");
        }

        var names = OcrLanguages.Split(languages);
        var missing = names.Where(name => installed.All(l => l.Name != name)).ToList();
        if (missing.Count > 0)
        {
            throw new CliException(
                $"No language data for {string.Join(", ", missing)}; installed: {(installed.Count == 0 ? "none" : string.Join(", ", installed.Select(static l => l.Name)))}. Use --language with one of them, or --tessdata <folder>.");
        }

        var pages = options.Pages is null ? null : CliApp.SelectPages(options.Pages, document.PageCount).ToHashSet();
        var ocr = new OcrOptions(languages, options.OcrMode, options.OcrDpi, pages, options.Jobs);
        if (options.DryRun)
        {
            await DryRunAsync(document, ocr, output, ct).ConfigureAwait(false);
            return;
        }

        var target = options.OutputFile ?? throw new UsageException("The 'ocr' command needs --output <out.pdf>.");
        OcrRunResult result;
        try
        {
            var folder = store.ResolveFolder(names);
            var factory = (FactoryOverride ?? (static directory => new TesseractEngineFactory(directory)))(folder);
            result = await OcrRunner.RunAsync(document, ocr, factory, progress: null, ct).ConfigureAwait(false);
        }
        catch (OcrEngineException ex)
        {
            throw new CliException(Describe(ex), ex);
        }
        catch (TessdataMissingException ex)
        {
            throw new CliException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new CliException($"The language files could not be read: {ex.Message}", ex);
        }

        // "Redo" and "force" replace the layer a page already has; "skip" never writes a second one.
        var replace = options.OcrMode != OcrMode.Skip;
        var plan = await OcrLayerApplier.PlanAsync(document, result, replace, ct).ConfigureAwait(false);
        var report = result.Report with
        {
            WordsLeftOut = plan.LeftOut,
        };

        // What was found is told before the first byte is written, like the warnings of the other writing commands.
        OcrReportText.Write(report, error);
        if (report.PagesRecognized == 0)
        {
            throw new CliException("No page was recognized; nothing was written.");
        }

        if (plan.IsEmpty)
        {
            throw new CliException("No text was found on the recognized pages; nothing was written.");
        }

        var signatures = await document.GetSignatureInfoAsync(ct).ConfigureAwait(false);
        if (SignaturePolicy.Evaluate(signatures) == SignatureAdvice.WouldInvalidate)
        {
            await error.WriteLineAsync("warning: the file has a digital signature that allows no changes; the added text layer makes it show as invalid.".AsMemory(), ct).ConfigureAwait(false);
        }

        await error.FlushAsync(ct).ConfigureAwait(false);

        try
        {
            await OcrLayerApplier.WriteAsync(document, plan).ConfigureAwait(false);
            var counts = new Dictionary<int, int>(plan.After.Count);
            foreach (var page in plan.After.Keys)
            {
                counts[page] = (await document.GetAnnotationsAsync(page, ct).ConfigureAwait(false)).Count;
            }

            var request = new SaveRequest(
                Path.GetFullPath(options.File),
                Path.GetFullPath(target),
                OriginalStamp: null,
                password,
                document.PageCount,
                counts,
                OverwriteChangedOriginal: false,
                ReplaceExisting: options.Force);
            var saved = await DocumentSaver.SaveAsync(document, new PdfEngine(), request, progress, ct).ConfigureAwait(false);
            await saved.Reopened.DisposeAsync().ConfigureAwait(false);
        }
        catch (SaveFailureException ex)
        {
            throw new CliException(SaveFailureText.Describe(ex, target), ex);
        }

        await output.WriteLineAsync(target.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static TessdataStore StoreOf(CliOptions options)
    {
        if (options.Tessdata is { } folder)
        {
            var full = Path.GetFullPath(folder);
            if (!Directory.Exists(full))
            {
                throw new CliException($"The tessdata folder does not exist: '{folder}'.");
            }

            // An explicit folder is the whole story: nothing shipped, nothing imported is mixed in.
            return new TessdataStore(full, null);
        }

        return new TessdataStore(UserDataPaths.TessdataDirectory(), TessdataStore.ShippedDirectoryFor(AppContext.BaseDirectory));
    }

    // Lists what a run would do page by page (stdout), without a library and without writing: the decision and the resolution only.
    private static async Task DryRunAsync(PdfDocument document, OcrOptions options, TextWriter output, CancellationToken ct)
    {
        var pages = options.Pages is null ? Enumerable.Range(0, document.PageCount) : options.Pages.Order();
        var recognized = 0;
        foreach (var page in pages)
        {
            var info = await document.GetOcrPageInfoAsync(page, ct).ConfigureAwait(false);
            var decision = OcrPlanner.Decide(info, options.Mode);
            if (!decision.Run)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"page {page + 1}: skip ({OcrReportText.ReasonOf(new OcrSkippedPage(page, decision.Reason!.Value))})").AsMemory(), ct).ConfigureAwait(false);
                continue;
            }

            var size = await document.GetPageSizeAsync(page, ct).ConfigureAwait(false);
            var raster = OcrResolution.Resolve(size.Width, size.Height, info.ImageDpi, options.Dpi);
            if (raster.TooLow)
            {
                var reason = new OcrSkippedPage(page, raster.Lowered ? OcrSkipReason.TooLarge : OcrSkipReason.ResolutionTooLow, $"{raster.Dpi} dpi");
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"page {page + 1}: skip ({OcrReportText.ReasonOf(reason)})").AsMemory(), ct).ConfigureAwait(false);
                continue;
            }

            recognized++;
            var note = (raster.Lowered ? "; resolution lowered by the pixel limit" : string.Empty) + (decision.LittleText ? "; has a little text of its own, which stays" : string.Empty);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"page {page + 1}: recognize at {raster.Dpi} dpi{note}").AsMemory(), ct).ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"would recognize {recognized} {(recognized == 1 ? "page" : "pages")}").AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Describe(OcrEngineException ex) => ex.Error switch
    {
        OcrEngineError.LibraryMissing => $"The OCR library ({TesseractLibrary.FileName}) is not installed next to pagewing: {TesseractLibrary.Default.Folder}. Nothing was written.",
        OcrEngineError.LibraryUnusable => $"The OCR library could not be loaded: {ex.Message} Nothing was written.",
        OcrEngineError.LanguageMissing => $"{ex.Message} Use --language with one of them, or --tessdata <folder>.",
        OcrEngineError.DataCorrupt => $"{ex.Message} Nothing was written.",
        _ => $"{ex.Message} Nothing was written.",
    };
}
