using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Settings;

public sealed class RecentFilesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-recent-" + Guid.NewGuid().ToString("N"));

    public RecentFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FilePath => Path.Combine(_directory, "recent.json");

    private static RememberedView View(int page = 2, double zoom = 1.5) =>
        new(new ViewState(new PageAnchor(page, 0.25), -12.5), zoom, ZoomMode.Free, PageLayoutMode.TwoPagesWithCover);

    private static string[] Paths(RecentFiles files) => files.Items.Select(item => item.Path).ToArray();

    [Fact]
    public void TouchMovesToTheTopAndKeepsTheView()
    {
        var files = new RecentFiles();
        files.Remember("a.pdf", View(), Start);
        files.Touch("b.pdf", Start.AddMinutes(1));

        files.Touch("a.pdf", Start.AddMinutes(2));

        Assert.Equal(["a.pdf", "b.pdf"], Paths(files));
        Assert.Equal(View(), files.TryGet("a.pdf")!.View);
        Assert.Equal(Start.AddMinutes(2), files.TryGet("a.pdf")!.LastOpened);
        Assert.Null(files.TryGet("b.pdf")!.View);
    }

    [Fact]
    public void TouchOfAnUnknownPathAddsItWithoutAView()
    {
        var files = new RecentFiles();

        files.Touch("new.pdf", Start);

        var entry = Assert.Single(files.Items);
        Assert.Equal("new.pdf", entry.Path);
        Assert.Equal(Start, entry.LastOpened);
        Assert.Null(entry.View);
    }

    [Fact]
    public void RememberUpdatesAnExistingEntry()
    {
        var files = new RecentFiles();
        files.Remember("a.pdf", View(page: 1), Start);
        files.Touch("b.pdf", Start.AddMinutes(1));

        files.Remember("a.pdf", View(page: 7), Start.AddMinutes(2));

        Assert.Equal(["a.pdf", "b.pdf"], Paths(files));
        Assert.Equal(View(page: 7), files.TryGet("a.pdf")!.View);
        Assert.Equal(Start.AddMinutes(2), files.TryGet("a.pdf")!.LastOpened);
    }

    [Fact]
    public void OnlyThirtyEntriesAreKept()
    {
        var files = new RecentFiles();

        for (var i = 0; i < 35; i++)
        {
            files.Touch($"file{i}.pdf", Start.AddMinutes(i));
        }

        Assert.Equal(RecentFiles.MaxEntries, files.Items.Count);
        Assert.Equal("file34.pdf", files.Items[0].Path);
        Assert.Equal("file5.pdf", files.Items[^1].Path);
        Assert.Null(files.TryGet("file4.pdf"));
    }

    [Fact]
    public void TheConstructorCleansUpItsItems()
    {
        var items = new List<RecentFile> { new("", Start, null), new("a.pdf", Start, null), new("a.pdf", Start.AddDays(-1), View()) };
        for (var i = 0; i < 40; i++)
        {
            items.Add(new RecentFile($"f{i}.pdf", Start, null));
        }

        var files = new RecentFiles(items);

        Assert.Equal(RecentFiles.MaxEntries, files.Items.Count);
        Assert.Equal("a.pdf", files.Items[0].Path);
        Assert.Null(files.Items[0].View);
        Assert.Equal("f0.pdf", files.Items[1].Path);
    }

    [Fact]
    public void WindowsComparesPathsIgnoringCase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var files = new RecentFiles();
        files.Touch(@"C:\Docs\A.pdf", Start);

        files.Touch(@"c:\docs\a.PDF", Start.AddMinutes(1));

        Assert.Single(files.Items);
        Assert.NotNull(files.TryGet(@"C:\DOCS\a.pdf"));
        Assert.True(files.Remove(@"c:\docs\A.PDF"));
        Assert.Empty(files.Items);
    }

    [Fact]
    public void OtherSystemsComparePathsCaseSensitively()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var files = new RecentFiles();
        files.Touch("/docs/A.pdf", Start);
        files.Touch("/docs/a.pdf", Start.AddMinutes(1));

        Assert.Equal(2, files.Items.Count);
        Assert.Null(files.TryGet("/DOCS/a.pdf"));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var files = new RecentFiles();
        files.Touch("a.pdf", Start);
        files.Touch("b.pdf", Start);

        Assert.True(files.Remove("a.pdf"));
        Assert.False(files.Remove("a.pdf"));
        Assert.Equal(["b.pdf"], Paths(files));

        files.Clear();

        Assert.Empty(files.Items);
        Assert.Null(files.TryGet("b.pdf"));
    }

    [Fact]
    public void EmptyPathIsIgnored()
    {
        var files = new RecentFiles();

        files.Touch("", Start);
        files.Touch("  ", Start);
        files.Remember("", View(), Start);

        Assert.Empty(files.Items);
        Assert.Null(files.TryGet(""));
        Assert.False(files.Remove(""));
    }

    [Fact]
    public void StoreRoundTripKeepsViewAndOrder()
    {
        var files = new RecentFiles();
        files.Remember("a.pdf", View(page: 4, zoom: 2), Start);
        files.Touch("b.pdf", Start.AddMinutes(1));
        files.Remember("c.pdf", new RememberedView(new ViewState(new PageAnchor(0, 0), 0), 0.75, ZoomMode.FitWidth, PageLayoutMode.Continuous), Start.AddMinutes(2));
        var store = new RecentFilesStore(FilePath);

        Assert.True(store.TrySave(files));
        var loaded = new RecentFilesStore(FilePath).Load();

        Assert.Equal(files.Items, loaded.Items);
        Assert.Equal(["c.pdf", "b.pdf", "a.pdf"], Paths(loaded));
        Assert.Equal(ZoomMode.FitWidth, loaded.TryGet("c.pdf")!.View!.ZoomMode);
        Assert.Equal(new PageAnchor(4, 0.25), loaded.TryGet("a.pdf")!.View!.View.Anchor);
        Assert.Equal(-12.5, loaded.TryGet("a.pdf")!.View!.View.HorizontalFromCentre);
        Assert.Null(loaded.TryGet("b.pdf")!.View);
        Assert.Equal(["recent.json"], Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void StoreWritesEnumsAsTextAndVersionOne()
    {
        var files = new RecentFiles();
        files.Remember("a.pdf", View(), Start);

        Assert.True(new RecentFilesStore(FilePath).TrySave(files));

        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"Version\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"ZoomMode\": \"Free\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Layout\": \"TwoPagesWithCover\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreDropsEntriesWithInvalidZoomView()
    {
        File.WriteAllText(
            FilePath,
            """
            {
              "Version": 1,
              "Items": [
                { "Path": "zero.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "View": { "View": { "Anchor": { "Page": 1, "Fraction": 0.5 }, "HorizontalFromCentre": 0 }, "Zoom": 0, "ZoomMode": "Free", "Layout": "Continuous" } },
                { "Path": "huge.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "View": { "View": { "Anchor": { "Page": 1, "Fraction": 0.5 }, "HorizontalFromCentre": 0 }, "Zoom": 80, "ZoomMode": "Free", "Layout": "Continuous" } },
                { "Path": "badmode.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "View": { "View": { "Anchor": { "Page": 1, "Fraction": 0.5 }, "HorizontalFromCentre": 0 }, "Zoom": 1, "ZoomMode": 99, "Layout": "Continuous" } },
                { "Path": "noview.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "View": { "Zoom": 1, "ZoomMode": "Free", "Layout": "Continuous" } },
                { "Path": "good.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "View": { "View": { "Anchor": { "Page": 1, "Fraction": 0.5 }, "HorizontalFromCentre": 0 }, "Zoom": 8, "ZoomMode": "Free", "Layout": "Continuous" } },
                { "Path": "", "LastOpened": "2026-10-18T12:00:00+00:00" },
                { "Path": "good.pdf", "LastOpened": "2026-10-17T12:00:00+00:00" }
              ]
            }
            """);

        var loaded = new RecentFilesStore(FilePath).Load();

        Assert.Equal(["zero.pdf", "huge.pdf", "badmode.pdf", "noview.pdf", "good.pdf"], Paths(loaded));
        Assert.Null(loaded.TryGet("zero.pdf")!.View);
        Assert.Null(loaded.TryGet("huge.pdf")!.View);
        Assert.Null(loaded.TryGet("badmode.pdf")!.View);
        Assert.Null(loaded.TryGet("noview.pdf")!.View);
        Assert.Equal(8d, loaded.TryGet("good.pdf")!.View!.Zoom);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void StoreCapsAtThirtyEntriesOnLoad()
    {
        var entries = string.Join(",", Enumerable.Range(0, 45).Select(i => $$"""{ "Path": "f{{i}}.pdf", "LastOpened": "2026-10-18T12:00:00+00:00" }"""));
        File.WriteAllText(FilePath, "{ \"Items\": [" + entries + "] }");

        var loaded = new RecentFilesStore(FilePath).Load();

        Assert.Equal(RecentFiles.MaxEntries, loaded.Items.Count);
        Assert.Equal("f0.pdf", loaded.Items[0].Path);
    }

    [Fact]
    public void UnknownFieldsAndFutureVersionAreTolerated()
    {
        File.WriteAllText(FilePath, """{ "Version": 9, "Surprise": [1], "Items": [ { "Path": "a.pdf", "LastOpened": "2026-10-18T12:00:00+00:00", "Extra": true } ] }""");

        var loaded = new RecentFilesStore(FilePath).Load();

        Assert.Equal(["a.pdf"], Paths(loaded));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void CorruptFileGivesAnEmptyList()
    {
        File.WriteAllText(FilePath, "{\"Items\": [ {");

        var loaded = new RecentFilesStore(FilePath).Load();

        Assert.Empty(loaded.Items);
        Assert.Equal("{\"Items\": [ {", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void WrongShapeCountsAsCorrupt()
    {
        File.WriteAllText(FilePath, """{ "Items": [ { "Path": 5 } ] }""");

        Assert.Empty(new RecentFilesStore(FilePath).Load().Items);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void MissingEmptyAndUnreadableFilesGiveAnEmptyList()
    {
        Assert.Empty(new RecentFilesStore(FilePath).Load().Items);
        Assert.False(File.Exists(FilePath + ".bad"));

        File.WriteAllText(FilePath, "");
        Assert.Empty(new RecentFilesStore(FilePath).Load().Items);

        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);
        Assert.Empty(new RecentFilesStore(FilePath).Load().Items);
    }

    [Fact]
    public void SaveToAnImpossiblePathReturnsFalse()
    {
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");

        Assert.False(new RecentFilesStore(Path.Combine(blocker, "recent.json")).TrySave(new RecentFiles()));
    }
}
