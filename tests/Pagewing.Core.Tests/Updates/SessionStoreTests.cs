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
        var windows = Enumerable.Range(0, 12)
            .Select(w => new SessionWindow([.. Enumerable.Range(0, 25).Select(t => new SessionTab($"/w{w}/t{t}.pdf", null))], ActiveIndex: 24))
            .ToList();
        var store = new SessionStore(FilePath);

        Assert.True(store.TrySave(new SessionSnapshot(windows)));
        var loaded = store.Load();

        Assert.Equal(SessionSnapshot.MaxWindows, loaded.Windows.Count);
        Assert.Equal(10, SessionSnapshot.MaxWindows);
        Assert.Equal(20, SessionSnapshot.MaxTabsPerWindow);
        Assert.All(loaded.Windows, window => Assert.Equal(SessionSnapshot.MaxTabsPerWindow, window.Tabs.Count));
        Assert.Equal("/w9/t19.pdf", loaded.Windows[^1].Tabs[^1].Path);

        // The active tab was beyond the limit: the last tab that is kept takes its place.
        Assert.All(loaded.Windows, window => Assert.Equal(SessionSnapshot.MaxTabsPerWindow - 1, window.ActiveIndex));
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
