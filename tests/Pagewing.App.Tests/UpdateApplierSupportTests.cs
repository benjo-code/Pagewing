using Pagewing.App.Services;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// What the helper of the updater reads from the staging folder before it trusts any of it (security review before going
/// public, M12.1): a file over the limit is not read at all, whatever it holds.
/// </summary>
public sealed class UpdateApplierSupportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-support-" + Guid.NewGuid().ToString("N"));

    public UpdateApplierSupportTests() => Directory.CreateDirectory(_directory);

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
    public void ASmallFileIsReadUpToTheLimitAndNotBeyond()
    {
        var path = Path.Combine(_directory, UpdateStaging.ManifestFileName);

        File.WriteAllBytes(path, new byte[UpdateApplierSupport.MaxSmallFile]);
        Assert.Equal(UpdateApplierSupport.MaxSmallFile, UpdateApplierSupport.ReadSmall(path)!.Length);

        File.WriteAllBytes(path, new byte[UpdateApplierSupport.MaxSmallFile + 1]);
        Assert.Null(UpdateApplierSupport.ReadSmall(path));
    }

    [Fact]
    public void AMissingFileOrAFolderGivesNothing()
    {
        Assert.Null(UpdateApplierSupport.ReadSmall(Path.Combine(_directory, "missing.json")));

        var folder = Path.Combine(_directory, "folder.json");
        Directory.CreateDirectory(folder);
        Assert.Null(UpdateApplierSupport.ReadSmall(folder));
    }

    [Fact]
    public void AStagingFolderMustBeNamedByAVersionDirectlyBelowTheUpdatesFolder()
    {
        var updates = Path.Combine(_directory, UpdateStaging.DirectoryName);
        var good = Directory.CreateDirectory(Path.Combine(updates, "9.9.9")).FullName;
        var notAVersion = Directory.CreateDirectory(Path.Combine(updates, "latest")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(updates, "9.9.9", "9.9.8")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_directory, "9.9.7")).FullName;

        Assert.Equal(good, UpdateApplierSupport.AcceptedStaging(good, _directory));
        Assert.Equal(good, UpdateApplierSupport.AcceptedStaging(good + Path.DirectorySeparatorChar, _directory));
        Assert.Null(UpdateApplierSupport.AcceptedStaging(notAVersion, _directory));
        Assert.Null(UpdateApplierSupport.AcceptedStaging(nested, _directory));
        Assert.Null(UpdateApplierSupport.AcceptedStaging(elsewhere, _directory));
        Assert.Null(UpdateApplierSupport.AcceptedStaging(Path.Combine(updates, "..", "9.9.7"), _directory));
        Assert.Null(UpdateApplierSupport.AcceptedStaging(Path.Combine(updates, "9.9.0"), _directory));
    }
}
