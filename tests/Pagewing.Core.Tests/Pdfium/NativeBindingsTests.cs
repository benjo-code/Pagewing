using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed partial class NativeBindingsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Maps every constant of <see cref="FpdfConstants"/> to its <c>#define</c> in the PDFium headers.</summary>
    private static readonly Dictionary<string, string> HeaderNames = new()
    {
        [nameof(FpdfConstants.RenderAnnot)] = "FPDF_ANNOT",
        [nameof(FpdfConstants.ErrSuccess)] = "FPDF_ERR_SUCCESS",
        [nameof(FpdfConstants.ErrUnknown)] = "FPDF_ERR_UNKNOWN",
        [nameof(FpdfConstants.ErrFile)] = "FPDF_ERR_FILE",
        [nameof(FpdfConstants.ErrFormat)] = "FPDF_ERR_FORMAT",
        [nameof(FpdfConstants.ErrPassword)] = "FPDF_ERR_PASSWORD",
        [nameof(FpdfConstants.ErrSecurity)] = "FPDF_ERR_SECURITY",
        [nameof(FpdfConstants.ErrPage)] = "FPDF_ERR_PAGE",
        [nameof(FpdfConstants.BitmapBgrx)] = "FPDFBitmap_BGRx",
        [nameof(FpdfConstants.RenderReady)] = "FPDF_RENDER_READY",
        [nameof(FpdfConstants.RenderToBeContinued)] = "FPDF_RENDER_TOBECONTINUED",
        [nameof(FpdfConstants.RenderDone)] = "FPDF_RENDER_DONE",
        [nameof(FpdfConstants.RenderFailed)] = "FPDF_RENDER_FAILED",
    };

    [Fact]
    public async Task NativeLibraryLoadsAndInitializes()
    {
        var lastError = await PdfiumThread.Shared.InvokeAsync(
            () => Fpdf.FPDF_GetLastError(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0u, lastError.Value);
    }

    [Fact]
    public void ConstantsMatchThePackageHeaders()
    {
        var headerDirectory = Path.Combine(
            NuGetPackageRoot(),
            "bblanchon.pdfium.win32",
            PackageVersion("bblanchon.PDFium.Win32"),
            "build",
            "native",
            "include",
            "pdfium");
        var defines = new Dictionary<string, long>();
        foreach (var header in new[] { "fpdfview.h", "fpdf_progressive.h" })
        {
            foreach (Match match in DefinePattern().Matches(File.ReadAllText(Path.Combine(headerDirectory, header))))
            {
                var text = match.Groups["value"].Value;
                defines[match.Groups["name"].Value] = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : long.Parse(text, CultureInfo.InvariantCulture);
            }
        }

        var constants = typeof(FpdfConstants)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToList();

        Assert.NotEmpty(constants);
        foreach (var constant in constants)
        {
            Assert.True(HeaderNames.TryGetValue(constant.Name, out var define), $"{constant.Name} has no header mapping.");
            Assert.True(defines.TryGetValue(define, out var expected), $"#define {define} not found in the headers.");
            Assert.Equal(expected, Convert.ToInt64(constant.GetRawConstantValue(), CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void NoV8PackagesAreReferenced()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));

        Assert.Contains("bblanchon.PDFium.Win32", props, StringComparison.Ordinal);
        Assert.DoesNotContain("PDFiumV8", props, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlesNeverCloseOnForeignThreads()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;
        var pdfiumThreadId = await thread.InvokeAsync(() => Environment.CurrentManagedThreadId, cancellationToken: ct);
        var handle = new RecordingHandle(thread, null, new ConcurrentQueue<string>(), "document");
        handle.Attach(1);

        handle.Dispose();

        var closedOn = await handle.Closed.Task.WaitAsync(Timeout, ct);
        Assert.Equal(pdfiumThreadId, closedOn);
        Assert.NotEqual(Environment.CurrentManagedThreadId, closedOn);
    }

    [Fact]
    public async Task HandleReleasedOnThePdfiumThreadClosesImmediately()
    {
        using var thread = new PdfiumThread();
        var handle = new RecordingHandle(thread, null, new ConcurrentQueue<string>(), "document");
        handle.Attach(1);

        var closedSynchronously = await thread.InvokeAsync(
            () =>
            {
                handle.Dispose();
                return handle.Closed.Task.IsCompleted;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(closedSynchronously);
    }

    [Fact]
    public async Task ParentClosesOnlyAfterItsChildren()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;
        var log = new ConcurrentQueue<string>();
        var document = new RecordingHandle(thread, null, log, "document");
        document.Attach(1);
        var page = new RecordingHandle(thread, document, log, "page");
        page.Attach(2);

        document.Dispose();
        await thread.InvokeAsync(() => 0, cancellationToken: ct);
        Assert.Empty(log);

        page.Dispose();
        await document.Closed.Task.WaitAsync(Timeout, ct);
        Assert.Equal(["page", "document"], log);
    }

    [Fact]
    public async Task UnattachedChildReleasesItsParent()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;
        var log = new ConcurrentQueue<string>();
        var document = new RecordingHandle(thread, null, log, "document");
        document.Attach(1);
        var page = new RecordingHandle(thread, document, log, "page");

        page.Dispose();
        document.Dispose();

        await document.Closed.Task.WaitAsync(Timeout, ct);
        Assert.Equal(["document"], log);
        Assert.Equal(1, page.OwnedResourcesReleased);
    }

    private static string NuGetPackageRoot() =>
        typeof(NativeBindingsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NuGetPackageRoot")
            .Value!;

    private static string PackageVersion(string packageId) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == packageId)
            .Attribute("Version")!
            .Value;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (Pagewing.slnx) not found.");
    }

    [GeneratedRegex(@"^#define\s+(?<name>\w+)\s+(?<value>0x[0-9A-Fa-f]+|\d+)\b", RegexOptions.Multiline)]
    private static partial Regex DefinePattern();

    private sealed class RecordingHandle(PdfiumThread thread, PdfiumHandle? parent, ConcurrentQueue<string> log, string name)
        : PdfiumHandle(thread, parent)
    {
        private int ownedResourcesReleased;

        public TaskCompletionSource<int> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OwnedResourcesReleased => Volatile.Read(ref ownedResourcesReleased);

        protected override void CloseNative(nint value)
        {
            log.Enqueue(name);
            Closed.TrySetResult(Environment.CurrentManagedThreadId);
        }

        protected override void ReleaseOwnedResources() => Interlocked.Increment(ref ownedResourcesReleased);
    }
}
