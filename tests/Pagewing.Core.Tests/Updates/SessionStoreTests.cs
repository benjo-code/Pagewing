using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-session-" + Guid.NewGuid().ToString("N"));

    public SessionStoreTests() => Directory.CreateDirectory(_directory);

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

    private string FilePath => Path.Combine(_directory, SessionStore.FileName);

    private static RememberedView View(int page, double zoom = 1.5) =>
        new(new ViewState(new PageAnchor(page, 0.25), -12), zoom, ZoomMode.Free, PageLayoutMode.TwoPages);

    [Fact]
    public void RoundTrips()
    {
        var snapshot = new SessionSnapshot(
        [
            new SessionWindow([new SessionTab(@"C:\a.pdf", View(3)), new SessionTab(@"C:\b.pdf", null)], ActiveIndex: 1),
            new SessionWindow([new SessionTab("/home/u/c.pdf", View(0, zoom: 0.5))], ActiveIndex: 0),
        ]);
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(snapshot));
        var loaded = new SessionStore(FilePath).Load();

        Assert.Equal(Describe(snapshot), Describe(loaded));
        Assert.Equal([FilePath], Directory.GetFiles(_directory));
        Assert.Equal("session.json", SessionStore.FileName);
    }

    [Fact]
    public void MissingFileGivesEmpty()
    {
        var snapshot = new SessionStore(FilePath).Load();

        Assert.Empty(snapshot.Windows);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void CorruptFileIsKeptAsBad()
    {
        File.WriteAllText(FilePath, "{\"Windows\": [");

        var snapshot = new SessionStore(FilePath).Load();

        Assert.Empty(snapshot.Windows);
        Assert.Equal("{\"Windows\": [", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void CleansPathsAndViews()
    {
        // Empty and missing paths, a null tab, a duplicate (the active one), an unusable view, an active index out of
        // range, a window without a usable tab and a null window.
        File.WriteAllText(
            FilePath,
            """
            {
              "Version": 1,
              "Windows": [
                {
                  "ActiveIndex": 4,
                  "Tabs": [
                    { "Path": "", "View": null },
                    { "Path": "/a.pdf", "View": { "View": { "Anchor": { "Page": 2, "Fraction": 0.5 }, "HorizontalFromCentre": 0 }, "Zoom": 99, "ZoomMode": "Free", "Layout": "Continuous" } },
                    null,
                    { "View": null },
                    { "Path": "/a.pdf" },
                    { "Path": "/b.pdf", "View": { "View": { "Anchor": { "Page": 1, "Fraction": 0 }, "HorizontalFromCentre": 0 }, "Zoom": 2, "ZoomMode": "FitWidth", "Layout": "SinglePage" } }
                  ]
                },
                { "ActiveIndex": 0, "Tabs": [ { "Path": "   " } ] },
                null,
                { "ActiveIndex": 99, "Tabs": [ { "Path": "/c.pdf" }, { "Path": "/d.pdf" } ] },
                { "ActiveIndex": -3, "Tabs": [ { "Path": "/e.pdf" }, { "Path": "/f.pdf" } ] },
                { "Tabs": null }
              ]
            }
            """);

        var snapshot = new SessionStore(FilePath).Load();

        Assert.Equal(3, snapshot.Windows.Count);
        var first = snapshot.Windows[0];
        Assert.Equal(["/a.pdf", "/b.pdf"], first.Tabs.Select(tab => tab.Path));
        Assert.Null(first.Tabs[0].View);
        Assert.Equal(new RememberedView(new ViewState(new PageAnchor(1, 0), 0), 2, ZoomMode.FitWidth, PageLayoutMode.SinglePage), first.Tabs[1].View);

        // The active tab was the duplicate of /a.pdf: the tab of that file stays the active one.
        Assert.Equal(0, first.ActiveIndex);
        Assert.Equal(1, snapshot.Windows[1].ActiveIndex);
        Assert.Equal(0, snapshot.Windows[2].ActiveIndex);
        Assert.Equal(["/e.pdf", "/f.pdf"], snapshot.Windows[2].Tabs.Select(tab => tab.Path));
    }

    [Fact]
    public void LimitsTabsAndWindows()
    {
        // The limits only defend against a hostile session.json; a real session stays far below them.
        Assert.Equal(50, SessionSnapshot.MaxWindows);
        Assert.Equal(500, SessionSnapshot.MaxTabsPerWindow);

        var windows = Enumerable.Range(0, 60)
            .Select(w => new SessionWindow([new SessionTab($"/w{w}/a.pdf", null), new SessionTab($"/w{w}/b.pdf", null)], ActiveIndex: 1))
            .ToList();
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(new SessionSnapshot(windows)));
        var loaded = store.Load();

        // Windows beyond the limit are dropped; the first ones (the oldest) are kept.
        Assert.Equal(SessionSnapshot.MaxWindows, loaded.Windows.Count);
        Assert.Equal("/w0/a.pdf", loaded.Windows[0].Tabs[0].Path);
        Assert.Equal("/w49/b.pdf", loaded.Windows[^1].Active.Path);
    }

    [Fact]
    public void AWindowOverTheLimitKeepsItsActiveTab()
    {
        var tabs = Enumerable.Range(0, 600).Select(t => new SessionTab($"/t{t:D3}.pdf", null)).ToList();
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(new SessionSnapshot([new SessionWindow(tabs, ActiveIndex: 550)])));
        var window = Assert.Single(store.Load().Windows);

        // A run of tabs around the active one, in their order; the active one is never dropped.
        Assert.Equal(SessionSnapshot.MaxTabsPerWindow, window.Tabs.Count);
        Assert.Equal("/t550.pdf", window.Active.Path);
        var first = int.Parse(window.Tabs[0].Path[2..5], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Enumerable.Range(first, SessionSnapshot.MaxTabsPerWindow).Select(t => $"/t{t:D3}.pdf"), window.Tabs.Select(tab => tab.Path));
        Assert.Equal(550 - first, window.ActiveIndex);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(599, 499)]
    public void TheRunOfKeptTabsStaysInsideTheWindow(int active, int keptActive)
    {
        var tabs = Enumerable.Range(0, 600).Select(t => new SessionTab($"/t{t:D3}.pdf", null)).ToList();

        var window = Assert.Single(new SessionSnapshot([new SessionWindow(tabs, active)]).KeepFiles(_ => true).Windows);

        Assert.Equal(SessionSnapshot.MaxTabsPerWindow, window.Tabs.Count);
        Assert.Equal($"/t{active:D3}.pdf", window.Active.Path);
        Assert.Equal(keptActive, window.ActiveIndex);
    }

    [Fact]
    public void ANormalSessionIsUnchanged()
    {
        var snapshot = new SessionSnapshot(
        [
            new SessionWindow([.. Enumerable.Range(0, 30).Select(t => new SessionTab($"/a{t}.pdf", t == 7 ? View(t) : null))], ActiveIndex: 27),
            new SessionWindow([new SessionTab("/b.pdf", null)], ActiveIndex: 0),
        ]);
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(snapshot));

        Assert.Equal(Describe(snapshot), Describe(store.Load()));
    }

    [Fact]
    public void ASessionAtTheLimitsFitsIntoTheFile()
    {
        // Every tab with a long path and a view: the file is larger than the 1 MiB of the other JSON files, and must
        // still be read back, or a session at the limits would be thrown away as corrupt.
        var folder = "/home/somebody/Documents/" + new string('x', 80) + "/";
        var windows = Enumerable.Range(0, SessionSnapshot.MaxWindows)
            .Select(w => new SessionWindow([.. Enumerable.Range(0, SessionSnapshot.MaxTabsPerWindow).Select(t => new SessionTab($"{folder}w{w}/t{t}.pdf", View(t)))], ActiveIndex: 3))
            .ToList();
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(new SessionSnapshot(windows)));
        Assert.True(new FileInfo(FilePath).Length > 1024 * 1024);
        var loaded = store.Load();

        Assert.Equal(SessionSnapshot.MaxWindows, loaded.Windows.Count);
        Assert.All(loaded.Windows, window => Assert.Equal(SessionSnapshot.MaxTabsPerWindow, window.Tabs.Count));
        Assert.Equal(View(499), loaded.Windows[^1].Tabs[^1].View);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void DeleteRemovesTheFileAndNeverThrows()
    {
        var store = new SessionStore(FilePath);
        Assert.True(store.TrySave(new SessionSnapshot([new SessionWindow([new SessionTab("/a.pdf", null)], 0)])));

        store.Delete();
        store.Delete();

        Assert.False(File.Exists(FilePath));
        Assert.Empty(store.Load().Windows);
        new SessionStore("bad\0path:*?<>|session.json").Delete();
        Assert.False(new SessionStore("bad\0path:*?<>|session.json").TrySave(SessionSnapshot.Empty));
    }

    [Fact]
    public void KeepFilesDropsMissingOnesAndTheirEmptyWindows()
    {
        var snapshot = new SessionSnapshot(
        [
            new SessionWindow([new SessionTab("/a.pdf", View(1)), new SessionTab("/gone.pdf", null), new SessionTab("/b.pdf", null)], ActiveIndex: 1),
            new SessionWindow([new SessionTab("/gone-too.pdf", null)], ActiveIndex: 0),
            new SessionWindow([new SessionTab("/c.pdf", null)], ActiveIndex: 0),
        ]);

        var kept = snapshot.KeepFiles(path => !path.Contains("gone", StringComparison.Ordinal));

        Assert.Equal(2, kept.Windows.Count);
        Assert.Equal(["/a.pdf", "/b.pdf"], kept.Windows[0].Tabs.Select(tab => tab.Path));
        Assert.Equal(View(1), kept.Windows[0].Tabs[0].View);

        // The active tab is gone: its right neighbour takes its place, as when a tab is closed.
        Assert.Equal(1, kept.Windows[0].ActiveIndex);
        Assert.Equal("/c.pdf", Assert.Single(kept.Windows[1].Tabs).Path);
    }

    private static string Describe(SessionSnapshot snapshot) =>
        string.Join(
            " | ",
            snapshot.Windows.Select(window => $"{window.ActiveIndex}: " + string.Join(", ", window.Tabs.Select(tab => $"{tab.Path} {tab.View}"))));
}
