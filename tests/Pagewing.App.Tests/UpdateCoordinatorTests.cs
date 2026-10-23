using Pagewing.App.Services;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>The coordinator on its own: one run at a time, silent failures, a time limit, and what it reports.</summary>
// The host sets the culture of Strings, which is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateCoordinatorTests : UpdateTestHost
{
    [Fact]
    public async Task SimultaneousChecksShareOneRun()
    {
        Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = NewCoordinator();

        // The check of the start and the command at the same moment: one fetch, both wait for it.
        var start = coordinator.StartAsync();
        var command = coordinator.CheckNowAsync();
        await WaitForAsync(() => Source.FetchCalls >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, Source.FetchCalls);
        Assert.False(start.IsCompleted);
        Assert.False(command.IsCompleted);

        Source.Gate.SetResult();
        await Task.WhenAll(start, command).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, Source.FetchCalls);
    }

    [Fact]
    public async Task StartingTwiceChecksOnce()
    {
        var coordinator = NewCoordinator();

        await coordinator.StartAsync();
        await coordinator.StartAsync();

        Assert.True(coordinator.CheckStarted);
        Assert.Equal(1, Source.FetchCalls);
    }

    [Fact]
    public async Task StartingWithTheSettingOffDoesNothing()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { UpdateCheck = false });
        var coordinator = NewCoordinator(settings);

        await coordinator.StartAsync();

        Assert.True(coordinator.CheckStarted);
        Assert.Equal(0, Source.FetchCalls);
        Assert.False(File.Exists(StatusPath));
        Assert.Equal(UpdateState.None, coordinator.State);
    }

    [Fact]
    public async Task ASourceThatThrowsStaysSilent()
    {
        Source.Throws = new HttpRequestException("connection reset");
        var coordinator = NewCoordinator();
        var changes = 0;
        coordinator.Changed += () => changes++;

        await coordinator.StartAsync();
        await coordinator.CheckNowAsync();

        Assert.Equal(2, Source.FetchCalls);
        Assert.Equal(UpdateState.None, coordinator.State);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ANewerVersionIsReportedOnce()
    {
        Source.Offer("9.9.9");
        var coordinator = NewCoordinator();
        var changes = 0;
        coordinator.Changed += () => changes++;

        await coordinator.StartAsync();

        Assert.Equal(UpdateStage.Available, coordinator.State.Stage);
        Assert.Equal(new UpdateVersion(9, 9, 9), coordinator.State.Version);
        Assert.Equal(1, changes);

        // The command finds the same version: nothing changed, nobody is told again.
        await coordinator.CheckNowAsync();
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AVersionThatIsNotNewerOffersNothing()
    {
        Source.Offer("0.1.0");
        var coordinator = NewCoordinator();

        await coordinator.StartAsync();

        Assert.Equal(UpdateState.None, coordinator.State);
    }

    [Fact]
    public async Task TheCheckAlwaysRunsWithATimeout()
    {
        // The source hangs after it was asked; the time limit of the check (on the clock of the test) ends it, silently.
        Source.Hang = true;
        var clock = new PrintClock();
        var coordinator = NewCoordinator(clock: clock);

        var check = coordinator.StartAsync();
        await WaitForAsync(() => Source.FetchCalls == 1);
        Assert.False(check.IsCompleted);

        clock.Advance(UpdateChecker.CheckTimeout + TimeSpan.FromSeconds(1));
        await check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.None, coordinator.State);
    }

    [Fact]
    public async Task DisposingEndsARunningCheck()
    {
        Source.Hang = true;
        var coordinator = NewCoordinator();
        var check = coordinator.StartAsync();
        await WaitForAsync(() => Source.FetchCalls == 1);

        coordinator.Dispose();

        await check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.None, coordinator.State);
    }

    [Fact]
    public async Task TheInstallIsOnlyAPlaceholderForNow()
    {
        var coordinator = NewCoordinator();

        await coordinator.BeginInstallAsync();

        Assert.Equal(0, Source.FetchCalls);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not come true.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
