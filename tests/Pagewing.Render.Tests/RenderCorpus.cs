using System.Text;
using Pagewing.Cli;
using Pagewing.TestSupport;

namespace Pagewing.Render.Tests;

/// <summary>One PDF of the render corpus.</summary>
/// <param name="PdfName">File name in <c>testdata/pdfs</c>.</param>
/// <param name="Mode">How its pages are compared with the references.</param>
/// <param name="Reason">Why this mode, in one line.</param>
/// <param name="Password">User password, <see langword="null"/> for unencrypted files.</param>
/// <param name="Pages">Page selection for <c>--pages</c>, <see langword="null"/> for all pages.</param>
internal sealed record RenderCase(string PdfName, CompareMode Mode, string Reason, string? Password = null, string? Pages = null)
{
    /// <summary>The file name without extension; also the name of the reference directory.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(PdfName);
}

/// <summary>
/// The PDFs of the render regression and how they are rendered: <c>pagewing render &lt;pdf&gt; --dpi 144</c>.
/// Every PDF in <c>testdata/pdfs</c> is either in <see cref="Cases"/> or in <see cref="Skipped"/>.
/// </summary>
internal static class RenderCorpus
{
    /// <summary>The resolution of all reference images.</summary>
    public const string Dpi = "144";

    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private const string NoText = "no text: renders nearly identically on Windows and Linux";

    private const string FontSubstitution = "text: the substitute fonts for the non-embedded standard fonts differ between Windows and Linux";

    /// <summary>The rendered PDFs.</summary>
    public static IReadOnlyList<RenderCase> Cases { get; } =
    [
        new("annotations.pdf", CompareMode.Tolerant, FontSubstitution),
        new("cropbox.pdf", CompareMode.Tolerant, FontSubstitution),
        new("encrypted_hello_world_r3.pdf", CompareMode.Tolerant, FontSubstitution, UserPassword),
        new("encrypted_hello_world_r6.pdf", CompareMode.Tolerant, FontSubstitution, UserPassword),
        new("hello_world.pdf", CompareMode.Tolerant, FontSubstitution),
        new("image-rgb.pdf", CompareMode.Strict, NoText),
        new("rotated-page.pdf", CompareMode.Tolerant, FontSubstitution),
        new("text-basic.pdf", CompareMode.Tolerant, FontSubstitution),
        new("thousand-pages.pdf", CompareMode.Tolerant, FontSubstitution, Pages: "1,1000"), // the first and the last page are enough
        new("transparency.pdf", CompareMode.Strict, NoText),
        new("vector-shapes.pdf", CompareMode.Strict, NoText),
        new("wide-page.pdf", CompareMode.Tolerant, FontSubstitution),
    ];

    /// <summary>
    /// Test PDFs that are not rendered on purpose: error cases, and files whose structure is the test subject
    /// while the page content is a plain grey area (the outline files).
    /// </summary>
    public static IReadOnlyList<string> Skipped { get; } = ["empty.pdf", "not-a-pdf.pdf", "outline.pdf", "outline-cycle.pdf"];

    /// <summary>Looks up a case by its file name.</summary>
    public static RenderCase Get(string pdfName) => Cases.Single(c => c.PdfName == pdfName);

    /// <summary>Renders a case through the CLI into <paramref name="outputDirectory"/> and returns the written files.</summary>
    public static async Task<IReadOnlyList<string>> RenderAsync(RenderCase renderCase, string outputDirectory, CancellationToken ct, string? pages = null)
    {
        List<string> args = ["render", TestData.Pdf(renderCase.PdfName), "--dpi", Dpi, "--output", outputDirectory];
        if (renderCase.Password is not null)
        {
            args.AddRange(["--password", renderCase.Password]);
        }

        if ((pages ?? renderCase.Pages) is { } selection)
        {
            args.AddRange(["--pages", selection]);
        }

        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        var exitCode = await CliApp.RunAsync([.. args], stdout, stderr, static _ => null, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"pagewing {string.Join(' ', args)} exited with {exitCode}: {stderr}");
        }

        var files = Encoding.UTF8.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"pagewing {string.Join(' ', args)} wrote no files.");
        }

        return files;
    }
}
