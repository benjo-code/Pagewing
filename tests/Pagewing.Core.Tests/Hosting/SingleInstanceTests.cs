using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
    [SupportedOSPlatform("windows")]
    public void ThePipeIsOpenToTheCurrentUserOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The access list of a named pipe exists on Windows only.");
        var name = UniqueName();
        using var primary = Primary(name);
        Listen(primary);

        using var client = new NamedPipeClientStream(
            ".", SingleInstance.PipeNameFor(name), PipeAccessRights.Write | PipeAccessRights.ReadPermissions, PipeOptions.CurrentUserOnly, System.Security.Principal.TokenImpersonationLevel.None, HandleInheritability.None);
        client.Connect((int)Due.TotalMilliseconds);
        var rules = client.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        // Allow entries for the account that runs the program only. .NET's CurrentUserOnly names the OWNER of the token: for
        // the user it is the user, for an elevated administrator it is the group Administrators (found by the first hosted CI
        // run, which runs elevated). That is still closed to every user who is not an administrator.
        var identity = WindowsIdentity.GetCurrent();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        Assert.All(rules, rule => Assert.True(
            IsTheAccountOfTheProgram((SecurityIdentifier)rule.IdentityReference, identity.User!, identity.Owner),
            $"{rule.IdentityReference.Value} may use the pipe; the user is {identity.User!.Value}, the token owner {identity.Owner?.Value}."));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnlyTheUserOrTheOwnerOfTheTokenIsTheAccountOfTheProgram()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Security identifiers of Windows.");
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        Assert.True(IsTheAccountOfTheProgram(user, user, owner: null));
        Assert.True(IsTheAccountOfTheProgram(user, user, administrators));
        Assert.True(IsTheAccountOfTheProgram(administrators, user, administrators));

        // The elevated case without the elevated token: Administrators are not the account unless they own the token.
        Assert.False(IsTheAccountOfTheProgram(administrators, user, owner: null));
        Assert.False(IsTheAccountOfTheProgram(administrators, user, user));
        foreach (var wellKnown in new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.InteractiveSid })
        {
            Assert.False(IsTheAccountOfTheProgram(new SecurityIdentifier(wellKnown, null), user, owner: null));
            Assert.False(IsTheAccountOfTheProgram(new SecurityIdentifier(wellKnown, null), user, user));
        }
    }

    // The user, or the owner of the token (the Administrators group when elevated); never Everyone, Users, Authenticated Users.
    [SupportedOSPlatform("windows")]
    private static bool IsTheAccountOfTheProgram(SecurityIdentifier rule, SecurityIdentifier user, SecurityIdentifier? owner)
    {
        SecurityIdentifier[] broad =
        [
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
        ];
        return !broad.Contains(rule) && (rule == user || (owner is not null && rule == owner));
    }

    [Fact]
    public void TheSocketPathIsTheRootedNameOrTheFileOfTheTempFolder()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "run", "pagewing-x.pipe");

        Assert.Equal(rooted, SingleInstance.SocketPathFor(rooted));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "CoreFxPipe_pagewing-x"), SingleInstance.SocketPathFor("pagewing-x"));
    }

    [Fact]
    public void RestrictingASocketThatIsNotThereOrNotAFileNeverThrows()
    {
        SingleInstance.RestrictSocket(Path.Combine(Path.GetTempPath(), "pagewing-missing-" + Guid.NewGuid().ToString("N")));
        SingleInstance.RestrictSocket("pagewing-missing-" + Guid.NewGuid().ToString("N"));
        SingleInstance.RestrictSocket("bad name");

        // A folder at that path is left alone (never the temp folder itself: only what the test made).
        var folder = Directory.CreateTempSubdirectory("pagewing-socket-");
        try
        {
            var before = OperatingSystem.IsWindows() ? (UnixFileMode?)null : ModeOf(folder.FullName);

            SingleInstance.RestrictSocket(folder.FullName);

            Assert.True(folder.Exists);
            Assert.Equal(before, OperatingSystem.IsWindows() ? null : ModeOf(folder.FullName));
        }
        finally
        {
            folder.Delete(recursive: true);
        }

        [UnsupportedOSPlatform("windows")]
        static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void RestrictingASocketLeavesReadAndWriteForTheOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only.");
        var file = Path.Combine(Path.GetTempPath(), "pagewing-socket-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, string.Empty);
        try
        {
            File.SetUnixFileMode(file, (UnixFileMode)0b111_101_101);

            SingleInstance.RestrictSocket(file);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void TheSocketIsOpenToTheCurrentUserOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets exist on Linux only.");
        var name = UniqueName();
        using var primary = Primary(name);
        Listen(primary);
        var pipe = SingleInstance.PipeNameFor(name);

        // With $XDG_RUNTIME_DIR the pipe name is the socket path; without it .NET puts the socket into the temp folder.
        var socket = Path.IsPathRooted(pipe) ? pipe : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipe);

        // The listener binds on a pool thread after Listen returned, and .NET restricts the mode after the bind: wait for both.
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        var until = Environment.TickCount64 + (long)Due.TotalMilliseconds;
        var mode = (UnixFileMode?)null;
        while (Environment.TickCount64 < until)
        {
            try
            {
                mode = File.Exists(socket) ? File.GetUnixFileMode(socket) : null;
            }
            catch (IOException)
            {
                mode = null;
            }

            if (mode is { } found && (found & Others) == UnixFileMode.None)
            {
                break;
            }

            Thread.Sleep(20);
        }

        if (mode is null)
        {
            // The listing is built only on failure, so a problem while listing can never fail a passing run.
            var directory = Path.GetDirectoryName(socket)!;
            var entries = Directory.Exists(directory)
                ? string.Join(", ", Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Take(30))
                : "(the folder does not exist)";
            Assert.Fail($"No socket at {socket} after {Due.TotalSeconds:0} s. Folder {directory} holds: {entries}. XDG_RUNTIME_DIR={Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")}.");
        }

        Assert.True((mode!.Value & Others) == UnixFileMode.None, $"The socket {socket} has the mode {mode}; group and others must have no access.");
    }

    [Fact]
    public async Task AMessageOverTheSizeLimitIsDroppedWithoutReadingItAll()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);

        // A valid message for one path followed by far more bytes than any message may have: refused as a whole.
        var big = new byte[Message("a.pdf").Length + (8 * 1024 * 1024)];
        Message("a.pdf").CopyTo(big, 0);
        try
        {
            SendRaw(name, big);
        }
        catch (IOException)
        {
            // The listener stops reading at its limit and closes the pipe; the sender may notice.
        }

        Assert.True(SingleInstance.TrySend(name, ["good.pdf"], Due));
        Assert.Equal(["good.pdf"], await NextAsync(received));
        await AssertNothingAsync(received);
    }

    [Fact]
    public async Task AClientThatNeverFinishesIsDroppedAfterTheReadTimeout()
    {
        var name = UniqueName();
        using var primary = Primary(name);
        var received = Listen(primary);

        // Connects, sends half a message and stays: the next sender must still be served, within a few seconds.
        using var stalled = new NamedPipeClientStream(".", SingleInstance.PipeNameFor(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
        stalled.Connect((int)Due.TotalMilliseconds);
        var half = Message("one.pdf", "two.pdf");
        stalled.Write(half.AsSpan(0, half.Length - 3));
        stalled.Flush();

        var watch = Stopwatch.StartNew();
        Assert.True(SingleInstance.TrySend(name, ["good.pdf"], Due));
        Assert.Equal(["good.pdf"], await NextAsync(received));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), watch.Elapsed.ToString());
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
