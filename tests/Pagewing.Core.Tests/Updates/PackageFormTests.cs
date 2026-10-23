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

    private static PackageForm Linux(string? processPath, string? appImage) =>
        PackageFormDetector.Detect(
            isWindows: false,
            processPath,
            _ => false,
            name => name == "APPIMAGE" ? appImage : null);

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
    [InlineData("/tmp/.mount_x/usr/bin/Pagewing", "/home/u/Pagewing.AppImage", PackageForm.AppImage)]
    [InlineData("/usr/lib/pagewing/Pagewing", "/home/u/Pagewing.AppImage", PackageForm.AppImage)]
    [InlineData("/usr/lib/pagewing/Pagewing", "", PackageForm.Deb)]
    [InlineData("/usr/lib/pagewing/Pagewing", null, PackageForm.Deb)]
    [InlineData("/usr/lib/pagewing-other/Pagewing", null, PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", null, PackageForm.Unknown)]
    [InlineData("/opt/pagewing/Pagewing", "", PackageForm.Unknown)]
    [InlineData(null, null, PackageForm.Unknown)]
    public void LinuxFormFollowsTheEnvironmentAndThePath(string? processPath, string? appImage, PackageForm expected) =>
        Assert.Equal(expected, Linux(processPath, appImage));

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
