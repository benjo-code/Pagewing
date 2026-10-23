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

    [Fact]
    public async Task DisposingDuringTheStagingWriteLeavesNoFolderBehind()
    {
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var host = new ProbeHost();
        host.Source.Offer("0.2.0");
        var coordinator = host.MakeCoordinator(() =>
        {
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(15));
        });
        await coordinator.CheckNowAsync();
        var install = coordinator.BeginInstallAsync();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        var folder = host.Config;

        // The write goes on after the host was asked to clean up (as when a test ends during an installation).
        var releasing = Task.Run(
            async () =>
            {
                await Task.Delay(200);
                release.Set();
            },
            TestContext.Current.CancellationToken);
        ((IDisposable)host).Dispose();

        // The host waited for the installation of its coordinator before it deleted the folder, and nothing brought the
        // folder back: no pagewing-update-* folder stays in %TEMP%.
        Assert.True(install.IsCompleted);
        Assert.False(Directory.Exists(folder));
        await releasing;
        Assert.False(Directory.Exists(folder));
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

        public string Config => ConfigDirectory;

        public void OwnIt(IDisposable item) => Own(item);

        public Pagewing.App.Services.UpdateCoordinator MakeCoordinator(Action? stagingWriteStarted = null) =>
            NewCoordinator(stagingWriteStarted: stagingWriteStarted);
    }
}
