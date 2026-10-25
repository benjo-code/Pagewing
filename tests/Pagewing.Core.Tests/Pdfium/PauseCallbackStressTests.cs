using System.Diagnostics;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// Runs alone: it keeps a second thread forcing collections, which would slow down (and could time out) tests that
/// measure wall-clock time or queue on the shared PDFium thread.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PauseCallbackStressGroup
{
    public const string Name = "PauseCallbackStress";
}

[Collection(PauseCallbackStressGroup.Name)]
public sealed class PauseCallbackStressTests
{
    /// <summary>How long the test renders; long enough for the old callback to crash in every run seen.</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task PauseCallbackSurvivesGarbageCollectionsDuringTheRender()
    {
        // Regression: the pause callback once read the clock inline (Stopwatch.GetTimestamp, a P/Invoke with
        // SuppressGCTransition). The JIT then placed that call's GC poll after the reverse P/Invoke exit, so a garbage
        // collection pending at that moment sent the thread back into PDFium in cooperative mode, and PDFium's next call
        // of the callback within the same render call ended the process with 0xC0000602, "attempted to call a
        // UnmanagedCallersOnly method from managed code" (DECISIONS.md, entry "Absturz „UnmanagedCallersOnly aus
        // verwaltetem Code“ …"). With collections running all the time on another thread the old callback crashed this
        // test process in every run. If the defect returns, the fail-fast ends the whole test host with that message.
        //
        // Only meaningful in Release (the CI runs `dotnet test -c Release`): only optimized code inlines the clock into
        // the callback; in Debug the poll lies in Stopwatch.GetTimestamp itself and the test passes even with the old
        // callback. A race cannot be forced, so the test renders for a fixed time; the collection count shows that
        // collections really overlapped the renders. Each render is its own item on the shared PDFium thread, so other
        // work can run in between.
        var thread = PdfiumThread.Shared;
        var cancellation = TestContext.Current.CancellationToken;
        using var stop = new CancellationTokenSource();
        var collector = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            }
        })
        {
            IsBackground = true,
        };

        var document = await thread.InvokeAsync(
            () => PdfiumDocument.Open(thread, TestData.Pdf("wide-page.pdf"), null),
            cancellationToken: cancellation);
        var renders = 0;
        var collectionsBefore = GC.CollectionCount(0);
        try
        {
            collector.Start();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < Duration)
            {
                var steps = await thread.InvokeAsync(
                    () =>
                    {
                        // Unlimited slice: the whole page in one native call, during which PDFium asks the callback more
                        // than once; the call after the one that returned in cooperative mode was the one that failed.
                        using var work = new PdfiumRenderWork(document, 0, 0.5, null, annotations: true, TimeSpan.MaxValue);
                        while (!work.Step())
                        {
                        }

                        return work.StepCount;
                    },
                    cancellationToken: cancellation);
                Assert.Equal(1, steps);
                renders++;
            }
        }
        finally
        {
            await stop.CancelAsync();
            if (collector.IsAlive)
            {
                collector.Join();
            }

            await thread.InvokeAsync(document.Dispose, cancellationToken: CancellationToken.None);
        }

        Assert.True(renders > 0);
        Assert.True(GC.CollectionCount(0) - collectionsBefore > renders, "Collections did not overlap the renders.");
    }
}
