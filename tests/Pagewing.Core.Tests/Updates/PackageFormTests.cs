using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class PackageFormTests
{
    private static readonly string InstallDirectory = Path.Combine(Path.GetTempPath(), "Pagewing-app");

    private static readonly string ExePath = Path.Combine(InstallDirectory, "Pagewing.App.exe");

    private static PackageForm Windows(bool uninstallerExists) =>
        PackageFormDetector.Detect(
            isWindows: true,
            ExePath,
            path => uninstallerExists && path == Path.Combine(InstallDirectory, "unins000.exe"),
            _ => null);

    private static PackageForm Linux(string? processPath, string? appImage, string? appDir = null) =>
        PackageFormDetector.Detect(
            isWindows: false,
            processPath,
            _ => false,
            name => name switch { "APPIMAGE" => appImage, "APPDIR" => appDir, _ => null });

    [Fact]
    public void AnUninstallerNextToTheProgramMeansTheInstaller() =>
        Assert.Equal(PackageForm.WindowsInstaller, Windows(uninstallerExists: true));

    [Fact]
    public void NoUninstallerMeansThePortableArchive() =>
        Assert.Equal(PackageForm.WindowsPortable, Windows(uninstallerExists: false));

    [Fact]
    public void WindowsWithoutAProcessPathIsPortable() =>
        Assert.Equal(
            PackageForm.WindowsPortable,
            PackageFormDetector.Detect(isWindows: true, processPath: null, _ => true, _ => null));

    [Theory]
    [InlineData("/tmp/.mount_x/usr/lib/pagewing/Pagewing", "/home/u/Pagewing.AppImage", "/tmp/.mount_x", PackageForm.AppImage)]
    [InlineData("/tmp/.mount_x/usr/lib/pagewing/Pagewing", "/home/u/Pagewing.AppImage", "/tmp/.mount_x/", PackageForm.AppImage)]
    [InlineData("/usr/lib/pagewing/Pagewing", "", null, PackageForm.Deb)]
    [InlineData("/usr/lib/pagewing/Pagewing", null, null, PackageForm.Deb)]
    [InlineData("/usr/lib/pagewing-other/Pagewing", null, null, PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", null, null, PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", "", null, PackageForm.Unknown)]
    [InlineData(null, null, null, PackageForm.Unknown)]
    public void LinuxFormFollowsTheEnvironmentAndThePath(string? processPath, string? appImage, string? appDir, PackageForm expected) =>
        Assert.Equal(expected, Linux(processPath, appImage, appDir));

    // Every child of an AppImage inherits APPIMAGE (and APPDIR): a Pagewing that was started by one is not that AppImage.
    [Theory]
    [InlineData("/usr/lib/pagewing/Pagewing", null, PackageForm.Deb)]
    [InlineData("/usr/lib/pagewing/Pagewing", "/tmp/.mount_other", PackageForm.Deb)]
    [InlineData("/opt/pagewing/Pagewing", null, PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", "", PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", "/tmp/.mount_other", PackageForm.Unknown)]
    [InlineData("/tmp/.mount_abc/usr/lib/pagewing/Pagewing", "/tmp/.mount_ab", PackageForm.Unknown)]
    [InlineData("/tmp/.mount_ab/usr/lib/pagewing/Pagewing", "/tmp/.mount_abc", PackageForm.Unknown)]
    [InlineData("/tmp/.mount_x/../evil/Pagewing", "/tmp/.mount_x", PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", "/", PackageForm.Unknown)]
    [InlineData("/tmp/.mount_x", "/tmp/.mount_x", PackageForm.Unknown)]
    [InlineData(null, "/tmp/.mount_x", PackageForm.Unknown)]
    public void AnInheritedAppImageVariableAloneIsNotTheAppImage(string? processPath, string? appDir, PackageForm expected) =>
        Assert.Equal(expected, Linux(processPath, "/home/u/Other.AppImage", appDir));

    [Fact]
    public void AnAppDirWithoutTheAppImageVariableIsNotTheAppImage() =>
        Assert.Equal(PackageForm.Unknown, Linux("/tmp/.mount_x/usr/lib/pagewing/Pagewing", null, "/tmp/.mount_x"));

    private static UpdateManifest ManifestWithAllKinds() =>
        new(
            1,
            "0.2.0",
            UpdateFileKinds.All.Select(kind => new UpdateFile(kind, "f-" + kind, 10, new string('a', 64))).ToList());

    [Theory]
    [InlineData(PackageForm.WindowsInstaller, UpdateFileKinds.WindowsSetup)]
    [InlineData(PackageForm.AppImage, UpdateFileKinds.LinuxAppImage)]
    public void PickChoosesTheFileOfTheForm(PackageForm form, string kind) =>
        Assert.Equal(kind, UpdateChecker.Pick(ManifestWithAllKinds(), form)?.Kind);

    [Theory]
    [InlineData(PackageForm.WindowsPortable)]
    [InlineData(PackageForm.Deb)]
    [InlineData(PackageForm.Unknown)]
    public void PickGivesNothingForFormsThatDoNotUpdateThemselves(PackageForm form) =>
        Assert.Null(UpdateChecker.Pick(ManifestWithAllKinds(), form));

    [Fact]
    public void PickGivesNothingWhenTheManifestLacksTheFile()
    {
        var manifest = new UpdateManifest(1, "0.2.0", [new UpdateFile(UpdateFileKinds.LinuxDeb, "x.deb", 10, new string('a', 64))]);

        Assert.Null(UpdateChecker.Pick(manifest, PackageForm.WindowsInstaller));
    }
}
