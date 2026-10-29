using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;
using Pagewing.Core.Tests.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Signatures;

public sealed class SignatureStoreTests : IDisposable
{
    private readonly TempFolder folder = new();

    private string Directory => System.IO.Path.Combine(folder.Path, "signatures");

    public void Dispose() => folder.Dispose();

    private static IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes() =>
    [
        [new(10, 80), new(40, 20), new(70, 90), new(110, 30)],
        [new(20, 95), new(100, 95)],
    ];

    /// <summary>The head of a PNG of 200 x 80 pixels (all the store looks at; the pixels are the codec's business).</summary>
    private static byte[] Png(int width = 200, int height = 80, int extra = 100)
    {
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, (byte)(width >> 8), (byte)width,
            0, 0, (byte)(height >> 8), (byte)height,
            8, 6, 0, 0, 0,
        ];
        return [.. head, .. new byte[extra]];
    }

    private string IndexPath => System.IO.Path.Combine(Directory, "signatures.json");

    [Fact]
    public void AddedSignaturesSurviveReload()
    {
        var store = SignatureStore.Load(Directory);
        var drawn = store.AddDrawn("Work", Strokes(), DrawColor.Blue);
        var png = Png();
        var image = store.AddImage("Scan", png, 2.5);

        var reloaded = SignatureStore.Load(Directory);

        Assert.Equal(["Work", "Scan"], reloaded.Items.Select(s => s.Name));
        Assert.Equal([drawn.Id, image.Id], reloaded.Items.Select(s => s.Id));
        var first = reloaded.Items[0];
        Assert.Equal(SavedSignatureKind.Drawn, first.Kind);
        Assert.Equal(DrawColor.Blue, first.Color);
        Assert.Equal(100.0 / 75.0, first.Aspect, 6);
        Assert.Equal(2, first.Strokes!.Count);
        Assert.All(first.Strokes.SelectMany(s => s), p => Assert.InRange(p.X, 0, 1));
        Assert.All(first.Strokes.SelectMany(s => s), p => Assert.InRange(p.Y, 0, 1));
        Assert.Equal(new DisplayPoint(0, 0.8), first.Strokes[0][0], new PointComparer());
        Assert.Equal(SavedSignatureKind.Image, reloaded.Items[1].Kind);
        Assert.Equal(2.5, reloaded.Items[1].Aspect);
        Assert.Null(reloaded.Items[1].Strokes);
        Assert.Equal(png, reloaded.ReadPng(image.Id));
        Assert.Null(reloaded.ReadPng(drawn.Id));
    }

    private sealed class PointComparer : IEqualityComparer<DisplayPoint>
    {
        public bool Equals(DisplayPoint a, DisplayPoint b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

        public int GetHashCode(DisplayPoint point) => 0;
    }

    [Fact]
    public void ElevenSignaturesAreRefused()
    {
        var store = SignatureStore.Load(Directory);
        for (var i = 0; i < SignatureLimits.MaxSignatures; i++)
        {
            store.AddDrawn("Sig " + i, Strokes(), DrawColor.Black);
        }

        Assert.Throws<SignatureLimitException>(() => store.AddDrawn("Eleventh", Strokes(), DrawColor.Black));
        Assert.Throws<SignatureLimitException>(() => store.AddImage("Eleventh", Png(), 2));
        Assert.Equal(10, store.Items.Count);
        Assert.Equal(10, SignatureStore.Load(Directory).Items.Count);
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.png"));

        // Making room lets the next one in.
        store.Remove(store.Items[0].Id);
        Assert.Equal("Eleventh", store.AddDrawn("Eleventh", Strokes(), DrawColor.Black).Name);
    }

    [Fact]
    public void NameWithPathCharactersDoesNotChangeTheFileName()
    {
        var store = SignatureStore.Load(Directory);

        var saved = store.AddImage(@"..\..\evil/x:*?""<>|", Png(), 2.5);
        store.AddDrawn("../../../up", Strokes(), DrawColor.Black);

        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
        var files = System.IO.Directory.GetFileSystemEntries(Directory).Select(System.IO.Path.GetFileName).Order().ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("signatures.json", files);
        Assert.Contains(saved.Id + ".png", files);
        Assert.Equal(["signatures"], System.IO.Directory.GetDirectories(folder.Path).Select(System.IO.Path.GetFileName));
        Assert.Empty(System.IO.Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void EmptyOrOverlongNameIsRepaired()
    {
        var store = SignatureStore.Load(Directory);

        var empty = store.AddDrawn("", Strokes(), DrawColor.Black);
        var blank = store.AddDrawn("   \t ", Strokes(), DrawColor.Black);
        var long_ = store.AddDrawn(new string('x', 500), Strokes(), DrawColor.Black);
        var control = store.AddDrawn("a\u0000b\nc", Strokes(), DrawColor.Black);

        Assert.False(string.IsNullOrWhiteSpace(empty.Name));
        Assert.False(string.IsNullOrWhiteSpace(blank.Name));
        Assert.NotEqual(empty.Name, blank.Name);
        Assert.Equal(SignatureStore.MaxNameLength, long_.Name.Length);
        Assert.DoesNotContain(control.Name, char.IsControl);
        Assert.Equal("abc", control.Name);

        store.Rename(empty.Id, "  Renamed  ");
        Assert.Equal("Renamed", SignatureStore.Load(Directory).Items.Single(s => s.Id == empty.Id).Name);
        store.Rename(empty.Id, "");
        Assert.False(string.IsNullOrWhiteSpace(SignatureStore.Load(Directory).Items.Single(s => s.Id == empty.Id).Name));
        store.Rename("not-an-id", "x");
        store.Rename(new string('0', 32), "x");
    }

    [Fact]
    public void CorruptIndexIsMovedToBadAndStartsEmpty()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(IndexPath, "{ this is not json");

        var store = SignatureStore.Load(Directory);

        Assert.Empty(store.Items);
        Assert.True(File.Exists(IndexPath + ".bad"));
        Assert.Equal("{ this is not json", File.ReadAllText(IndexPath + ".bad"));

        // The library works again, and a reload sees what was added.
        store.AddDrawn("After", Strokes(), DrawColor.Black);
        Assert.Equal(["After"], SignatureStore.Load(Directory).Items.Select(s => s.Name));
    }

    [Fact]
    public void MissingPngDropsOnlyThatEntry()
    {
        var store = SignatureStore.Load(Directory);
        var drawn = store.AddDrawn("Kept", Strokes(), DrawColor.Black);
        var image = store.AddImage("Lost", Png(), 2);
        var other = store.AddImage("Also kept", Png(), 2);
        File.Delete(System.IO.Path.Combine(Directory, image.Id + ".png"));

        var reloaded = SignatureStore.Load(Directory);

        Assert.Equal([drawn.Id, other.Id], reloaded.Items.Select(s => s.Id));
        Assert.Null(reloaded.ReadPng(image.Id));
    }

    [Fact]
    public void TenDenseSignaturesSurviveAReload()
    {
        var store = SignatureStore.Load(Directory);
        var random = new Random(5);
        for (var i = 0; i < SignatureLimits.MaxSignatures; i++)
        {
            // What a busy scribble gives after thinning and smoothing: some thousand points with full-precision coordinates.
            IReadOnlyList<DisplayPoint> stroke = [.. Enumerable.Range(0, 2_600).Select(_ => new DisplayPoint(random.NextDouble() * 400, random.NextDouble() * 150))];
            store.AddDrawn("Dense " + i, [stroke], DrawColor.Black);
        }

        Assert.Equal(SignatureLimits.MaxSignatures, SignatureStore.Load(Directory).Items.Count);
    }

    [Fact]
    public void AnOverlyDenseSignatureIsRefusedInsteadOfBreakingTheLibrary()
    {
        var store = SignatureStore.Load(Directory);
        var random = new Random(6);
        IReadOnlyList<DisplayPoint> stroke = [.. Enumerable.Range(0, 20_000).Select(_ => new DisplayPoint(random.NextDouble() * 400, random.NextDouble() * 150))];

        Assert.Throws<ArgumentException>(() => store.AddDrawn("Too dense", [stroke], DrawColor.Black));
        store.AddDrawn("Fine", Strokes(), DrawColor.Black);
        Assert.Single(SignatureStore.Load(Directory).Items);
    }

    [Fact]
    public void OversizedIndexIsIgnored()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(IndexPath, "{\"Version\":1,\"Items\":[]," + new string(' ', (int)SignatureStore.MaxIndexLength + 10) + "}");

        var store = SignatureStore.Load(Directory);

        Assert.Empty(store.Items);
        store.AddDrawn("Works", Strokes(), DrawColor.Black);
        Assert.Single(SignatureStore.Load(Directory).Items);
    }

    [Fact]
    public void HostileEntriesInTheIndexAreDropped()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var good = new string('a', 32);
        const string Stroke = "[{\"X\":0,\"Y\":0},{\"X\":1,\"Y\":1}]";
        var json = new StringBuilder()
            .Append("{\"Version\":1,\"Items\":[")
            .Append("{\"Id\":\"..\\\\..\\\\x\",\"Name\":\"traversal\",\"Kind\":\"Image\",\"Aspect\":2,\"Color\":\"Black\"},")
            .Append("{\"Id\":\"").Append(good).Append("\",\"Name\":\"ok\",\"Kind\":\"Drawn\",\"Aspect\":2,\"Color\":\"Black\",\"Strokes\":[").Append(Stroke).Append("]},")
            .Append("{\"Id\":\"").Append(new string('b', 32)).Append("\",\"Name\":\"nan\",\"Kind\":\"Drawn\",\"Aspect\":1e999,\"Color\":\"Black\",\"Strokes\":[").Append(Stroke).Append("]},")
            .Append("{\"Id\":\"").Append(new string('c', 32)).Append("\",\"Name\":\"nostrokes\",\"Kind\":\"Drawn\",\"Aspect\":2,\"Color\":\"Black\"},")
            .Append("{\"Id\":\"").Append(new string('d', 32)).Append("\",\"Name\":\"farpoint\",\"Kind\":\"Drawn\",\"Aspect\":2,\"Color\":\"Black\",\"Strokes\":[[{\"X\":0,\"Y\":0},{\"X\":1000000,\"Y\":1}]]},")
            .Append("{\"Id\":\"").Append(good).Append("\",\"Name\":\"duplicate id\",\"Kind\":\"Drawn\",\"Aspect\":2,\"Color\":\"Black\",\"Strokes\":[").Append(Stroke).Append("]},")
            .Append("null]}")
            .ToString();
        File.WriteAllText(IndexPath, json);

        var store = SignatureStore.Load(Directory);

        Assert.Equal(["ok"], store.Items.Select(s => s.Name));
    }

    [Fact]
    public void TwoStoresOnTheSameFolderDoNotCorruptEachOther()
    {
        var first = SignatureStore.Load(Directory);
        var second = SignatureStore.Load(Directory);

        var a = first.AddDrawn("From first", Strokes(), DrawColor.Black);
        var b = second.AddDrawn("From second", Strokes(), DrawColor.Blue);
        var c = first.AddImage("Image from first", Png(), 2);

        var third = SignatureStore.Load(Directory);
        Assert.Equal([a.Id, b.Id, c.Id], third.Items.Select(s => s.Id));

        // The stores see each other's entries after their next change, and a removal by one keeps the other's additions.
        second.Remove(a.Id);
        Assert.Equal([b.Id, c.Id], SignatureStore.Load(Directory).Items.Select(s => s.Id));
        Assert.DoesNotContain(a.Id, second.Items.Select(s => s.Id));
        Assert.Contains(c.Id, second.Items.Select(s => s.Id));
    }

    [Fact]
    public void RemoveDeletesTheFile()
    {
        var store = SignatureStore.Load(Directory);
        var image = store.AddImage("Scan", Png(), 2);
        var path = System.IO.Path.Combine(Directory, image.Id + ".png");
        Assert.True(File.Exists(path));

        store.Remove(image.Id);

        Assert.False(File.Exists(path));
        Assert.Empty(SignatureStore.Load(Directory).Items);
        store.Remove(image.Id);
    }

    [Fact]
    public void AddDrawnRefusesASingleDotAndEmptyStrokes()
    {
        var store = SignatureStore.Load(Directory);

        Assert.Throws<ArgumentException>(() => store.AddDrawn("Dot", [[new(5, 5)]], DrawColor.Black));
        Assert.Throws<ArgumentException>(() => store.AddDrawn("Same point", [[new(5, 5), new(5, 5), new(5, 5)]], DrawColor.Black));
        Assert.Throws<ArgumentException>(() => store.AddDrawn("None", [], DrawColor.Black));
        Assert.Throws<ArgumentException>(() => store.AddDrawn("Not finite", [[new(0, 0), new(double.NaN, 3)]], DrawColor.Black));
        Assert.Empty(store.Items);
        Assert.False(File.Exists(IndexPath));
    }

    [Fact]
    public void AFlatLineGetsAThinBoxNotAnInfiniteAspect()
    {
        var store = SignatureStore.Load(Directory);

        var saved = store.AddDrawn("Line", [[new(0, 50), new(100, 50)]], DrawColor.Black);

        Assert.True(saved.Aspect is > 1 and <= 20, $"aspect {saved.Aspect}");
        Assert.All(saved.Strokes!.SelectMany(s => s), p => Assert.InRange(p.Y, 0, 1));
    }

    [Fact]
    public void AddImageRefusesWhatIsNotAReasonablePng()
    {
        var store = SignatureStore.Load(Directory);

        Assert.Throws<ArgumentException>(() => store.AddImage("Text", "not a png"u8.ToArray(), 2));
        Assert.Throws<ArgumentException>(() => store.AddImage("Huge", Png(5000, 80), 2));
        Assert.Throws<ArgumentException>(() => store.AddImage("Aspect", Png(), 0));
        Assert.Throws<ArgumentException>(() => store.AddImage("Aspect", Png(), double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => store.AddImage("Big", [.. Png(), .. new byte[SignatureLimits.MaxFileBytes]], 2));
        Assert.Empty(store.Items);
        Assert.Empty(System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.png") : []);
    }

    [Fact]
    public void ALibraryInAnUnwritableFolderThrowsAnIoExceptionAndChangesNothing()
    {
        // A file where the folder should be.
        var blocked = System.IO.Path.Combine(folder.Path, "blocked");
        File.WriteAllText(blocked, "x");
        var store = SignatureStore.Load(blocked);

        Assert.Empty(store.Items);
        Assert.ThrowsAny<IOException>(() => store.AddDrawn("Nope", Strokes(), DrawColor.Black));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void UnixFilesAreOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only.");
        var store = SignatureStore.Load(Directory);
        var image = store.AddImage("Scan", Png(), 2);

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(System.IO.Path.Combine(Directory, image.Id + ".png")));
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(IndexPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Directory));
        }
    }
}
