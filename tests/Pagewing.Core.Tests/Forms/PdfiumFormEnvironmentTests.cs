using System.Reflection;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// The lifetime of PDFium's form-fill environment at the document handle (milestone 11, task 4): created in the open job
/// only for documents with a form, announced to and from every page, exited after the last page and before the document.
/// </summary>
public sealed class PdfiumFormEnvironmentTests : IDisposable
{
    /// <summary>How often each order of closing runs; a crash in a wrong order is rare, so one run proves little.</summary>
    private const int Rounds = 50;

    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfiumThread Pdfium => PdfiumThread.Shared;

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData(FormsBasic, FpdfConstants.FormTypeAcroForm)]
    [InlineData(FormsCalc, FpdfConstants.FormTypeAcroForm)]
    [InlineData(FormsSigned, FpdfConstants.FormTypeAcroForm)]
    [InlineData(FormsRotated, FpdfConstants.FormTypeAcroForm)]
    [InlineData(FormsHostile, FpdfConstants.FormTypeAcroForm)]
    [InlineData(FormsXfa, FpdfConstants.FormTypeXfaForeground)]
    public async Task DocumentWithAFormGetsAnEnvironmentAtOpen(string name, int formType)
    {
        var (type, handle, created, loadedPages) = await Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(name), null);
                var environment = document.FormEnvironment;
                Assert.NotNull(environment);
                return (document.FormType, environment.Handle, document.FormEnvironmentsCreated, environment.LoadedPages);
            },
            cancellationToken: Ct);

        Assert.Equal(formType, type);
        Assert.NotEqual(0, handle);
        Assert.Equal(1, created);
        Assert.Equal(0, loadedPages);
    }

    [Fact]
    public async Task DocumentOpenedThroughTheEngineGetsOneEnvironment()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);

        // Every page load announces the page; rendering and reading must work with the environment in place.
        var image = await document.RenderPageAsync(0, 1.0, cancellationToken: Ct);
        var widgets = await document.GetAnnotationsAsync(0, Ct);

        Assert.Equal(1, document.FormEnvironmentsCreated);
        Assert.True(image.Width > 0);
        Assert.Equal(17, widgets.Count);
    }

    [Fact]
    public async Task DocumentWithoutAFormGetsNone()
    {
        var (type, hasEnvironment, created) = await Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf("text-basic.pdf"), null);
                using (document.LoadPage(0))
                {
                    return (document.FormType, document.FormEnvironment is not null, document.FormEnvironmentsCreated);
                }
            },
            cancellationToken: Ct);

        Assert.Equal(FpdfConstants.FormTypeNone, type);
        Assert.False(hasEnvironment);
        Assert.Equal(0, created);

        // The same through the engine, after the work a viewer does: rendering, text, annotations.
        await using var opened = await OpenFixtureAsync("text-basic.pdf");
        _ = await opened.RenderPageAsync(0, 1.0, cancellationToken: Ct);
        _ = await opened.GetPageTextAsync(0, Ct);
        _ = await opened.GetAnnotationsAsync(0, Ct);
        Assert.Equal(0, opened.FormEnvironmentsCreated);
    }

    [Fact]
    public async Task EnvironmentTouchedFromAnotherThreadThrows()
    {
        var (document, environment) = await Pdfium.InvokeAsync(
            () =>
            {
                var opened = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                return (opened, opened.FormEnvironment!);
            },
            cancellationToken: Ct);
        try
        {
            Assert.False(Pdfium.IsCurrent);
            Assert.Throws<PdfiumThreadViolationException>(() => environment.Handle);
            Assert.Throws<PdfiumThreadViolationException>(() => environment.FillInfo);
            Assert.Throws<PdfiumThreadViolationException>(() => environment.PageLoaded(1));
            Assert.Throws<PdfiumThreadViolationException>(() => environment.PageClosing(1));
            Assert.Throws<PdfiumThreadViolationException>(environment.Dispose);
            Assert.Throws<PdfiumThreadViolationException>(() => document.FormEnvironment);
            Assert.Throws<PdfiumThreadViolationException>(() => document.FormType);
            Assert.False(environment.IsDisposed);
        }
        finally
        {
            document.Dispose();
        }

        await Pdfium.InvokeAsync(() => 0, cancellationToken: Ct);
        Assert.True(environment.IsDisposed);
    }

    /// <summary>
    /// Both orders of closing, many times. That nothing crashes is no proof (a wrong order did not crash in the spike
    /// either); the guarantee is the order at the handle, which the counters show: every page leaves the environment
    /// before it closes, and the environment is exited only after the last page.
    /// </summary>
    [Fact]
    public async Task ClosingPagesAndDocumentInAnyOrderDoesNotCrash()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var pagesFirst = await Pdfium.InvokeAsync(
                () =>
                {
                    var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                    var environment = document.FormEnvironment!;
                    var first = document.LoadPage(0);
                    var second = document.LoadPage(1);
                    var loaded = environment.LoadedPages;
                    first.Dispose();
                    var afterFirst = environment.LoadedPages;
                    second.Dispose();
                    var keptWithoutPages = !environment.IsDisposed;
                    document.Dispose();
                    return (loaded, afterFirst, keptWithoutPages, environment.LoadedPages, environment.IsDisposed);
                },
                cancellationToken: Ct);
            Assert.Equal((2, 1, true, 0, true), pagesFirst);

            var documentFirst = await Pdfium.InvokeAsync(
                () =>
                {
                    var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                    var environment = document.FormEnvironment!;
                    var first = document.LoadPage(0);
                    var second = document.LoadPage(1);
                    document.Dispose();
                    var keptWithTwoPages = !environment.IsDisposed;
                    second.Dispose();
                    var keptWithOnePage = !environment.IsDisposed;
                    first.Dispose();
                    return (keptWithTwoPages, keptWithOnePage, environment.LoadedPages, environment.IsDisposed);
                },
                cancellationToken: Ct);
            Assert.Equal((true, true, 0, true), documentFirst);

            // The app's way: a render in flight while the document is disposed on another thread.
            var opened = await OpenFixtureAsync(FormsBasic);
            var render = opened.RenderPageAsync(0, 1.0, cancellationToken: Ct);
            opened.Dispose();
            try
            {
                _ = await render;
            }
            catch (ObjectDisposedException)
            {
                // The render had not loaded its page yet; either way nothing may crash.
            }
        }

        await Pdfium.InvokeAsync(() => 0, cancellationToken: Ct);
    }

    [Fact]
    public async Task DisposeFromAnotherThreadIsQueued()
    {
        var (document, page, environment) = await Pdfium.InvokeAsync(
            () =>
            {
                var opened = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                return (opened, opened.LoadPage(0), opened.FormEnvironment!);
            },
            cancellationToken: Ct);

        Assert.False(Pdfium.IsCurrent);
        document.Dispose();
        page.Dispose();

        // The closes were posted to the PDFium thread; the next job runs after them.
        await Pdfium.InvokeAsync(() => 0, cancellationToken: Ct);
        Assert.True(environment.IsDisposed);
        Assert.Equal(0, environment.LoadedPages);
    }

    /// <summary>
    /// Spike (Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 6): no callback is needed, so every pointer of the
    /// structure stays null and there is no new <c>[UnmanagedCallersOnly]</c> method (UnmanagedCallbackTests unchanged).
    /// Null pointers also keep URI, named, go-to and submit actions from ever running.
    /// </summary>
    [Fact]
    public async Task NoPointerOfTheFillInfoIsSet()
    {
        var info = await Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                return document.FormEnvironment!.FillInfo;
            },
            cancellationToken: Ct);

        Assert.Equal(1, info.Version);
        var fields = typeof(FpdfFormFillInfo).GetFields(BindingFlags.Public | BindingFlags.Instance);
        Assert.True(fields.Length > 30);
        foreach (var field in fields.Where(f => f.Name != nameof(FpdfFormFillInfo.Version)))
        {
            var value = field.GetValue(info);
            Assert.True(value is int and 0 || (value is nint pointer && pointer == 0), $"{field.Name} is set.");
        }
    }

    [Fact]
    public async Task ReopenedDocumentGetsItsOwnEnvironment()
    {
        var path = Path.Combine(folder.Path, FormsBasic);
        File.Copy(TestData.Pdf(FormsBasic), path);
        var engine = new PdfEngine();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var stamp = FileStamp.Of(path);
        await document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(20, 20, 120, 40)], Yellow, "Saved", "Ada", Ct);
        var count = (await document.GetAnnotationsAsync(0, Ct)).Count;
        var request = new SaveRequest(path, path, stamp, null, document.PageCount, new Dictionary<int, int> { [0] = count }, OverwriteChangedOriginal: false);

        // The same path as the app: the saver reopens the written file through the engine, the tab adopts that document.
        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Equal(1, result.Reopened.FormEnvironmentsCreated);
            Assert.Equal(1, document.FormEnvironmentsCreated);
            var image = await result.Reopened.RenderPageAsync(1, 1.0, cancellationToken: Ct);
            Assert.True(image.Width > 0);
            Assert.Equal(count, (await result.Reopened.GetAnnotationsAsync(0, Ct)).Count);
        }
    }
}
