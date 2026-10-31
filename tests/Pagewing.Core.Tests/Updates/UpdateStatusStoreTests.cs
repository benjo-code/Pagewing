using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class UpdateStatusStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-update-" + Guid.NewGuid().ToString("N"));

    public UpdateStatusStoreTests() => Directory.CreateDirectory(_directory);

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

    private string FilePath => Path.Combine(_directory, "update.json");

    [Fact]
    public void MissingFileGivesAnEmptyStatus()
    {
        var status = new UpdateStatusStore(FilePath).Load();

        Assert.Null(status.LastChecked);
        Assert.Null(status.AvailableVersion);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void RoundTrips()
    {
        var status = new UpdateStatus(new DateTimeOffset(2026, 10, 23, 8, 30, 15, TimeSpan.FromHours(2)), "1.2.3");
        var store = new UpdateStatusStore(FilePath);

        Assert.True(store.TrySave(status));

        Assert.Equal(status, new UpdateStatusStore(FilePath).Load());
        Assert.Equal([FilePath], Directory.GetFiles(_directory));

        // A status without an available version keeps both fields apart.
        var checkedOnly = new UpdateStatus(status.LastChecked, null);
        Assert.True(store.TrySave(checkedOnly));
        Assert.Equal(checkedOnly, store.Load());
    }

    [Fact]
    public void CorruptFileIsKeptAsBadAndGivesEmpty()
    {
        File.WriteAllText(FilePath, "{\"LastChecked\":");

        var status = new UpdateStatusStore(FilePath).Load();

        Assert.Equal(new UpdateStatus(null, null), status);
        Assert.Equal("{\"LastChecked\":", File.ReadAllText(FilePath + ".bad"));
    }

    [Theory]
    [InlineData("not a version")]
    [InlineData("1.2")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("")]
    [InlineData("..\\..\\evil")]
    public void DropsAnUnparsableVersion(string version)
    {
        File.WriteAllText(
            FilePath,
            $$"""{ "LastChecked": "2026-10-23T08:00:00+00:00", "AvailableVersion": "{{version.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

        var status = new UpdateStatusStore(FilePath).Load();

        Assert.Null(status.AvailableVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero), status.LastChecked);
    }

    [Fact]
    public void KeepsAParsableVersionFromAHandEditedFile()
    {
        File.WriteAllText(FilePath, """{ "AvailableVersion": "v0.2.0" }""");

        Assert.Equal("v0.2.0", new UpdateStatusStore(FilePath).Load().AvailableVersion);
    }

    [Fact]
    public void NeverThrowsForABadPath()
    {
        var store = new UpdateStatusStore("bad\0path:*?<>|update.json");

        Assert.Equal(new UpdateStatus(null, null), store.Load());
        Assert.False(store.TrySave(new UpdateStatus(DateTimeOffset.UnixEpoch, "1.0.0")));

        // A folder where the file should be is a file system failure too.
        Directory.CreateDirectory(FilePath);
        var onDirectory = new UpdateStatusStore(FilePath);
        Assert.Equal(new UpdateStatus(null, null), onDirectory.Load());
        Assert.False(onDirectory.TrySave(new UpdateStatus(DateTimeOffset.UnixEpoch, "1.0.0")));
    }

    [Fact]
    public void AGarbageVersionNeverComesBackFromLoad()
    {
        var store = new UpdateStatusStore(FilePath);

        Assert.True(store.TrySave(new UpdateStatus(null, "garbage")));

        Assert.Null(store.Load().AvailableVersion);
    }
}
