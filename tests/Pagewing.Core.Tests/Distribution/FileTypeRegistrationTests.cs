using Pagewing.Core.Distribution;
using Xunit;

namespace Pagewing.Core.Tests.Distribution;

public sealed class FileTypeRegistrationTests
{
    private const string Exe = @"C:\Apps\Pagewing\Pagewing.exe";

    [Fact]
    public void EntriesHaveTheOpenCommandForTheExe()
    {
        var entries = FileTypeRegistration.Entries(Exe);

        var progId = Assert.Single(entries, e => e.KeyPath == @"Software\Classes\Pagewing.PDF\shell\open\command");
        Assert.Null(progId.ValueName);
        Assert.Equal("\"C:\\Apps\\Pagewing\\Pagewing.exe\" \"%1\"", progId.Value);
        Assert.Contains(entries, e => e.KeyPath == @"Software\Classes\Applications\Pagewing.exe\shell\open\command" && e.Value == progId.Value);
        Assert.Contains(entries, e => e.KeyPath == @"Software\Classes\Pagewing.PDF\DefaultIcon" && e.Value == "\"C:\\Apps\\Pagewing\\Pagewing.exe\",0");
        Assert.Contains(entries, e => e.KeyPath == @"Software\Classes\.pdf\OpenWithProgids" && e.ValueName == "Pagewing.PDF" && e.Value.Length == 0);
        Assert.Contains(entries, e => e.KeyPath == @"Software\Pagewing\Capabilities" && e.ValueName == "ApplicationName");
        Assert.Contains(entries, e => e.KeyPath == @"Software\Pagewing\Capabilities" && e.ValueName == "ApplicationDescription");
        Assert.Contains(entries, e => e.KeyPath == @"Software\Pagewing\Capabilities\FileAssociations" && e.ValueName == ".pdf" && e.Value == "Pagewing.PDF");
        Assert.Contains(entries, e => e.KeyPath == @"Software\RegisteredApplications" && e.ValueName == "Pagewing" && e.Value == @"Software\Pagewing\Capabilities");
    }

    [Fact]
    public void CommandQuotesAPathWithSpacesAndUmlauts()
    {
        var entries = FileTypeRegistration.Entries(@"C:\Program Files\Pagewing Test ä&\Pagewing.exe");

        var command = Assert.Single(entries, e => e.KeyPath == @"Software\Classes\Pagewing.PDF\shell\open\command");
        Assert.Equal("\"C:\\Program Files\\Pagewing Test ä&\\Pagewing.exe\" \"%1\"", command.Value);
    }

    [Fact]
    public void EveryEntryHasARemoval()
    {
        var removals = FileTypeRegistration.Removals();

        foreach (var entry in FileTypeRegistration.Entries(Exe))
        {
            var covered = removals.Any(r => r.ValueName is null
                ? entry.KeyPath.Equals(r.KeyPath, StringComparison.OrdinalIgnoreCase)
                    || entry.KeyPath.StartsWith(r.KeyPath + "\\", StringComparison.OrdinalIgnoreCase)
                : entry.KeyPath.Equals(r.KeyPath, StringComparison.OrdinalIgnoreCase) && entry.ValueName == r.ValueName);
            Assert.True(covered, $"{entry.KeyPath} / {entry.ValueName} is not removed");
        }
    }

    [Fact]
    public void NothingTouchesTheUserChoice()
    {
        var paths = FileTypeRegistration.Entries(Exe).Select(e => e.KeyPath)
            .Concat(FileTypeRegistration.Removals().Select(r => r.KeyPath));

        Assert.DoesNotContain(paths, p => p.Contains("UserChoice", StringComparison.OrdinalIgnoreCase)
            || p.Contains("FileExts", StringComparison.OrdinalIgnoreCase));
    }
}
