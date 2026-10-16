using System.Text;
using Pagewing.Cli;
using Pagewing.TestSupport;

namespace Pagewing.Render.Tests;

/// <summary>One PDF of the render corpus.</summary>
/// <param name="PdfName">File name in <c>testdata/pdfs</c>.</param>
/// <param name="Password">User password, <see langword="null"/> for unencrypted files.</param>
/// <param name="Pages">Page selection for <c>--pages</c>, <see langword="null"/> for all pages.</param>
internal sealed record RenderCase(string PdfName, string? Password = null, string? Pages = null)
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

    /// <summary>The rendered PDFs.</summary>
    public static IReadOnlyList<RenderCase> Cases { get; } =
    [
        new("annotations.pdf"),
        new("cropbox.pdf"),
        new("encrypted_hello_world_r3.pdf", UserPassword),
        new("encrypted_hello_world_r6.pdf", UserPassword),
        new("hello_world.pdf"),
        new("image-rgb.pdf"),
        new("rotated-page.pdf"),
        new("text-basic.pdf"),
        new("thousand-pages.pdf", Pages: "1,1000"), // the first and the last page are enough
        new("transparency.pdf"),
        new("vector-shapes.pdf"),
        new("wide-page.pdf"),
    ];

    /// <summary>Test PDFs that cannot be rendered on purpose (error cases).</summary>
    public static IReadOnlyList<string> Skipped { get; } = ["empty.pdf", "not-a-pdf.pdf"];

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
