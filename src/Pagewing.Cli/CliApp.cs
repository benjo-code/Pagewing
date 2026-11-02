using System.Text;
using Pagewing.Cli.Commands;
using Pagewing.Core;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli;

/// <summary>
/// The command line of Pagewing: <c>info</c>, <c>render</c>, <c>text</c>, <c>search</c>, <c>rotate</c>, <c>merge</c>, <c>split</c>, <c>export</c>, <c>redact</c> and <c>ocr</c>. Exit codes: 0 success, 1 runtime
/// error (<c>pagewing: &lt;message&gt;</c> on stderr), 2 usage error (message and usage text on stderr).
/// </summary>
public static class CliApp
{
    /// <summary>Name of the environment variable that holds the password of an encrypted PDF.</summary>
    internal const string PasswordVariable = "PAGEWING_PASSWORD";

    private const string Usage =
        """
        Usage:
          pagewing info   <file> [--password <pw>]
          pagewing render <file> [--pages <range>] [--dpi <n>] [--output <dir>] [--no-annotations] [--password <pw>]
          pagewing text   <file> [--pages <range>] [--password <pw>]
          pagewing search <file> <query> [--pages <range>] [--match-case] [--whole-word] [--password <pw>]
          pagewing rotate <file> --pages <range> --angle <90|180|270|-90> --output <out.pdf> [--force] [--password <pw>]
          pagewing merge  --output <out.pdf> <file> [--pages <range>] [--password <pw>] <file> [--pages <range>] [--password <pw>] ... [--force]
          pagewing split  <file> --output <dir> (--every <n> | --ranges <ranges> | --bookmarks) [--force] [--password <pw>]
          pagewing export <file> [--pages <range>] [--dpi <n>] [--output <dir>] [--no-annotations] [--force] [--password <pw>]
          pagewing redact <file> --output <out.pdf> [--text <term>]... [--pattern <email|iban|phone|card>]... [--include-unchecked] [--rect <page>:<x>,<y>,<w>,<h>]... [--pages <range>] [--rasterize-pages <range>] [--rasterize-unredactable] [--dpi <72-300>] [--match-case] [--dry-run] [--allow-unsearched] [--fill <black|white|#RRGGBB>] [--image-fill <solid|pixelate|blur>] [--image-strength <coarse|very-coarse>] [--overlay-text <text>] [--overlay-repeat] [--force] [--password <pw>]
          pagewing ocr    <file> --output <out.pdf> [--language <deu+eng>] [--tessdata <folder>] [--pages <range>] [--mode <skip|redo|force>] [--dpi <150-600>] [--jobs <n>] [--dry-run] [--force] [--password <pw>]
          pagewing --version
          pagewing --help

        """;

