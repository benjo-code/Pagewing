namespace Pagewing.TestSupport;

/// <summary>Locates the shared test data (<c>testdata/pdfs</c>, <c>testdata/references</c>) from the repository root.</summary>
internal static class TestData
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    /// <summary>The repository root, found by walking up from the test output directory to <c>Pagewing.slnx</c>.</summary>
    public static string Root => RootDirectory.Value;

    /// <summary>Full path of a file in <c>testdata/pdfs</c>.</summary>
    public static string Pdf(string name) => Path.Combine(Root, "testdata", "pdfs", name);

    /// <summary>Full path of the shipped Geist Regular, the font of the text boxes (<c>src/Pagewing.App/Assets/Fonts</c>).</summary>
    public static string GeistRegular => Path.Combine(Root, "src", "Pagewing.App", "Assets", "Fonts", "Geist-Regular.ttf");

    /// <summary>Directory with the reference images of one test PDF (<c>testdata/references/&lt;stem&gt;</c>).</summary>
    public static string ReferenceDir(string pdfStem) => Path.Combine(Root, "testdata", "references", pdfStem);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"Pagewing.slnx was not found above '{AppContext.BaseDirectory}'.");
    }
}
