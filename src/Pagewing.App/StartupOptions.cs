using Pagewing.Core.Distribution;

namespace Pagewing.App;

/// <summary>Command-line options of the app.</summary>
/// <param name="Paths">Files to open, as full paths.</param>
/// <param name="MeasureStartup">Hidden switch <c>--measure-startup &lt;pdf&gt;</c>.</param>
/// <param name="MeasureSecond">Hidden switch <c>--measure-second &lt;pdfA&gt; &lt;pdfB&gt;</c>.</param>
/// <param name="MeasureScroll">Hidden switch <c>--measure-scroll &lt;pdf&gt;</c>.</param>
/// <param name="Language">UI language override <c>--lang &lt;code&gt;</c> (testing and manual checks).</param>
/// <param name="MeasureZoom">
/// <c>--zoom &lt;percent&gt;</c> of <c>--measure-scroll</c> as given (checked by the measurement), an empty string when the
/// value is missing, or <see langword="null"/> without the option.
/// </param>
/// <param name="MeasureSettings">
/// Hidden switch <c>--measure-settings &lt;file&gt;</c> as given: the settings file a measurement run starts with (only
/// read, never written), an empty string when the value is missing (the measurement fails), or <see langword="null"/>
/// without the option. Ignored without a measurement switch, like <c>--zoom</c>.
/// </param>
/// <param name="RegisterFileTypes">
/// Switch <c>--register-file-types[=user|machine]</c> (Windows: register Pagewing for PDF files and exit), or
/// <see langword="null"/> without it or with an unknown scope.
/// </param>
/// <param name="UnregisterFileTypes">Switch <c>--unregister-file-types[=user|machine]</c>, like <paramref name="RegisterFileTypes"/>.</param>
/// <param name="RestoreSession">
/// Hidden switch <c>--restore-session</c>, which the updater passes when it starts the new version: the windows and tabs
/// of the session file (<c>SessionStore</c>) open again. Only then is the file read. Never set for a measurement.
/// </param>
public sealed record StartupOptions(
    IReadOnlyList<string> Paths,
    string? MeasureStartup,
    (string First, string Second)? MeasureSecond,
    string? MeasureScroll,
    string? Language,
    string? MeasureZoom = null,
    string? MeasureSettings = null,
    RegistrationScope? RegisterFileTypes = null,
    RegistrationScope? UnregisterFileTypes = null,
    bool RestoreSession = false)
{
    /// <summary>Gets whether a hidden measurement switch was given (the run bypasses the single-instance election).</summary>
    public bool IsMeasurement => MeasureStartup is not null || MeasureSecond is not null || MeasureScroll is not null;

    /// <summary>
    /// Gets the file a measurement opens first (<c>--measure-startup</c> before <c>--measure-second</c> before
    /// <c>--measure-scroll</c>, as in <c>MeasurementRun.Create</c>), or <see langword="null"/> without a measurement.
    /// </summary>
    public string? MeasurementPath => MeasureStartup ?? MeasureSecond?.First ?? MeasureScroll;

    /// <summary>
    /// Gets the files that open as further tabs after the first frame: the paths after the first one (which
    /// <c>Program.Main</c> opens early), so that with it at most <see cref="ViewModels.MainViewModel.MaxFilesPerOpen"/>
    /// files open. Empty for a measurement, which opens only its own file.
    /// </summary>
    public IReadOnlyList<string> MorePaths =>
        IsMeasurement ? [] : [.. Paths.Skip(1).Take(ViewModels.MainViewModel.MaxFilesPerOpen - 1)];

    /// <summary>
    /// Parses the command line. Arguments without <c>--</c> are paths (made absolute against the working directory).
    /// Unknown <c>--</c> options and options with missing values are ignored, except <c>--zoom</c> and
    /// <c>--measure-settings</c> (see <see cref="MeasureZoom"/> and <see cref="MeasureSettings"/>).
    /// </summary>
    public static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var paths = new List<string>();
        string? measureStartup = null;
        (string First, string Second)? measureSecond = null;
        string? measureScroll = null;
        string? language = null;
        string? measureZoom = null;
        string? measureSettings = null;
        RegistrationScope? register = null;
        RegistrationScope? unregister = null;
        var restoreSession = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                // An empty argument ("" from a script) names no file.
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                paths.Add(FullPathOrAsGiven(arg));
                continue;
            }

            if (TryParseScopeSwitch(arg, "--register-file-types", out var registerScope))
            {
                register = registerScope;
                continue;
            }

            if (TryParseScopeSwitch(arg, "--unregister-file-types", out var unregisterScope))
            {
                unregister = unregisterScope;
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
                case "--zoom":
                    // Without a value the measurement fails instead of scrolling at 100 %.
                    measureZoom = i + 1 < args.Length ? args[++i] : string.Empty;
                    break;
                case "--measure-settings":
                    // Without a value the measurement fails instead of measuring without settings.
                    measureSettings = i + 1 < args.Length ? args[++i] : string.Empty;
                    break;
                case "--restore-session":
                    restoreSession = true;
                    break;
                case "--lang" when i + 1 < args.Length:
                    language = args[++i];
                    break;
            }
        }

        var options = new StartupOptions(paths, measureStartup, measureSecond, measureScroll, language, measureZoom, measureSettings, register, unregister);

        // A measurement never restores a session: it measures the start with its own file only.
        return restoreSession && !options.IsMeasurement ? options with { RestoreSession = true } : options;
    }

    // "--name" means the user scope, "--name=user|machine" the given one; an unknown scope counts as not given.
    private static bool TryParseScopeSwitch(string arg, string name, out RegistrationScope? scope)
    {
        scope = null;
        if (arg.Equals(name, StringComparison.Ordinal))
        {
            scope = RegistrationScope.User;
            return true;
        }

        if (!arg.StartsWith(name + "=", StringComparison.Ordinal))
        {
            return false;
        }

        var value = arg[(name.Length + 1)..];
        if (value.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            scope = RegistrationScope.User;
        }
        else if (value.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            scope = RegistrationScope.Machine;
        }

        return true;
    }

    // A path the system rejects (for example with a NUL character) must not stop the start: it is kept as given
    // and fails when it is opened, as a "cannot be read" error card (ViewerSession.OpenAsync).
    private static string FullPathOrAsGiven(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }
}
