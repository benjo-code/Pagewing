using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Proves that the cleanup of <see cref="UpdateTestHost"/> runs when xUnit disposes a test class (through the interface).</summary>
// The host sets the culture of Strings, which is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateTestHostCleanupTests
{
    [Fact]
    public async Task DisposingTheHostThroughTheInterfaceCleansUp()
    {
        var host = new ProbeHost();
        var owned = new Probe();
        host.OwnIt(owned);
        host.Source.Hang = true;
        var coordinator = host.MakeCoordinator();
        var check = coordinator.StartAsync();
        while (host.Source.FetchCalls == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var folder = Path.GetDirectoryName(host.Status)!;
        Assert.True(Directory.Exists(folder));

        // As xUnit does it: through IDisposable, not through the type of the test class.
        ((IDisposable)host).Dispose();

        Assert.True(owned.Disposed);
        Assert.False(Directory.Exists(folder));

        // The coordinator was disposed too: its hanging check ended.
        await check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ProbeHost : UpdateTestHost
    {
        public new FakeUpdateSourceApp Source => base.Source;

        public string Status => StatusPath;

        public void OwnIt(IDisposable item) => Own(item);

        public Pagewing.App.Services.UpdateCoordinator MakeCoordinator() => NewCoordinator();
    }
}
