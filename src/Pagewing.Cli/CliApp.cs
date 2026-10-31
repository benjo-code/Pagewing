using System.Text;
using Pagewing.Cli.Commands;
using Pagewing.Core;
using Pagewing.Core.Pdf;

namespace Pagewing.Cli;

/// <summary>
/// The command line of Pagewing: <c>info</c>, <c>render</c>, <c>text</c>, <c>search</c> and <c>rotate</c>. Exit codes: 0 success, 1 runtime
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

        Options:
          --pages <range>    Pages to process: 3, 2-5, 1,3,5-7 or all (default: all; rotate: required).
          --dpi <n>          Resolution of rendered pages (default: 144).
          --output <dir>     Directory for the PNG files, created if missing (default: .).
                             For rotate: the PDF file to write (required).
          --angle <deg>      Rotate: turn the pages by 90, 180, 270 or -90 degrees (required).
          --force            Commands that write a file: replace an existing output file. The
                             output is never the input, with or without --force.
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

    /// <summary>
    /// Commands that take several files and so cannot open "the" document first (<c>merge</c>, milestone 13 task 14): they
    /// parse their own arguments and open their own documents, and are routed before a single document is opened. Empty
    /// until such a command exists.
    /// </summary>
    private static readonly Dictionary<string, MultiFileCommand> MultiFileCommands = new(StringComparer.Ordinal);

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
            return await ExecuteAsync(args, output, stderr, getEnvironmentVariable, openDocument, runCommand, ct).ConfigureAwait(false);
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
        TextWriter stderr,
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

        if (MultiFileCommands.TryGetValue(args[0], out var multiFile))
        {
            await multiFile(new CliContext(args[1..], output, stderr, getEnvironmentVariable), ct).ConfigureAwait(false);
            return 0;
        }

        var options = CliOptions.Parse(args[0], args.AsSpan(1));

        // Before anything is opened: a command that writes a file never writes over its input or over a file it was not told to replace.
        if (options.OutputFile is not null)
        {
            OutputGuard.Check(options.File, options.OutputFile, options.Force);
        }

        // The document is disposed inside the try block of RunAsync, so a failure while closing it is reported, too.
        await using var document = await OpenAsync(options, getEnvironmentVariable, openDocument, ct).ConfigureAwait(false);
        if (runCommand is not null)
        {
            await runCommand(document, options, output, ct).ConfigureAwait(false);
        }
        else if (options.Command == "rotate")
        {
            await RotateCommand.RunAsync(document, options, PasswordOf(options, getEnvironmentVariable), output, stderr, ct).ConfigureAwait(false);
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
internal sealed record CliContext(string[] Args, TextWriter Output, TextWriter Error, Func<string, string?> GetEnvironmentVariable);

/// <summary>A command that takes several files and opens them itself.</summary>
/// <param name="context">The arguments and the streams.</param>
/// <param name="ct">Cancels the command (Ctrl+C).</param>
internal delegate Task MultiFileCommand(CliContext context, CancellationToken ct);
