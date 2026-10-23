using System.Text;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly UpdateVersion Current = new(0, 1, 0);
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-checker-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUpdateSource _source = new();
    private readonly ManualClock _clock = new(Start);

    public UpdateCheckerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _source.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string StatusPath => Path.Combine(_directory, "update.json");

    private UpdateStatusStore Store() => new(StatusPath);

    private UpdateChecker Checker() => new(_source, Store(), _clock, Current, _source.Keys);

    private void SaveStatus(DateTimeOffset? lastChecked, string? available = null) =>
        Assert.True(Store().TrySave(new UpdateStatus(lastChecked, available)));

    [Fact]
    public async Task NoCallWhenNotDueAndNotForced()
    {
        SaveStatus(Start - TimeSpan.FromHours(1));
        _source.Offer("0.2.0");

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(0, _source.FetchCalls);
        Assert.Equal(UpdateStage.None, state.Stage);
    }

    [Fact]
    public async Task ChecksWhenNoStatusExists()
    {
        _source.Offer("0.2.0");

        await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(1, _source.FetchCalls);
    }

    [Fact]
    public async Task ChecksAgainAfter24Hours()
    {
        SaveStatus(Start);
        _source.Offer("0.2.0");

        _clock.Advance(UpdateChecker.Interval - TimeSpan.FromSeconds(1));
        await Checker().CheckAsync(force: false, CancellationToken.None);
        Assert.Equal(0, _source.FetchCalls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Checker().CheckAsync(force: false, CancellationToken.None);
        Assert.Equal(1, _source.FetchCalls);
    }

    [Fact]
    public async Task ForceChecksEvenWhenNotDue()
    {
        SaveStatus(Start);
        _source.Offer("0.2.0");

        var state = await Checker().CheckAsync(force: true, CancellationToken.None);

        Assert.Equal(1, _source.FetchCalls);
        Assert.Equal(UpdateStage.Available, state.Stage);
    }

    [Fact]
    public async Task FutureLastCheckedCountsAsDue()
    {
        SaveStatus(Start + TimeSpan.FromDays(400));
        _source.Offer("0.2.0");

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(1, _source.FetchCalls);
        Assert.Equal(UpdateStage.Available, state.Stage);

        // The wrong date is replaced by the real one, so the next check is not blocked either.
        Assert.Equal(Start, Store().Load().LastChecked);
    }

    [Fact]
    public async Task StoresTheFoundVersionAndTheTime()
    {
        _source.Offer("v0.2.0");

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(new UpdateVersion(0, 2, 0), state.Version);
        var status = Store().Load();
        Assert.Equal(Start, status.LastChecked);
        Assert.True(UpdateVersion.TryParse(status.AvailableVersion, out var stored));
        Assert.Equal(new UpdateVersion(0, 2, 0), stored);
        Assert.Equal([StatusPath], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task KnownKeepsTheOfferAcrossRestarts()
    {
        _source.Offer("0.2.0");
        await Checker().CheckAsync(force: false, CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));
        var fetchesBefore = _source.FetchCalls;

        var restarted = Checker();
        var known = restarted.Known();
        var state = await restarted.CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.Available, known.Stage);
        Assert.Equal(new UpdateVersion(0, 2, 0), known.Version);
        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(fetchesBefore, _source.FetchCalls);
    }

    [Fact]
    public void KnownIsNoneWithoutAFile()
    {
        var state = Checker().Known();

        Assert.Equal(UpdateState.None, state);
        Assert.Equal(0, _source.FetchCalls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    public void KnownIsNoneWhenTheStoredVersionIsNotNewer(string stored)
    {
        SaveStatus(Start, stored);

        Assert.Equal(UpdateStage.None, Checker().Known().Stage);
    }

    [Fact]
    public void KnownAcceptsAPrefixedStoredVersion()
    {
        SaveStatus(Start, "v0.3.0");

        var state = Checker().Known();

        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(new UpdateVersion(0, 3, 0), state.Version);
    }

    [Fact]
    public async Task NewerManifestGivesAvailable()
    {
        _source.Offer("1.0.0");

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(new UpdateVersion(1, 0, 0), state.Version);
        Assert.Null(state.Error);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.5")]
    public async Task SameOrOlderGivesNone(string offered)
    {
        SaveStatus(Start - TimeSpan.FromDays(2), "0.1.5");
        _source.Offer(offered);

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.None, state.Stage);

        // The check itself succeeded: the time is recorded (no new request on every start) and the old offer is gone.
        var status = Store().Load();
        Assert.Equal(Start, status.LastChecked);
        Assert.Null(status.AvailableVersion);
    }

    [Fact]
    public async Task FailureKeepsTheLastCheckedUntouched()
    {
        var earlier = Start - TimeSpan.FromDays(2);
        SaveStatus(earlier, "0.2.0");
        _source.Documents = null;

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(1, _source.FetchCalls);
        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(new UpdateStatus(earlier, "0.2.0"), Store().Load());
    }

    [Fact]
    public async Task RejectedManifestChangesNothing()
    {
        var earlier = Start - TimeSpan.FromDays(2);
        SaveStatus(earlier, "0.2.0");
        _source.Offer("0.9.0");
        _source.Documents = _source.Documents! with
        {
            Manifest = Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"9.9.9\",\"files\":[]}"),
        };

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.Available, state.Stage);
        Assert.Equal(new UpdateVersion(0, 2, 0), state.Version);
        Assert.Equal(new UpdateStatus(earlier, "0.2.0"), Store().Load());
    }

    [Fact]
    public async Task ASignatureNamingAnUnknownKeyChangesNothing()
    {
        _source.Offer("5.0.0");
        _source.Documents = _source.Documents! with { Signature = "other:" + _source.Documents.Signature.Split(':')[1] };

        var state = await Checker().CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(UpdateStage.None, state.Stage);
        Assert.False(File.Exists(StatusPath));
    }

    [Fact]
    public async Task TimeoutIsSilent()
    {
        var earlier = Start - TimeSpan.FromDays(2);
        SaveStatus(earlier);
        _source.Hang = true;

        var pending = Checker().CheckAsync(force: false, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        _clock.Advance(UpdateChecker.CheckTimeout);
        var state = await pending;

        Assert.Equal(UpdateStage.None, state.Stage);
        Assert.Equal(new UpdateStatus(earlier, null), Store().Load());
    }

    [Fact]
    public async Task CancellationThrows()
    {
        _source.Hang = true;
        using var cts = new CancellationTokenSource();

        var pending = Checker().CheckAsync(force: false, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(StatusPath));
    }

    [Fact]
    public async Task FetchVerifiedReturnsTheManifestAndWritesNothing()
    {
        _source.Offer("0.2.0");

        var result = await Checker().FetchVerifiedAsync(CancellationToken.None);

        Assert.Null(result.Rejection);
        Assert.Equal("0.2.0", result.Manifest?.Version);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FetchVerifiedGivesTheReasonForARejection()
    {
        _source.Offer("0.1.0");

        var result = await Checker().FetchVerifiedAsync(CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.NotNewer, result.Rejection);
    }

    [Fact]
    public async Task FetchVerifiedGivesNeitherWhenNothingWasReceived()
    {
        var result = await Checker().FetchVerifiedAsync(CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Null(result.Rejection);
    }

    [Fact]
    public async Task FetchVerifiedDocumentsKeepsTheBytesAsReceived()
    {
        _source.Offer("0.2.0");
        var sent = _source.Documents!;

        var (result, documents) = await Checker().FetchVerifiedDocumentsAsync(CancellationToken.None);

        // The signature covers these exact bytes, so they are what the installer verifies again.
        Assert.Equal("0.2.0", result.Manifest?.Version);
        Assert.Same(sent, documents);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FetchVerifiedDocumentsGivesNoDocumentsForARejection()
    {
        _source.Offer("0.1.0");

        var (result, documents) = await Checker().FetchVerifiedDocumentsAsync(CancellationToken.None);

        Assert.Equal(ManifestRejection.NotNewer, result.Rejection);
        Assert.Null(documents);
    }

    [Fact]
    public async Task FetchVerifiedTimesOutSilently()
    {
        _source.Hang = true;

        var pending = Checker().FetchVerifiedAsync(CancellationToken.None);
        _clock.Advance(UpdateChecker.CheckTimeout);
        var result = await pending;

        Assert.Null(result.Manifest);
        Assert.Null(result.Rejection);
    }
}
