using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The end-to-end pipeline of a redaction (milestone 14, task 8): working copy, analysis, page by page, render check, rebuild, the
/// checked full rewrite and the adoption by the session. The files are copies in a temp folder; nothing is written next to the inputs.
/// </summary>
public sealed class RedactionApplierTests : IDisposable
{
    private const string Term = "Zephyrstraße 17";
    private const string UserPassword = "hôtel";

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    // ------------------------------------------------------------------ helpers

    private string Copy(string name, string? asName = null)
    {
        var path = Path.Combine(folder.Path, asName ?? name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Target(string name = "redacted.pdf") => Path.Combine(folder.Path, name);

    /// <summary>
    /// A copy of <c>redact-everywhere-nopattern.pdf</c>: <c>redact-everywhere.pdf</c> without the one thing PDFium cannot redact around (the
    /// tiling pattern fill of page one and the copy of the term inside the pattern). Everything else hides the term as before: form object,
    /// note, reply, form field, invisible text, bookmark, information dictionary, XMP, attachment.
    /// </summary>
    private string NoPatternCopy(string name = "everywhere.pdf")
    {
        var path = Write(name, File.ReadAllBytes(TestData.Pdf("redact-everywhere-nopattern.pdf")));
        var bytes = File.ReadAllBytes(path);
        Assert.True(RawPdf.ContainsAnywhere(bytes, Term));
        Assert.True(RawPdf.ContainsAnywhere(bytes, "Quellmann"));
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("/Pattern", text, StringComparison.Ordinal);
        Assert.Contains("/Outlines", text, StringComparison.Ordinal);
        Assert.Contains("/EmbeddedFile", text, StringComparison.Ordinal);
        return path;
    }

    private static PageRect Union(IEnumerable<PageRect> boxes) =>
        boxes.Aggregate(static (a, b) => new PageRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));

    /// <summary>Adds a mark for every occurrence of the term on every page (case sensitive), over the boxes of its characters.</summary>
    private static async Task<RedactionPlan> WithTermAsync(RedactionPlan plan, PdfDocument document, string term, bool everywhere)
    {
        for (var page = 0; page < document.PageCount; page++)
        {
            var text = await document.GetPageTextAsync(page, Ct);
            foreach (var match in TextMatcher.Find(text.Chars, term, new SearchOptions(MatchCase: true)))
            {
                var boxes = text.Chars.Skip(match.FirstChar).Take(match.LastChar - match.FirstChar + 1).Where(static c => !c.IsGenerated).Select(static c => c.Box);
                plan = plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, page, [Union(boxes)], term, everywhere));
            }
        }

