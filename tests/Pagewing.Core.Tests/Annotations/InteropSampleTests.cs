using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>
/// Writes the samples for the check in other viewers (Chrome, Acrobat Reader, pdf.js, Okular …; <c>packaging/README.md</c>,
/// "Annotationen im Fremdviewer prüfen"): every source file next to a copy annotated by Pagewing's own pipeline
/// (<see cref="AnnotationEditor"/> over a <see cref="ViewerSession"/>, saved through the editor and
/// <c>DocumentSaver</c>), plus <c>README-interop.txt</c> with what to look for. Runs only with
/// <c>PAGEWING_INTEROP_OUT=&lt;folder&gt;</c>; nothing is written anywhere else (the work happens in a temp folder).
/// </summary>
public sealed partial class InteropSampleTests
{
    /// <summary>The environment variable that names the output folder.</summary>
    public const string OutputVariable = "PAGEWING_INTEROP_OUT";

    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private const string Author = "Pagewing Interop";

    /// <summary>The fixed clock of every change, so the samples differ only in their random <c>/NM</c>.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The samples: source, password and the changes, in this order.</summary>
    private static IReadOnlyList<Sample> Samples { get; } =
    [
        new("text-basic.pdf", null, "Two plain pages with one line of text each.",
        [
            new Mark(0, "Hello", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: yellow highlight on 'Hello'"),
            new Mark(0, "Pagewing", AnnotationKind.Underline, MarkupColor.Blue, null),
            new Mark(1, "Größe", AnnotationKind.StrikeOut, MarkupColor.Pink, null),
        ]),
        new("text-rich.pdf", null, "Two pages, headings, a link and a selection over two lines.",
        [
            new Mark(0, "Handbuch", AnnotationKind.Highlight, MarkupColor.Green, null),
            new Mark(0, "schneller PDF-Viewer für Win-dows und Linux.", AnnotationKind.Highlight, MarkupColor.Orange, "Interop: orange highlight over two lines",
                Shown: "two lines, from \"schneller\" to \"Win-\" (line 1) and on from \"dows\" to \"Linux.\" (line 2), one markup with two quadrilaterals",
                Lines: 2),
            new Mark(0, "https://example.org/pagewing", AnnotationKind.Underline, MarkupColor.Blue, null),
            new Mark(0, "kursiv", AnnotationKind.StrikeOut, MarkupColor.Pink, null),
            new Mark(1, "Seite zwei", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight on page 2"),
        ]),
        new("rotated-page.pdf", null, "The page has /Rotate 90.",
        [
            new Mark(0, "Rotated", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight on a rotated page"),
        ]),
        new("cropbox.pdf", null, "The CropBox is smaller than the MediaBox and offset.",
        [
            new Mark(0, "CropBox", AnnotationKind.Highlight, MarkupColor.Orange, "Interop: highlight inside a CropBox"),
        ]),
        new("annotations.pdf", null, "Already has annotations of another program; Pagewing recolours the foreign highlight.",
        [
            new Mark(0, "Link area", AnnotationKind.Underline, MarkupColor.Green, null),
        ],
        Recolor: new Recolor(0, "Highlight", MarkupColor.Blue, "Interop: foreign highlight recoloured to blue")),
        new("annotations-foreign.pdf", null, "Annotations of another program (reply, popup, ink, free text, note, link); Pagewing deletes the bare green highlight.",
        [
            new Mark(0, "Ink is drawn below.", AnnotationKind.Highlight, MarkupColor.Green, "Interop: new highlight next to foreign annotations"),
            new Mark(1, "underlined", AnnotationKind.Highlight, MarkupColor.Yellow, null),
        ],
        Delete: new Delete(0, "No name, no appearance")),
        new("signed-docmdp-3.pdf", null, "Signature dictionary with DocMDP level 3 (forms and annotations allowed); a structure dummy without a valid signature.",
        [
            new Mark(0, "Signed document", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: annotated after signing (DocMDP 3)"),
        ]),
        new("signed-docmdp-1.pdf", null, "Signature dictionary with DocMDP level 1 (no changes allowed); a structure dummy without a valid signature. The app warns before it saves such a file; this sample is saved anyway to see what viewers report.",
        [
            new Mark(0, "Signed document", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: annotated although DocMDP 1 forbids changes"),
        ]),
        new("encrypted_hello_world_r6.pdf", UserPassword, "Encrypted (AES-256, revision 6), user password 'hôtel'. The added objects are encrypted as well.",
        [
            new Mark(0, "Hello", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight in an encrypted file"),
        ]),
        new("linearized.pdf", null, "Linearized (fast web view), two pages; the saved file is no longer linearized (incremental update).",
        [
            new Mark(0, "page one", AnnotationKind.Highlight, MarkupColor.Yellow, null),
            new Mark(1, "page two", AnnotationKind.Underline, MarkupColor.Blue, "Interop: underline on page 2"),
        ]),
    ];

    [Fact]
    public async Task WritesAnnotatedSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var engine = new PdfEngine();
        var readme = new StringBuilder();
        WriteReadmeHeader(readme);
        using var work = new TempFolder();
        foreach (var sample in Samples)
        {
            var report = await WriteSampleAsync(engine, sample, work.Path, output);
            readme.Append(report);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "README-interop.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);

        // The sources, their annotated copies and the readme are there; no temporary file of a save is left behind.
        foreach (var name in Samples.SelectMany(s => new[] { s.File, AnnotatedName(s.File) }).Append("README-interop.txt"))
        {
            Assert.True(File.Exists(Path.Combine(output, name)), $"{name} is missing in {output}.");
        }

        Assert.Empty(Directory.GetFiles(output, "*.tmp"));
    }

    private static string AnnotatedName(string file) => Path.GetFileNameWithoutExtension(file) + "__annotated.pdf";

    private static async Task<string> WriteSampleAsync(PdfEngine engine, Sample sample, string workFolder, string output)
    {
        var source = TestData.Pdf(sample.File);
        var original = await File.ReadAllBytesAsync(source, Ct);
        var working = Path.Combine(workFolder, sample.File);
        File.Copy(source, working, overwrite: true);

        var expected = new List<Expected>();
        string? deletedComment = null;
        string? recolored = null;
        using (var session = new ViewerSession(engine))
        {
            await session.OpenAsync(working, sample.Password);
            Assert.Equal(ViewerState.Ready, session.State);
            session.Document!.Clock = new FixedClock(Now);
            var editor = new AnnotationEditor(session);

            foreach (var mark in sample.Marks)
            {
                var rects = await RectsOfAsync(session.Document!, mark);
                var created = await editor.ApplyMarkupAsync(mark.Page, mark.Kind, rects, AnnotationPalette.ColorOf(mark.Color), mark.Comment, Author);
                Assert.NotNull(created);
                Assert.Equal(mark.Lines, created.QuadRects.Count);
                expected.Add(new Expected(created.Name!, mark.Page, mark.Kind, AnnotationPalette.ColorOf(mark.Color), mark.Comment, created.QuadRects));
            }

            if (sample.Recolor is { } recolor)
            {
                var foreign = (await session.Document!.GetAnnotationsAsync(recolor.Page, Ct)).First(a => a.Subtype == recolor.Subtype && a.Editable);
                var changed = await editor.UpdateAsync(new AnnotationLocator(recolor.Page, foreign.Index, foreign.Name), new AnnotationEdit(recolor.Comment, AnnotationPalette.ColorOf(recolor.Color)));
                expected.Add(new Expected(changed.Name!, recolor.Page, changed.Kind, AnnotationPalette.ColorOf(recolor.Color), recolor.Comment, changed.QuadRects));
                recolored = changed.Name;
            }

            if (sample.Delete is { } delete)
            {
                var foreign = (await session.Document!.GetAnnotationsAsync(delete.Page, Ct)).Single(a => a.Contents == delete.Comment);
                await editor.DeleteAsync(new AnnotationLocator(delete.Page, foreign.Index, foreign.Name));
                deletedComment = delete.Comment;
            }

            Assert.True(editor.IsDirty);
            var saved = await editor.SaveAsync(engine, working, overwriteChangedOriginal: false, progress: null, Ct);
            Assert.True(saved.Adopted);
            Assert.False(editor.IsDirty);
        }

        var bytes = await File.ReadAllBytesAsync(working, Ct);

        // The original bytes are untouched (signed files keep the signed byte range).
        Assert.True(bytes.Length > original.Length, $"{sample.File}: nothing was appended.");
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original), $"{sample.File}: the original bytes were changed.");

        await CheckRoundTripAsync(engine, sample, working, expected, deletedComment);
        CheckAppearanceObjects(sample, original, bytes, expected);

        File.Copy(source, Path.Combine(output, sample.File), overwrite: true);
        File.Copy(working, Path.Combine(output, AnnotatedName(sample.File)), overwrite: true);
        return Describe(sample, expected, recolored is not null, original.Length, bytes.Length);
    }

    /// <summary>The selection rectangles of the text of a mark, as the view selects it (character rectangles per line).</summary>
    private static async Task<IReadOnlyList<PageRect>> RectsOfAsync(PdfDocument document, Mark mark)
    {
        var text = await document.GetPageTextAsync(mark.Page, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var start = IndexOfIgnoringBreaks(all, mark.Text, out var length);
        Assert.True(start >= 0, $"'{mark.Text}' is not on page {mark.Page}: '{all}'");
        var rects = text.RectsOf(start, start + length);
        Assert.NotEmpty(rects);
        return rects;
    }

    /// <summary>Finds <paramref name="needle"/> in <paramref name="haystack"/>, where a line break may stand between two characters of it.</summary>
    private static int IndexOfIgnoringBreaks(string haystack, string needle, out int length)
    {
        var direct = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (direct >= 0)
        {
            length = needle.Length;
            return direct;
        }

        for (var start = 0; start < haystack.Length; start++)
        {
            int h = start, n = 0;
            while (h < haystack.Length && n < needle.Length)
            {
                if (haystack[h] == needle[n])
                {
                    h++;
                    n++;
                }
                else if (n > 0 && haystack[h] is '\r' or '\n')
                {
                    h++;
                }
                else
                {
                    break;
                }
            }

            if (n == needle.Length)
            {
                length = h - start;
                return start;
            }
        }

        length = 0;
        return -1;
    }

    /// <summary>The saved file opens with PDFium, renders, and lists the markups with the expected kinds, colours, lines and comments.</summary>
    private static async Task CheckRoundTripAsync(PdfEngine engine, Sample sample, string path, List<Expected> expected, string? deletedComment)
    {
        if (sample.Password is not null)
        {
            var refused = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(path, cancellationToken: Ct));
            Assert.Equal(PdfOpenError.PasswordRequired, refused.Error);
        }

        await using var reopened = await engine.OpenAsync(path, sample.Password, Ct);
        foreach (var page in expected.Select(e => e.Page).Distinct())
        {
            _ = await reopened.RenderPageAsync(page, 1, cancellationToken: Ct);
        }

        foreach (var want in expected)
        {
            var annotations = await reopened.GetAnnotationsAsync(want.Page, Ct);
            var got = annotations.SingleOrDefault(a => a.Name == want.Name);
            Assert.True(got is not null, $"{sample.File}: the markup {want.Name} is missing on page {want.Page}.");
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Color, got.Color);
            Assert.Equal(want.Comment, got.Contents);
            Assert.True(got.Editable);
            Assert.Equal(want.Lines.Count, got.QuadRects.Count);
            for (var i = 0; i < want.Lines.Count; i++)
            {
                AssertRectNear(want.Lines[i], got.QuadRects[i], 0.1);
            }
        }

        if (deletedComment is not null)
        {
            for (var page = 0; page < reopened.PageCount; page++)
            {
                Assert.DoesNotContain(await reopened.GetAnnotationsAsync(page, Ct), a => a.Contents == deletedComment);
            }
        }
    }

    /// <summary>
    /// Every created or changed markup is an indirect object of the appended revision whose <c>/AP /N</c> names a form
    /// XObject that is an object of its own in that revision. Names are strings, which an encrypted file encrypts, so there
    /// the markups are counted by subtype instead of found by <c>/NM</c>.
    /// </summary>
    private static void CheckAppearanceObjects(Sample sample, byte[] original, byte[] saved, List<Expected> expected)
    {
        var appended = Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
        var objects = ObjectPattern().Matches(appended).ToDictionary(m => m.Groups["number"].Value, m => m.Groups["body"].Value);
        var markups = objects.Values.Where(body => MarkupPattern().IsMatch(body)).ToList();
        Assert.True(markups.Count >= expected.Count, $"{sample.File}: {markups.Count} markup objects in the appended revision, expected at least {expected.Count}.");
        if (sample.Password is null)
        {
            markups = [.. expected.Select(want => markups.Single(body => body.Contains("/NM(" + want.Name + ")", StringComparison.Ordinal)))];
        }

        foreach (var body in markups)
        {
            var reference = AppearancePattern().Match(body);
            Assert.True(reference.Success, $"{sample.File}: a markup has no /AP << /N n 0 R >>: {body}");
            Assert.True(objects.TryGetValue(reference.Groups["n"].Value, out var appearance), $"{sample.File}: the appearance object {reference.Groups["n"].Value} is not in the appended revision.");
            var stream = appearance.IndexOf("stream", StringComparison.Ordinal);
            Assert.Matches(FormPattern(), stream < 0 ? appearance : appearance[..stream]);
        }
    }

    private static void WriteReadmeHeader(StringBuilder readme)
    {
        readme.AppendLine("Pagewing - annotation samples for other viewers");
        readme.AppendLine("================================================");
        readme.AppendLine();
        readme.AppendLine("Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs (PAGEWING_INTEROP_OUT).");
        readme.AppendLine("Every <name>.pdf is the unchanged source from testdata/pdfs, every <name>__annotated.pdf the same file");
        readme.AppendLine("annotated by Pagewing's own pipeline (AnnotationEditor, then Save through DocumentSaver: incremental update,");
        readme.AppendLine("checked before it replaced the file). Open both side by side; the checklist is in packaging/README.md,");
        readme.AppendLine("section \"Annotationen im Fremdviewer prüfen\".");
        readme.AppendLine();
        readme.AppendLine(CultureInfo.InvariantCulture, $"Author of every new markup: {Author}. Date of every change: {Now:yyyy-MM-dd HH:mm} UTC.");
        readme.AppendLine("The /NM names are random and differ from run to run; everything else is fixed.");
        readme.AppendLine();
        readme.AppendLine("Colours (palette of Pagewing, RGB):");
        foreach (var color in AnnotationPalette.All)
        {
            var value = AnnotationPalette.ColorOf(color);
            readme.AppendLine(CultureInfo.InvariantCulture, $"  {color,-7} #{value.R:X2}{value.G:X2}{value.B:X2}");
        }

        readme.AppendLine();
        readme.AppendLine("For every markup a viewer must show:");
        readme.AppendLine("  - highlight: a coloured area behind the words, the words stay readable (multiplied, not covered);");
        readme.AppendLine("  - underline: a coloured line just below the words; strike-out: a coloured line through their middle;");
        readme.AppendLine("  - exactly over the words named below, also on the rotated page and inside the CropBox;");
        readme.AppendLine("  - a comment, where one is named: in the comment list (Acrobat: Comments pane) and on hover or click;");
        readme.AppendLine("  - the same markups after printing or print preview (the print flag is set).");
        readme.AppendLine("Pagewing never claims that a signature is valid; for the signed files only report what the viewer says.");
        readme.AppendLine();
    }

    private static string Describe(Sample sample, List<Expected> expected, bool recolored, long originalLength, long savedLength)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{sample.File} -> {AnnotatedName(sample.File)}");
        text.AppendLine(new string('-', sample.File.Length + AnnotatedName(sample.File).Length + 4));
        text.AppendLine(CultureInfo.InvariantCulture, $"  {sample.About}");
        if (sample.Password is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Password: {sample.Password} (both files ask for it; the annotated one must show the markup after it).");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  Original {originalLength} bytes, annotated {savedLength} bytes; the first {originalLength} bytes are identical.");
        foreach (var mark in sample.Marks)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {mark.Page + 1}: {KindName(mark.Kind)} {mark.Color} on {mark.Shown ?? $"\"{mark.Text}\""}{CommentOf(mark.Comment)}");
        }

        if (sample.Recolor is { } recolor && recolored)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {recolor.Page + 1}: the existing yellow highlight of the other program is now {recolor.Color}{CommentOf(recolor.Comment)} (Pagewing made a new appearance)");
        }

        if (sample.Delete is { } delete)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {delete.Page + 1}: the green highlight of the other program with the comment \"{delete.Comment}\" is deleted (over \"Second mark without a name.\"); the other foreign annotations stay");
        }

        if (sample.File.StartsWith("signed-docmdp-3", StringComparison.Ordinal))
        {
            text.AppendLine("  Signature: DocMDP 3 allows annotations. With a real signature a viewer reports the signature as valid with");
            text.AppendLine("  later changes that are permitted (Acrobat: \"changes permitted by the certifier\"). This dummy has no valid");
            text.AppendLine("  signature, so expect \"invalid\"/\"not verifiable\" for that reason, in the source as well as in the annotated copy.");
        }
        else if (sample.File.StartsWith("signed-docmdp-1", StringComparison.Ordinal))
        {
            text.AppendLine("  Signature: DocMDP 1 forbids every change. With a real signature a viewer reports the annotated copy as changed");
            text.AppendLine("  after certification (invalid). This dummy is not verifiable in either file; compare source and copy.");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  {expected.Count} markup(s) created or changed by Pagewing.");
        text.AppendLine();
        return text.ToString();
    }

    private static string KindName(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => "highlight",
        AnnotationKind.Underline => "underline",
        AnnotationKind.StrikeOut => "strike-out",
        _ => kind.ToString(),
    };

    private static string CommentOf(string? comment) => comment is null ? string.Empty : $", comment \"{comment}\"";

    [GeneratedRegex(@"(?<!\d)(?<number>\d+)\s+0\s+obj\b(?<body>.*?)\bendobj", RegexOptions.Singleline)]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"/Type\s*/Annot\b.*/Subtype\s*/(Highlight|Underline|StrikeOut)\b|/Subtype\s*/(Highlight|Underline|StrikeOut)\b.*/Type\s*/Annot\b", RegexOptions.Singleline)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"/AP\s*<<\s*/N\s+(?<n>\d+)\s+0\s+R\s*>>")]
    private static partial Regex AppearancePattern();

    [GeneratedRegex(@"/Subtype\s*/Form")]
    private static partial Regex FormPattern();

    /// <summary>One sample: the source in <c>testdata/pdfs</c>, its password, and the changes.</summary>
    private sealed record Sample(string File, string? Password, string About, IReadOnlyList<Mark> Marks, Recolor? Recolor = null, Delete? Delete = null);

    /// <summary>A new markup over <paramref name="Text"/> (a line break of the page may stand inside it).</summary>
    private sealed record Mark(int Page, string Text, AnnotationKind Kind, MarkupColor Color, string? Comment, string? Shown = null, int Lines = 1);

    /// <summary>A new colour and comment for the first editable annotation of <paramref name="Subtype"/> on the page.</summary>
    private sealed record Recolor(int Page, string Subtype, MarkupColor Color, string Comment);

    /// <summary>Deletes the annotation of another program whose comment is <paramref name="Comment"/>.</summary>
    private sealed record Delete(int Page, string Comment);

    private sealed record Expected(string Name, int Page, AnnotationKind Kind, AnnotationColor Color, string? Comment, IReadOnlyList<PageRect> Lines);
}
