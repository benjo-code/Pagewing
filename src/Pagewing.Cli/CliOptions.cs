using System.Globalization;
using Pagewing.Cli.Commands;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;

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
    bool WholeWord = false,
    int Angle = 0,
    string? OutputFile = null,
    bool Force = false,
    int Every = 0,
    string? Ranges = null,
    bool SplitBookmarks = false,
    IReadOnlyList<string>? Texts = null,
    IReadOnlyList<RedactRect>? Rects = null,
    bool DryRun = false,
    bool AllowUnsearched = false,
    string? RasterizePages = null,
    bool RasterizeUnredactable = false,
    int RasterDpi = RedactionLimits.DefaultRasterDpi,
    IReadOnlyList<RedactionPatternKind>? Patterns = null,
    bool IncludeUnchecked = false,
    RedactionFill Fill = default,
    RedactionImageTreatment ImageFill = RedactionImageTreatment.Solid,
    RedactionImageStrength ImageStrength = RedactionImageStrength.Coarse,
    string? OverlayText = null,
    bool OverlayRepeat = false,
    string? Languages = null,
    string? Tessdata = null,
    OcrMode OcrMode = OcrMode.Skip,
    int OcrDpi = 0,
    int Jobs = 0)
{
    public const string DefaultDpi = "144";

    /// <summary>The names <c>--pattern</c> accepts, in the order the help and the messages list them.</summary>
    public static IReadOnlyList<(string Name, RedactionPatternKind Kind)> PatternNames { get; } =
    [
        ("email", RedactionPatternKind.Email),
        ("iban", RedactionPatternKind.Iban),
        ("phone", RedactionPatternKind.Phone),
        ("card", RedactionPatternKind.Card),
    ];

    /// <summary>The angles <c>rotate</c> accepts.</summary>
    private static readonly int[] Angles = [90, 180, 270, -90];

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
            "rotate" => ["--pages", "--angle", "--output", "--force", "--password"],
            "split" => ["--output", "--every", "--ranges", "--bookmarks", "--force", "--password"],
            "export" => ["--pages", "--dpi", "--output", "--no-annotations", "--force", "--password"],
            "redact" => ["--text", "--pattern", "--include-unchecked", "--rect", "--pages", "--rasterize-pages", "--rasterize-unredactable", "--dpi", "--match-case", "--dry-run", "--allow-unsearched", "--fill", "--image-fill", "--image-strength", "--overlay-text", "--overlay-repeat", "--output", "--force", "--password"],
            "ocr" => ["--language", "--tessdata", "--pages", "--mode", "--dpi", "--jobs", "--dry-run", "--output", "--force", "--password"],
            _ => throw new UsageException($"Unknown command '{command}'."),
        };

        var isSearch = command == "search";
        var isRotate = command == "rotate";
        var isSplit = command == "split";
        var isRedact = command == "redact";
        var isOcr = command == "ocr";

        // The commands of milestones 13 and 14 refuse an option given twice (the older commands keep their lenient reading);
        // --text and --rect of redact may be given as often as needed.
        var strict = command is "split" or "export" or "redact" or "ocr";
        var texts = new List<string>();
        var patterns = new List<RedactionPatternKind>();
        var includeUnchecked = false;
        var fill = RedactionFill.Black;
        var imageFill = RedactionImageTreatment.Solid;
        var imageStrength = RedactionImageStrength.Coarse;
        var imageStrengthGiven = false;
        string? overlayText = null;
        var overlayRepeat = false;
        var rects = new List<RedactRect>();
        var dryRun = false;
        var allowUnsearched = false;
        var rasterizeUnredactable = false;
        string? rasterizePages = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? everyText = null;
        string? ranges = null;
        var splitBookmarks = false;
        string? file = null;
        string? query = null;
        var matchCase = false;
        var wholeWord = false;
        var force = false;
        var optionsEnded = false;
        string? pages = null;
        string? dpiText = null;
        string? languageText = null;
        string? tessdata = null;
        string? modeText = null;
        string? jobsText = null;
        string? angleText = null;
        string? output = null;
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

                if (strict && name is not ("--text" or "--rect" or "--pattern") && !seen.Add(name))
                {
                    throw new UsageException($"The option '{name}' is given twice.");
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

                if (name is "--match-case" or "--whole-word" or "--force" or "--bookmarks" or "--dry-run" or "--allow-unsearched" or "--rasterize-unredactable" or "--include-unchecked" or "--overlay-repeat")
                {
                    if (value is not null)
                    {
                        throw new UsageException($"The option '{name}' takes no value.");
                    }

                    if (name == "--match-case")
                    {
                        matchCase = true;
                    }
                    else if (name == "--force")
                    {
                        force = true;
                    }
                    else if (name == "--bookmarks")
                    {
                        splitBookmarks = true;
                    }
                    else if (name == "--dry-run")
                    {
                        dryRun = true;
                    }
                    else if (name == "--allow-unsearched")
                    {
                        allowUnsearched = true;
                    }
                    else if (name == "--rasterize-unredactable")
                    {
                        rasterizeUnredactable = true;
                    }
                    else if (name == "--include-unchecked")
                    {
                        includeUnchecked = true;
                    }
                    else if (name == "--overlay-repeat")
                    {
                        overlayRepeat = true;
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
                    case "--language":
                        languageText = value;
                        break;
                    case "--tessdata":
                        tessdata = value;
                        break;
                    case "--mode":
                        modeText = value;
                        break;
                    case "--jobs":
                        jobsText = value;
                        break;
                    case "--dpi":
                        dpiText = value;
                        break;
                    case "--rasterize-pages":
                        rasterizePages = value;
                        break;
                    case "--angle":
                        angleText = value;
                        break;
                    case "--every":
                        everyText = value;
                        break;
                    case "--ranges":
                        ranges = value;
                        break;
                    case "--output":
                        output = value;
                        break;
                    case "--text":
                        AddTerm(texts, value);
                        break;
                    case "--rect":
                        rects.Add(RedactRect.Parse(value));
                        break;
                    case "--pattern":
                        AddPattern(patterns, value);
                        break;
                    case "--fill":
                        fill = ParseFill(value);
                        break;
                    case "--image-fill":
                        imageFill = ParseImageFill(value);
                        break;
                    case "--image-strength":
                        imageStrength = ParseImageStrength(value);
                        imageStrengthGiven = true;
                        break;
                    case "--overlay-text":
                        overlayText = ParseOverlayText(value);
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

        if (rasterizePages is not null)
        {
            try
            {
                PageRange.Validate(rasterizePages);
            }
            catch (FormatException ex)
            {
                throw new UsageException($"Invalid value for --rasterize-pages: {ex.Message}");
            }
        }

        // For redact --dpi is the resolution of the pages that become images (a whole number from 72 to 300), not of a render; for ocr
        // it is the resolution the pages are recognized at (parsed with the other options of the command below).
        var dpi = ParseDpi(isRedact || isOcr ? DefaultDpi : dpiText ?? DefaultDpi);
        if (isSplit)
        {
            if (string.IsNullOrEmpty(output))
            {
                throw new UsageException("The 'split' command needs --output <dir>.");
            }

            var every = ParseSplitMode(everyText, ranges, splitBookmarks);
            return new CliOptions(command, file, null, dpi, output, annotations, password, Force: force, Every: every, Ranges: ranges, SplitBookmarks: splitBookmarks);
        }

        if (isRedact)
        {
            if (texts.Count == 0 && patterns.Count == 0 && rects.Count == 0 && rasterizePages is null)
            {
                throw new UsageException("The 'redact' command needs at least one --text <term>, --pattern <email|iban|phone|card>, --rect <page>:<x>,<y>,<w>,<h> or --rasterize-pages <range>.");
            }

            // A page can only be refused because of a mark on it, so without a mark there is nothing to rescue.
            if (rasterizeUnredactable && texts.Count == 0 && patterns.Count == 0 && rects.Count == 0)
            {
                throw new UsageException("The option '--rasterize-unredactable' turns pages that cannot be redacted in place into images; the command has no --text, --pattern or --rect that could mark such a page (use --rasterize-pages to choose pages).");
            }

            // --include-unchecked only decides what to do with the matches of a pattern whose checksum is wrong.
            if (includeUnchecked && patterns.Count == 0)
            {
                throw new UsageException("The option '--include-unchecked' decides about pattern matches whose checksum or length is wrong; the command has no --pattern.");
            }

            var rasterDpi = RedactionLimits.DefaultRasterDpi;
            if (dpiText is not null)
            {
                if (rasterizePages is null && !rasterizeUnredactable)
                {
                    throw new UsageException("The option '--dpi' sets the resolution of pages that become images; the command has no --rasterize-pages or --rasterize-unredactable.");
                }

                if (!int.TryParse(dpiText, NumberStyles.None, CultureInfo.InvariantCulture, out rasterDpi)
                    || rasterDpi < RedactionLimits.MinRasterDpi
                    || rasterDpi > RedactionLimits.MaxRasterDpi)
                {
                    throw new UsageException($"Invalid value for --dpi: '{dpiText}' (use a whole number from {RedactionLimits.MinRasterDpi.ToString(CultureInfo.InvariantCulture)} to {RedactionLimits.MaxRasterDpi.ToString(CultureInfo.InvariantCulture)}).");
                }
            }

            if (pages is not null && texts.Count == 0 && patterns.Count == 0)
            {
                throw new UsageException("The option '--pages' limits --text and --pattern; the command has neither.");
            }

            // The strength says how coarse the blocks are; with the solid fill there are none.
            if (imageStrengthGiven && imageFill == RedactionImageTreatment.Solid)
            {
                throw new UsageException("The option '--image-strength' sets the blocks of --image-fill pixelate or --image-fill blur; the command has neither.");
            }

            // The text goes on the marks of the call; repeating it needs the text.
            if (overlayRepeat && overlayText is null)
            {
                throw new UsageException("The option '--overlay-repeat' repeats the text of --overlay-text; the command has none.");
            }

            if (overlayText is not null && texts.Count == 0 && patterns.Count == 0 && rects.Count == 0)
            {
                throw new UsageException("The option '--overlay-text' puts a text on the marked areas; the command has no --text, --pattern or --rect that marks one.");
            }

            if (output is not null && output.Length == 0)
            {
                throw new UsageException("The option '--output' needs a file name.");
            }

            if (output is null && !dryRun)
            {
                throw new UsageException("The 'redact' command needs --output <out.pdf> (only --dry-run works without it).");
            }

            return new CliOptions(command, file, pages, dpi, ".", annotations, password, MatchCase: matchCase, OutputFile: output, Force: force, Texts: texts, Rects: rects, DryRun: dryRun, AllowUnsearched: allowUnsearched, RasterizePages: rasterizePages, RasterizeUnredactable: rasterizeUnredactable, RasterDpi: rasterDpi, Patterns: patterns, IncludeUnchecked: includeUnchecked, Fill: fill, ImageFill: imageFill, ImageStrength: imageStrength, OverlayText: overlayText, OverlayRepeat: overlayRepeat);
        }

        if (isOcr)
        {
            return ParseOcr(file, pages, languageText, tessdata, modeText, dpiText, jobsText, output, dryRun, force, password, dpi, annotations);
        }

        if (isRotate)
        {
            if (pages is null)
            {
                throw new UsageException("The 'rotate' command needs --pages (for example --pages 1-3, or --pages all).");
            }

            if (angleText is null)
            {
                throw new UsageException("The 'rotate' command needs --angle (90, 180, 270 or -90).");
            }

            if (string.IsNullOrEmpty(output))
            {
                throw new UsageException("The 'rotate' command needs --output <out.pdf>.");
            }

            return new CliOptions(command, file, pages, dpi, ".", annotations, password, Angle: ParseAngle(angleText), OutputFile: output, Force: force);
        }

        if (output is not null && output.Length == 0)
        {
            throw new UsageException("The option '--output' needs a directory.");
        }

        return new CliOptions(command, file, pages, dpi, output ?? ".", annotations, password, query, matchCase, wholeWord, Force: force);
    }

    /// <summary>
    /// The rest of the parse of <c>pagewing ocr</c>: the languages (<c>[a-z_]+</c> joined with <c>+</c>), the mode (<c>skip</c>, <c>redo</c>, <c>force</c>),
    /// the resolution (whole number from 150 to 600, default automatic), the jobs (1 to 16) and the output file (not needed with <c>--dry-run</c>).
    /// </summary>
    private static CliOptions ParseOcr(
        string file,
        string? pages,
        string? languageText,
        string? tessdata,
        string? modeText,
        string? dpiText,
        string? jobsText,
        string? output,
        bool dryRun,
        bool force,
        string? password,
        double dpi,
        bool annotations)
    {
        string? languages = null;
        if (languageText is not null)
        {
            languages = OcrLanguages.Normalize(languageText)
                ?? throw new UsageException($"Invalid value for --language: '{Shown(languageText)}' (use the names of language models, letters a to z and _, joined with +, for example deu+eng).");
        }

        if (tessdata is not null && tessdata.Length == 0)
        {
            throw new UsageException("The option '--tessdata' needs a folder.");
        }

        var mode = OcrMode.Skip;
        if (modeText is not null)
        {
            mode = modeText.ToLowerInvariant() switch
            {
                "skip" => OcrMode.Skip,
                "redo" => OcrMode.Redo,
                "force" => OcrMode.Force,
                _ => throw new UsageException($"Invalid value for --mode: '{Shown(modeText)}' (use skip, redo or force)."),
            };
        }

        var ocrDpi = 0;
        if (dpiText is not null
            && (!int.TryParse(dpiText, NumberStyles.None, CultureInfo.InvariantCulture, out ocrDpi) || ocrDpi < OcrResolution.MinDpi || ocrDpi > OcrOptions.MaxDpi))
        {
            throw new UsageException($"Invalid value for --dpi: '{Shown(dpiText)}' (use a whole number from {OcrResolution.MinDpi.ToString(CultureInfo.InvariantCulture)} to {OcrOptions.MaxDpi.ToString(CultureInfo.InvariantCulture)}).");
        }

        var jobs = 0;
        if (jobsText is not null
            && (!int.TryParse(jobsText, NumberStyles.None, CultureInfo.InvariantCulture, out jobs) || jobs < 1 || jobs > OcrOptions.MaxJobs))
        {
            throw new UsageException($"Invalid value for --jobs: '{Shown(jobsText)}' (use a whole number from 1 to {OcrOptions.MaxJobs.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (output is not null && output.Length == 0)
        {
            throw new UsageException("The option '--output' needs a file name.");
        }

        if (output is null && !dryRun)
        {
            throw new UsageException("The 'ocr' command needs --output <out.pdf> (only --dry-run works without it).");
        }

        return new CliOptions("ocr", file, pages, dpi, ".", annotations, password, OutputFile: output, Force: force, DryRun: dryRun, Languages: languages, Tessdata: tessdata, OcrMode: mode, OcrDpi: ocrDpi, Jobs: jobs);
    }

    private static string Shown(string value) => value.Length > 40 ? value[..40] + "..." : value;

    /// <summary>Reads the value of <c>--fill</c>: <c>black</c>, <c>white</c> or a colour written as <c>#RRGGBB</c>.</summary>
    private static RedactionFill ParseFill(string value)
    {
        if (string.Equals(value, "black", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionFill.Black;
        }

        if (string.Equals(value, "white", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionFill.White;
        }

        if (RedactionFill.TryParse(value, out var fill))
        {
            return fill;
        }

        var shown = value.Length > 40 ? value[..40] + "..." : value;
        throw new UsageException($"Invalid value for --fill: '{shown}' (use black, white or a colour written as #RRGGBB).");
    }

    /// <summary>Reads the value of <c>--image-fill</c>: <c>solid</c> (the default), <c>pixelate</c> or <c>blur</c>.</summary>
    private static RedactionImageTreatment ParseImageFill(string value)
    {
        if (string.Equals(value, "solid", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionImageTreatment.Solid;
        }

        if (string.Equals(value, "pixelate", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionImageTreatment.Pixelate;
        }

        if (string.Equals(value, "blur", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionImageTreatment.Blur;
        }

        var shown = value.Length > 40 ? value[..40] + "..." : value;
        throw new UsageException($"Invalid value for --image-fill: '{shown}' (use solid, pixelate or blur).");
    }

    /// <summary>Reads the value of <c>--image-strength</c>: <c>coarse</c> (up to 6 blocks along an axis) or <c>very-coarse</c> (up to 3).</summary>
    private static RedactionImageStrength ParseImageStrength(string value)
    {
        if (string.Equals(value, "coarse", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionImageStrength.Coarse;
        }

        if (string.Equals(value, "very-coarse", StringComparison.OrdinalIgnoreCase))
        {
            return RedactionImageStrength.VeryCoarse;
        }

        var shown = value.Length > 40 ? value[..40] + "..." : value;
        throw new UsageException($"Invalid value for --image-strength: '{shown}' (use coarse or very-coarse).");
    }

    /// <summary>
    /// Reads the value of <c>--overlay-text</c>: at most <see cref="RedactionLimits.MaxOverlayTextLength"/> characters, not empty, and only
    /// characters the font of the text can draw. A text with any other character is refused as a whole; the message names the position
    /// (from 1), never the text.
    /// </summary>
    private static string ParseOverlayText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException("The option '--overlay-text' needs a text that is not empty and not only white space.");
        }

        if (value.Length > RedactionLimits.MaxOverlayTextLength)
        {
            throw new UsageException($"The option '--overlay-text' takes at most {RedactionLimits.MaxOverlayTextLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (!TextBoxCharacters.IsSupported(value[i]))
            {
                throw new UsageException($"The option '--overlay-text' has a character the text on the areas cannot draw (unsupported character at position {(i + 1).ToString(CultureInfo.InvariantCulture)}); the text is not changed, give another.");
            }
        }

        return value;
    }

    private static void AddTerm(List<string> texts, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException("The option '--text' needs a term that is not empty and not only white space.");
        }

        if (value.Length > RedactionLimits.MaxTermLength)
        {
            throw new UsageException($"The option '--text' takes at most {RedactionLimits.MaxTermLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (!texts.Contains(value, StringComparer.Ordinal))
        {
            texts.Add(value);
        }
    }

    private static void AddPattern(List<RedactionPatternKind> patterns, string value)
    {
        foreach (var (name, kind) in PatternNames)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                if (!patterns.Contains(kind))
                {
                    patterns.Add(kind);
                }

                return;
            }
        }

        // The value is part of the message; a control character never reaches the terminal.
        var shown = string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
        throw new UsageException($"Invalid value for --pattern: '{shown}' (use one of: {string.Join(", ", PatternNames.Select(static p => p.Name))}).");
    }

    /// <summary>Checks that exactly one way of cutting was chosen and that it is well formed; returns <c>--every</c> (0 when another way was chosen).</summary>
    private static int ParseSplitMode(string? everyText, string? ranges, bool bookmarks)
    {
        var chosen = (everyText is not null ? 1 : 0) + (ranges is not null ? 1 : 0) + (bookmarks ? 1 : 0);
        if (chosen != 1)
        {
            throw new UsageException("The 'split' command needs exactly one of --every <n>, --ranges <list> or --bookmarks.");
        }

        if (ranges is not null)
        {
            try
            {
                // One part per comma separated range; each is a selection in the syntax of --pages.
                foreach (var segment in ranges.Split(','))
                {
                    PageRange.Validate(segment);
                }
            }
            catch (FormatException ex)
            {
                throw new UsageException($"Invalid value for --ranges: {ex.Message}");
            }

            return 0;
        }

        if (everyText is null)
        {
            return 0;
        }

        if (!int.TryParse(everyText, NumberStyles.None, CultureInfo.InvariantCulture, out var every) || every < 1)
        {
            throw new UsageException($"Invalid value for --every: '{everyText}' is not a whole number of pages of at least 1.");
        }

        return every;
    }

    private static int ParseAngle(string text)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var angle) || !Angles.Contains(angle))
        {
            throw new UsageException($"Invalid value for --angle: '{text}' (use 90, 180, 270 or -90).");
        }

        return angle;
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
