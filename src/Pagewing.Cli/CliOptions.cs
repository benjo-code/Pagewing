using System.Globalization;

namespace Pagewing.Cli;

/// <summary>The command line was wrong (exit code 2). The message is shown with the usage text.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}

/// <summary>A runtime problem with a clear message (exit code 1).</summary>
internal sealed class CliException : Exception
{
    public CliException(string message)
        : base(message)
    {
    }

    public CliException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The parsed command line of one command.</summary>
internal sealed record CliOptions(
    string Command,
    string File,
    string? Pages,
    double Dpi,
    string OutputDirectory,
    bool Annotations,
    string? Password,
    string? Query = null,
    bool MatchCase = false,
    bool WholeWord = false)
{
    public const string DefaultDpi = "144";

    /// <summary>Gets the pixels per point of <see cref="Dpi"/> (72 dpi = 1).</summary>
    public double Scale => Dpi / 72.0;

    /// <summary>Parses <c>args</c> (without the command name handled by the caller) for <paramref name="command"/>.</summary>
    /// <remarks>
    /// <c>search</c> takes two positional arguments, the file and then the query. A <c>--</c> ends option parsing, so
    /// that a query may start with a dash.
    /// </remarks>
    /// <exception cref="UsageException">An option is unknown, has no value or an invalid value, or the file (or the query of <c>search</c>) is missing or given twice.</exception>
    public static CliOptions Parse(string command, ReadOnlySpan<string> args)
    {
        var allowed = command switch
        {
            "info" => new[] { "--password" },
            "render" => ["--pages", "--dpi", "--output", "--no-annotations", "--password"],
            "text" => ["--pages", "--password"],
            "search" => ["--pages", "--match-case", "--whole-word", "--password"],
            _ => throw new UsageException($"Unknown command '{command}'."),
        };

        var isSearch = command == "search";
        string? file = null;
        string? query = null;
        var matchCase = false;
        var wholeWord = false;
        var optionsEnded = false;
        string? pages = null;
        string? dpiText = null;
        string output = ".";
        var annotations = true;
        string? password = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
            }
            else if (!optionsEnded && arg.Length > 2 && arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg;
                string? value = null;
                var equals = arg.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    name = arg[..equals];
                    value = arg[(equals + 1)..];
                }

                if (!allowed.Contains(name, StringComparer.Ordinal))
                {
                    throw new UsageException($"Unknown option '{name}' for '{command}'.");
                }

                if (name == "--no-annotations")
                {
                    if (value is not null)
                    {
                        throw new UsageException("The option '--no-annotations' takes no value.");
                    }

                    annotations = false;
                    continue;
                }

                if (name is "--match-case" or "--whole-word")
                {
                    if (value is not null)
                    {
                        throw new UsageException($"The option '{name}' takes no value.");
                    }

                    if (name == "--match-case")
                    {
                        matchCase = true;
                    }
                    else
                    {
                        wholeWord = true;
                    }

                    continue;
                }

                if (value is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException($"The option '{name}' needs a value.");
                    }

                    value = args[++i];
                }

                switch (name)
                {
                    case "--pages":
                        pages = value;
                        break;
                    case "--dpi":
                        dpiText = value;
                        break;
                    case "--output":
                        output = value;
                        break;
                    default:
                        password = value;
                        break;
                }
            }
            else if (!optionsEnded && arg.Length > 1 && arg[0] == '-')
            {
                throw new UsageException($"Unknown option '{arg}' for '{command}'.");
            }
            else if (file is null)
            {
                file = arg;
            }
            else if (isSearch && query is null)
            {
                query = arg;
            }
            else
            {
                throw new UsageException(isSearch
                    ? $"Unexpected argument '{arg}'; 'search' takes a file and a query."
                    : $"Unexpected argument '{arg}'; '{command}' takes a single file.");
            }
        }

        if (string.IsNullOrEmpty(file))
        {
            throw new UsageException($"The '{command}' command needs a PDF file.");
        }

        if (isSearch && string.IsNullOrWhiteSpace(query))
        {
            throw new UsageException("The 'search' command needs a non-empty query.");
        }

        if (pages is not null)
        {
            try
            {
                PageRange.Validate(pages);
            }
            catch (FormatException ex)
            {
                throw new UsageException($"Invalid value for --pages: {ex.Message}");
            }
        }

        var dpi = ParseDpi(dpiText ?? DefaultDpi);
        if (string.IsNullOrEmpty(output))
        {
            throw new UsageException("The option '--output' needs a directory.");
        }

        return new CliOptions(command, file, pages, dpi, output, annotations, password, query, matchCase, wholeWord);
    }

    private static double ParseDpi(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) || !double.IsFinite(dpi) || dpi <= 0)
        {
            throw new UsageException($"Invalid value for --dpi: '{text}' is not a positive number.");
        }

        return dpi;
    }
}
