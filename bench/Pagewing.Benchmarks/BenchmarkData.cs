namespace Pagewing.Benchmarks;

/// <summary>Locates the test PDFs in <c>testdata/pdfs</c> from the repository root.</summary>
internal static class BenchmarkData
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    /// <summary>Full path of a file in <c>testdata/pdfs</c>.</summary>
    public static string Pdf(string name) => Path.Combine(RootDirectory.Value, "testdata", "pdfs", name);

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
