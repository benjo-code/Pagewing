using System.Runtime.Versioning;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Settings;

/// <summary>
/// The size limits of the small JSON files in the user folder (security review before going public, M12.1): a file over
/// its limit is refused by its length, before anything is read. Every file holds valid JSON followed by blanks, so only
/// the limit can be the reason it is not read; a parser would accept it.
/// </summary>
public sealed class JsonFileLimitsTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-limits-" + Guid.NewGuid().ToString("N"));

    public JsonFileLimitsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that stays is harmless.
        }
    }

    [Fact]
    public void RecentFilesOverTheLimitAreRefusedByTheirLength()
    {
        var path = Path.Combine(_directory, "recent.json");
        var store = new RecentFilesStore(path);
        Assert.True(store.TrySave(Recent(Path.Combine(_directory, "a.pdf"))));
        Assert.Single(store.Load().Items);

        Pad(path, JsonFile.MaxFileLength + 1);

        Assert.Empty(store.Load().Items);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void ARecentFilesFileAtTheLimitIsStillRead()
    {
        var path = Path.Combine(_directory, "recent.json");
        var store = new RecentFilesStore(path);
        Assert.True(store.TrySave(Recent(Path.Combine(_directory, "a.pdf"))));

        Pad(path, JsonFile.MaxFileLength);

        Assert.Single(store.Load().Items);
    }

    [Fact]
    public void TheUpdateStatusOverTheLimitIsRefusedByItsLength()
    {
        var path = Path.Combine(_directory, "update.json");
        var store = new UpdateStatusStore(path);
        Assert.True(store.TrySave(new UpdateStatus(DateTimeOffset.UnixEpoch, "9.9.9")));
        Assert.Equal("9.9.9", store.Load().AvailableVersion);

        Pad(path, JsonFile.MaxFileLength + 1);

        Assert.Null(store.Load().AvailableVersion);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void TheSessionOverTheLimitIsRefusedByItsLength()
    {
        var path = Path.Combine(_directory, "session.json");
        var store = new SessionStore(path);
        var file = Path.Combine(_directory, "a.pdf");
        Assert.True(store.TrySave(new SessionSnapshot([new SessionWindow([new SessionTab(file, null)], 0)])));
        Assert.Single(store.Load().Windows);

        // More than the other files may hold (a real session can), but not more than its own limit.
        Pad(path, SessionStore.MaxFileLength);
        Assert.Single(store.Load().Windows);

        Pad(path, SessionStore.MaxFileLength + 1);
        Assert.Empty(store.Load().Windows);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void TheRequestOverTheLimitIsRefusedByItsLength()
    {
        var path = Path.Combine(_directory, UpdateStaging.RequestFileName);
        var request = new UpdateRequest { Version = "9.9.9", FileName = "setup.exe", Form = PackageForm.WindowsInstaller };
        Assert.True(request.TryWrite(path));
        Assert.NotNull(UpdateRequest.Load(path));

        Pad(path, JsonFile.MaxFileLength + 1);

        Assert.Null(UpdateRequest.Load(path));
    }

    [Fact]
    public void AFileOfAnyOtherKindOnTheNameOfAJsonFileIsNotRead()
    {
        // A folder where the file belongs (a hostile profile): nothing is read, nothing throws.
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(path);

        Assert.Equal(AppSettings.Default, new SettingsStore(path).Load());
        Assert.Empty(new RecentFilesStore(path).Load().Items);
        Assert.Empty(new SessionStore(path).Load().Windows);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ConfigFilesAreWrittenForTheOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only; Windows inherits the rules of the profile.");
        var folder = Path.Combine(_directory, "new", "pagewing");
        var path = Path.Combine(folder, "recent.json");

        Assert.True(new RecentFilesStore(path).TrySave(Recent("/home/user/secret.pdf")));

        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(Owner, File.GetUnixFileMode(path));
        Assert.Equal(Owner | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    private static RecentFiles Recent(string path)
    {
        var files = new RecentFiles();
        files.Touch(path, DateTimeOffset.UnixEpoch);
        return files;
    }

    // Valid JSON as it is, padded with blanks to exactly this many bytes.
    private static void Pad(string path, long length)
    {
        var json = File.ReadAllBytes(path);
        Assert.True(json.Length < length);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(json);
        var blanks = new byte[64 * 1024];
        Array.Fill(blanks, (byte)' ');
        for (var left = length - json.Length; left > 0; left -= blanks.Length)
        {
            stream.Write(blanks, 0, (int)Math.Min(left, blanks.Length));
        }
    }
}
