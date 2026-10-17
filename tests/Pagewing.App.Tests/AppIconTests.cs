using Avalonia.Headless.XUnit;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class AppIconTests
{
    [AvaloniaFact]
    public void WindowIconIsEmbeddedAndDecodes()
    {
        var icon = App.LoadWindowIcon();

        Assert.NotNull(icon);
    }

    [Fact]
    public void ExecutableIconIsAnIcoWithTheUsualSizes()
    {
        var path = Path.Combine(TestData.Root, "src", "Pagewing.App", "Assets", "pagewing.ico");
        var bytes = File.ReadAllBytes(path);

        // ICONDIR: reserved 0, type 1 (icon), then the number of images.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + (16 * i)] is 0 ? 256 : bytes[6 + (16 * i)]).ToHashSet();
        Assert.Superset(new HashSet<int> { 16, 24, 32, 48, 64, 256 }, sizes);
    }
}
