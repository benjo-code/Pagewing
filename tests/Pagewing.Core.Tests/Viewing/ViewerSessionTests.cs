using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class ViewerSessionTests
{
    /// <summary>User password of the encrypted PDFium test files (same value as in <c>OpenTests</c>).</summary>
    private const string UserPassword = "hôtel";

    // How long a test waits for something that must happen.
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    // How long a test waits to show that something does not happen.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task OpenReachesReadyAndLoadsAllPageSizes()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));

        var ready = recorder.Events.First(e => e.State == ViewerState.Ready);
        Assert.True(ready.Sizes >= 32, $"Ready came with only {ready.Sizes} page sizes.");
        Assert.Equal(ViewerState.Loading, recorder.Events[0].State);
        Assert.NotNull(session.Document);
        Assert.NotNull(session.Cache);
        Assert.NotNull(session.Scheduler);
        Assert.Equal(Path.GetFullPath(TestData.Pdf("thousand-pages.pdf")), session.Path);
        Assert.Null(session.Error);

        await recorder.WaitUntilAsync(() => session.LoadedSizes.Count == 1000);
        Assert.All(session.LoadedSizes, size => Assert.Equal(new PdfPageSize(200, 100), size));

        // Every Changed after Ready shows a growing prefix, never a shrinking or reset one.
        var sizes = recorder.Events.Select(e => e.Sizes).ToArray();
        Assert.Equal(sizes.Order(), sizes);
    }

    [Fact]
    public async Task EncryptedFileAsksForAPasswordThenAcceptsIt()
    {
        var path = TestData.Pdf("encrypted_hello_world_r3.pdf");
        using var session = new ViewerSession(engine);

        await session.OpenAsync(path);
        Assert.Equal(ViewerState.PasswordRequired, session.State);
        Assert.Equal(PdfOpenError.PasswordRequired, session.Error);
        Assert.Equal(Path.GetFullPath(path), session.Path);
        Assert.Null(session.Document);

        await session.OpenAsync(session.Path!, "wrong");
        Assert.Equal(ViewerState.PasswordIncorrect, session.State);
        Assert.Equal(PdfOpenError.PasswordIncorrect, session.Error);
        Assert.Equal(Path.GetFullPath(path), session.Path);
        Assert.Null(session.Document);

        await session.OpenAsync(session.Path!, UserPassword);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.Equal(1, session.Document!.PageCount);
        Assert.Equal(new PdfPageSize(200, 200), session.LoadedSizes.Single());
    }

    [Fact]
    public async Task MissingFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("does-not-exist.pdf"));

        AssertFailed(session, PdfOpenError.FileNotFound);
    }

    [Fact]
    public async Task NotAPdfFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
    }

    [Fact]
    public async Task EmptyFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("empty.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
    }

    [Fact]
    public async Task DirectoryInsteadOfFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(Path.Combine(TestData.Root, "testdata", "pdfs"));

        AssertFailed(session, PdfOpenError.CannotRead);
    }

    [Fact]
    public async Task InvalidPathCharactersMapToCannotRead()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        await session.OpenAsync("bad\0name.pdf");

        AssertFailed(session, PdfOpenError.CannotRead);
        Assert.Equal([ViewerState.Loading, ViewerState.Failed], recorder.Events.Select(e => e.State));
    }

    [Fact]
    public async Task TooLongPathMapsToCannotRead()
    {
        using var session = new ViewerSession(engine);

        // Longer than any supported path; Path.GetFullPath throws PathTooLongException (Windows) or the open fails.
        await session.OpenAsync(Path.Combine(TestData.Root, new string('a', 70_000), "x.pdf"));

        Assert.Equal(ViewerState.Failed, session.State);
        Assert.NotNull(session.Error);
        Assert.Null(session.Document);
    }

    [Fact]
    public async Task EmptyPathIsAnArgumentError()
    {
        using var session = new ViewerSession(engine);

        await Assert.ThrowsAsync<ArgumentException>(() => session.OpenAsync(string.Empty));
        Assert.Equal(ViewerState.Empty, session.State);
    }

    [Fact]
    public async Task PathWithUnicodeAndSpacesOpens()
    {
        var directory = Directory.CreateTempSubdirectory("pagewing-tests-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory.FullName, "Ünï cödé"));
            var copy = Path.Combine(folder.FullName, "hello world ß.pdf");
            File.Copy(TestData.Pdf("hello_world.pdf"), copy);
            using var session = new ViewerSession(engine);

            await session.OpenAsync(copy);

            Assert.Equal(ViewerState.Ready, session.State);
            Assert.Equal(copy, session.Path);
            Assert.Equal(1, session.Document!.PageCount);

            // The file is closed again, so the folder can be removed.
            session.Close();
            await WaitForAsync(() => TryDelete(directory));
        }
        finally
        {
            if (directory.Exists)
            {
                TryDelete(directory);
            }
        }
    }

    [Fact]
    public async Task LatestOpenWins()
    {
        var first = TestData.Pdf("thousand-pages.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        var firstOpen = session.OpenAsync(first);
        var secondOpen = session.OpenAsync(second);
        await firstOpen;
        await secondOpen;

        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Equal(2, session.Document!.PageCount);

        // Nothing of the first file leaks in afterwards, not even from its background batches.
        var before = recorder.Count;
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.Equal(before, recorder.Count);
        Assert.Equal(2, session.LoadedSizes.Count);
        var lastSecondReady = recorder.Events.FindLastIndex(e => e.State == ViewerState.Ready);
        Assert.DoesNotContain(recorder.Events.Skip(lastSecondReady), e => e.Path == Path.GetFullPath(first));
    }

    [Fact]
    public async Task SupersededRequestThatFinishesLastChangesNothing()
    {
        var first = TestData.Pdf("hello_world.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(first);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var firstOpen = session.OpenAsync(first);
        await session.OpenAsync(second);
        await recorder.WaitUntilAsync(() => session.LoadedSizes.Count == 2);
        var events = recorder.Count;
        var currentDocument = session.Document;

        // The older open only now completes with a document. It must be closed, and nothing may change.
        gate.SetResult();
        await firstOpen;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Same(currentDocument, session.Document);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(first).PageCount);
        Assert.Equal(2, currentDocument!.PageCount);
    }

    [Fact]
    public async Task SupersededFailureIsSilent()
    {
        var missing = TestData.Pdf("does-not-exist.pdf");
        var good = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(missing);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var failingOpen = session.OpenAsync(missing);
        await session.OpenAsync(good);
        var events = recorder.Count;

        gate.SetResult();
        await failingOpen;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task PreviousDocumentStaysOpenUntilTheNewOneIsAccepted()
    {
        var first = TestData.Pdf("hello_world.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(second);
        using var session = new ViewerSession(gates.Open);
        await session.OpenAsync(first);
        var firstDocument = session.Document!;
        var firstScheduler = session.Scheduler;

        var secondOpen = session.OpenAsync(second);

        Assert.Equal(ViewerState.Loading, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Same(firstDocument, session.Document);
        Assert.Equal(1, firstDocument.PageCount);

        gate.SetResult();
        await secondOpen;

        Assert.Equal(ViewerState.Ready, session.State);
        Assert.NotSame(firstDocument, session.Document);
        Assert.NotSame(firstScheduler, session.Scheduler);
        Assert.Throws<ObjectDisposedException>(() => firstDocument.PageCount);
    }

    [Fact]
    public async Task FailedOpenKeepsNothingOfTheOldDocument()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var oldDocument = session.Document!;
        Assert.Equal(ViewerState.Ready, session.State);

        await session.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
        Assert.Empty(session.LoadedSizes);
        Assert.Throws<ObjectDisposedException>(() => oldDocument.PageCount);
    }

    [Fact]
    public async Task PasswordPromptKeepsNothingOfTheOldDocument()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var oldDocument = session.Document!;

        await session.OpenAsync(TestData.Pdf("encrypted_hello_world_r6.pdf"));

        Assert.Equal(ViewerState.PasswordRequired, session.State);
        Assert.Null(session.Document);
        Assert.Null(session.Scheduler);
        Assert.Null(session.Cache);
        Assert.Throws<ObjectDisposedException>(() => oldDocument.PageCount);
    }

    [Fact]
    public async Task CloseReturnsToEmptyAndReleasesTheDocument()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var document = session.Document!;

        session.Close();

        Assert.Equal(ViewerState.Empty, session.State);
        Assert.Null(session.Path);
        Assert.Null(session.Error);
        Assert.Null(session.Document);
        Assert.Null(session.Cache);
        Assert.Null(session.Scheduler);
        Assert.Empty(session.LoadedSizes);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);
        Assert.Equal(ViewerState.Empty, recorder.Events[^1].State);

        // Closing an empty session is not a change.
        var events = recorder.Count;
        session.Close();
        Assert.Equal(events, recorder.Count);
    }

    [Fact]
    public async Task CloseCancelsAnOpenThatIsStillRunning()
    {
        var path = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(path);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var open = session.OpenAsync(path);
        session.Close();
        var events = recorder.Count;
        gate.SetResult();
        await open;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Empty, session.State);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(path).PageCount);
    }

    [Fact]
    public async Task NoChangedEventsAfterDispose()
    {
        var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);
        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        var document = session.Document!;

        // The remaining page sizes are probably still being loaded.
        session.Dispose();
        var events = recorder.Count;
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        Assert.Equal(events, recorder.Count);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);

        // A disposed session ignores further requests and can be disposed again.
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        session.Close();
        session.Dispose();
        Assert.Equal(events, recorder.Count);
        Assert.Null(session.Document);
    }

    [Fact]
    public async Task DisposeDuringAnOpenClosesTheDocumentItGets()
    {
        var path = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(path);
        var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var open = session.OpenAsync(path);
        session.Dispose();
        var events = recorder.Count;
        gate.SetResult();
        await open;

        Assert.Equal(events, recorder.Count);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(path).PageCount);
    }

    [Fact]
    public async Task HandlerMayDisposeTheSession()
    {
        var session = new ViewerSession(engine);
        session.Changed += () =>
        {
            if (session.State == ViewerState.Ready)
            {
                session.Dispose();
            }
        };

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));

        Assert.Null(session.Document);
    }

    private static void AssertFailed(ViewerSession session, PdfOpenError error)
    {
        Assert.Equal(ViewerState.Failed, session.State);
        Assert.Equal(error, session.Error);
        Assert.Null(session.Document);
        Assert.Null(session.Cache);
        Assert.Null(session.Scheduler);
        Assert.Empty(session.LoadedSizes);
        Assert.NotNull(session.Path);
    }

    private static bool TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Due;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private readonly record struct Snapshot(ViewerState State, string? Path, int Sizes);

    /// <summary>Records what a handler of <see cref="ViewerSession.Changed"/> sees, and waits for conditions.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly ViewerSession session;
        private readonly object gate = new();
        private readonly List<Snapshot> events = [];
        private readonly SemaphoreSlim signal = new(0);

        public Recorder(ViewerSession session)
        {
            this.session = session;
            session.Changed += OnChanged;
        }

        public List<Snapshot> Events
        {
            get
            {
                lock (gate)
                {
                    return [.. events];
                }
            }
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return events.Count;
                }
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Due;
            while (!condition())
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(
                    remaining > TimeSpan.Zero && await signal.WaitAsync(remaining, TestContext.Current.CancellationToken),
                    $"The condition did not become true in time (state {session.State}, {session.LoadedSizes.Count} sizes).");
            }
        }

        public void Dispose()
        {
            session.Changed -= OnChanged;
            signal.Dispose();
        }

        private void OnChanged()
        {
            lock (gate)
            {
                events.Add(new Snapshot(session.State, session.Path, session.LoadedSizes.Count));
            }

            try
            {
                signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // The test is over.
            }
        }
    }

    /// <summary>
    /// Wraps the real engine so that a test can hold back the result of one open. The held open ignores the
    /// cancellation token, like an open that has already started on the PDFium thread.
    /// </summary>
    private sealed class GatedOpen(PdfEngine engine)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> gates = [];
        private readonly Dictionary<string, PdfDocument> opened = [];

        public TaskCompletionSource Gate(string path)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                gates[Path.GetFullPath(path)] = source;
            }

            return source;
        }

        public PdfDocument Opened(string path)
        {
            lock (gate)
            {
                return opened[Path.GetFullPath(path)];
            }
        }

        public async Task<PdfDocument> Open(string path, string? password, CancellationToken cancellationToken)
        {
            TaskCompletionSource? held;
            lock (gate)
            {
                gates.TryGetValue(path, out held);
            }

            if (held is not null)
            {
                await held.Task;
            }

            var document = await engine.OpenAsync(path, password, held is null ? cancellationToken : CancellationToken.None);
            lock (gate)
            {
                opened[path] = document;
            }

            return document;
        }
    }
}
