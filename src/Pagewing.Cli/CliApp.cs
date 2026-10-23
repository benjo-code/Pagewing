using System.Text;
using Pagewing.Cli.Commands;
using Pagewing.Core;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli;

/// <summary>
/// The command line of Pagewing: <c>info</c>, <c>render</c>, <c>text</c> and <c>search</c>. Exit codes: 0 success, 1 runtime
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

        Options:
          --pages <range>    Pages to process: 3, 2-5, 1,3,5-7 or all (default: all).
          --dpi <n>          Resolution of rendered pages (default: 144).
          --output <dir>     Directory for the PNG files, created if missing (default: .).
          --no-annotations   Render without annotations.
          --match-case       Search: distinguish upper and lower case (default: ignore case).
          --whole-word       Search: match only whole words.
          --password <pw>    Password of an encrypted PDF.

        The password can also be given in the environment variable PAGEWING_PASSWORD;
        the option wins. Command-line arguments are visible to other processes on this
        computer (for example in the process list), so prefer the environment variable.

        Ctrl+C cancels the command and exits with code 1. Press Ctrl+C again to abort immediately.

        Exit codes: 0 success, 1 error, 2 invalid command line.

        """;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        StreamWriter? output = null;
        try
        {
            output = new StreamWriter(stdout, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
            return await ExecuteAsync(args, output, getEnvironmentVariable, openDocument, runCommand, ct).ConfigureAwait(false);
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
        catch (ArgumentOutOfRangeException ex) when (ex.ActualValue is int page)
        {
            var noun = pageCount == 1 ? "page" : "pages";
            throw new CliException($"Page {page} is out of range (the document has {pageCount} {noun}).");
        }
    }

    private static async Task<int> ExecuteAsync(
        string[] args,
        StreamWriter output,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string?, CancellationToken, Task<PdfDocument>>? openDocument,
        Func<PdfDocument, CliOptions, TextWriter, CancellationToken, Task>? runCommand,
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

        var options = CliOptions.Parse(args[0], args.AsSpan(1));

        // The document is disposed inside the try block of RunAsync, so a failure while closing it is reported, too.
        await using var document = await OpenAsync(options, getEnvironmentVariable, openDocument, ct).ConfigureAwait(false);
        await (runCommand ?? RunCommandAsync)(document, options, output, ct).ConfigureAwait(false);
        return 0;
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
        var password = options.Password ?? getEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            password = null;
        }

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
