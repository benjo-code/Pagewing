using Pagewing.App.Platform;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class RenderingModeSelectorTests
{
    private const uint Render = GraphicsAdapter.RenderSupportedFlag;
    private const uint Display = GraphicsAdapter.DisplaySupportedFlag;
    private const uint Software = GraphicsAdapter.SoftwareDeviceFlag;

    /// <summary>Microsoft Basic Render Driver (WARP).</summary>
    private static readonly GraphicsAdapter BasicRenderDriver = new(1, Render | Software);

    /// <summary>Microsoft Basic Display Adapter or a VirtIO display-only driver: displays, does not render.</summary>
    private static readonly GraphicsAdapter DisplayOnly = new(2, Display);

    /// <summary>A GPU, integrated or discrete.</summary>
    private static readonly GraphicsAdapter Gpu = new(3, Render | Display);

    /// <summary>A GPU without outputs (render-only, for example the discrete one of a hybrid laptop or GPU-P in a VM).</summary>
    private static readonly GraphicsAdapter RenderOnlyGpu = new(4, Render);

    [Fact]
    public void WithoutAGpuItRendersInSoftware()
    {
        Assert.True(RenderingModeSelector.UseSoftwareRendering(null, () => [DisplayOnly, BasicRenderDriver]));
        Assert.True(RenderingModeSelector.UseSoftwareRendering(null, () => [BasicRenderDriver]));
    }

    [Fact]
    public void AnyHardwareRendererKeepsTheDefault()
    {
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => [Gpu, BasicRenderDriver]));
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => [BasicRenderDriver, DisplayOnly, RenderOnlyGpu]));
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => [Gpu]));
    }

    [Fact]
    public void AdapterTypesFollowTheKernelFlags()
    {
        Assert.True(Gpu.IsHardwareRenderer);
        Assert.True(RenderOnlyGpu.IsHardwareRenderer);
        Assert.False(BasicRenderDriver.IsHardwareRenderer);
        Assert.True(BasicRenderDriver.IsSoftwareRenderer);
        Assert.False(DisplayOnly.IsHardwareRenderer);
        Assert.False(DisplayOnly.IsSoftwareRenderer);
    }

    [Fact]
    public void AFailedOrUncertainDetectionKeepsTheDefault()
    {
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => null));
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => []));
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => throw new InvalidOperationException("broken driver")));

        // No renderer at all is not what Windows lists (it always has the Basic Render Driver): uncertain.
        Assert.False(RenderingModeSelector.UseSoftwareRendering(null, () => [DisplayOnly]));
    }

    [Theory]
    [InlineData("software", true)]
    [InlineData("Software", true)]
    [InlineData("default", false)]
    [InlineData("DEFAULT", false)]
    public void TheOverrideSkipsTheDetection(string value, bool software)
    {
        var detected = false;
        var adapters = software ? new[] { Gpu } : new[] { BasicRenderDriver };
        Assert.Equal(software, RenderingModeSelector.UseSoftwareRendering(value, () =>
        {
            detected = true;
            return adapters;
        }));
        Assert.False(detected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("angle")]
    public void AnUnknownOverrideIsIgnored(string value)
    {
        Assert.True(RenderingModeSelector.UseSoftwareRendering(value, () => [BasicRenderDriver]));
        Assert.False(RenderingModeSelector.UseSoftwareRendering(value, () => [Gpu]));
    }

    [Fact]
    public void TheKernelListsTheAdaptersOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("D3DKMT exists only on Windows");
            return;
        }

        var adapters = KmtAdapters.TryEnumerate();

        // Windows 8 or later lists at least one renderer: a GPU or the Microsoft Basic Render Driver.
        Assert.NotNull(adapters);
        Assert.Contains(adapters, adapter => adapter.IsHardwareRenderer || adapter.IsSoftwareRenderer);
    }
}