        return plan;
    }

    private static RedactionPlan WithArea(RedactionPlan plan, int page, PageRect area) =>
        plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, page, [area], null, false));

    /// <summary>A file of two lines on one page: the secret and a public line that must stay as it is.</summary>
    private string SecretFile(string name = "secret.pdf", string secret = "secret value")
    {
        var pdf = new VerifierPdf($"BT /F1 12 Tf 20 150 Td ({secret}) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET");
        return Write(name, pdf.Build());
    }

    private static async Task<RedactionPlan> SecretPlanAsync(PdfDocument document, bool everywhere = false, string term = "secret value") =>
        await WithTermAsync(RedactionPlan.Empty, document, term, everywhere);

    /// <summary>A file of two pages in which the second carries a page key PDFium cannot remove.</summary>
    private string ThumbFile()
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (one) Tj ET", "BT /F1 12 Tf 20 150 Td (two) Tj ET");
        var stream = file.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0x66]);
        file.PageEntriesByPage = ["", $"/Thumb {stream} 0 R"];
        return Write("thumb.pdf", file.Build());
    }

    private static string[] LeftOver(string directory, params string[] expected) =>
        [.. Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Where(n => !expected.Contains(n)).Select(static n => n!)];

    private static RedactionApplyHooks Hooks(Action<PdfDocument>? workingCopyOpened = null, Func<int, IReadOnlyList<PageRect>, IReadOnlyList<PageRect>>? areas = null, bool skipScan = false, Func<PdfDocument, Task>? built = null) =>
        new() { WorkingCopyOpened = workingCopyOpened, AreasToRedact = areas, SkipEarlyPageKeyScan = skipScan, CleanBuilt = built };

    private static async Task<string> TextOf(PdfDocument document, int page) =>
        string.Concat((await document.GetPageTextAsync(page, Ct)).Chars.Select(static c => c.Text));

    private sealed class Recorder : IProgress<double>
    {
        private readonly object gate = new();
        private readonly List<double> values = [];

        public Action<double>? OnReport { get; init; }

        public IReadOnlyList<double> Values
        {
            get
            {
                lock (gate)
                {
                    return [.. values];
                }
            }
        }

        public void Report(double value)
        {
            lock (gate)
            {
                values.Add(value);
            }

            OnReport?.Invoke(value);
        }
    }

    // ------------------------------------------------------------------ the acceptance test

    [Fact]
    public async Task RedactingTheFixtureLeavesNothingBehind()
    {
        // redact-everywhere.pdf hides the term in the page text, a form object, invisible text over a scan, a note, its reply, a form
        // field, a bookmark, the information dictionary, the XMP, an attachment and a tiling pattern. PDFium keeps a tiling pattern as an
        // unused resource and cannot paint around one (UnremovableContentFailsTheApplyViaTheRawCheck and the render check refuse that
        // file); redact-everywhere-nopattern.pdf is the same file without that one place, redacted at every other place it hides.
        var original = NoPatternCopy();
        var before = await File.ReadAllBytesAsync(original, Ct);
        Assert.True(RawPdf.ContainsAnywhere(before, Term));
        Assert.True(RawPdf.ContainsAnywhere(before, "Quellmann"));

        using var session = new ViewerSession(engine);
        await session.OpenAsync(original);
        var shown = session.Document!;
        var plan = await WithTermAsync(RedactionPlan.Empty, shown, Term, everywhere: true);
        Assert.Equal(4, plan.Marks.Count);

        // The note, its reply and the form field are annotations: marked by an area each.
        plan = WithArea(plan, 0, new PageRect(248, 40, 290, 62));
        plan = WithArea(plan, 0, new PageRect(20, 275, 200, 300));
        var target = Target();

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, original, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        Assert.True(analysis.PageKeysChecked);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct);

        Assert.True(result.Adopted);
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));

        // Every line of what was not checked comes with its kind (the interface says it in its own language from the kind): the
        // lines and the data are the same list, and none is left without a kind of its own.
        Assert.Equal(result.Check.NotChecked, result.Check.Items.Select(static item => item.Text));
        Assert.DoesNotContain(result.Check.Items, static item => item.Kind == RedactionNotCheckedKind.Other);
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, Term));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Quellmann"));
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        Assert.Equal(2, session.Document!.PageCount);
        for (var page = 0; page < 2; page++)
        {
            Assert.DoesNotContain("Zephyr", await TextOf(session.Document, page), StringComparison.Ordinal);
            Assert.DoesNotContain("17", (await TextOf(session.Document, page)).Replace("Seite zwei", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        // The rest of the page stays: the headline of page one and "Seite zwei".
        Assert.Contains("Redaction fixture, page one", await TextOf(session.Document, 0), StringComparison.Ordinal);
        Assert.Contains("Seite zwei", await TextOf(session.Document, 1), StringComparison.Ordinal);
        Assert.Empty(LeftOver(folder.Path, "everywhere.pdf", "redacted.pdf"));
    }

    [Fact]
    public async Task AnOlderRevisionOfTheFileIsNotPartOfTheResult()
    {
        // redact-revisions.pdf shows "Address removed", but its first revision still holds the term; PDFium sees only the second one. A
        // redaction writes the file anew, so that revision is gone from the result (an incremental save would have kept it).
        var original = Copy("redact-revisions.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        Assert.True(RawPdf.ContainsAnywhere(before, Term));
        Assert.Equal(2, Regex.Count(Encoding.Latin1.GetString(before), "%%EOF", RegexOptions.None, TimeSpan.FromSeconds(5)));

        using var session = new ViewerSession(engine);
        await session.OpenAsync(original);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 60, 215, 90));
        var target = Target();

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.False(RawPdf.ContainsAnywhere(bytes, Term));
        Assert.Equal(1, Regex.Count(text, "%%EOF", RegexOptions.None, TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("/Prev", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Address", await TextOf(session.Document!, 0), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
    }

    [Fact]
    public async Task AFileAttachedToAnAnnotationStaysInTheFileAndTheReportSaysSoWhileTheCatalogAttachmentGoes()
    {
        // The names tree of the catalog is document-level data the rebuild drops; a paperclip is an annotation that comes with its page,
        // and PDFium copies its file with it. The report must not call that file cleaned.
        var original = Copy("redact-attachments.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        Assert.True(RawPdf.ContainsAnywhere(before, "CATALOGFILE-7421"));
        Assert.True(RawPdf.ContainsAnywhere(before, "ANNOTFILE-9183"));
        using (var stream = new MemoryStream(before))
        {
            var inventory = RawContentScanner.Inventory(stream, Ct);
            Assert.Equal(2, inventory.EmbeddedFiles);
            Assert.Equal(1, inventory.AttachmentAnnotations);
        }

        using var session = new ViewerSession(engine);
        await session.OpenAsync(original);
        var shown = session.Document!;
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 62, 280, 90));
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, original, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        Assert.Equal(1, analysis.AttachmentAnnotations);
        var attachments = Assert.Single(analysis.Cleanup, static item => item.Kind == CleanupKind.Attachments);
        Assert.Equal("1", attachments.Detail);

        var target = Target();
        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "CATALOGFILE-7421"));
        Assert.True(RawPdf.ContainsAnywhere(bytes, "ANNOTFILE-9183"));

        foreach (var report in new[] { RedactionReportText.Of(analysis), RedactionReportText.Of(analysis, result.Check) })
        {
            Assert.Contains("Attached files: 1.", report, StringComparison.Ordinal);
            var notCleaned = report[report.IndexOf("Not cleaned:", StringComparison.Ordinal)..];
            notCleaned = notCleaned[..notCleaned.IndexOf("\n\n", StringComparison.Ordinal)];
            Assert.Contains("up to 1", notCleaned, StringComparison.Ordinal);
            Assert.Contains("File attachments of annotations", notCleaned, StringComparison.Ordinal);
            Assert.Contains("stay in the file", notCleaned, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheReportAlwaysSaysThatAnnotationsOutsideTheMarksComeAlongUnchanged()
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await SecretPlanAsync(session.Document!);
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, null, Ct);

        var report = RedactionReportText.Of(analysis);
        var notCleaned = report[report.IndexOf("Not cleaned:", StringComparison.Ordinal)..];
        notCleaned = notCleaned[..notCleaned.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("Annotations outside the marked areas", notCleaned, StringComparison.Ordinal);
        Assert.Contains("unchanged", notCleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("File attachments of annotations", notCleaned, StringComparison.Ordinal);
        foreach (var word in new[] { "valid", "legally", "guarantee", "safe" })
        {
            Assert.DoesNotContain(word, notCleaned, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task ApplyRefusesAFileWithPageKeysWithPagesAndWritesNothing()
    {
        var path = ThumbFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 35, 60, 60));
        var target = Target();

        // The analysis lists it ...
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, path, null, Ct);
        Assert.True(analysis.IsRefused);
        var listed = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, listed.Kind);
        Assert.Equal([1], listed.Pages);
        Assert.False(listed.PageKeysUnverified);
        Assert.Contains("/Thumb on page 2", listed.Reason, StringComparison.Ordinal);

        // ... and a caller that did not look is refused all the same, with nothing written.
        var refused = await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct));

        var refusal = Assert.Single(refused.Refusals);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal([1], refusal.Pages);
        Assert.Contains("page 2", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(target));
        Assert.Empty(LeftOver(folder.Path, "thumb.pdf"));
    }

    [Fact]
    public async Task ApplyRefusesAPageThatFailsTheRenderCheckWithPageAndReasonAndWritesNothing()
    {
        // Page one of the fixture paints with a tiling pattern: PDFium writes the page again without the pattern (the area turns black).
        var path = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await WithTermAsync(RedactionPlan.Empty, session.Document!, Term, everywhere: false);
        plan = plan.Without([.. plan.Marks.Where(static m => m.Page != 0).Select(static m => m.Id)]);
        var target = Target();

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, path, null, Ct);
        Assert.True(analysis.IsRefused);
        var listed = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, listed.Kind);
        Assert.Equal([0], listed.Pages);

        var refused = await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct));

        var refusal = Assert.Single(refused.Refusals);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        Assert.Contains("looks different outside the marked areas", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("page 1", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(target));
        Assert.Empty(LeftOver(folder.Path, "redact-everywhere.pdf"));
    }

    [Fact]
    public async Task RefusedApplyKeepsPlanAndOriginal()
    {
        var path = ThumbFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 35, 60, 60));
        var marks = plan.Marks.ToArray();

        await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct));

        // The plan is immutable and the same object; the session still shows the same document.
        Assert.Equal(marks, plan.Marks);
        Assert.Same(shown, session.Document);
        Assert.Equal(2, session.Document!.PageCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        // Applying the same plan again, after the refusal, is refused the same way (nothing was left half done).
        await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct));
    }

    [Fact]
    public async Task EncryptedIsRefusedBeforeAnythingIsWritten()
    {
        var path = Copy("encrypted_hello_world_r3.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, UserPassword);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(10, 10, 100, 40));
        var opened = 0;
        var hooks = Hooks(workingCopyOpened: _ => opened++);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, UserPassword, Ct);
        Assert.True(analysis.IsRefused);
        Assert.Equal(RedactionRefusalKind.Encrypted, Assert.Single(analysis.Refusals).Kind);

        var refused = await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct));

        Assert.Equal(RedactionRefusalKind.Encrypted, Assert.Single(refused.Refusals).Kind);
        Assert.Contains("encrypted", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, opened); // refused before a working copy was even opened
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Empty(LeftOver(folder.Path, "encrypted_hello_world_r3.pdf"));
    }

    // ------------------------------------------------------------------ the check

    [Fact]
    public async Task ALeakMakesTheApplyFailAndWritesNothing()
    {
        var path = SecretFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await SecretPlanAsync(session.Document!, everywhere: true);
        Assert.Single(plan.Marks);

        // The engine "forgets" the place: the page keeps its text, the plan says it is gone.
        var hooks = Hooks(areas: static (_, _) => []);
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent && f.Page == 0);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.All(check.Findings, static f => Assert.DoesNotContain("secret value", f.Detail, StringComparison.Ordinal));
        Assert.Contains("still in the page text", RedactionRefusalText.Of(check), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", RedactionRefusalText.Of(check), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));
    }

    [Fact]
    public async Task OriginalNeverChangesOnAnyFailure()
    {
        var path = SecretFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = await SecretPlanAsync(shown!, everywhere: true);

        // Writing over the original itself: every way to fail leaves its bytes as they were.
        var request = new RedactionApplyRequest(plan, path);
        await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, request, null, Hooks(areas: static (_, _) => []), Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        using var cancel = new CancellationTokenSource();
        var progress = new Recorder { OnReport = value => { if (value > 0.1) { cancel.Cancel(); } } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedactionApplier.ApplyAsync(session, engine, request, progress, null, cancel.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        var faults = new SaveFaults { AfterWrite = static () => throw new IOException("the disk went away") };
        var writeFailure = await Assert.ThrowsAnyAsync<Exception>(() => RedactionApplier.ApplyAsync(session, engine, request, null, new RedactionApplyHooks { Faults = faults }, Ct));
        Assert.True(writeFailure is SaveFailureException or IOException, writeFailure.GetType().Name);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        Assert.Same(shown, session.Document);
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceChangedBetweenMarkAndApplyIsRefused(bool inOriginal)
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = await SecretPlanAsync(shown!, everywhere: true);

        // Another program rewrote the file after it was marked.
        File.AppendAllText(path, "\n%changed\n");
        var changed = await File.ReadAllBytesAsync(path, Ct);
        var target = inOriginal ? path : Target();

        // Not even with the override: the pages built from another file are never put on the checks of the shown one.
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target, OverwriteChangedOriginal: true), null, Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.Equal(changed, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Same(shown, session.Document);
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));

        // The analysis refuses it by name too.
        var again = await Assert.ThrowsAsync<SaveFailureException>(() => new RedactionExecutor(engine).AnalyzeAsync(plan, shown!, path, null, Ct));
        Assert.Equal(SaveFailureReason.SourceModified, again.Reason);
    }

    [Fact]
    public async Task EveryDocumentTheRebuiltOneReadsFromIsListedAndGuarded()
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var plan = await SecretPlanAsync(shown, everywhere: true);
        RewriteRequest? seen = null;
        PdfDocument? clean = null;
        PdfDocument? working = null;
        var hooks = new RedactionApplyHooks
        {
            RewriteStarting = (request, c, w) =>
            {
                seen = request;
                clean = c;
                working = w;
            },
            Faults = new SaveFaults
            {
                // The file is changed in place while PDFium writes: it ends in the same dead end as everywhere else.
                AfterWrite = () => File.AppendAllText(path, "\n%changed while writing\n"),
            },
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.NotNull(seen);
        Assert.True(seen.RequiresListedSources);
        Assert.Equal(2, seen.UnchangedSources.Count);
        Assert.Contains(shown, seen.UnchangedSources);
        Assert.Contains(working!, seen.UnchangedSources);
        Assert.NotSame(shown, working);
        Assert.NotNull(clean);
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));
    }

    [Fact]
    public async Task OnlyMarkedPagesAreComparedWithoutTextAndTheOthersKeepTheirText()
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (secret value) Tj ET", "BT /F1 12 Tf 20 150 Td (second page) Tj ET", "BT /F1 12 Tf 20 150 Td (third page) Tj ET");
        var path = Write("three.pdf", file.Build());
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await WithTermAsync(RedactionPlan.Empty, session.Document!, "secret value", everywhere: true);
        RewriteRequest? seen = null;
        var hooks = new RedactionApplyHooks { RewriteStarting = (request, _, _) => seen = request };

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct);

        Assert.True(result.Check.IsClean);
        Assert.NotNull(seen);
        Assert.Equal(3, seen.ExpectedPages.Count);
        Assert.False(seen.ExpectedPages[0].CompareText);
        Assert.True(seen.ExpectedPages[1].CompareText);
        Assert.True(seen.ExpectedPages[2].CompareText);
        Assert.NotEqual(0, seen.ExpectedPages[1].TextSetHash);
        Assert.Equal(300, seen.ExpectedPages[0].Width);
    }

    [Fact]
    public async Task NothingOutsideTheMarksChanges()
    {
        var original = NoPatternCopy();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(original);
        var shown = session.Document!;
        var plan = await WithTermAsync(RedactionPlan.Empty, shown, Term, everywhere: true);
        plan = WithArea(plan, 0, new PageRect(248, 40, 290, 62));
        plan = WithArea(plan, 0, new PageRect(20, 275, 200, 300));
        var beforeText = new List<string>();
        for (var page = 0; page < shown.PageCount; page++)
        {
            beforeText.Add(await TextOf(shown, page));
        }

        var options = new RenderOptions { Priority = PdfPriority.Background, Annotations = false };
        var rendersBefore = new List<RenderedImage>();
        for (var page = 0; page < shown.PageCount; page++)
        {
            rendersBefore.Add(await shown.RenderPageAsync(page, 2, options, Ct));
        }

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, original, null, Ct);
        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);
        Assert.True(result.Check.IsClean);

        var after = session.Document!;
        for (var page = 0; page < after.PageCount; page++)
        {
            // The characters of the page are the ones before minus the term (once per copy), in the same order.
            var expected = beforeText[page].Replace(Term, string.Empty, StringComparison.Ordinal);
            var actual = await TextOf(after, page);
            Assert.Equal(expected.Where(static c => !char.IsWhiteSpace(c)), actual.Where(static c => !char.IsWhiteSpace(c)));

            // Outside the marks (and the places content was removed) the page looks as before.
            var areas = RedactionPageAreas.For(plan.AreasOn(page), analysis.Pages.Single(p => p.Page == page)).Areas;
            var now = await after.RenderPageAsync(page, 2, options, Ct);
            var compare = RedactionRenderCheck.Compare(rendersBefore[page], now, 2, areas);
            Assert.True(compare.Passes, $"page {page + 1}: {compare.Different} pixels differ around {compare.Box}");
        }
    }

    [Fact]
    public async Task OverwrittenImageCellsThatReachOutOfTheMarkAreLeftOutOfTheRenderCheck()
    {
        // The scan is 32 x 32 pixels shown at 200 x 80 pt: a pixel is 6.25 x 2.5 pt. A mark over a word of the invisible text over it
        // overwrites the whole cells it touches, which reach out of the mark by up to a cell; the page differs there on purpose.
        var path = NoPatternCopy();
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var mark = new PageRect(30.25, 211.25, 117.5, 222.5);
        var plan = WithArea(RedactionPlan.Empty, 0, mark);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var impact = Assert.Single(analysis.Pages);
        Assert.Equal(1, impact.ImagesOverwritten);
        var cells = Assert.Single(impact.RemovedAreas!, a => a.Left < mark.Left - 1 && a.Right > mark.Right);
        Assert.True(cells.Left >= 26 && cells.Right <= 120.1, cells.ToString());
        Assert.Contains(analysis.Losses, static l => l.Kind == RedactionLossKind.ColorSpaceBecomesRgb);
    }

    [Fact]
    public async Task ChangedPixelOutsideTheAreasFailsTheCheck()
    {
        var path = SecretFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await SecretPlanAsync(session.Document!, everywhere: true);

        // The rebuilt document gets a highlight over the public line: a change outside the marks that nothing else of the check sees.
        var hooks = Hooks(built: clean => clean.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(18, 88, 120, 104)], AnnotationTestFiles.Yellow, null, null, Ct));
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        var finding = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.PageChangedOutsideAreas);
        Assert.Equal(0, finding.Page);
        Assert.Contains("looks different outside the marked areas", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));
    }

    /// <summary>A one-page file that still has one document-level item the cleaning rebuild is meant to remove.</summary>
    private string LeftoverFile(string item)
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (left over) Tj ET");
        switch (item)
        {
            case "info":
                file.TrailerEntries = $"/Info {file.Add("<< /Author (zzauthorzz) >>")} 0 R";
                break;
            case "bookmark":
                var bookmark = file.Add("<< /Title (zzbookmarkzz) /Parent 7 0 R >>");
                var outlines = file.Add($"<< /Type /Outlines /First {bookmark} 0 R /Last {bookmark} 0 R /Count 1 >>");
                file.CatalogEntries = $"/Outlines {outlines} 0 R";
                break;
            case "attachment":
                var spec = file.Add("<< /Type /Filespec /F (zzattachmentzz.txt) >>");
                file.CatalogEntries = $"/Names << /EmbeddedFiles << /Names [(zzattachmentzz.txt) {spec} 0 R] >> >>";
                break;
            default:
                file.CatalogEntries = "/PageLabels << /Nums [0 << /S /D >>] >>";
                break;
        }

        return Write($"leftover-{item}.pdf", file.Build());
    }

    private async Task<IReadOnlyList<RedactionFinding>> ReadBackAsync(string path)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var stream = File.OpenRead(path);
        var scan = PageKeyScanner.Scan(stream, document.PageCount, allowCrossReferenceStream: false, cancellationToken: Ct);
        Assert.True(scan.WasChecked, scan.NotChecked);
        return await CleanupReadBack.ReadAsync(document, scan, Ct);
    }

    [Theory]
    [InlineData("info", "Author entry in the information dictionary", "/Info")]
    [InlineData("bookmark", "still has bookmarks", "/Outlines")]
    [InlineData("attachment", "/Names at the document level", "attached files")]
    [InlineData("labels", "still has page labels", "/PageLabels")]
    public async Task TheReadBackFindsEveryItemTheRebuildShouldHaveRemovedAndNamesNeverItsContent(string item, string expected, string alsoExpected)
    {
        var findings = await ReadBackAsync(LeftoverFile(item));

        Assert.NotEmpty(findings);
        Assert.All(findings, static f => Assert.Equal(RedactionFindingKind.CleanupItemStillPresent, f.Kind));
        Assert.Contains(findings, f => f.Detail.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Detail.Contains(alsoExpected, StringComparison.Ordinal));
        Assert.DoesNotContain(findings, static f => f.Detail.Contains("zz", StringComparison.Ordinal));
        Assert.All(findings, static f => Assert.Equal(RedactionFailureKind.CleanupItemStillPresent, RedactionRefusalText.KindOf(f)));
    }

    [Fact]
    public async Task TheReadBackOfARebuiltFileFindsNothingForAnyFixtureAndTheOriginalHasEverything()
    {
        // The rebuilt result of the files with an outline, attachments, a form and annotations has none of it any more.
        foreach (var name in new[] { "redact-everywhere.pdf", "outline.pdf", "forms-basic.pdf", "text-rich.pdf", "annotations-foreign.pdf" })
        {
            var source = Copy(name);
            await using var original = await engine.OpenAsync(source, cancellationToken: Ct);
            var executor = new RedactionExecutor(engine);
            await using var build = await executor.BuildAsync(WithArea(RedactionPlan.Empty, 0, new PageRect(1, 1, 3, 3)), original, source, null, null, Ct);
            var target = Target("out-" + name);
            await using (var output = File.Create(target))
            {
                await build.Clean.SaveRewriteAsync(output, Ct);
            }

            Assert.Empty(await ReadBackAsync(target));
        }

        // And the original of the fixture is full of it, so the read is not blind.
        var everything = await ReadBackAsync(Copy("redact-everywhere.pdf", "again.pdf"));
        var details = string.Join('\n', everything.Select(static f => f.Detail));
        foreach (var part in new[] { "Author entry", "Subject entry", "bookmarks", "/Names", "/Outlines", "/Metadata", "/AcroForm", "/Info" })
        {
            Assert.Contains(part, details, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("info")]
    [InlineData("bookmark")]
    [InlineData("attachment")]
    [InlineData("labels")]
    public async Task AnItemTheCleanupShouldHaveRemovedFailsTheApplyAndWritesNothing(string item)
    {
        // The read-back is made to see a file that still has the item (as if a PDFium update carried it through the import).
        var path = SecretFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        var leftover = LeftoverFile(item);
        await using var leftoverDocument = await engine.OpenAsync(leftover, cancellationToken: Ct);
        await using var leftoverStream = File.OpenRead(leftover);
        var leftoverScan = PageKeyScanner.Scan(leftoverStream, leftoverDocument.PageCount, allowCrossReferenceStream: false, cancellationToken: Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await SecretPlanAsync(session.Document!, everywhere: true);
        var hooks = new RedactionApplyHooks { CleanupReadBack = (_, _, token) => CleanupReadBack.ReadAsync(leftoverDocument, leftoverScan, token) };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        Assert.NotEmpty(check.Findings);
        Assert.All(check.Findings, static f => Assert.Equal(RedactionFindingKind.CleanupItemStillPresent, f.Kind));
        var text = RedactionRefusalText.OfFailure(failure);
        Assert.Contains("The cleanup should have removed it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zz", text, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "secret.pdf", Path.GetFileName(leftover)));
    }

    [Fact]
    public async Task ACleanResultHasNoCleanupFindingForFilesWithAnOutlineAndAnnotations()
    {
        foreach (var name in new[] { "outline.pdf", "text-rich.pdf" })
        {
            var path = Copy(name);
            using var session = new ViewerSession(engine);
            await session.OpenAsync(path);
            var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(1, 1, 4, 4));

            var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target("out-" + name)), null, Ct);

            Assert.True(result.Check.IsClean, name);
            Assert.DoesNotContain(result.Check.Findings, static f => f.Kind == RedactionFindingKind.CleanupItemStillPresent);
            Assert.Empty(await session.Document!.GetOutlineAsync(Ct));
        }
    }

    [Fact]
    public async Task PageKeysInTheFinalOutputFailTheCheck()
    {
        var path = ThumbFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 35, 60, 60));

        // The early scan found nothing (here: it did not run); the scan of the written file decides.
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Hooks(skipScan: true), Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        var finding = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.PageKeysStillPresent);
        Assert.Equal(1, finding.Page);
        Assert.Contains("/Thumb", finding.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "thumb.pdf"));
    }

    [Fact]
    public async Task UnremovableContentFailsTheApplyViaTheRawCheck()
    {
        // A mark over the pattern fill removes the path that paints it, so the page passes the render check; the pattern itself is a
        // resource without a page object, which PDFium keeps in the file with the term in it.
        var path = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var plan = await WithTermAsync(RedactionPlan.Empty, shown, Term, everywhere: true);
        plan = WithArea(plan, 0, new PageRect(20, 120, 220, 150));
        plan = WithArea(plan, 0, new PageRect(248, 40, 290, 62));
        plan = WithArea(plan, 0, new PageRect(20, 275, 200, 300));

        // Not part of IsRefused: nothing in the analysis knows about it.
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        var finding = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.Equal(RedactionFailureKind.UnremovableContent, RedactionRefusalText.KindOf(finding));
        Assert.Contains(0, RedactionRefusalText.PagesOf(finding));
        var text = RedactionRefusalText.Of(finding);
        Assert.Contains("not a page object", text, StringComparison.Ordinal);
        Assert.Contains("page", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", text, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Empty(LeftOver(folder.Path, "redact-everywhere.pdf"));
    }

    // ------------------------------------------------------------------ progress, cancellation, adoption

    [Fact]
    public async Task ProgressNeverGoesBack()
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = await SecretPlanAsync(session.Document!, everywhere: true);
        var progress = new Recorder();

        await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), progress, Ct);

        var values = progress.Values;
        Assert.NotEmpty(values);
        Assert.Equal(values.Order(), values);
        Assert.All(values, static v => Assert.InRange(v, 0, 1));
        Assert.Equal(1, values[^1]);
        Assert.True(values.Count > 3);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.55)]
    [InlineData(0.9)]
    public async Task CancelLeavesNoTemporaryFile(double cancelAt)
    {
        var path = SecretFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = await SecretPlanAsync(shown!, everywhere: true);
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder { OnReport = value => { if (value >= cancelAt) { cancel.Cancel(); } } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), progress, null, cancel.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Target()));
        Assert.Same(shown, session.Document);
        Assert.Empty(LeftOver(folder.Path, "secret.pdf"));
    }

    [Fact]
    public async Task AdoptionGivesTheTabTheRedactedFileAtTheTarget()
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var oldCache = session.Cache;
        var plan = await SecretPlanAsync(shown, everywhere: true);
        var target = Target();
        PdfDocument? announced = null;

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target, Rewritten: document => announced = document), null, Ct);

        Assert.True(result.Adopted);
        Assert.Same(result.Saved.Reopened, announced);
        Assert.Same(result.Saved.Reopened, session.Document);
        Assert.NotSame(shown, session.Document);
        Assert.NotSame(oldCache, session.Cache);
        Assert.Equal(Path.GetFullPath(target), session.Path);
        Assert.DoesNotContain("secret", await TextOf(session.Document!, 0), StringComparison.Ordinal);
        Assert.Contains("public line", await TextOf(session.Document!, 0), StringComparison.Ordinal);
        Assert.False(RawPdf.ContainsAnywhere(await File.ReadAllBytesAsync(target, Ct), "secret value"));
    }

    [Fact]
    public async Task WriteWithoutASessionLeavesTheResultToTheCaller()
    {
        var path = SecretFile();
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = await SecretPlanAsync(shown, everywhere: true);

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target(), ReplaceExisting: false), null, Ct);

        await using var reopened = result.Saved.Reopened;
        Assert.False(result.Adopted);
        Assert.True(result.Check.IsClean);
        Assert.DoesNotContain("secret", await TextOf(reopened, 0), StringComparison.Ordinal);

        // Without --force nothing is replaced: the file that stands there now stays.
        var again = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target(), ReplaceExisting: false), null, Ct));
        Assert.Equal(SaveFailureReason.TargetExists, again.Reason);
    }

    // ------------------------------------------------------------------ the texts

    [Fact]
    public void RefusalTextIsSharedByCardAndCli()
    {
        var texts = new Dictionary<RedactionRefusalKind, string>();
        foreach (var kind in Enum.GetValues<RedactionRefusalKind>())
        {
            var refusal = new RedactionRefusal(kind, kind == RedactionRefusalKind.Encrypted ? [] : [2, 4], "the engine's reason");
            var text = RedactionRefusalText.Of(refusal);
            Assert.False(string.IsNullOrWhiteSpace(text), kind.ToString());
            Assert.DoesNotMatch(new Regex("[äöüß]", RegexOptions.None, TimeSpan.FromSeconds(1)), text);
            texts[kind] = text;
        }

        Assert.Equal(texts.Count, texts.Values.Distinct().Count());
        Assert.Contains("encrypted", texts[RedactionRefusalKind.Encrypted], StringComparison.Ordinal);
        Assert.Contains("pages 3, 5", texts[RedactionRefusalKind.UnremovablePageKeys], StringComparison.Ordinal);
        Assert.Contains("pages 3, 5", texts[RedactionRefusalKind.PageNotRedactable], StringComparison.Ordinal);
        Assert.Contains("pages 3, 5", texts[RedactionRefusalKind.OverlayRepeatsTerm], StringComparison.Ordinal);

        // "Could not be checked" is told by its flag and has its own text, no pages.
        var unverified = new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [], PageKeyScanner.UnverifiedReason, PageKeysUnverified: true);
        Assert.Contains("could not be checked", RedactionRefusalText.Of(unverified), StringComparison.Ordinal);
        Assert.NotEqual(texts[RedactionRefusalKind.UnremovablePageKeys], RedactionRefusalText.Of(unverified));

        // The exception the card and the command line catch carries exactly this text.
        var refusals = new[] { new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "x"), unverified };
        Assert.Equal(RedactionRefusalText.Of(refusals), new RedactionRefusedException(refusals).Message);
        Assert.Equal(2, RedactionRefusalText.Of(refusals).Split('\n').Length);
    }

    [Fact]
    public void EveryFindingHasATextAndAKindAndNeverQuotesTheRemovedText()
    {
        foreach (var kind in Enum.GetValues<RedactionFindingKind>())
        {
            var finding = new RedactionFinding(kind, kind == RedactionFindingKind.RawBytesStillPresent ? null : 3, kind == RedactionFindingKind.CleanupItemStillPresent ? "The written file still has bookmarks." : "Removed text #1 (6 characters)", kind == RedactionFindingKind.RawBytesStillPresent ? [0, 2] : null);
            var text = RedactionRefusalText.Of(finding);
            Assert.False(string.IsNullOrWhiteSpace(text), kind.ToString());
            Assert.DoesNotContain("#1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("valid", text.Replace("invalid", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        var raw = new RedactionFinding(RedactionFindingKind.RawBytesStillPresent, null, "d", [0, 2]);
        Assert.Equal(RedactionFailureKind.UnremovableContent, RedactionRefusalText.KindOf(raw));
        Assert.Equal([0, 2], RedactionRefusalText.PagesOf(raw));
        Assert.Contains("removed from pages 1, 3", RedactionRefusalText.Of(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("(page", RedactionRefusalText.Of(raw), StringComparison.Ordinal);
        Assert.Equal(RedactionRefusalText.CheckNotCompleted, RedactionRefusalText.Of((RedactionCheck?)null));
        Assert.Equal(RedactionRefusalText.CheckNotCompleted, RedactionRefusalText.Of(new RedactionCheck([], [])));
    }

    [Fact]
    public async Task ReportListsRemovedCleanedLostAndNotChecked()
    {
        var original = NoPatternCopy();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(original);
        var shown = session.Document!;
        var plan = await WithTermAsync(RedactionPlan.Empty, shown, Term, everywhere: true);
        plan = WithArea(plan, 0, new PageRect(248, 40, 290, 62));
        plan = WithArea(plan, 0, new PageRect(20, 275, 200, 300));
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, original, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));

        // Before applying: removed, cleaned, lost, not cleaned, and what will be checked.
        var before = RedactionReportText.Of(analysis);
        foreach (var heading in new[] { "Removed:", "Will be cleaned (always, with every redaction):", "Lost:", "Not cleaned:", "Will be checked after writing:" })
        {
            Assert.Contains(heading, before, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Checked:", before.Replace("Will be checked after writing:", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Page 1:", before, StringComparison.Ordinal);
        Assert.Contains("Author in the information dictionary: Quellmann", before, StringComparison.Ordinal);
        Assert.Contains("XMP metadata: up to 1", before, StringComparison.Ordinal);
        Assert.Contains("form field is no longer reliably fillable", before, StringComparison.Ordinal);
        Assert.Contains("/Metadata, /PieceInfo", before, StringComparison.Ordinal);
        Assert.Contains("(/V, /DV)", before, StringComparison.Ordinal);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);
        var report = RedactionReportText.Of(analysis, result.Check);

        foreach (var heading in new[] { "Removed:", "Cleaned (read back from the written file):", "Lost:", "Not cleaned:", "Checked:", "Not checked:" })
        {
            Assert.Contains(heading, report, StringComparison.Ordinal);
        }

        // The not-checked lines are the check's own, none dropped, and the ones of the apply step.
        foreach (var line in result.Check.NotChecked)
        {
            Assert.Contains(line, report, StringComparison.Ordinal);
        }

        Assert.Contains("Render comparison", report, StringComparison.Ordinal);

        // Under "Cleaned" only what the read-back of the written file confirmed; XMP and scripts (page-level copies stay) are told
        // under "Not checked", and the "Checked" lines name the read-back.
        var cleaned = report[report.IndexOf("Cleaned (read back", StringComparison.Ordinal)..report.IndexOf("Lost:", StringComparison.Ordinal)];
        Assert.Contains("Author in the information dictionary", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("XMP metadata", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("JavaScript", cleaned, StringComparison.Ordinal);
        var notChecked = report[report.IndexOf("Not checked:", StringComparison.Ordinal)..];
        Assert.Contains("XMP metadata and JavaScript: removed from the document level by building the file anew; the entries inside page-level objects", notChecked, StringComparison.Ordinal);
        Assert.Contains("Cleanup: the information dictionary, bookmarks", report[report.IndexOf("Checked:", StringComparison.Ordinal)..], StringComparison.Ordinal);

        // The cleaned list says what was in the file (that is its point); nothing after it quotes a removed text.
        Assert.DoesNotContain("Zephyr", report[report.IndexOf("Lost:", StringComparison.Ordinal)..], StringComparison.Ordinal);

        // The checked lines only appear for a check without findings.
        var failed = new RedactionCheck([new RedactionFinding(RedactionFindingKind.TextStillPresent, 0, "d")], ["x"]);
        var failedReport = RedactionReportText.Of(analysis, failed);
        Assert.DoesNotContain("\nChecked:", "\n" + failedReport, StringComparison.Ordinal);
        Assert.DoesNotContain("Cleaned", failedReport, StringComparison.Ordinal);
        Assert.Contains("Not checked:", failedReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportNamesTheCopiesTheFileBytesCannotTellFromALeak()
    {
        // A text that is not marked everywhere and still occurs on the page: the file bytes cannot tell a leaked copy from it.
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (secret value) Tj ET");
        var path = Write("twice.pdf", file.Build());
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var text = await shown.GetPageTextAsync(0, Ct);
        var first = TextMatcher.Find(text.Chars, "secret value", new SearchOptions(MatchCase: true))[0];
        var area = Union(text.Chars.Skip(first.FirstChar).Take(first.LastChar - first.FirstChar + 1).Where(static c => !c.IsGenerated).Select(static c => c.Box));
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 0, [area], "secret value", Everywhere: false));
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);

        Assert.True(result.Check.IsClean);
        var report = RedactionReportText.Of(analysis, result.Check);
        Assert.Contains("leaked copies could hide", report, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"up to 1 leaked copies", RegexOptions.None, TimeSpan.FromSeconds(1)), report);
        Assert.DoesNotContain("secret", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMarkWithoutATermIsNamedInTheNotCheckedLines()
    {
        var path = SecretFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 35, 150, 60));
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, null, Ct);
        Assert.Equal(1, analysis.MarksWithoutTerm);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);

        var report = RedactionReportText.Of(analysis, result.Check);
        Assert.Contains("mark has no term", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Page text: every removed text", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", await TextOf(session.Document!, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureLossSaysItNoLongerVerifiesAndItsDataStaysInert()
    {
        var path = Copy("forms-signed.pdf");
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = WithArea(RedactionPlan.Empty, 0, new PageRect(10, 10, 60, 30));

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        var loss = Assert.Single(analysis.Losses, static l => l.Kind == RedactionLossKind.SignatureInvalidated);
        var report = RedactionReportText.Of(analysis);
        Assert.Contains("no longer verif", report, StringComparison.Ordinal);
        Assert.Contains("inert", report, StringComparison.Ordinal);
        Assert.Contains("The data of a digital signature (certificate name, contents) stays in the file as inert data.", report, StringComparison.Ordinal);
        Assert.True(loss.Count >= 1);
        Assert.DoesNotMatch(new Regex(@"\bvalid\b|legally|binding|rechtsg", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), report);
        Assert.DoesNotContain("shown as invalid", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithoutRedactionDetailsIsAFailureWithoutDetailsNeverAPass()
    {
        var withCheck = new SaveFailureException(SaveFailureReason.RedactionFailed, "m") { Details = new RedactionCheck([new RedactionFinding(RedactionFindingKind.CharInsideArea, 1, "d")], []) };
        var otherDetails = new SaveFailureException(SaveFailureReason.RedactionFailed, "m") { Details = new object() };
        var noDetails = new SaveFailureException(SaveFailureReason.RedactionFailed, "m");
        var otherReason = new SaveFailureException(SaveFailureReason.DiskFull, "the disk is full");

        Assert.Contains("inside a marked area", RedactionRefusalText.OfFailure(withCheck), StringComparison.Ordinal);
        Assert.Equal(RedactionRefusalText.CheckNotCompleted, RedactionRefusalText.OfFailure(otherDetails));
        Assert.Equal(RedactionRefusalText.CheckNotCompleted, RedactionRefusalText.OfFailure(noDetails));
        Assert.Equal("the disk is full", RedactionRefusalText.OfFailure(otherReason));
    }

    [Fact]
    public async Task AnUnreadablePageOfTheOriginalMakesTheCheckFailInsteadOfPass()
    {
        // The count of the copies the document had needs every page; a page that cannot be read leaves the number unknown.
        var bytes = new VerifierPdf("BT /F1 12 Tf 20 150 Td (secret value) Tj ET", "BT /F1 12 Tf 20 150 Td (second) Tj ET").Build();
        var text = Encoding.Latin1.GetString(bytes);
        var broken = Write("broken.pdf", Encoding.Latin1.GetBytes(text.Replace("/Kids [4 0 R 6 0 R]", "/Kids [4 0 R 9 0 R]", StringComparison.Ordinal)));
        await using var document = await engine.OpenAsync(broken, cancellationToken: Ct);
        Assert.Equal(2, document.PageCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => document.GetPageTextAsync(1, Ct));
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 0, [new PageRect(15, 35, 100, 60)], "secret value", Everywhere: false));

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionExecutor.ExpectationAsync(plan, document, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        Assert.Equal(RedactionFindingKind.UnreadableStream, Assert.Single(check.Findings).Kind);
        Assert.DoesNotContain("secret", RedactionRefusalText.OfFailure(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyPlanIsNothingToApplyAndAMarkBeyondTheDocumentIsRefused()
    {
        var path = SecretFile();
        var executor = new RedactionExecutor(engine);

        await Assert.ThrowsAsync<ArgumentException>(() => executor.BuildAsync(RedactionPlan.Empty, path, null, null, Ct));
        await Assert.ThrowsAsync<RedactionException>(() => executor.AnalyzeAsync(WithArea(RedactionPlan.Empty, 5, new PageRect(1, 1, 5, 5)), path, null, Ct));
        await Assert.ThrowsAsync<RedactionException>(() => executor.BuildAsync(WithArea(RedactionPlan.Empty, 5, new PageRect(1, 1, 5, 5)), path, null, null, Ct));
    }
}
