using System.Globalization;
using System.Text;
using Pagewing.Cli;
using Pagewing.Core.Imaging;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;

namespace Pagewing.Render.Tests;

/// <summary>One PDF of the render corpus, rendered in one page theme.</summary>
/// <param name="PdfName">File name in <c>testdata/pdfs</c>.</param>
/// <param name="Mode">How its pages are compared with the references.</param>
/// <param name="Reason">Why this mode, in one line.</param>
/// <param name="Password">User password, <see langword="null"/> for unencrypted files.</param>
/// <param name="Pages">Page selection for <c>--pages</c>, <see langword="null"/> for all pages.</param>
/// <param name="Theme">The page theme; <see cref="PageTheme.Light"/> renders through the CLI like a user would.</param>
/// <param name="Suffix">Appended to the file stem for the name of a case that renders a PDF of another case again.</param>
internal sealed record RenderCase(string PdfName, CompareMode Mode, string Reason, string? Password = null, string? Pages = null, PageTheme Theme = PageTheme.Light, string? Suffix = null)
{
    /// <summary>The name of the case: the file name without extension plus <see cref="Suffix"/>; also the name of the reference directory and the stem of the rendered files.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(PdfName) + Suffix;
}

/// <summary>
/// The PDFs of the render regression and how they are rendered: <c>pagewing render &lt;pdf&gt; --dpi 144</c>; a case
/// with another <see cref="RenderCase.Theme"/> renders through <see cref="PdfDocument.RenderPageAsync"/> at the same
/// resolution, because the CLI has no page theme. Every PDF in <c>testdata/pdfs</c> is either in
/// <see cref="Cases"/> or in <see cref="Skipped"/>.
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
        new("dark-sample.pdf", CompareMode.Tolerant, FontSubstitution),
        new("dark-sample.pdf", CompareMode.Tolerant, FontSubstitution, Theme: PageTheme.Dark, Suffix: "-dark"),
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
    /// Test PDFs that are not rendered on purpose: the error cases; the outline files, whose structure is the test
    /// subject and whose pages are a plain grey area; and the text files of the search and the links, whose subject is
    /// the text layer, not the rendering (their pages use only standard-14 fonts: Helvetica, Helvetica-Bold and
    /// Helvetica-Oblique).
    /// </summary>
    public static IReadOnlyList<string> Skipped { get; } =
        ["empty.pdf", "not-a-pdf.pdf", "outline.pdf", "outline-cycle.pdf", "text-dense.pdf", "text-rich.pdf"];

    /// <summary>Looks up a case by its name (<see cref="RenderCase.Stem"/>).</summary>
    public static RenderCase Get(string name) => Cases.Single(c => c.Stem == name);

    /// <summary>Renders a case into <paramref name="outputDirectory"/> and returns the written files.</summary>
    public static async Task<IReadOnlyList<string>> RenderAsync(RenderCase renderCase, string outputDirectory, CancellationToken ct, string? pages = null)
    {
        if (renderCase.Theme != PageTheme.Light)
        {
            return await RenderThemedAsync(renderCase, outputDirectory, pages, ct);
        }

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

    /// <summary>Renders every page like <c>pagewing render</c> does (same scale, annotations, file names), in the theme of the case.</summary>
    private static async Task<IReadOnlyList<string>> RenderThemedAsync(RenderCase renderCase, string outputDirectory, string? pages, CancellationToken ct)
    {
        if ((pages ?? renderCase.Pages) is not null)
        {
            throw new NotSupportedException($"{renderCase.Stem}: a page selection is only supported for cases rendered through the CLI.");
        }

        await using var document = await new PdfEngine().OpenAsync(TestData.Pdf(renderCase.PdfName), renderCase.Password, ct);
        var scale = double.Parse(Dpi, CultureInfo.InvariantCulture) / 72.0;
        var options = new RenderOptions { PageTheme = renderCase.Theme };
        var format = "D" + Math.Max(3, document.PageCount.ToString(CultureInfo.InvariantCulture).Length).ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outputDirectory);
        var files = new List<string>();
        for (var page = 0; page < document.PageCount; page++)
        {
            var image = await document.RenderPageAsync(page, scale, options, ct);
            var path = Path.Combine(outputDirectory, $"{renderCase.Stem}-p{(page + 1).ToString(format, CultureInfo.InvariantCulture)}.png");
            await File.WriteAllBytesAsync(path, PngEncoder.Encode(image), ct);
            files.Add(path);
        }

        return files;
    }
}
