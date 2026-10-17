namespace Pagewing.App;

/// <summary>Command-line options of the app.</summary>
/// <param name="Paths">Files to open, as full paths.</param>
/// <param name="MeasureStartup">Hidden switch <c>--measure-startup &lt;pdf&gt;</c>.</param>
/// <param name="MeasureSecond">Hidden switch <c>--measure-second &lt;pdfA&gt; &lt;pdfB&gt;</c>.</param>
/// <param name="MeasureScroll">Hidden switch <c>--measure-scroll &lt;pdf&gt;</c>.</param>
/// <param name="Language">UI language override <c>--lang &lt;code&gt;</c> (testing and manual checks).</param>
public sealed record StartupOptions(
    IReadOnlyList<string> Paths,
    string? MeasureStartup,
    (string First, string Second)? MeasureSecond,
    string? MeasureScroll,
    string? Language)
{
    /// <summary>
    /// Parses the command line. Arguments without <c>--</c> are paths (made absolute against the working directory).
    /// Unknown <c>--</c> options and options with missing values are ignored.
    /// </summary>
    public static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var paths = new List<string>();
        string? measureStartup = null;
        (string First, string Second)? measureSecond = null;
        string? measureScroll = null;
        string? language = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                paths.Add(Path.GetFullPath(arg));
                continue;
            }

            switch (arg)
            {
                case "--measure-startup" when i + 1 < args.Length:
                    measureStartup = args[++i];
                    break;
                case "--measure-second" when i + 2 < args.Length:
                    measureSecond = (args[i + 1], args[i + 2]);
                    i += 2;
                    break;
                case "--measure-second":
                    // Incomplete: the remaining argument (if any) belongs to the option, not to the paths.
                    i = args.Length;
                    break;
                case "--measure-scroll" when i + 1 < args.Length:
                    measureScroll = args[++i];
                    break;
                case "--lang" when i + 1 < args.Length:
                    language = args[++i];
                    break;
            }
        }

        return new StartupOptions(paths, measureStartup, measureSecond, measureScroll, language);
    }
}
