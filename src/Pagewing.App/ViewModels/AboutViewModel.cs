namespace Pagewing.App.ViewModels;

/// <summary>
/// The about card (<see cref="OverlayKind.About"/>): name, version, licence, the offline promise and the third-party
/// notices. The notices are about 390 KB; they are read when they are first asked for (the user opens the list), never
/// when the card is made.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    /// <summary>The longest run of lines in one row of the notice list; a row is one text block, so it stays small.</summary>
    internal const int MaxLinesPerRow = 24;

    private const string ResourceName = "Pagewing.App.Resources.ThirdPartyNotices.txt";

    private readonly Func<string> _loadNotices;
    private string? _noticesText;
    private IReadOnlyList<NoticeRow>? _notices;
    private bool _noticesOpen;

    /// <summary>Makes the view model.</summary>
    /// <param name="version">The version of the program.</param>
    /// <param name="loadNotices">Reads the text of the notices; called at most once, when they are first asked for.</param>
    public AboutViewModel(string version, Func<string> loadNotices)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(loadNotices);
        Version = version;
        _loadNotices = loadNotices;
    }

    /// <summary>Gets the name of the program.</summary>
    public string Name { get; } = "Pagewing";

    /// <summary>Gets the version of the program.</summary>
    public string Version { get; }

    /// <summary>Gets the version line of the card ("Version 1.2.3").</summary>
    public string VersionLine => Resources.Strings.AboutVersion.Replace("{0}", Version, StringComparison.Ordinal);

    /// <summary>Gets the licence of the program (an SPDX identifier, not translated).</summary>
    public string LicenseLine { get; } = "GPL-3.0-or-later";

    /// <summary>Gets the line that says there is no telemetry and no network access.</summary>
    public string OfflineLine { get; } = Resources.Strings.AboutOffline;

    /// <summary>Gets or sets whether the list of notices is open; opening it reads the notices.</summary>
    public bool NoticesOpen
    {
        get => _noticesOpen;
        set => SetProperty(ref _noticesOpen, value);
    }

    /// <summary>Gets the whole text of the third-party notices, read on the first access.</summary>
    public string NoticesText => _noticesText ??= _loadNotices();

    /// <summary>Gets the notices in rows for a virtualizing list (read on the first access).</summary>
    public IReadOnlyList<NoticeRow> Notices => _notices ??= Split(NoticesText);

    /// <summary>Reads the notices embedded in this assembly.</summary>
    internal static string LoadEmbedded()
    {
        using var stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The third-party notices are not embedded.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Cuts the text into rows. An entry starts after a line "====": its first line (the name) is a row of its own, the
    /// rest goes in rows of at most <see cref="MaxLinesPerRow"/> lines. The biggest entry (a licence text) has about
    /// 140 KB; as one text block it would take seconds to measure.
    /// </summary>
    internal static IReadOnlyList<NoticeRow> Split(string text)
    {
        var rows = new List<NoticeRow>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var run = new List<string>();
        var titleNext = false;

        void Flush()
        {
            // Blank lines at the ends of a row add nothing; the spacing between rows does that.
            var start = 0;
            var end = run.Count;
            while (start < end && run[start].Length == 0)
            {
                start++;
            }

            while (end > start && run[end - 1].Length == 0)
            {
                end--;
            }

            if (end > start)
            {
                rows.Add(new NoticeRow(string.Join('\n', run.Skip(start).Take(end - start)), false));
            }

            run.Clear();
        }

        foreach (var line in lines)
        {
            if (line == "====")
            {
                Flush();
                titleNext = true;
                continue;
            }

            if (titleNext)
            {
                titleNext = false;
                if (line.Length > 0)
                {
                    rows.Add(new NoticeRow(line, true));
                    continue;
                }
            }

            run.Add(line);
            if (run.Count >= MaxLinesPerRow)
            {
                Flush();
            }
        }

        Flush();
        return rows;
    }
}

/// <summary>A row of the notice list: some lines of text, or the name of a component.</summary>
/// <param name="Text">The text of the row.</param>
/// <param name="IsTitle">Whether the row is the name of a component.</param>
public sealed record NoticeRow(string Text, bool IsTitle);
