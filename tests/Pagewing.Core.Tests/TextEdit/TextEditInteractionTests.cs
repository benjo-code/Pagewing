using System.Diagnostics;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Cli;
using Pagewing.Core.Tests.Redaction;
using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>
/// An edited line next to the other features (milestone 16, task 7): redacting it, organizing the pages, the text layer of a scan, text boxes, a signed file,
/// two quick edits, the dark page, and the hostile file.
/// </summary>
public sealed class TextEditInteractionTests : CliTestBase
{
    private async Task<ViewerSession> OpenAsync(string path)
    {
        var session = new ViewerSession(Engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }

    private static async Task<TextRun> RunAsync(ViewerSession session, int page, string text) =>
        (await session.Document!.GetTextRunsAsync(page, Ct)).Single(r => r.Text.TrimEnd() == text);

    private static async Task<string> TextOfAsync(PdfDocument document, int page)
    {
        var text = await document.GetPageTextAsync(page, Ct);
        return text.GetText(0, text.Chars.Count);
    }

    [Fact]
    public async Task RedactingEditedTextFindsItAndTheCheckPasses()
    {
        var path = Copy("text-edit-standard.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Quarterly report 2026");
        var edited = await editor.ApplyTextEditAsync(run, "Quarterly report 2027", TextEditMode.KeepFont);
        await editor.SaveAsync(Engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        var box = edited!.After!.Bounds;
        var area = new PageRect(box.Left - 1, box.Top - 1, box.Right + 1, box.Bottom + 1);
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [area], null, false));
        var target = Path.Combine(WorkDirectory.FullName, "redacted.pdf");

        var result = await RedactionApplier.ApplyAsync(session, Engine, new RedactionApplyRequest(plan, target), null, Ct);

        // The edit is ordinary text: the redaction finds it, and the written file has neither the new nor the old text (the old one is in no revision).
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Quarterly"));
        await using var document = await Engine.OpenAsync(target, cancellationToken: Ct);
        var all = await TextOfAsync(document, 0);
        Assert.DoesNotContain("2027", all, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", all, StringComparison.Ordinal);
        Assert.Contains("Dear customer", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganizingPagesAfterAnEditKeepsTheNewText()
    {
        var path = Copy("text-edit-locked.pdf");
        using (var session = await OpenAsync(path))
        {
            var editor = new AnnotationEditor(session);
            var run = await RunAsync(session, 1, "Editable on page two");
            await editor.ApplyTextEditAsync(run, "Edited on page two", TextEditMode.KeepFont);
            await editor.SaveAsync(Engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        }

        var output = Path.Combine(WorkDirectory.FullName, "turned.pdf");
        var result = await RunAsync("rotate", path, "--pages", "2", "--angle", "90", "--output", output);

        Assert.Equal(0, result.ExitCode);
        await using var document = await Engine.OpenAsync(output, cancellationToken: Ct);
        var all = await TextOfAsync(document, 1);
        Assert.Contains("Edited on page two", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Editable on page two", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OcrLayerIsNotOfferedForEditing()
    {
        var path = Copy("ocr-scan.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var words = OcrFixtures.WordsOf("ocr-scan.pdf", 0).Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, 90)).ToArray();
        await editor.ApplyOcrAsync(new OcrRunResult([new OcrPageResult(0, words, 200, false, 90)], OcrReport.Build([], [], [])), replace: false);

        var runs = await session.Document!.GetTextRunsAsync(0, Ct);

        Assert.NotEmpty(runs);
        Assert.All(runs, static r => Assert.Equal(TextEditLockReason.InvisibleText, r.Lock));
        await Assert.ThrowsAsync<TextRunLockedException>(() => editor.ApplyTextEditAsync(runs[0], "Changed", TextEditMode.KeepFont));
        Assert.False(editor.HasTextEdits);
    }

    [Fact]
    public async Task TextBoxAnnotationsAreNotRuns()
    {
        var path = Copy("text-edit-standard.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var created = await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 100, 200, 130), "Words in a box", 12, new AnnotationColor(0, 0, 0)), null);
        Assert.NotNull(created);

        var runs = await session.Document!.GetTextRunsAsync(0, Ct);

        Assert.DoesNotContain(runs, static r => r.Text.Contains("Words in a box", StringComparison.Ordinal));
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public async Task SignedDocumentSavesIncrementallyAndKeepsItsPrefix()
    {
        var path = Copy("signed-docmdp-3.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var runs = await session.Document!.GetTextRunsAsync(0, Ct);
        var run = runs.First(static r => !r.IsLocked && r.Text.Trim().Length > 2);
        await editor.ApplyTextEditAsync(run, run.Text.TrimEnd() + "!", TextEditMode.KeepFont);

        await editor.SaveAsync(Engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        var saved = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the signed bytes are not the beginning of the saved file");
    }

    [Fact]
    public async Task TwoQuickEditsOfTheSameRunSecondIsStaleNotWrong()
    {
        var path = Copy("text-edit-subset.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Invoice total: 1,250 EUR");

        // Both calls are queued before either runs; the editor runs them one after the other, the second finds another page than it read.
        var first = editor.ApplyTextEditAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont);
        var second = editor.ApplyTextEditAsync(run, "Invoice total: 1,999 EUR", TextEditMode.KeepFont);

        Assert.NotNull(await first);
        await Assert.ThrowsAsync<TextRunStaleException>(() => second);
        var all = await TextOfAsync(session.Document!, 0);
        Assert.Contains("Invoice total: 1,205 EUR", all, StringComparison.Ordinal);
        Assert.DoesNotContain("1,999", all, StringComparison.Ordinal);
        await editor.UndoAsync();
        Assert.False(editor.CanUndo);
        Assert.Contains("Invoice total: 1,250 EUR", await TextOfAsync(session.Document!, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingInADarkPageRendersLikeTheOriginalFont()
    {
        var path = Copy("text-edit-standard.pdf");
        await using var original = await Engine.OpenAsync(TestData.Pdf("text-edit-standard.pdf"), cancellationToken: Ct);
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Dear customer, thank you");
        var edited = await editor.ApplyTextEditAsync(run, "Dear customer, thanks a lot", TextEditMode.KeepFont);
        var options = new RenderOptions { PageTheme = PageTheme.Dark };

        var before = await original.RenderPageAsync(0, 1, options, Ct);
        var after = await session.Document!.RenderPageAsync(0, 1, options, Ct);

        // Outside the edited line the dark page is the same; inside it the new text is drawn (not the paper colour) in the same font.
        var changed = edited!.BoundsBefore.Union(edited.BoundsAfter).Inflate(3);
        var different = 0;
        var inkInside = 0;
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                var i = (y * before.Stride) + (x * 4);
                if (changed.Contains(x, y))
                {
                    if (after.Pixels.Span[i] != after.Pixels.Span[0])
                    {
                        inkInside++;
                    }

                    continue;
                }

                if (!before.Pixels.Span.Slice(i, 4).SequenceEqual(after.Pixels.Span.Slice(i, 4)))
                {
                    different++;
                }
            }
        }

        Assert.Equal(0, different);
        Assert.True(inkInside > 30, $"{inkInside} pixels of ink in the edited line");
        Assert.Equal(run.FontName, edited.After!.FontName);
    }

    [Fact]
    public async Task HostileFixtureEndsWithLocksAndBoundedTime()
    {
        await using var document = await Engine.OpenAsync(TestData.Pdf("text-edit-hostile.pdf"), cancellationToken: Ct);
        var clock = Stopwatch.StartNew();

        var grid = await document.GetTextRunsAsync(0, Ct);
        var line = await document.GetTextRunsAsync(1, Ct);

        // Locked with the reason instead of cut off, and quick enough to be read in one go (the limit is generous: the figures are in the measurements).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"reading took {clock.Elapsed}");
        Assert.Equal(TextEditFixtures.HostileRunCount, grid.Count);
        Assert.All(grid, static r => Assert.True(r.IsLocked));
        Assert.Equal(TextEditLockReason.RunTooLong, line.Single().Lock);
        await Assert.ThrowsAsync<TextRunLockedException>(() => document.ReplaceTextRunAsync(grid[0], "y", TextEditMode.KeepFont, Ct));
        await Assert.ThrowsAsync<TextRunLockedException>(() => document.ReplaceTextRunAsync(line[0], "short", TextEditMode.KeepFont, Ct));
    }
}
