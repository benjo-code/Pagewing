using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        [nameof(FpdfConstants.AnnotStamp)] = "FPDF_ANNOT_STAMP",
        [nameof(FpdfConstants.AnnotInk)] = "FPDF_ANNOT_INK",
        [nameof(FpdfConstants.BitmapBgra)] = "FPDFBitmap_BGRA",
        [nameof(FpdfConstants.AnnotPopup)] = "FPDF_ANNOT_POPUP",
        [nameof(FpdfConstants.AnnotWidget)] = "FPDF_ANNOT_WIDGET",
        [nameof(FpdfConstants.AnnotFlagHidden)] = "FPDF_ANNOT_FLAG_HIDDEN",
        [nameof(FpdfConstants.AnnotFlagPrint)] = "FPDF_ANNOT_FLAG_PRINT",
        [nameof(FpdfConstants.AnnotFlagNoView)] = "FPDF_ANNOT_FLAG_NOVIEW",
        [nameof(FpdfConstants.AnnotAppearanceNormal)] = "FPDF_ANNOT_APPEARANCEMODE_NORMAL",
        [nameof(FpdfConstants.SegmentLineTo)] = "FPDF_SEGMENT_LINETO",
        [nameof(FpdfConstants.SegmentBezierTo)] = "FPDF_SEGMENT_BEZIERTO",
        [nameof(FpdfConstants.SegmentMoveTo)] = "FPDF_SEGMENT_MOVETO",
        [nameof(FpdfConstants.FontTrueType)] = "FPDF_FONT_TRUETYPE",
        [nameof(FpdfConstants.FontType1)] = "FPDF_FONT_TYPE1",
        [nameof(FpdfConstants.PageObjUnknown)] = "FPDF_PAGEOBJ_UNKNOWN",
        [nameof(FpdfConstants.PageObjText)] = "FPDF_PAGEOBJ_TEXT",
        [nameof(FpdfConstants.PageObjPath)] = "FPDF_PAGEOBJ_PATH",
        [nameof(FpdfConstants.PageObjImage)] = "FPDF_PAGEOBJ_IMAGE",
        [nameof(FpdfConstants.PageObjShading)] = "FPDF_PAGEOBJ_SHADING",
        [nameof(FpdfConstants.PageObjForm)] = "FPDF_PAGEOBJ_FORM",
        [nameof(FpdfConstants.FillModeNone)] = "FPDF_FILLMODE_NONE",
        [nameof(FpdfConstants.FillModeAlternate)] = "FPDF_FILLMODE_ALTERNATE",
        [nameof(FpdfConstants.FillModeWinding)] = "FPDF_FILLMODE_WINDING",
        [nameof(FpdfConstants.ColorSpaceDeviceGray)] = "FPDF_COLORSPACE_DEVICEGRAY",
        [nameof(FpdfConstants.ColorSpaceDeviceRgb)] = "FPDF_COLORSPACE_DEVICERGB",
        [nameof(FpdfConstants.ColorSpaceDeviceCmyk)] = "FPDF_COLORSPACE_DEVICECMYK",
        [nameof(FpdfConstants.ColorSpaceIndexed)] = "FPDF_COLORSPACE_INDEXED",
        [nameof(FpdfConstants.FormTypeNone)] = "FORMTYPE_NONE",
        [nameof(FpdfConstants.FormTypeAcroForm)] = "FORMTYPE_ACRO_FORM",
        [nameof(FpdfConstants.FormTypeXfaFull)] = "FORMTYPE_XFA_FULL",
        [nameof(FpdfConstants.FormTypeXfaForeground)] = "FORMTYPE_XFA_FOREGROUND",
        [nameof(FpdfConstants.FormFieldUnknown)] = "FPDF_FORMFIELD_UNKNOWN",
        [nameof(FpdfConstants.FormFieldPushButton)] = "FPDF_FORMFIELD_PUSHBUTTON",
        [nameof(FpdfConstants.FormFieldCheckBox)] = "FPDF_FORMFIELD_CHECKBOX",
        [nameof(FpdfConstants.FormFieldRadioButton)] = "FPDF_FORMFIELD_RADIOBUTTON",
        [nameof(FpdfConstants.FormFieldComboBox)] = "FPDF_FORMFIELD_COMBOBOX",
        [nameof(FpdfConstants.FormFieldListBox)] = "FPDF_FORMFIELD_LISTBOX",
        [nameof(FpdfConstants.FormFieldTextField)] = "FPDF_FORMFIELD_TEXTFIELD",
        [nameof(FpdfConstants.FormFieldSignature)] = "FPDF_FORMFIELD_SIGNATURE",
        [nameof(FpdfConstants.FormFlagReadOnly)] = "FPDF_FORMFLAG_READONLY",
        [nameof(FpdfConstants.FormFlagRequired)] = "FPDF_FORMFLAG_REQUIRED",
        [nameof(FpdfConstants.FormFlagNoExport)] = "FPDF_FORMFLAG_NOEXPORT",
        [nameof(FpdfConstants.FormFlagTextMultiline)] = "FPDF_FORMFLAG_TEXT_MULTILINE",
        [nameof(FpdfConstants.FormFlagTextPassword)] = "FPDF_FORMFLAG_TEXT_PASSWORD",
        [nameof(FpdfConstants.FormFlagChoiceCombo)] = "FPDF_FORMFLAG_CHOICE_COMBO",
        [nameof(FpdfConstants.FormFlagChoiceEdit)] = "FPDF_FORMFLAG_CHOICE_EDIT",
        [nameof(FpdfConstants.FormFlagChoiceMultiSelect)] = "FPDF_FORMFLAG_CHOICE_MULTI_SELECT",
        [nameof(FpdfConstants.AnnotActionKeyStroke)] = "FPDF_ANNOT_AACTION_KEY_STROKE",
        [nameof(FpdfConstants.AnnotActionFormat)] = "FPDF_ANNOT_AACTION_FORMAT",
        [nameof(FpdfConstants.AnnotActionValidate)] = "FPDF_ANNOT_AACTION_VALIDATE",
        [nameof(FpdfConstants.AnnotActionCalculate)] = "FPDF_ANNOT_AACTION_CALCULATE",
    };

    /// <summary>
    /// Constants without a header define: font descriptor flags returned by <c>FPDFText_GetFontInfo</c>, whose
    /// header comment refers to the PDF specification (ISO 32000-1, table 123; flag bit n has the value 1 &lt;&lt; (n - 1)),
    /// the permission bits (table 22) and the field flags <c>fpdf_annot.h</c> does not name (tables 226, 228 and 230).
    /// </summary>
    private static readonly Dictionary<string, long> SpecificationValues = new()
    {
        [nameof(FpdfConstants.FontFlagForceBold)] = 1L << (19 - 1),
        [nameof(FpdfConstants.FontFlagItalic)] = 1L << (7 - 1),
        [nameof(FpdfConstants.TextRenderModeInvisible)] = 3,
        [nameof(FpdfConstants.PermissionPrint)] = 1L << (3 - 1),
        [nameof(FpdfConstants.PermissionAnnotate)] = 1L << (6 - 1),
        [nameof(FpdfConstants.FormFlagNoToggleToOff)] = 1L << (15 - 1),
        [nameof(FpdfConstants.FormFlagFileSelect)] = 1L << (21 - 1),
        [nameof(FpdfConstants.FormFlagDoNotSpellCheck)] = 1L << (23 - 1),
        [nameof(FpdfConstants.FormFlagDoNotScroll)] = 1L << (24 - 1),
        [nameof(FpdfConstants.FormFlagComb)] = 1L << (25 - 1),
        [nameof(FpdfConstants.FormFlagRichText)] = 1L << (26 - 1),
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
        var headerDirectory = HeaderDirectory();
        var defines = new Dictionary<string, long>();
        foreach (var header in new[] { "fpdfview.h", "fpdf_progressive.h", "fpdf_doc.h", "fpdf_save.h", "fpdf_annot.h", "fpdf_edit.h", "fpdf_formfill.h" })
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

        // The rendering mode is an enum in fpdfview.h, not a define.
        Assert.Contains("FPDF_TEXTRENDERMODE_INVISIBLE = 3", File.ReadAllText(Path.Combine(headerDirectory, "fpdfview.h")), StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="FpdfFormFillInfo"/> has the members of <c>FPDF_FORMFILLINFO</c> in <c>fpdf_formfill.h</c>, in the same order
    /// and of the same width (pointers and function pointers <see cref="nint"/>, <c>int</c> and <c>FPDF_BOOL</c>
    /// <see cref="int"/>). PDFium reads the whole structure, so a member added by an update must be added here too.
    /// </summary>
    [Fact]
    public void FormFillInfoMatchesTheHeader()
    {
        var header = File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdf_formfill.h"));
        var start = header.IndexOf("typedef struct _FPDF_FORMFILLINFO {", StringComparison.Ordinal);
        var end = header.IndexOf("} FPDF_FORMFILLINFO;", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "FPDF_FORMFILLINFO not found in fpdf_formfill.h.");
        var body = CommentPattern().Replace(header[start..end], string.Empty);
        body = body[(body.IndexOf('{', StringComparison.Ordinal) + 1)..];

        var members = MemberPattern().Matches(body)
            .Select(match => match.Groups["function"].Success
                ? (Name: Normalize(match.Groups["function"].Value), Kind: "pointer")
                : (Name: Normalize(match.Groups["field"].Value), Kind: KindOf(match.Groups["type"].Value)))
            .ToList();
        var fields = typeof(FpdfFormFillInfo)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(field => Marshal.OffsetOf<FpdfFormFillInfo>(field.Name).ToInt64())
            .Select(field => (Name: field.Name.ToLowerInvariant(), Kind: field.FieldType == typeof(nint) ? "pointer" : field.FieldType == typeof(int) ? "int" : field.FieldType.Name))
            .ToList();

        Assert.Equal(35, members.Count);
        Assert.Equal(members, fields);
        if (nint.Size == 8)
        {
            // Measured in the spike: 280 bytes on x64.
            Assert.Equal(280, Unsafe.SizeOf<FpdfFormFillInfo>());
        }

        static string Normalize(string name) =>
            (name.StartsWith("m_p", StringComparison.Ordinal) ? name[3..] : name).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        static string KindOf(string type) =>
            type.Contains('*', StringComparison.Ordinal) ? "pointer" : type.Trim() is "int" or "FPDF_BOOL" ? "int" : type.Trim();
    }

    /// <summary>
    /// Guards the experimental glyph API the text boxes draw with (milestone 10): <c>FPDFFont_GetGlyphPath</c> and
    /// <c>FPDFFont_GetGlyphWidth</c> take the Unicode value (the header only says "glyph"), the outline comes in em whatever
    /// the size, the width in points of the size, and the metrics of Geist Regular (ascent 1005, descent 295 per 1000) come
    /// through. If an update of PDFium fails this, check the header and the behaviour; do not loosen it.
    /// </summary>
    [Fact]
    public async Task GlyphApiTakesUnicodeAndReturnsEmUnits()
    {
        var font = await File.ReadAllBytesAsync(TestData.GeistRegular, TestContext.Current.CancellationToken);
        var table = await PdfiumThread.Shared.InvokeAsync(
            () => PdfiumGlyphs.Read(PdfiumThread.Shared, font, ['H', 'ü', '€', ' ']),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.005, table.Ascent, 3);
        Assert.InRange(table.Descent, 0.29, 0.30);
        Assert.True(table.TryGet('H', out var h));

        // Read at size 1, so the width is in em; at 20 pt it would be twenty times as much.
        Assert.InRange(h.Advance, 0.6, 0.8);
        var numbers = h.Outline.Split(' ').Where(t => int.TryParse(t, out _)).Select(int.Parse).ToArray();
        var ys = numbers.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(0, ys.Min());
        Assert.InRange(ys.Max(), 700, 720);
        Assert.EndsWith("f", h.Outline, StringComparison.Ordinal);
        Assert.True(table.TryGet('ü', out var u) && u.Outline.Length > 0, "ü has no outline: the glyph API no longer takes the Unicode value.");
        Assert.True(table.TryGet('€', out var euro) && euro.Outline.Length > 0);
        Assert.True(table.TryGet(' ', out var space));
        Assert.Equal(string.Empty, space.Outline);
        Assert.True(space.Advance > 0);
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

    private static string HeaderDirectory() =>
        Path.Combine(
            NuGetPackageRoot(),
            "bblanchon.pdfium.win32",
            PackageVersion("bblanchon.PDFium.Win32"),
            "build",
            "native",
            "include",
            "pdfium");

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

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex CommentPattern();

    /// <summary>A member of a C structure: a function pointer <c>(*Name)(</c>, or a field <c>type Name;</c> on its own line.</summary>
    [GeneratedRegex(@"\(\s*\*\s*(?<function>\w+)\s*\)\s*\(|^\s*(?<type>\w+\s*\**)\s*(?<field>\w+)\s*;", RegexOptions.Multiline)]
    private static partial Regex MemberPattern();

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

    /// <summary>
    /// Guards the functions the overlay text of a redaction is drawn with (milestone 14.1): <c>FPDFPageObj_CreateNewPath</c>,
    /// <c>FPDFPath_MoveTo</c>, <c>LineTo</c>, <c>BezierTo</c> and <c>Close</c>. The declarations in <c>fpdf_edit.h</c> must read as the
    /// bindings do (the same parameters in the same order and of the same width: a handle is a pointer, a coordinate a <c>float</c>, a
    /// <c>FPDF_BOOL</c> an <c>int</c>), and PDFium must build what the overlay expects: the creation is itself a move to the first point,
    /// a curve is three segments, and a close marks the last segment without adding one.
    /// </summary>
    [Fact]
    public async Task PathBuildingApiMatchesTheHeaderAndBuildsWhatTheOverlayExpects()
    {
        var header = Regex.Replace(File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdf_edit.h")), @"\s+", " ");
        var declared = new (string Name, string Result, string Parameters)[]
        {
            ("FPDFPageObj_CreateNewPath", "FPDF_PAGEOBJECT", "float x, float y"),
            ("FPDFPath_MoveTo", "FPDF_BOOL", "FPDF_PAGEOBJECT path, float x, float y"),
            ("FPDFPath_LineTo", "FPDF_BOOL", "FPDF_PAGEOBJECT path, float x, float y"),
            ("FPDFPath_BezierTo", "FPDF_BOOL", "FPDF_PAGEOBJECT path, float x1, float y1, float x2, float y2, float x3, float y3"),
            ("FPDFPath_Close", "FPDF_BOOL", "FPDF_PAGEOBJECT path"),
        };
        foreach (var (name, result, parameters) in declared)
        {
            Assert.Contains($"{result} FPDF_CALLCONV {name}({parameters});", header, StringComparison.Ordinal);
            var method = typeof(Fpdf).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            var expected = parameters.Split(", ").Select(static p => p.Split(' ')[0] == "float" ? typeof(float) : typeof(nint)).ToArray();
            Assert.Equal(expected, method.GetParameters().Select(static p => p.ParameterType).ToArray());
            Assert.Equal(result == "FPDF_BOOL" ? typeof(int) : typeof(nint), method.ReturnType);
        }

        var segments = await PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                var document = Fpdf.FPDF_CreateNewDocument();
                Assert.NotEqual(0, document);
                try
                {
                    var page = Fpdf.FPDFPage_New(document, 0, 200, 100);
                    Assert.NotEqual(0, page);
                    try
                    {
                        var path = Fpdf.FPDFPageObj_CreateNewPath(10, 20);
                        Assert.NotEqual(0, path);
                        Assert.Equal(1, Fpdf.FPDFPath_LineTo(path, 50, 20));
                        Assert.Equal(1, Fpdf.FPDFPath_BezierTo(path, 60, 20, 70, 30, 70, 40));
                        Assert.Equal(1, Fpdf.FPDFPath_Close(path));
                        Assert.Equal(1, Fpdf.FPDFPath_MoveTo(path, 100, 50));
                        Assert.Equal(1, Fpdf.FPDFPath_LineTo(path, 120, 50));
                        Assert.Equal(1, Fpdf.FPDFPath_LineTo(path, 120, 70));
                        Assert.Equal(1, Fpdf.FPDFPath_Close(path));
                        Assert.Equal(1, Fpdf.FPDFPageObj_SetFillColor(path, 255, 255, 255, 255));
                        Assert.Equal(1, Fpdf.FPDFPath_SetDrawMode(path, FpdfConstants.FillModeWinding, 0));
                        Assert.Equal(1, Fpdf.FPDFPage_InsertObject(page, path));

                        var list = new List<(int Type, float X, float Y, bool Close)>();
                        unsafe
                        {
                            for (var i = 0; i < Fpdf.FPDFPath_CountSegments(path); i++)
                            {
                                var segment = Fpdf.FPDFPath_GetPathSegment(path, i);
                                float x = 0, y = 0;
                                Assert.Equal(1, Fpdf.FPDFPathSegment_GetPoint(segment, &x, &y));
                                list.Add((Fpdf.FPDFPathSegment_GetType(segment), x, y, Fpdf.FPDFPathSegment_GetClose(segment) != 0));
                            }

                            float left = 0, bottom = 0, right = 0, top = 0;
                            Assert.Equal(1, Fpdf.FPDFPageObj_GetBounds(path, &left, &bottom, &right, &top));
                            Assert.Equal((10f, 20f, 120f, 70f), (left, bottom, right, top));
                        }

                        return list;
                    }
                    finally
                    {
                        Fpdf.FPDF_ClosePage(page);
                    }
                }
                finally
                {
                    Fpdf.FPDF_CloseDocument(document);
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // The creation is the first move, a curve is three segments (control, control, end), a close only marks the last point.
        Assert.Equal(
            [
                (FpdfConstants.SegmentMoveTo, 10f, 20f, false),
                (FpdfConstants.SegmentLineTo, 50f, 20f, false),
                (FpdfConstants.SegmentBezierTo, 60f, 20f, false),
                (FpdfConstants.SegmentBezierTo, 70f, 30f, false),
                (FpdfConstants.SegmentBezierTo, 70f, 40f, true),
                (FpdfConstants.SegmentMoveTo, 100f, 50f, false),
                (FpdfConstants.SegmentLineTo, 120f, 50f, false),
                (FpdfConstants.SegmentLineTo, 120f, 70f, true),
            ],
            segments);
    }

    /// <summary>
    /// Pins what the header says about who owns a page object when <c>FPDFPage_InsertObject</c> fails: PDFium frees it. Redaction therefore
    /// never destroys an object after a failed insert (that would free it twice, as the overlay text once did). The header is silent about
    /// <c>FPDFPage_InsertObjectAtIndex</c> on failure (it only says the ownership moves back to PDFium), so its callers are not changed
    /// until the header says it; if an update words either text differently, check every insert in <c>PdfiumRedaction*</c> again.
    /// </summary>
    [Fact]
    public void InsertObjectFreesThePageObjectOnFailureAsTheHeaderSays()
    {
        var header = File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdf_edit.h")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var block = header.Split("\n\n").SingleOrDefault(b => b.Contains("\nFPDFPage_InsertObject(", StringComparison.Ordinal));
        Assert.True(block is not null, "FPDFPage_InsertObject is not declared in fpdf_edit.h.");
        var words = Regex.Replace(block, @"\s*//\s*", " ").Trim();
        Assert.Contains("Ownership of |page_object| transfers to |page| on success. |page_object| is freed on failure.", words, StringComparison.Ordinal);
        Assert.Contains("FPDFPage_InsertObjectAtIndex(", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the experimental clipping path API that decides whether an image can be pixelated or blurred (milestone 14.1):
    /// <c>FPDFPageObj_GetClipPath</c>, <c>FPDFClipPath_CountPaths</c>, <c>FPDFClipPath_CountPathSegments</c> and
    /// <c>FPDFClipPath_GetPathSegment</c> are still experimental in the header, and they answer as the rule assumes: an image that nothing
    /// cuts has no path in its clip (the header says -1 is a failure, PDFium also answers -1 for a clip without content, so -1 and 0
    /// both mean "no cut"), an image cut to a rectangle has one path of a closed rectangle (a move and four lines). If an update of PDFium
    /// fails this, check the header and the behaviour before you change <c>PdfiumRedactionObjects.IsClipped</c>; do not loosen it.
    /// </summary>
    [Fact]
    public async Task ClipPathApiAnswersAsTheTreatmentAssumes()
    {
        var header = File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdf_transformpage.h")).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var name in new[] { "FPDFPageObj_GetClipPath(", "FPDFClipPath_CountPaths(", "FPDFClipPath_CountPathSegments(", "FPDFClipPath_GetPathSegment(" })
        {
            var block = header.Split("\n\n").SingleOrDefault(b => b.Contains(name, StringComparison.Ordinal));
            Assert.True(block is not null, $"{name} is not declared in fpdf_transformpage.h.");
            Assert.StartsWith("// Experimental API.", block, StringComparison.Ordinal);
        }

        var images = await PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(PdfiumThread.Shared, TestData.Pdf("redact-image-blocks.pdf"), null);
                using var page = PdfiumRedaction.OpenPage(document, 0);
                var found = new List<(int Paths, int Segments, int FirstType, bool Clipped)>();
                for (var i = 0; i < page.CountObjects(); i++)
                {
                    var obj = page.GetObject(i);
                    if (Fpdf.FPDFPageObj_GetType(obj) != FpdfConstants.PageObjImage)
                    {
                        continue;
                    }

                    var clip = Fpdf.FPDFPageObj_GetClipPath(obj);
                    Assert.NotEqual(0, clip);
                    var paths = Fpdf.FPDFClipPath_CountPaths(clip);
                    var segments = paths > 0 ? Fpdf.FPDFClipPath_CountPathSegments(clip, 0) : 0;
                    var firstType = segments > 0 ? Fpdf.FPDFPathSegment_GetType(Fpdf.FPDFClipPath_GetPathSegment(clip, 0, 0)) : -1;
                    found.Add((paths, segments, firstType, PdfiumRedactionObjects.IsClipped(obj)));
                }

                return found;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, images.Count);
        foreach (var plain in images.Take(4))
        {
            Assert.True(plain.Paths <= 0, $"An image nothing cuts has {plain.Paths} clip paths.");
            Assert.False(plain.Clipped);
        }

        var clipped = images[4];
        Assert.Equal(1, clipped.Paths);
        Assert.Equal(5, clipped.Segments);
        Assert.Equal(FpdfConstants.SegmentMoveTo, clipped.FirstType);
        Assert.True(clipped.Clipped);
    }

    /// <summary>
    /// Guards the experimental content mark API the OCR text layer is recognized by (milestone 15): <c>FPDFPageObj_CountMarks</c>,
    /// <c>FPDFPageObj_GetMark</c>, <c>FPDFPageObj_AddMark</c> and <c>FPDFPageObjMark_GetName</c> are still experimental in the header, take the
    /// tag as a byte string and give the name back as UTF-16 with its length in bytes. If an update of PDFium fails this, check the header and the
    /// layer tests (<c>OcrLayerTests</c>, <c>OcrLayerSpikeTests</c>) before you change <c>PdfiumOcrLayer</c>; do not loosen it.
    /// </summary>
    [Fact]
    public void ContentMarkApiIsStillTheExperimentalOneTheLayerReads()
    {
        var header = File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdf_edit.h")).ReplaceLineEndings("\n");
        var declarations = new Dictionary<string, string>
        {
            ["FPDFPageObj_CountMarks("] = "FPDFPageObj_CountMarks(FPDF_PAGEOBJECT page_object);",
            ["FPDFPageObj_GetMark("] = "FPDFPageObj_GetMark(FPDF_PAGEOBJECT page_object, unsigned long index);",
            ["FPDFPageObj_AddMark("] = "FPDFPageObj_AddMark(FPDF_PAGEOBJECT page_object, FPDF_BYTESTRING name);",
            ["FPDFPageObjMark_GetName("] = "FPDFPageObjMark_GetName(FPDF_PAGEOBJECTMARK mark,",
            ["FPDFPageObjMark_CountParams("] = "FPDFPageObjMark_CountParams(FPDF_PAGEOBJECTMARK mark);",
            ["FPDFPageObjMark_GetParamKey("] = "FPDFPageObjMark_GetParamKey(FPDF_PAGEOBJECTMARK mark,",
            ["FPDFPageObjMark_GetParamValueType(FPDF_PAGEOBJECTMARK mark,"] = "FPDFPageObjMark_GetParamValueType(FPDF_PAGEOBJECTMARK mark,",
            ["FPDFPageObjMark_GetParamIntValue(FPDF_PAGEOBJECTMARK mark,"] = "FPDFPageObjMark_GetParamIntValue(FPDF_PAGEOBJECTMARK mark,",
            ["FPDFPageObjMark_SetIntParam(FPDF_DOCUMENT document,"] = "FPDFPageObjMark_SetIntParam(FPDF_DOCUMENT document,",
        };
        foreach (var (name, declaration) in declarations)
        {
            var block = header.Split("\n\n").SingleOrDefault(b => b.Contains(name, StringComparison.Ordinal));
            Assert.True(block is not null, $"{name} is not declared in fpdf_edit.h.");
            Assert.StartsWith("// Experimental API.", block, StringComparison.Ordinal);
            Assert.Contains(declaration, block.ReplaceLineEndings(" ").Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.Contains("FPDF_WCHAR* buffer,", header, StringComparison.Ordinal);
        Assert.Contains("unsigned long buflen,", header, StringComparison.Ordinal);
        Assert.Contains("unsigned long* out_buflen);", header, StringComparison.Ordinal);

        // The render mode "invisible" the layer is written in (fpdfview.h).
        Assert.Equal(3, FpdfConstants.TextRenderModeInvisible);
        Assert.Contains("FPDF_TEXTRENDERMODE_INVISIBLE = 3", File.ReadAllText(Path.Combine(HeaderDirectory(), "fpdfview.h")), StringComparison.Ordinal);
    }
}