    private const string Help =
        """

        Commands:
          info     Print page count, PDF version, metadata and page sizes.
          render   Render pages as PNG files <name>-p<NNN>.png and print their paths.
          text     Print the text of the pages, each page followed by a form feed (U+000C).
          search   Print one line per hit: <page> TAB <character index> TAB <before>[<match>]<after>
                   (pages count from 1; use -- before a query that starts with a dash).
          rotate   Turn pages by --angle and write the result to a new file. What the change
                   loses (for example a digital signature, which is then shown as invalid) is
                   printed on stderr as "warning:" lines before the file is written. The input
                   is never changed; an encrypted input stays encrypted.
          merge    Write the pages of several files to one new file. --pages and --password
                   after a file belong to that file. Only the first file keeps its bookmarks,
                   form, page labels and information; what the others lose is printed as
                   "warning:" lines. An encrypted first file stays encrypted; an encrypted file
                   cannot be merged into an unencrypted first file. At most 20 files besides the first.
          split    Cut one file into several new files in a folder, named <name>-p<NNN>-<NNN>.pdf
                   (by bookmark: the bookmark title). Prints the path of each file written. Warnings
                   per file as "warning: <file>: ...". The folder must exist; nothing is replaced
                   without --force.
          export   Write pages as PNG images like render (same names, same options), but images
                   are only replaced with --force, and a page above 64 megapixels is refused.
          redact   Remove text and areas for good and write the result to a new file. --text finds every
                   match of a term (page text, text of annotations, values of form fields) and removes
                   it; --pattern finds e-mail addresses, IBANs, telephone numbers or card numbers the same
                   way; --rect removes whatever lies under a rectangle. The file is built anew (bookmarks,
                   information, XMP, attachments and scripts of the document are always left out), and the
                   written file is read back and checked: if the check finds anything, the command fails
                   (exit 1) and no output file exists. The report on stderr says what was removed, cleaned,
                   lost, not searched, checked and NOT checked, and never contains a removed text. A file
                   that cannot be redacted safely (encrypted, a page with /Thumb, /PieceInfo, /Metadata or
                   /AA, a page that cannot be written again unchanged outside the marks) is refused with
                   the reason and the pages. A digitally signed input is announced with a warning; the
                   signature is shown as invalid afterwards. The input is never changed. A page can also
                   be turned into an image (--rasterize-pages, --rasterize-unredactable): see there.

          ocr      Recognize the text of scanned pages and write the result to a new file: the page picture
                   stays as it is, the recognized words go behind it as invisible text, so the file can be
                   searched and its text selected. The file is written incrementally (the bytes of the input
                   stay the beginning of the output) and never replaces the input. Pages that have text of
                   their own are skipped (--mode skip); redo replaces the text layer of an earlier run, force
                   recognizes every page. The report on stderr says how many pages and words, the engine's own
                   confidence (it is not a promise: recognition makes mistakes) and every page that was skipped
                   with the reason; it never contains the recognized text. A run that recognizes no page is an
                   error (exit 1, nothing written). --dry-run lists the pages that would be recognized and at what
                   resolution, loads no library and writes nothing. The language models are the .traineddata files
                   of the tessdata folder next to pagewing and of the one you imported them into, or of --tessdata.

        Options:
          --pages <range>    Pages to process: 3, 2-5, 1,3,5-7 or all (default: all; rotate: required).
                             For redact: only the matches of --text and --pattern on these pages are removed.
                             Pages are taken in ascending order, each once: --pages 3,1 gives pages 1 and 3,
                             so --pages cannot reorder pages.
                             For merge: after a file, the pages taken from that file.
          --language <names> Ocr: the language models to recognize with, joined with +, in the order of their priority,
                             for example deu+eng (default: the language of the system and English, as far as installed).
                             An unknown language is an error (exit 1) that lists the installed ones.
          --tessdata <dir>   Ocr: the folder with the <language>.traineddata files, instead of the ones next to pagewing.
          --mode <how>       Ocr: skip (default: pages with text or with an OCR layer are skipped), redo (replace the OCR
                             layer of an earlier run, still skip pages with text of their own) or force (recognize every page).
          --jobs <n>         Ocr: how many pages are recognized at once, 1 to 16 (default: one less than the cores, at most 4).
          --dpi <n>          Resolution of rendered pages (default: 144).
                             For ocr: the resolution pages are recognized at, a whole number from 150 to 600 (default: from the
                             picture of the page, 150 to 300). A page too large for 50 megapixels gets a lower one, which the
                             report names; below 150 dpi it is skipped.
                             For redact: the resolution of the pages that become images, a whole number from
                             72 to 300 (default: 200); only with --rasterize-pages or --rasterize-unredactable.
                             A page too large for it gets a lower resolution, which the report names.
          --output <dir>     Directory for the PNG files, created if missing (default: .).
                             For rotate, merge and redact: the PDF file to write (required; redact: not with --dry-run).
                             For split: the folder for the new files (required, must exist).
          --angle <deg>      Rotate: turn the pages by 90, 180, 270 or -90 degrees (required).
          --every <n>        Split: a new file after every n pages.
          --ranges <list>    Split: a new file per range, for example "1-3,4-9" (ranges may overlap).
          --bookmarks        Split: a new file per top-level bookmark; the pages before the first
                             one form a file of their own. A file without such a bookmark is an error.
          --force            Commands that write files (rotate, merge, split, export, redact, ocr): replace an existing
                             output file. The output is never the input, with or without --force.
          --no-annotations   Render and export without annotations.
          --text <term>      Redact: remove every match of the term. May be given several times. Without --pages
                             the term must be gone from the whole document afterwards; a term that is not found
                             is an error. The term is plain text (no pattern); white space and line breaks
                             count as one space.
          --pattern <kind>   Redact: remove every match of a kind of pattern: email, iban, phone or card. May be
                             given several times and together with --text and --rect. A pattern finds only what
                             the text of the page shows: one split across a line break or a page, digits broken
                             up by other characters than the usual separators, and numbers in images are not
                             found, so the result is never "all of them". An IBAN or card number whose checksum
                             or length is wrong is left alone and reported (count and pages, never the value).
                             A run in which no kind has a match is an error.
          --include-unchecked
                             Redact: with --pattern, redact the matches whose checksum or length is wrong too.
          --rect <spec>      Redact: remove what lies under a rectangle, <page>:<x>,<y>,<w>,<h> (page from 1,
                             points, origin top left, dot as decimal separator). May be given several times;
                             a part beyond the page is clipped and reported.
          --rasterize-pages <range>
                             Redact: turn these pages into images (same syntax as --pages; --text and --rect are
                             not needed). The whole text of such a page is lost, as are its links, comments and
                             form fields; the marked areas are filled in the image. A page that is an image can
                             only be checked for its structure and the marked areas: a visible occurrence of a
                             removed text elsewhere on it stays and cannot be found without OCR, so such pages
                             cannot be checked for visible occurrences outside the marks.
          --rasterize-unredactable
                             Redact: turn exactly the pages into images that are refused as "would change content
                             outside the marked areas", and name them on stderr. Without it, or without
                             --rasterize-pages, such a page ends the command (exit 1). It does not help against an
                             encrypted file or a page key that cannot be removed, and it needs --text, --pattern or --rect.
          --dry-run          Redact: only search and analyse; print the number of matches per page and the
                             report, write nothing (exit 0 if the file would be written, the final check
                             of the written file excepted). Ocr: list the pages that would be recognized.
          --allow-unsearched Redact: go on although some pages could not be searched completely (a page that cannot be
                             read, an annotation or form field left out by a limit or damage). Without it the command
                             stops (exit 1, nothing written, also with --dry-run) and names the pages, because the
                             text may still be there. With it the report lists these pages as "Not searched (accepted)".
          --fill <colour>    Redact: the colour of the areas that replace the removed content: black (default),
                             white or a colour written as #RRGGBB. Pages that become images take it too.
          --image-fill <how> Redact: what the parts of images under the marks become: solid (default, the fill
                             colour), pixelate or blur (a few coarse blocks of the image: only their averages
                             remain; no block is smaller than 8 pixels). Text in an image can stay legible even
                             then, and Pagewing does not recognise text in images: for such text choose solid.
                             Text and paths under a mark always get the solid fill. Where the rule does not apply
                             (a turned, sheared or clipped image, a mark smaller than 2 x 2 blocks of 8 pixels,
                             marks that overlap on one image) the area gets the solid fill, and the report says how
                             many and why.
          --image-strength <s> Redact, with --image-fill pixelate or blur: coarse (default, up to 6 blocks along an axis)
                             or very-coarse (up to 3).
          --overlay-text <text>
                             Redact: write a text on every marked area (of this call), for example "redacted" or a
                             file number: at most 200 characters, only characters the font of the text has (a text
                             with any other character is refused as a whole, never changed). It is sized to fit
                             (not below 6 pt); where it does not fit, the area stays filled without text and the
                             report counts it. Not on image areas that are pixelated or blurred. A text that holds a
                             redacted term (any case, spaces ignored) is refused (exit 1, nothing written). The text is
                             drawn as shapes: it is not selectable, no file holds it as text, and the check of the
                             written file does not read it back.
          --overlay-repeat   Redact, with --overlay-text: repeat the text over the area instead of writing it once.
          --match-case       Search and redact: distinguish upper and lower case (default: ignore case; with
                             redact every spelling found is removed).
          --whole-word       Search: match only whole words.
          --password <pw>    Password of an encrypted PDF (merge: of the file before it).

        The password can also be given in the environment variable PAGEWING_PASSWORD;
        the option wins. Command-line arguments are visible to other processes on this
        computer (for example in the process list), so prefer the environment variable.

        Examples:
          pagewing merge --output all.pdf cover.pdf report.pdf --pages 2-9 appendix.pdf
          pagewing split report.pdf --output parts --every 10
          pagewing split report.pdf --output chapters --bookmarks
          pagewing export report.pdf --pages 1-3 --dpi 200 --output images
          pagewing redact contract.pdf --output contract-redacted.pdf --text "Jane Doe" --rect 2:72,100,200,24
          pagewing redact contract.pdf --output out.pdf --text 4711 --dry-run
          pagewing redact contract.pdf --output out.pdf --pattern iban --pattern email
          pagewing ocr scan.pdf --output scan-searchable.pdf --language deu+eng
          pagewing ocr scan.pdf --dry-run

        A file whose name starts with a dash is written ./-name.pdf (or after --, which ends
        the options; --pages and --password then cannot follow). Options and --help are
        recognised everywhere, so a file cannot be named like one.

        Ctrl+C cancels the command and exits with code 1. Press Ctrl+C again to abort immediately.

        Exit codes: 0 success, 1 error, 2 invalid command line.

        """;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Commands that take several files and so cannot open "the" document first (<c>merge</c>, milestone 13 task 14): they
    /// parse their own arguments and open their own documents, and are routed before a single document is opened.
    /// </summary>
    private static readonly Dictionary<string, MultiFileCommand> MultiFileCommands = new(StringComparer.Ordinal)
    {
        ["merge"] = MergeCommand.RunAsync,
    };

