using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed partial class NativeBindingsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Maps every constant of <see cref="FpdfConstants"/> to its <c>#define</c> in the PDFium headers.</summary>
    private static readonly Dictionary<string, string> HeaderNames = new()
    {
        [nameof(FpdfConstants.RenderAnnot)] = "FPDF_ANNOT",
        [nameof(FpdfConstants.RenderConvertFillToStroke)] = "FPDF_CONVERT_FILL_TO_STROKE",
        [nameof(FpdfConstants.RenderPrinting)] = "FPDF_PRINTING",
        [nameof(FpdfConstants.SaveNoIncremental)] = "FPDF_NO_INCREMENTAL",
        [nameof(FpdfConstants.SaveRemoveSecurity)] = "FPDF_REMOVE_SECURITY",
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
        [nameof(FpdfConstants.ActionGoTo)] = "PDFACTION_GOTO",
        [nameof(FpdfConstants.ActionUri)] = "PDFACTION_URI",
        [nameof(FpdfConstants.SaveIncremental)] = "FPDF_INCREMENTAL",
        [nameof(FpdfConstants.AnnotText)] = "FPDF_ANNOT_TEXT",
        [nameof(FpdfConstants.AnnotLink)] = "FPDF_ANNOT_LINK",
        [nameof(FpdfConstants.AnnotFreeText)] = "FPDF_ANNOT_FREETEXT",
        [nameof(FpdfConstants.AnnotSquare)] = "FPDF_ANNOT_SQUARE",
        [nameof(FpdfConstants.AnnotCircle)] = "FPDF_ANNOT_CIRCLE",
        [nameof(FpdfConstants.AnnotHighlight)] = "FPDF_ANNOT_HIGHLIGHT",
        [nameof(FpdfConstants.AnnotUnderline)] = "FPDF_ANNOT_UNDERLINE",
        [nameof(FpdfConstants.AnnotSquiggly)] = "FPDF_ANNOT_SQUIGGLY",
        [nameof(FpdfConstants.AnnotStrikeout)] = "FPDF_ANNOT_STRIKEOUT",
        [nameof(FpdfConstants.AnnotInk)] = "FPDF_ANNOT_INK",
        [nameof(FpdfConstants.AnnotPopup)] = "FPDF_ANNOT_POPUP",
        [nameof(FpdfConstants.AnnotWidget)] = "FPDF_ANNOT_WIDGET",
        [nameof(FpdfConstants.AnnotFlagHidden)] = "FPDF_ANNOT_FLAG_HIDDEN",
        [nameof(FpdfConstants.AnnotFlagPrint)] = "FPDF_ANNOT_FLAG_PRINT",
        [nameof(FpdfConstants.AnnotFlagNoView)] = "FPDF_ANNOT_FLAG_NOVIEW",
        [nameof(FpdfConstants.AnnotAppearanceNormal)] = "FPDF_ANNOT_APPEARANCEMODE_NORMAL",
    };

    /// <summary>
    /// Constants without a header define: font descriptor flags returned by <c>FPDFText_GetFontInfo</c>, whose
    /// header comment refers to the PDF specification (ISO 32000-1, table 123; flag bit n has the value 1 &lt;&lt; (n - 1)).
    /// </summary>
    private static readonly Dictionary<string, long> SpecificationValues = new()
    {
        [nameof(FpdfConstants.FontFlagForceBold)] = 1L << (19 - 1),
        [nameof(FpdfConstants.FontFlagItalic)] = 1L << (7 - 1),
        [nameof(FpdfConstants.PermissionPrint)] = 1L << (3 - 1),
        [nameof(FpdfConstants.PermissionAnnotate)] = 1L << (6 - 1),
    };

    [Fact]
    public async Task NativeLibraryLoadsAndInitializes()
    {
        // The value of FPDF_GetLastError is not asserted: without a directly preceding failing call it is
        // unspecified and can be stale (for example 4 after an earlier failed open in the same process on Linux).
        // The call only has to resolve and return; a functional probe proves that the library is initialized.
        var pageCount = await PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                _ = Fpdf.FPDF_GetLastError();
                using var document = PdfiumDocument.Open(PdfiumThread.Shared, TestData.Pdf("text-basic.pdf"), null);
                return document.PageCount;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, pageCount);
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
        foreach (var header in new[] { "fpdfview.h", "fpdf_progressive.h", "fpdf_doc.h", "fpdf_save.h", "fpdf_annot.h" })
        {
            foreach (Match match in DefinePattern().Matches(File.ReadAllText(Path.Combine(headerDirectory, header))))
            {
                var text = match.Groups["value"].Value;
                defines[match.Groups["name"].Value] = match.Groups["shift"].Success
                    ? 1L << int.Parse(match.Groups["shift"].Value, CultureInfo.InvariantCulture)
                    : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
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
            if (SpecificationValues.TryGetValue(constant.Name, out var specified))
            {
                Assert.Equal(specified, Convert.ToInt64(constant.GetRawConstantValue(), CultureInfo.InvariantCulture));
                continue;
            }

            Assert.True(HeaderNames.TryGetValue(constant.Name, out var define), $"{constant.Name} has no header mapping.");
            Assert.True(defines.TryGetValue(define, out var expected), $"#define {define} not found in the headers.");
            Assert.Equal(expected, Convert.ToInt64(constant.GetRawConstantValue(), CultureInfo.InvariantCulture));
        }

        // The specification values only hold while FPDFText_GetFontInfo still reports the font descriptor flags.
        Assert.Contains("Font Descriptor Flags", File.ReadAllText(Path.Combine(headerDirectory, "fpdf_text.h")), StringComparison.Ordinal);
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

    [GeneratedRegex(@"^#define\s+(?<name>\w+)\s+(?:\(1 << (?<shift>\d+)\)|(?<value>0x[0-9A-Fa-f]+|\d+)\b)", RegexOptions.Multiline)]
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
