using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Pagewing.Core.Hosting;
using Xunit;

namespace Pagewing.Core.Tests.Hosting;

public sealed class SingleInstanceTests
{
    // How long a test waits for something that must happen.
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    // How long a test waits to show that something does not happen.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private static string UniqueName() => "pagewing-test-" + Guid.NewGuid().ToString("N");

    private static SingleInstance Primary(string name) =>
        SingleInstance.TryBecomePrimary(name) ?? throw new InvalidOperationException("Expected to become primary.");

    private static Channel<IReadOnlyList<string>> Listen(SingleInstance instance)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<string>>();
        instance.StartListening(paths => channel.Writer.TryWrite(paths));
        return channel;
    }

    private static async Task<IReadOnlyList<string>> NextAsync(Channel<IReadOnlyList<string>> channel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Due);
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task AssertNothingAsync(Channel<IReadOnlyList<string>> channel)
    {
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.False(channel.Reader.TryRead(out var unexpected), "Unexpected message: " + string.Join("|", unexpected ?? []));
    }

    private static byte[] Message(params string[] paths)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(paths.Length);
            foreach (var path in paths)
            {
                writer.Write(path);
            }
        }

        return stream.ToArray();
    }

    private static void SendRaw(string name, byte[] bytes)
    {
        using var client = new NamedPipeClientStream(".", SingleInstance.PipeNameFor(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect((int)Due.TotalMilliseconds);
        client.Write(bytes);
        client.Flush();
    }

    [Fact]
    public void FirstInstanceBecomesPrimaryAndSecondDoesNot()
    {
        var name = UniqueName();

        using var first = SingleInstance.TryBecomePrimary(name);
        using var second = SingleInstance.TryBecomePrimary(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void LockIsReleasedOnDispose()
    {
        var name = UniqueName();
        Primary(name).Dispose();

        using var again = SingleInstance.TryBecomePrimary(name);

        Assert.NotNull(again);
    }

    [Fact]
    public void DifferentNamesDoNotConflict()
    {
        using var first = SingleInstance.TryBecomePrimary(UniqueName());
        using var second = SingleInstance.TryBecomePrimary(UniqueName());

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void NamesWithUnusualCharactersWork()
    {
        using var instance = SingleInstance.TryBecomePrimary(UniqueName() + " a/b\\c:d*?|<>\"\u00e9");

        Assert.NotNull(instance);
    }

    [Fact]
    public void DefaultNameContainsTheUserName()
    {
        Assert.StartsWith("pagewing-", SingleInstance.DefaultName, StringComparison.Ordinal);
        Assert.EndsWith(Environment.UserName, SingleInstance.DefaultName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathsArriveAtThePrimary()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);
        string[] paths = ["C:\\Dokumente\\Gr\u00f6\u00dfe & Ma\u00df.pdf", "/tmp/with space/\u65e5\u672c\u8a9e.pdf", "plain.pdf"];

        var sent = SingleInstance.TrySend(name, paths, Due);

        Assert.True(sent);
        Assert.Equal(paths, await NextAsync(received));
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task EmptyListActivatesOnly()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);

        var sent = SingleInstance.TrySend(name, [], Due);

        Assert.True(sent);
        Assert.Empty(await NextAsync(received));
    }

    [Fact]
    public async Task MoreThanTheMessageLimitArrivesInSeveralMessages()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);
        var paths = Enumerable.Range(0, 150).Select(i => $"file-{i}.pdf").ToArray();

        var sent = SingleInstance.TrySend(name, paths, Due);

        Assert.True(sent);
        var all = new List<string>();
        while (all.Count < paths.Length)
        {
            var message = await NextAsync(received);
            Assert.InRange(message.Count, 1, 64);
            all.AddRange(message);
        }

        Assert.Equal(paths, all);
    }

    [Fact]
    public async Task SeveralSendersAreAllDelivered()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);

        var senders = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => SingleInstance.TrySend(name, [$"file-{i}.pdf"], Due), TestContext.Current.CancellationToken))
            .ToArray();

        Assert.All(await Task.WhenAll(senders), Assert.True);
        var seen = new List<string>();
        for (var i = 0; i < senders.Length; i++)
        {
            seen.AddRange(await NextAsync(received));
        }

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"file-{i}.pdf").Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SendWaitsForTheListener()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var send = Task.Run(() => SingleInstance.TrySend(name, ["late.pdf"], Due), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(send.IsCompleted);

        var received = Listen(primary);

        Assert.True(await send);
        Assert.Equal(["late.pdf"], await NextAsync(received));
    }

    [Fact]
    public void SendFailsAfterTheTimeoutWithoutAPrimary()
    {
        var timeout = TimeSpan.FromMilliseconds(400);
        var watch = Stopwatch.StartNew();

        var sent = SingleInstance.TrySend(UniqueName(), ["x.pdf"], timeout);

        watch.Stop();
        Assert.False(sent);
        Assert.InRange(watch.Elapsed, timeout - TimeSpan.FromMilliseconds(50), timeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GarbageOnThePipeIsIgnoredAndTheListenerKeepsWorking()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);

        // Random bytes with a huge positive count.
        var random = new byte[200];
        new Random(42).NextBytes(random);
        random[3] = 0x7F;
        SendRaw(name, random);

        // One path whose length prefix claims 2 GB.
        byte[] huge = [1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, (byte)'a', (byte)'b'];
        SendRaw(name, huge);

        // More paths than allowed.
        SendRaw(name, Message(Enumerable.Range(0, 65).Select(i => $"f{i}.pdf").ToArray()));

        // A path longer than allowed.
        SendRaw(name, Message(new string('x', 32_769)));

        // Truncated message: announces two paths, the second one is cut off.
        SendRaw(name, Message("one.pdf", "two.pdf")[..^3]);

        // Nothing but a negative count, and a connection that sends nothing at all.
        SendRaw(name, [0xFF, 0xFF, 0xFF, 0xFF]);
        SendRaw(name, []);

        // Invalid UTF-8 inside a path.
        SendRaw(name, [1, 0, 0, 0, 2, 0xC3, 0x28]);

        // Valid message followed by trailing bytes.
        SendRaw(name, [.. Message("trailing.pdf"), 1, 2, 3]);

        Assert.True(SingleInstance.TrySend(name, ["good.pdf"], Due));

        Assert.Equal(["good.pdf"], await NextAsync(received));
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task PathAtTheLengthLimitIsAccepted()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);
        var longPath = new string('\u00e4', 32_768);

        Assert.True(SingleInstance.TrySend(name, [longPath], Due));

        Assert.Equal([longPath], await NextAsync(received));
    }

    [Fact]
    public async Task ThrowingCallbackDoesNotStopTheListener()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var failures = Channel.CreateUnbounded<Exception>();
        primary.CallbackFailed = failure => failures.Writer.TryWrite(failure);
        var received = Channel.CreateUnbounded<IReadOnlyList<string>>();
        var first = 1;
        primary.StartListening(paths =>
        {
            received.Writer.TryWrite(paths);
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                throw new InvalidOperationException("Callback failure.");
            }
        });

        Assert.True(SingleInstance.TrySend(name, ["one.pdf"], Due));
        Assert.Equal(["one.pdf"], await NextAsync(received));
        var failure = await failures.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Due, TestContext.Current.CancellationToken);
        Assert.Equal("Callback failure.", failure.Message);
        Assert.True(SingleInstance.TrySend(name, ["two.pdf"], Due));

        Assert.Equal(["two.pdf"], await NextAsync(received));
    }

    [Fact]
    public async Task SendWithABlockedPrimaryReturnsWithinTheTimeout()
    {
        var name = UniqueName();
        using var release = new ManualResetEventSlim();
        using var primary = Primary(name);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(_ =>
        {
            entered.TrySetResult();
            release.Wait();
        });

        try
        {
            Assert.True(SingleInstance.TrySend(name, ["blocks.pdf"], Due));
            await entered.Task.WaitAsync(Due, TestContext.Current.CancellationToken);
            var timeout = TimeSpan.FromMilliseconds(500);

            // The primary no longer reads. Whatever fits into the pipe may be accepted, the rest must not hang.
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                SingleInstance.TrySend(name, [$"queued-{i}.pdf"], timeout);
                Assert.True(watch.Elapsed < timeout + TimeSpan.FromSeconds(2), $"Send {i} took {watch.Elapsed}.");
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void SendToAPrimaryThatNeverReadsFailsAtTheTimeout()
    {
        var name = UniqueName();
        using var server = new NamedPipeServerStream(
            SingleInstance.PipeNameFor(name),
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _ = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        // About 4 MB: far more than a pipe buffers, so the write blocks until the deadline.
        var paths = Enumerable.Repeat(new string('ä', 32_768), 64).ToArray();
        var timeout = TimeSpan.FromSeconds(1);
        var watch = Stopwatch.StartNew();

        var sent = SingleInstance.TrySend(name, paths, timeout);

        watch.Stop();
        Assert.False(sent);
        Assert.InRange(watch.Elapsed, timeout - TimeSpan.FromMilliseconds(100), timeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void InvalidNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SingleInstance.TryBecomePrimary(""));
        Assert.Throws<ArgumentException>(() => SingleInstance.TryBecomePrimary("   "));
        Assert.Throws<ArgumentException>(() => SingleInstance.TryBecomePrimary("anonymous"));
        Assert.Throws<ArgumentException>(() => SingleInstance.TryBecomePrimary("Anonymous"));
        Assert.Throws<ArgumentException>(() => SingleInstance.TrySend("", [], TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => SingleInstance.TrySend("anonymous", [], TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => SingleInstance.TryBecomePrimary(null!));
    }

    [Fact]
    public void UnusableLockFileDegradesToAnUncoordinatedInstance()
    {
        var name = UniqueName();
        var path = SingleInstance.LockPathFor(name);

        // A directory in the place of the lock file cannot be opened as a file (access denied).
        Directory.CreateDirectory(path);
        try
        {
            using var instance = SingleInstance.TryBecomePrimary(name);

            Assert.NotNull(instance);
            Assert.False(instance.IsCoordinated);
            instance.StartListening(_ => { });
            Assert.False(SingleInstance.TrySend(name, ["x.pdf"], TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public void RegularLockFileIsCoordinated()
    {
        using var instance = Primary(UniqueName());

        Assert.True(instance.IsCoordinated);
    }

    [Fact]
    public void RuntimeDirectoryIsOnlyUsedWhenItIsAnExistingAbsoluteDirectory()
    {
        var existing = Path.GetTempPath();

        Assert.Equal(existing, SingleInstance.RuntimeDirectoryFrom(existing));
        Assert.Null(SingleInstance.RuntimeDirectoryFrom(null));
        Assert.Null(SingleInstance.RuntimeDirectoryFrom(""));
        Assert.Null(SingleInstance.RuntimeDirectoryFrom("relative/dir"));
        Assert.Null(SingleInstance.RuntimeDirectoryFrom(Path.Combine(existing, Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task NoCallbacksAfterDispose()
    {
        var name = UniqueName();
        var primary = Primary(name);
        var received = Listen(primary);
        Assert.True(SingleInstance.TrySend(name, ["before.pdf"], Due));
        await NextAsync(received);

        primary.Dispose();

        Assert.False(SingleInstance.TrySend(name, ["after.pdf"], TimeSpan.FromMilliseconds(200)));
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task DisposeFromInsideTheCallbackDoesNotDeadlock()
    {
        var name = UniqueName();
        var primary = Primary(name);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(_ =>
        {
            primary.Dispose();
            done.TrySetResult();
        });

        Assert.True(SingleInstance.TrySend(name, ["x.pdf"], Due));

        await done.Task.WaitAsync(Due, TestContext.Current.CancellationToken);
        using var again = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void DisposeTwiceAndWithoutListeningIsFine()
    {
        var primary = Primary(UniqueName());

        primary.Dispose();
        primary.Dispose();
    }

    [Fact]
    public void StartListeningTwiceIsRejected()
    {
        using var primary = Primary(UniqueName());
        primary.StartListening(_ => { });

        Assert.Throws<InvalidOperationException>(() => primary.StartListening(_ => { }));
    }
}