    /// <summary>Runs the command line.</summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <param name="stdout">Receives the output as UTF-8 without BOM; stays open.</param>
    /// <param name="stderr">Receives error messages and the usage text.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable, <see langword="null"/> when unset.</param>
    /// <param name="ct">Cancels the command (Ctrl+C).</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(
        string[] args,
        Stream stdout,
        TextWriter stderr,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken ct = default) =>
        RunAsync(args, stdout, stderr, getEnvironmentVariable, openDocument: null, runCommand: null, ct);

    /// <summary>
    /// <see cref="RunAsync(string[], Stream, TextWriter, Func{string, string?}, CancellationToken)"/> with seams for tests:
    /// <paramref name="openDocument"/> replaces opening the file, <paramref name="runCommand"/> replaces the command.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        Stream stdout,
        TextWriter stderr,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string?, CancellationToken, Task<PdfDocument>>? openDocument,
        Func<PdfDocument, CliOptions, TextWriter, CancellationToken, Task>? runCommand,
        CancellationToken ct,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        StreamWriter? output = null;
        try
        {
            output = new StreamWriter(stdout, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
            return await ExecuteAsync(args, output, stderr, getEnvironmentVariable, openDocument, runCommand, progress, ct).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"pagewing: {ex.Message}");
            stderr.WriteLine();
            stderr.Write(Usage);
            return 2;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("pagewing: Canceled.");
            return 1;
        }
        catch (Exception ex) when (ex is CliException or PdfOpenException or PdfRenderException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            stderr.WriteLine($"pagewing: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // Anything else (a missing or mismatched PDFium library, out of memory, a bug) is not something the
            // user can fix by changing the command line; name the type so the report is useful, but never print
            // a stack trace and never let the exception escape.
            stderr.WriteLine($"pagewing: Internal error ({ex.GetType().Name}): {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (output is not null)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // stdout is closed or broken (for example a broken pipe); there is nobody left to tell.
            }
        }
    }

    /// <summary>Selects the pages of a document; a page beyond the end is a runtime error.</summary>
    internal static IReadOnlyList<int> SelectPages(string? pages, int pageCount)
    {
        try
        {
            return PageRange.Parse(pages ?? "all", pageCount);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ActualValue is int)
        {
            throw PageOutOfRange(ex, pageCount);
        }
    }

    /// <summary>The error for a page beyond the end of a document, from the exception <see cref="PageRange"/> throws.</summary>
    internal static CliException PageOutOfRange(ArgumentOutOfRangeException ex, int pageCount)
    {
        var noun = pageCount == 1 ? "page" : "pages";
        return new CliException($"Page {ex.ActualValue} is out of range (the document has {pageCount} {noun}).");
    }

    private static async Task<int> ExecuteAsync(
        string[] args,
        StreamWriter output,
        TextWriter stderr,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string?, CancellationToken, Task<PdfDocument>>? openDocument,
        Func<PdfDocument, CliOptions, TextWriter, CancellationToken, Task>? runCommand,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (args.Length == 0)
        {
            throw new UsageException("No command given.");
        }

        if (args.Any(static arg => arg is "--help" or "-h"))
        {
            await output.WriteAsync($"{Usage}{Help}".AsMemory(), ct).ConfigureAwait(false);
            return 0;
        }

        if (args is ["--version"])
        {
            await output.WriteAsync($"{AppInfo.Version}\n".AsMemory(), ct).ConfigureAwait(false);
            return 0;
        }

        if (MultiFileCommands.TryGetValue(args[0], out var multiFile))
        {
            await multiFile(new CliContext(args[1..], output, stderr, getEnvironmentVariable, progress), ct).ConfigureAwait(false);
            return 0;
        }

        var options = CliOptions.Parse(args[0], args.AsSpan(1));

        // Before anything is opened: a command that writes a file never writes over its input or over a file it was not told to replace.
        if (options.OutputFile is not null)
        {
            OutputGuard.Check(options.File, options.OutputFile, options.Force);
        }
        else if (options.Command == "split")
        {
            OutputGuard.CheckFolder(options.OutputDirectory);
        }

        // The document is disposed inside the try block of RunAsync, so a failure while closing it is reported, too.
        await using var document = await OpenAsync(options, getEnvironmentVariable, openDocument, ct).ConfigureAwait(false);
        if (runCommand is not null)
        {
            await runCommand(document, options, output, ct).ConfigureAwait(false);
        }
        else if (options.Command == "rotate")
        {
            await RotateCommand.RunAsync(document, options, PasswordOf(options, getEnvironmentVariable), output, stderr, ct, progress).ConfigureAwait(false);
        }
        else if (options.Command == "split")
        {
            await SplitCommand.RunAsync(document, options, PasswordOf(options, getEnvironmentVariable), output, stderr, ct, progress).ConfigureAwait(false);
        }
        else if (options.Command == "redact")
        {
            await RedactCommand.RunAsync(document, options, PasswordOf(options, getEnvironmentVariable), output, stderr, ct, progress).ConfigureAwait(false);
        }
        else if (options.Command == "ocr")
        {
            await OcrCommand.RunAsync(document, options, PasswordOf(options, getEnvironmentVariable), output, stderr, ct, progress).ConfigureAwait(false);
        }
        else if (options.Command == "export")
        {
            await ExportCommand.RunAsync(document, options, output, stderr, ct, progress).ConfigureAwait(false);
        }
        else
        {
            await RunCommandAsync(document, options, output, ct).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>The password of the file: the option wins over the environment variable; empty means none.</summary>
    internal static string? PasswordOf(CliOptions options, Func<string, string?> getEnvironmentVariable)
    {
        var password = options.Password ?? getEnvironmentVariable(PasswordVariable);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    private static Task RunCommandAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct) =>
        options.Command switch
        {
            "info" => InfoCommand.RunAsync(document, options, output, ct),
            "render" => RenderCommand.RunAsync(document, options, output, ct),
            "search" => SearchCommand.RunAsync(document, options, output, ct),
            _ => TextCommand.RunAsync(document, options, output, ct),
        };

    private static async Task<PdfDocument> OpenAsync(
        CliOptions options,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string?, CancellationToken, Task<PdfDocument>>? openDocument,
        CancellationToken ct)
    {
        var password = PasswordOf(options, getEnvironmentVariable);
        try
        {
            return await (openDocument ?? new PdfEngine().OpenAsync)(options.File, password, ct).ConfigureAwait(false);
        }
        catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired)
        {
            throw new CliException($"Password required. Use --password or the {PasswordVariable} environment variable.", ex);
        }
        catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordIncorrect)
        {
            throw new CliException(ex.Message, ex);
        }
        catch (PdfOpenException ex)
        {
            throw new CliException($"Cannot open '{options.File}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Path problems that PdfEngine does not translate: NUL in the path, too long, unsupported format.
            throw new CliException($"Cannot open '{options.File}': {ex.Message}", ex);
        }
    }
}

/// <summary>What a command that handles its own arguments (<see cref="CliApp"/> routes it before opening a document) gets.</summary>
/// <param name="Args">The arguments after the command name.</param>
/// <param name="Output">Standard output (UTF-8 without BOM).</param>
/// <param name="Error">Standard error: warnings before a write, never the result.</param>
/// <param name="GetEnvironmentVariable">Reads an environment variable, <see langword="null"/> when unset.</param>
/// <param name="Progress">Receives the progress of the write (0 to 1), or <see langword="null"/>; only tests look at it.</param>
internal sealed record CliContext(string[] Args, TextWriter Output, TextWriter Error, Func<string, string?> GetEnvironmentVariable, IProgress<double>? Progress = null);

/// <summary>A command that takes several files and opens them itself.</summary>
/// <param name="context">The arguments and the streams.</param>
/// <param name="ct">Cancels the command (Ctrl+C).</param>
internal delegate Task MultiFileCommand(CliContext context, CancellationToken ct);
