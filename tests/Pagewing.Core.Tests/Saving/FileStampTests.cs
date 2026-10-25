using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

public sealed class FileStampTests
{
    [Fact]
    public void StampChangesWhenLengthOrTimeChanges()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "file.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var time = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, time);

        var first = FileStamp.Of(path);
        Assert.Equal(new FileStamp(3, time), first);
        Assert.Equal(first, FileStamp.Of(path));

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, time);
        var longer = FileStamp.Of(path);
        Assert.NotEqual(first, longer);
        Assert.Equal(4, longer!.Value.Length);

        File.SetLastWriteTimeUtc(path, time.AddSeconds(2));
        var later = FileStamp.Of(path);
        Assert.NotEqual(longer, later);
        Assert.Equal(time.AddSeconds(2), later!.Value.LastWriteUtc);
        Assert.Equal(DateTimeKind.Utc, later.Value.LastWriteUtc.Kind);
    }

    [Fact]
    public void MissingFileGivesNull()
    {
        using var folder = new TempFolder();
        Assert.Null(FileStamp.Of(Path.Combine(folder.Path, "missing.pdf")));
        Assert.Null(FileStamp.Of(Path.Combine(folder.Path, "no-folder", "missing.pdf")));
        Assert.Null(FileStamp.Of(folder.Path));
    }
}
