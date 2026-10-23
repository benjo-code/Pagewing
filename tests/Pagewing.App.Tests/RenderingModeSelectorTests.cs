using Pagewing.App.Platform;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class RenderingModeSelectorTests
{
    [Fact]
    public void WindowsRendersInSoftwareByDefault() =>
        Assert.True(RenderingModeSelector.UseSoftwareRendering(null, isWindows: true));

    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    [InlineData("Default")]
    [InlineData(" default ")]
    [InlineData("\tdefault")]
    public void DefaultTakesAvaloniasGpuPathOnWindows(string value) =>
        Assert.False(RenderingModeSelector.UseSoftwareRendering(value, isWindows: true));

    [Theory]
    [InlineData("software")]
    [InlineData("Software")]
    [InlineData(" software")]
    public void SoftwareKeepsSoftwareRenderingOnWindows(string value) =>
        Assert.True(RenderingModeSelector.UseSoftwareRendering(value, isWindows: true));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("auto")]
    [InlineData("angle")]
    [InlineData("gpu")]
    [InlineData("defaults")]
    public void AnUnknownValueKeepsSoftwareRenderingOnWindows(string value) =>
        Assert.True(RenderingModeSelector.UseSoftwareRendering(value, isWindows: true));

    [Theory]
    [InlineData(null)]
    [InlineData("software")]
    [InlineData("default")]
    [InlineData("auto")]
    public void OtherSystemsKeepAvaloniasDefault(string? value) =>
        Assert.False(RenderingModeSelector.UseSoftwareRendering(value, isWindows: false));
}
