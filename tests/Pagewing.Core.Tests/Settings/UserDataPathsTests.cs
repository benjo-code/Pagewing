using Pagewing.Core.Settings;
using Xunit;

namespace Pagewing.Core.Tests.Settings;

public sealed class UserDataPathsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void WindowsUsesAppData()
    {
        var path = UserDataPaths.ConfigDirectory(Env(("APPDATA", @"C:\Users\x\AppData\Roaming")), isWindows: true);

        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Roaming", "Pagewing"), path);
    }

    [Fact]
    public void LinuxPrefersXdgConfigHomeThenHome()
    {
        Assert.Equal(
            Path.Combine("/cfg", "pagewing"),
            UserDataPaths.ConfigDirectory(Env(("XDG_CONFIG_HOME", "/cfg"), ("HOME", "/home/x")), isWindows: false));
        Assert.Equal(
            Path.Combine("/home/x", ".config", "pagewing"),
            UserDataPaths.ConfigDirectory(Env(("HOME", "/home/x")), isWindows: false));
    }

    [Fact]
    public void EmptyVariableCountsAsUnset()
    {
        Assert.Equal(
            Path.Combine("/home/x", ".config", "pagewing"),
            UserDataPaths.ConfigDirectory(Env(("XDG_CONFIG_HOME", ""), ("HOME", "/home/x")), isWindows: false));
        Assert.Equal(
            Path.Combine("/home/x", ".config", "pagewing"),
            UserDataPaths.ConfigDirectory(Env(("XDG_CONFIG_HOME", "  "), ("HOME", "/home/x")), isWindows: false));
    }

    [Fact]
    public void WithoutAnyVariableTheDirectoryIsStillUsable()
    {
        Assert.False(string.IsNullOrWhiteSpace(UserDataPaths.ConfigDirectory(Env(), isWindows: true)));
        Assert.False(string.IsNullOrWhiteSpace(UserDataPaths.ConfigDirectory(Env(), isWindows: false)));
    }
}
