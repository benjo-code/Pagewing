using System.Runtime.ExceptionServices;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.Cli.Commands;

/// <summary>One input file of <c>merge</c> with the options that came after it.</summary>
/// <param name="Path">The file as the user wrote it.</param>
/// <param name="Pages">Its <c>--pages</c> (<see langword="null"/>: all pages).</param>
/// <param name="Password">Its <c>--password</c>, or <see langword="null"/> to use the environment variable.</param>
internal sealed record MergeInput(string Path, string? Pages, string? Password);

/// <summary>The parsed command line of <c>merge</c>.</summary>
/// <param name="Output">The file to write.</param>
/// <param name="Force">Whether an existing output may be replaced.</param>
/// <param name="Inputs">The input files in order; the first is the main file.</param>
internal sealed record MergeRequest(string Output, bool Force, IReadOnlyList<MergeInput> Inputs);

/// <summary>
/// <c>pagewing merge</c>: the pages of several files in one new file. Like <c>qpdf --pages</c>, <c>--pages</c> and
/// <c>--password</c> belong to the file they follow; <c>--output</c> and <c>--force</c> belong to the command and may stand
/// anywhere. The first file is the main file: only its bookmarks, form, page labels and information are kept, and the warnings
/// before the write say what the others lose. The result is written by the same checked, atomic full rewrite as the other
/// commands; an encrypted main file stays encrypted, and an encrypted file inserted into an unencrypted one is refused.
/// A file whose name starts with a dash must be written <c>./-name.pdf</c> (or after <c>--</c>, which ends the options).
/// </summary>
internal static class MergeCommand
{
    public static async Task RunAsync(CliContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = Parse(context.Args);
        var inputs = request.Inputs;
        if (inputs.Count - 1 > PageEditLimits.MaxInsertedFiles)
        {
            throw new CliException($"Too many files: merge takes the pages of at most {PageEditLimits.MaxInsertedFiles} other files besides the first (got {inputs.Count - 1}).");
        }

        // Before anything is opened: the output is none of the inputs (any of them, also by link), and not a file to keep.
        foreach (var input in inputs)
        {
            OutputGuard.Check(input.Path, request.Output, force: true);
        }

        OutputGuard.Check(inputs[0].Path, request.Output, request.Force);

        var engine = new PdfEngine();
        var documents = new List<PdfDocument>(inputs.Count);
        ExceptionDispatchInfo? closeFailure = null;
        try
        {
            var passwords = new string?[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                passwords[i] = PasswordOf(inputs[i], context.GetEnvironmentVariable);
                documents.Add(await OpenAsync(engine, inputs[i], passwords[i], ct).ConfigureAwait(false));
            }

            await MergeAsync(context, request, engine, documents, passwords, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var document in documents)
            {
                try
                {
                    await document.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Every document gets its close, and a close failure never replaces an error that is already on its way
                    // (this block must not throw); it is reported like rotate does, after the success, below.
                    closeFailure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
        }

        await context.Output.WriteLineAsync(request.Output.AsMemory(), ct).ConfigureAwait(false);
        await context.Output.FlushAsync(ct).ConfigureAwait(false);
        closeFailure?.Throw();
    }

    /// <summary>Reads the command line of <c>merge</c>.</summary>
    /// <exception cref="UsageException">An option is unknown, given twice, without value or in the wrong place, or the files or the output are missing.</exception>
    internal static MergeRequest Parse(IReadOnlyList<string> args)
    {
        string? output = null;
        var force = false;
        var optionsEnded = false;
        var paths = new List<string>();
        var pages = new List<string?>();
        var passwords = new List<string?>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
            }
            else if (!optionsEnded && arg.Length > 2 && arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg;
                string? value = null;
                var equals = arg.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    name = arg[..equals];
                    value = arg[(equals + 1)..];
                }

                if (name is not ("--output" or "--force" or "--pages" or "--password"))
                {
                    throw new UsageException($"Unknown option '{name}' for 'merge'.");
                }

                if (name == "--force")
                {
                    if (value is not null)
                    {
                        throw new UsageException("The option '--force' takes no value.");
                    }

                    force = true;
                    continue;
                }

                if (value is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new UsageException($"The option '{name}' needs a value.");
                    }

                    value = args[++i];
                }

                switch (name)
                {
                    case "--output":
                        if (output is not null)
                        {
                            throw new UsageException("The option '--output' is given twice.");
                        }

                        output = value.Length > 0 ? value : throw new UsageException("The option '--output' needs the name of the file to write.");
                        break;
                    case "--pages":
                        SetForLastFile(pages, paths.Count, name, ValidPages(value));
                        break;
                    default:
                        SetForLastFile(passwords, paths.Count, name, value);
                        break;
                }
            }
            else if (!optionsEnded && arg.Length > 1 && arg[0] == '-')
            {
                throw new UsageException($"Unknown option '{arg}' for 'merge'.");
            }
            else
            {
                if (arg.Length == 0)
                {
                    throw new UsageException("A file name is empty.");
                }

                paths.Add(arg);
                pages.Add(null);
                passwords.Add(null);
            }
        }

        if (string.IsNullOrEmpty(output))
        {
            throw new UsageException("The 'merge' command needs --output <out.pdf>.");
        }

        if (paths.Count < 2)
        {
            throw new UsageException("The 'merge' command needs at least two PDF files.");
        }

        return new MergeRequest(output, force, [.. paths.Select((path, index) => new MergeInput(path, pages[index], passwords[index]))]);
    }

    private static string ValidPages(string value)
    {
        try
        {
            PageRange.Validate(value);
            return value;
        }
        catch (FormatException ex)
        {
            throw new UsageException($"Invalid value for --pages: {ex.Message}");
        }
    }

    // An option after a file belongs to that file: it goes into the slot of the file that came last, once.
    private static void SetForLastFile(List<string?> slots, int fileCount, string name, string value)
    {
        if (fileCount == 0)
        {
            throw new UsageException($"The option '{name}' belongs to a file: write it after the file it is meant for.");
        }

        if (slots[fileCount - 1] is not null)
        {
            throw new UsageException($"The option '{name}' is given twice for the same file.");
        }

        slots[fileCount - 1] = value;
    }

    private static string? PasswordOf(MergeInput input, Func<string, string?> getEnvironmentVariable)
    {
        var password = input.Password ?? getEnvironmentVariable(CliApp.PasswordVariable);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    private static async Task<PdfDocument> OpenAsync(PdfEngine engine, MergeInput input, string? password, CancellationToken ct)
    {
        try
        {
            return await engine.OpenAsync(input.Path, password, ct).ConfigureAwait(false);
        }
        catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired)
        {
            throw new CliException($"Password required for '{input.Path}'. Use --password after that file or the {CliApp.PasswordVariable} environment variable.", ex);
        }
        catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordIncorrect)
        {
            throw new CliException($"The password for '{input.Path}' is incorrect.", ex);
        }
        catch (PdfOpenException ex)
        {
            throw new CliException($"Cannot open '{input.Path}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new CliException($"Cannot open '{input.Path}': {ex.Message}", ex);
        }
    }

    private static async Task MergeAsync(
        CliContext context,
        MergeRequest request,
        PdfEngine engine,
        List<PdfDocument> documents,
        string?[] passwords,
        CancellationToken ct)
    {
        var inputs = request.Inputs;
        try
        {
            var primary = documents[0];
            var plan = PageEditPlan.Identity(primary.PageCount);
            var sources = new Dictionary<PageSourceId, PdfDocument>();
            var files = new Dictionary<PageSourceId, PageSourceFile>();
            for (var i = 0; i < inputs.Count; i++)
            {
                var pages = SelectPages(inputs[i], documents[i].PageCount);
                if (i == 0)
                {
                    // The main file keeps the pages of its own selection, in their order: the others are deleted.
                    var drop = Enumerable.Range(0, primary.PageCount).Except(pages).ToArray();
                    plan = drop.Length == 0 ? plan : plan.Delete(drop);
                    continue;
                }

                var id = new PageSourceId(i);
                plan = plan.InsertFrom(id, pages, plan.Slots.Count);
                sources[id] = documents[i];
                files[id] = PageSourceFile.Of(Path.GetFullPath(inputs[i].Path), passwords[i], documents[i]);
            }

            string NameOf(int source) => inputs[source].Path;

            var report = await PageEditExecutor.AnalyzeAsync(plan, primary, sources, ct).ConfigureAwait(false);
            if (report.IsRefused)
            {
                throw new CliException($"The page changes cannot be applied: {PageLossText.RefusalOf(report, NameOf)}");
            }

            // The user is told what is lost before the first byte is written.
            PageLossText.WriteWarnings(report, context.Error, NameOf);
            await context.Error.FlushAsync(ct).ConfigureAwait(false);

            // The output is none of the inputs (OutputGuard), so there is no stamp of "the original" to compare at the target.
            var apply = new PageApplyRequest(plan, Path.GetFullPath(request.Output), files, ReplaceExisting: request.Force);
            var saved = await PageEditApplier.WriteAsync(engine, primary, Path.GetFullPath(inputs[0].Path), passwords[0], openedStamp: null, apply, context.Progress, ct).ConfigureAwait(false);
            await saved.Reopened.DisposeAsync().ConfigureAwait(false);
        }
        catch (PageEditException ex)
        {
            throw new CliException(ex.Message, ex);
        }
        catch (SaveFailureException ex)
        {
            throw new CliException(SaveFailureText.Describe(ex, request.Output), ex);
        }
    }

    // The pages of one file, with the file named when a page is beyond its end.
    private static IReadOnlyList<int> SelectPages(MergeInput input, int pageCount)
    {
        try
        {
            return CliApp.SelectPages(input.Pages, pageCount);
        }
        catch (CliException ex)
        {
            throw new CliException($"'{input.Path}': {ex.Message}", ex);
        }
    }
}
