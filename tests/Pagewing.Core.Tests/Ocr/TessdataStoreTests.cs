using Pagewing.Core.Ocr;
using Pagewing.Core.Settings;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>Language files: what is installed, how one is imported, and that nothing shipped is ever overwritten.</summary>
public sealed class TessdataStoreTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-tessdata-" + Guid.NewGuid().ToString("N"))).FullName;

    private string Shipped => Path.Combine(root, "shipped");

    private string User => Path.Combine(root, "user");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private TessdataStore Store()
    {
        Directory.CreateDirectory(Shipped);
        File.WriteAllBytes(Path.Combine(Shipped, "eng.traineddata"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Shipped, "deu.traineddata"), [4, 5, 6, 7]);
        return new TessdataStore(User, Shipped);
    }

    private string Source(string fileName, int length = 16)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public void TheFolderIsTessdataInTheUserFolder()
    {
        Assert.Equal(Path.Combine(UserDataPaths.ConfigDirectory(), "tessdata"), UserDataPaths.TessdataDirectory());
    }

    [Fact]
    public void ListShowsShippedAndImportedLanguagesSorted()
    {
        var store = Store();
        store.Import(Source("frk.traineddata"));

        var list = store.List();

        Assert.Equal(["deu", "eng", "frk"], list.Select(static l => l.Name));
        Assert.Equal([TessdataOrigin.Shipped, TessdataOrigin.Shipped, TessdataOrigin.Imported], list.Select(static l => l.Origin));
    }

    [Fact]
    public void ImportCopiesIntoTheUserFolderWithASafeName()
    {
        var store = Store();
        var source = Source("fra.traineddata", 40);

        var result = store.Import(source);

        Assert.Equal(TessdataImportError.None, result.Error);
        Assert.Equal("fra", result.Language);
        Assert.Equal(Path.Combine(User, "fra.traineddata"), result.Path);
        Assert.Equal(40, new FileInfo(result.Path!).Length);
        Assert.True(File.Exists(source));
        Assert.Equal(["fra.traineddata"], Directory.GetFiles(User).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("Eng.traineddata")]
    [InlineData("my lang.traineddata")]
    [InlineData("..traineddata")]
    [InlineData("eng2.traineddata")]
    [InlineData("deu.txt")]
    [InlineData("traineddata")]
    [InlineData(".traineddata")]
    public void NamesOutsideTheAllowedSetAreRefused(string fileName)
    {
        var store = Store();

        var result = store.Import(Source(fileName));

        Assert.Equal(TessdataImportError.InvalidName, result.Error);
        Assert.False(Directory.Exists(User));
    }

    [Fact]
    public void FilesOver200MbAreRefused()
    {
        var store = Store();
        var source = Source("big.traineddata", 0);
        using (var stream = new FileStream(source, FileMode.Open))
        {
            stream.SetLength(TessdataStore.MaxFileLength + 1);
        }

        var result = store.Import(source);

        Assert.Equal(TessdataImportError.TooLarge, result.Error);
        Assert.False(File.Exists(Path.Combine(User, "big.traineddata")));
    }

    [Fact]
    public void AnEmptyFileAndAFolderAreRefused()
    {
        var store = Store();
        var empty = Source("none.traineddata", 0);
        var folder = Directory.CreateDirectory(Path.Combine(root, "downloads", "dir.traineddata")).FullName;

        Assert.Equal(TessdataImportError.Empty, store.Import(empty).Error);
        Assert.Equal(TessdataImportError.NotAFile, store.Import(folder).Error);
        Assert.Equal(TessdataImportError.NotAFile, store.Import(Path.Combine(root, "missing.traineddata")).Error);
    }

    [Fact]
    public void ImportNeverOverwritesAShippedFile()
    {
        var store = Store();
        var shippedBefore = File.ReadAllBytes(Path.Combine(Shipped, "eng.traineddata"));
        var source = Source("eng.traineddata", 99);

        var result = store.Import(source);

        // The imported file lands in the user folder and, being chosen on purpose, is the one a run uses; the shipped one stays as it was.
        Assert.Equal(TessdataImportError.None, result.Error);
        Assert.Equal(shippedBefore, File.ReadAllBytes(Path.Combine(Shipped, "eng.traineddata")));
        Assert.Equal(99, new FileInfo(Path.Combine(User, "eng.traineddata")).Length);
        Assert.Equal(TessdataOrigin.Imported, store.List().Single(static l => l.Name == "eng").Origin);
    }

    [Fact]
    public void ImportingTheSameNameAgainNeedsAnExplicitReplace()
    {
        var store = Store();
        store.Import(Source("frk.traineddata", 10));
        var again = Source("frk.traineddata", 20);

        Assert.Equal(TessdataImportError.AlreadyImported, store.Import(again).Error);
        Assert.Equal(10, new FileInfo(Path.Combine(User, "frk.traineddata")).Length);
        Assert.Equal(TessdataImportError.None, store.Import(again, replaceImported: true).Error);
        Assert.Equal(20, new FileInfo(Path.Combine(User, "frk.traineddata")).Length);
    }

    [Fact]
    public void AFailedImportLeavesNoTemporaryFile()
    {
        var store = Store();
        store.Import(Source("frk.traineddata"));
        store.Import(Source("frk.traineddata"));

        Assert.Equal(["frk.traineddata"], Directory.GetFiles(User).Select(Path.GetFileName));
    }

    [Fact]
    public void ResolveFolderPrefersOneFolderThatHasEverything()
    {
        var store = Store();
        store.Import(Source("frk.traineddata"));

        Assert.Equal(Shipped, store.ResolveFolder(["deu", "eng"]));
        Assert.Equal(User, store.ResolveFolder(["frk"]));
    }

    [Fact]
    public void ResolveFolderMixesShippedAndImportedIntoACacheFolder()
    {
        var store = Store();
        store.Import(Source("frk.traineddata", 33));

        var folder = store.ResolveFolder(["deu", "frk"]);

        Assert.NotEqual(Shipped, folder);
        Assert.NotEqual(User, folder);
        Assert.StartsWith(User, folder, StringComparison.Ordinal);
        Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(Path.Combine(folder, "deu.traineddata")));
        Assert.Equal(33, new FileInfo(Path.Combine(folder, "frk.traineddata")).Length);
        Assert.Equal(folder, store.ResolveFolder(["deu", "frk"]));
    }

    [Fact]
    public void ResolveFolderForAMissingLanguageThrowsAndNamesIt()
    {
        var store = Store();

        var failure = Assert.Throws<TessdataMissingException>(() => store.ResolveFolder(["eng", "xyz"]));

        Assert.Equal(["xyz"], failure.Missing);
        Assert.Equal(["deu", "eng"], failure.Installed);
    }

    [Fact]
    public void OnlyTheCommandLineFolderSharesTheLanguageFilesOfTheParent()
    {
        var root = Directory.CreateTempSubdirectory("pagewing-shipped-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tessdata"));
            var cli = Directory.CreateDirectory(Path.Combine(root, "cli")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(root, "Pagewing")).FullName;

            Assert.Equal(Path.Combine(root, "tessdata"), TessdataStore.ShippedDirectoryFor(cli));
            Assert.Equal(Path.Combine(root, "tessdata"), TessdataStore.ShippedDirectoryFor(cli + Path.DirectorySeparatorChar));

            // An application folder with no tessdata of its own never reads the folder next to it (Downloads, Programs, ...).
            Assert.Equal(Path.Combine(app, "tessdata"), TessdataStore.ShippedDirectoryFor(app));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
