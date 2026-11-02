using System.Globalization;

namespace Pagewing.TestSupport;

/// <summary>A line of a text edit fixture: its origin (start of the baseline) in points from the top left of the displayed page (page is 0-based).</summary>
internal sealed record KnownRun(string File, int Page, string Text, string FontKind, double Left, double Baseline, double Size);

/// <summary>
/// The text edit fixtures of milestone 16 (<c>text-edit-*.pdf</c>) and the lines they show, as <c>tools/make-testpdfs.cs</c> wrote them
/// (<c>testdata/pdfs/text-edit-runs.tsv</c>: file, page from 1, text, font kind, left, baseline, size).
/// </summary>
internal static class TextEditFixtures
{
    /// <summary>The number of runs on page 1 of <c>text-edit-hostile.pdf</c>.</summary>
    public const int HostileRunCount = 10000;

    /// <summary>The length of the run on page 2 of <c>text-edit-hostile.pdf</c>.</summary>
    public const int HostileRunLength = 2001;

    private static readonly Lazy<IReadOnlyList<KnownRun>> Runs = new(Read);

    /// <summary>Gets every known line of every fixture.</summary>
    public static IReadOnlyList<KnownRun> KnownRuns => Runs.Value;

    /// <summary>The known lines of one page (0-based) of a fixture, in the order they were written.</summary>
    public static IReadOnlyList<KnownRun> RunsOf(string file, int page) =>
        [.. KnownRuns.Where(run => run.File == file && run.Page == page)];

    /// <summary>The known line of a fixture with this text.</summary>
    public static KnownRun Run(string file, string text) => KnownRuns.Single(run => run.File == file && run.Text == text);

    private static IReadOnlyList<KnownRun> Read() =>
    [
        .. File.ReadAllLines(Path.Combine(TestData.Root, "testdata", "pdfs", "text-edit-runs.tsv"))
            .Where(static line => line.Length > 0)
            .Select(static line => line.Split('\t'))
            .Select(static parts => new KnownRun(
                parts[0],
                int.Parse(parts[1], CultureInfo.InvariantCulture) - 1,
                parts[2],
                parts[3],
                double.Parse(parts[4], CultureInfo.InvariantCulture),
                double.Parse(parts[5], CultureInfo.InvariantCulture),
                double.Parse(parts[6], CultureInfo.InvariantCulture))),
    ];
}
