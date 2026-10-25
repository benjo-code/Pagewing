using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The view model of the "Comments" panel (milestone 9, task 15): what it lists, in which order, how it filters and sums
/// up, when it reads and how it follows the editor and a save. Without a window: the tab is driven as the document view
/// drives it. Every file is a copy in a temp folder.
/// </summary>
// Strings has a process-wide culture (see LocalizationTests).
[Collection("Localization")]
public sealed class CommentsViewModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-comments-" + Guid.NewGuid().ToString("N"))).FullName;

    public CommentsViewModelTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task ListsAnnotationsOfAllPagesSortedByPageAndPosition()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);

        // Page 1 from top to bottom (the link is no comment), then page 2.
        Assert.Equal([0, 0, 0, 0, 0, 1, 1], comments.Items.Select(item => item.PageIndex));
        Assert.Equal(["Highlight", "Highlight", "Ink", "FreeText", "Text", "Underline", "StrikeOut"], comments.Items.Select(item => item.Annotation.Subtype));
        Assert.Equal(["hl-0001", null, "ink-0001", "ft-0001", "note-0001", "ul-0001", "so-0001"], comments.Items.Select(item => item.Annotation.Name));

        // What an entry shows: page mark, kind in words, author, date, the text under it and the comment.
        var first = comments.Items[0];
        Assert.Equal("1", first.PageText);
        Assert.Equal(Strings.CommentKindHighlight, first.KindText);
        Assert.True(first.IsHighlight);
        Assert.Equal("Alice", first.Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("d", Strings.Culture), first.DateText);
        Assert.Contains("sentence", first.Excerpt, StringComparison.Ordinal);
        Assert.Equal("Check this sentence", first.Comment);
        Assert.True(first.HasColor);
        Assert.Equal("Colour #FFE600", first.ColorName);
        var ink = comments.Items[2];
        Assert.False(ink.IsOther);
        Assert.True(ink.IsDrawing);
        Assert.Equal(Strings.ToolInk, ink.KindText);
        Assert.Null(ink.Excerpt);
        Assert.Equal(Strings.CommentKindUnderline, comments.Items[5].KindText);
        Assert.Equal(Strings.CommentKindStrikeOut, comments.Items[6].KindText);

        // The order is that of the position on the page, not that of the page's list of annotations.
        var reversed = await OpenAsync("annotations-foreign.pdf", "reversed.pdf", tab => tab.AnnotationReader = async (document, page, token) =>
            [.. (await document.GetAnnotationsAsync(page, token)).Reverse()]);
        var reversedComments = await OpenCommentsAsync(reversed);
        Assert.Equal(comments.Items.Select(item => item.Annotation.Name), reversedComments.Items.Select(item => item.Annotation.Name));
    }

    [AvaloniaFact]
    public async Task FilterByKindShowsOnlyThatKind()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);
        Assert.Equal(CommentFilter.All, comments.Filter);
        Assert.Equal(7, comments.Items.Count);

        comments.Filter = CommentFilter.Highlight;
        Assert.Equal(["hl-0001", null], comments.Items.Select(item => item.Annotation.Name));
        Assert.Equal("2 comments, all on page 1", comments.Summary);
        comments.Filter = CommentFilter.Underline;
        Assert.Equal(["ul-0001"], comments.Items.Select(item => item.Annotation.Name));
        Assert.Equal("1 comment on page 2", comments.Summary);
        comments.Filter = CommentFilter.StrikeOut;
        Assert.Equal(["so-0001"], comments.Items.Select(item => item.Annotation.Name));
        comments.Filter = CommentFilter.Other;
        Assert.Equal(["ft-0001", "note-0001"], comments.Items.Select(item => item.Annotation.Name));
        comments.Filter = CommentFilter.Drawings;
        Assert.Equal(["ink-0001"], comments.Items.Select(item => item.Annotation.Name));
        comments.Filter = CommentFilter.Other;

        // The choices of the list are the filters, in this order, and choosing one sets the filter.
        Assert.Equal([CommentFilter.All, CommentFilter.Highlight, CommentFilter.Underline, CommentFilter.StrikeOut, CommentFilter.Drawings, CommentFilter.Other], comments.FilterChoices.Select(choice => choice.Value));
        Assert.Equal(CommentFilter.Other, comments.SelectedFilterChoice!.Value);
        comments.SelectedFilterChoice = comments.FilterChoices[0];
        Assert.Equal(CommentFilter.All, comments.Filter);
        Assert.Equal(7, comments.Items.Count);

        // Without a comment of the kind: "No comments", but the document is not empty (no hint).
        var basic = await OpenCommentsAsync(await OpenAsync("annotations.pdf"));
        basic.Filter = CommentFilter.StrikeOut;
        Assert.Empty(basic.Items);
        Assert.Equal(Strings.CommentsNone, basic.Summary);
        Assert.False(basic.HasNoComments);
    }

    [AvaloniaFact]
    public async Task PopupsAndRepliesAreNotListed()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);

        Assert.DoesNotContain(comments.Items, item => item.Annotation.Subtype is "Popup" or "Link");
        Assert.DoesNotContain(comments.Items, item => item.Annotation.Name == "reply-0001");
        Assert.Single(comments.Items, item => item.Annotation.Subtype == "Text");
    }

    [AvaloniaTheory]
    [InlineData("en", "7 comments on 2 pages", "Loading …", "No comments")]
    [InlineData("de", "7 Kommentare auf 2 Seiten", "Lädt …", "Keine Kommentare")]
    public async Task SummaryCountsCommentsAndPages(string language, string summary, string loading, string none)
    {
        Strings.Culture = new CultureInfo(language);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = async (document, page, token) =>
        {
            await gate.Task.WaitAsync(token);
            return await document.GetAnnotationsAsync(page, token);
        });
        tab.SidePanel = SidePanelKind.Comments;
        var comments = tab.Comments;

        // While it reads: "Loading …".
        Pump();
        Assert.True(comments.IsLoading);
        Assert.Equal(loading, comments.Summary);
        gate.SetResult();
        await WaitUntilAsync(() => !comments.IsLoading);
        Assert.Equal(summary, comments.Summary);

        // A document without annotations: "No comments" and the hint.
        var empty = await OpenCommentsAsync(await OpenAsync("text-basic.pdf"));
        Assert.Equal(none, empty.Summary);
        Assert.True(empty.HasNoComments);
        Assert.Empty(empty.Items);

        // One page: the page is named.
        comments.Filter = CommentFilter.Other;
        Assert.Equal(language == "en" ? "2 comments, all on page 1" : "2 Kommentare, alle auf Seite 1", comments.Summary);
        comments.Filter = CommentFilter.Underline;
        Assert.Equal(language == "en" ? "1 comment on page 2" : "1 Kommentar auf Seite 2", comments.Summary);
    }

    [AvaloniaFact]
    public async Task ListIsNotReadBeforeThePanelOpens()
    {
        var reads = 0;
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (document, page, token) =>
        {
            Interlocked.Increment(ref reads);
            return document.GetAnnotationsAsync(page, token);
        });

        // Open with its first sharp page shown, another panel open: nothing of the comments exists or was read.
        tab.SidePanel = SidePanelKind.Pages;
        Pump();
        Assert.False(tab.IsCommentsCreated);
        Assert.False(tab.HasEditor);
        Assert.Equal(0, Volatile.Read(ref reads));

        // Opened before the first sharp page of a document, it waits for that page.
        var early = await OpenAsync("annotations-foreign.pdf", "early.pdf", tab => tab.AnnotationReader = (document, page, token) =>
        {
            Interlocked.Increment(ref reads);
            return document.GetAnnotationsAsync(page, token);
        }, sharp: false);
        early.SidePanel = SidePanelKind.Comments;
        var waiting = early.Comments;
        Pump();
        Assert.True(waiting.IsLoading);
        Assert.Equal(0, Volatile.Read(ref reads));
        early.HasShownSharpPage = true;
        await WaitUntilAsync(() => !waiting.IsLoading);
        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(7, waiting.Items.Count);

        // The panel of the first tab reads once it opens.
        tab.SidePanel = SidePanelKind.Comments;
        var comments = tab.Comments;
        await WaitUntilAsync(() => !comments.IsLoading);
        Assert.Equal(4, Volatile.Read(ref reads));
    }

    [AvaloniaFact]
    public async Task AMarkupCreatedInTheViewAppearsInTheList()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);
        var kept = comments.Items[5];
        comments.JumpCommand.Execute(kept);
        Assert.True(kept.IsCurrent);

        // A strike-out at the bottom of page 1 (nothing there yet): only page 1 is read again.
        var created = await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.StrikeOut, [new PageRect(40, 250, 300, 270)], new AnnotationColor(255, 0, 0), "New", "Bob");
        Assert.NotNull(created);
        await WaitUntilAsync(() => comments.Items.Count == 8);
        Assert.Equal("New", comments.Items[5].Comment);
        Assert.Equal("Bob", comments.Items[5].Author);
        Assert.Equal(0, comments.Items[5].PageIndex);

        // The entries of the other page are the same objects: the list keeps their containers and the current entry.
        Assert.Same(kept, comments.Items[6]);
        Assert.True(kept.IsCurrent);
        Assert.Equal("8 comments on 2 pages", comments.Summary);

        // Undo takes it out again.
        await tab.Editor.UndoAsync();
        await WaitUntilAsync(() => comments.Items.Count == 7);
        Assert.Same(kept, comments.Items[5]);
    }

    [AvaloniaFact]
    public async Task AfterTheSaveTheListStillShowsTheAnnotations()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);
        await tab.Editor.ApplyMarkupAsync(1, AnnotationKind.Highlight, [new PageRect(40, 250, 300, 270)], new AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(() => comments.Items.Count == 8);
        var before = comments.Items.ToArray();
        var old = tab.Document!;

        var saved = await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken);
        Assert.True(saved.Adopted);
        await WaitUntilAsync(() => tab.Document is { } document && !ReferenceEquals(document, old));

        // The same view model, the same entries.
        Assert.Same(comments, tab.Comments);
        Assert.Equal(before, comments.Items);
        Assert.False(comments.IsLoading);

        // A change after the save is read from the saved document.
        await tab.Editor.ApplyMarkupAsync(1, AnnotationKind.Underline, [new PageRect(40, 200, 300, 220)], new AnnotationColor(0, 0, 255), "After", null);
        await WaitUntilAsync(() => comments.Items.Count == 9);
        Assert.Contains(comments.Items, item => item.Comment == "After");
    }

    [AvaloniaFact]
    public async Task ASaveDuringTheFirstReadDoesNotStopTheList()
    {
        // The session closes the old document before the tab learns of the saved one: a read of page 1 meets the closed
        // document while the tab still has it (simulated: the reader throws as the closed document would).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedOnce = 0;
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = async (document, page, token) =>
        {
            if (page == 0 && Interlocked.Exchange(ref closedOnce, 1) == 0)
            {
                await gate.Task.WaitAsync(token);
                throw new ObjectDisposedException(nameof(PdfDocument));
            }

            return await document.GetAnnotationsAsync(page, token);
        });
        await tab.Editor.ApplyMarkupAsync(1, AnnotationKind.Highlight, [new PageRect(40, 250, 300, 270)], new AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(() => tab.IsDirty);
        tab.SidePanel = SidePanelKind.Comments;
        var comments = tab.Comments;
        Pump();
        Assert.True(comments.IsReading);

        gate.SetResult();
        await WaitUntilAsync(() => !comments.IsReading);
        Assert.True(comments.IsLoading);

        // The save swaps the document: the reading goes on there, and every entry is listed (7 + the new highlight).
        var old = tab.Document!;
        var saved = await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken);
        Assert.True(saved.Adopted);
        await WaitUntilAsync(() => !ReferenceEquals(tab.Document, old) && !comments.IsLoading);
        Assert.Equal(8, comments.Items.Count);
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1], comments.Items.Select(item => item.PageIndex));
        Assert.Equal("8 comments on 2 pages", comments.Summary);
    }

    [AvaloniaFact]
    public async Task AnEditThatRacedTheSwapIsStillListed()
    {
        var closedOnce = 1;
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (document, page, token) =>
            page == 1 && Interlocked.Exchange(ref closedOnce, 1) == 0
                ? Task.FromException<IReadOnlyList<PdfAnnotation>>(new ObjectDisposedException(nameof(PdfDocument)))
                : document.GetAnnotationsAsync(page, token));
        var comments = await OpenCommentsAsync(tab);
        Assert.Equal(7, comments.Items.Count);

        // A change on page 2 is read again, but that read meets the old document the save closes (simulated) ...
        Volatile.Write(ref closedOnce, 0);
        await tab.Editor.ApplyMarkupAsync(1, AnnotationKind.Underline, [new PageRect(40, 250, 300, 270)], new AnnotationColor(0, 0, 255), "Raced", null);
        await WaitUntilAsync(() => Volatile.Read(ref closedOnce) == 1 && !comments.IsReading);
        Assert.Equal(7, comments.Items.Count);

        // ... and once the saved document is there, the page is read from it.
        var old = tab.Document!;
        await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => !ReferenceEquals(tab.Document, old) && comments.Items.Count == 8);
        Assert.Contains(comments.Items, item => item.Comment == "Raced");
    }

    [AvaloniaFact]
    public async Task AFailedReadAgainKeepsTheEntriesOfThePage()
    {
        var fail = 0;
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (document, page, token) =>
            page == 0 && Interlocked.Exchange(ref fail, 0) == 1
                ? Task.FromException<IReadOnlyList<PdfAnnotation>>(new InvalidDataException("The page is damaged."))
                : document.GetAnnotationsAsync(page, token));
        var comments = await OpenCommentsAsync(tab);
        var before = comments.Items.ToArray();

        Volatile.Write(ref fail, 1);
        comments.Invalidate(new HashSet<int> { 0 });
        await WaitUntilAsync(() => Volatile.Read(ref fail) == 0 && !comments.IsReading);

        Assert.Equal(before, comments.Items);
        Assert.Equal("7 comments on 2 pages", comments.Summary);
    }

    [AvaloniaFact]
    public async Task AnotherDocumentInTheTabDiscardsTheList()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);

        // Closing the panel keeps the list.
        tab.SidePanel = SidePanelKind.None;
        Assert.Same(comments, tab.Comments);

        var other = Copy("annotations.pdf");
        await tab.OpenAsync(other);
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.FullPath == other);
        tab.HasShownSharpPage = true;
        Assert.NotSame(comments, tab.Comments);
        Assert.Empty(comments.Items);
        var fresh = tab.Comments;
        await WaitUntilAsync(() => !fresh.IsLoading);
        Assert.All(fresh.Items, item => Assert.Equal(0, item.PageIndex));
        Assert.NotEmpty(fresh.Items);
    }

    [AvaloniaFact]
    public async Task JumpShowsTheAreaOfTheEntry()
    {
        var tab = await OpenAsync("annotations-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);
        var reveals = new List<(int Page, PageRect Rect)>();
        var picked = 0;
        tab.RevealRequested += (page, rect) => reveals.Add((page, rect));
        tab.PanelPagePicked += () => picked++;

        var underline = comments.Items[5];
        comments.JumpCommand.Execute(underline);
        var reveal = Assert.Single(reveals);
        Assert.Equal(1, reveal.Page);
        Assert.Equal(underline.Annotation.QuadRects[0], reveal.Rect);
        Assert.Equal(1, picked);
        Assert.True(underline.IsCurrent);
        Assert.False(comments.IsEditing);

        // Another entry becomes the current one; the old one is not current any more (an edge, not only a colour).
        comments.JumpCommand.Execute(comments.Items[0]);
        Assert.False(underline.IsCurrent);
        Assert.True(comments.Items[0].IsCurrent);
        Assert.Equal(2, reveals.Count);
    }

    [AvaloniaTheory]
    [InlineData("en", "Freehand", "Rectangle", "Oval", "Line", "Arrow")]
    [InlineData("de", "Freihand", "Rechteck", "Ellipse", "Linie", "Pfeil")]
    public async Task ListsDrawingsWithKind(string language, string ink, string rectangle, string ellipse, string line, string arrow)
    {
        Strings.Culture = new CultureInfo(language);
        var tab = await OpenAsync("text-basic.pdf");
        var red = DrawPalette.ColorOf(DrawColor.Red);

        // One drawing of each kind, from top to bottom, made as the drawing tools make them.
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Ink([[new DisplayPoint(40, 100), new DisplayPoint(80, 110), new DisplayPoint(120, 100)]], 2), red, null, "Bob");
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(40, 200, 140, 240), 2), red, "Box", "Bob");
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Ellipse(new PageRect(40, 300, 140, 340), 2), red, null, null);
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Line(new DisplayPoint(40, 400), new DisplayPoint(140, 420), 2), red, null, null);
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Arrow(new DisplayPoint(40, 500), new DisplayPoint(140, 520), 2), red, null, null);
        var comments = await OpenCommentsAsync(tab);

        // The kind in words (and by its symbol in the panel), never "Other"; author and comment as for any entry.
        Assert.Equal([ink, rectangle, ellipse, line, arrow], comments.Items.Select(item => item.KindText));
        Assert.Equal([AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow], comments.Items.Select(item => item.Kind));
        Assert.All(comments.Items, item => Assert.True(item.IsDrawing));
        Assert.All(comments.Items, item => Assert.False(item.IsOther));
        Assert.True(comments.Items[0].IsInk);
        Assert.True(comments.Items[1].IsRectangle);
        Assert.True(comments.Items[2].IsEllipse);
        Assert.True(comments.Items[3].IsLine);
        Assert.True(comments.Items[4].IsArrow);
        Assert.Equal("Bob", comments.Items[1].Author);
        Assert.Equal("Box", comments.Items[1].Comment);
        Assert.StartsWith(string.Format(CultureInfo.InvariantCulture, language == "en" ? "Page 1: {0}" : "Seite 1: {0}", rectangle), comments.Items[1].AutomationName, StringComparison.Ordinal);
        Assert.All(comments.Items, item => Assert.Null(item.LockReason));

        // Drawings of another program in the same words; a /Line of another program (not recognised) is "Other".
        var foreign = await OpenCommentsAsync(await OpenAsync("drawings-foreign.pdf"));
        var square = foreign.Items.First(item => item.Annotation.Subtype == "Square");
        Assert.Equal(rectangle, square.KindText);
        Assert.Equal(ellipse, foreign.Items.Single(item => item.Annotation.Subtype == "Circle").KindText);
        Assert.Equal(ink, foreign.Items.Single(item => item.Annotation.Name == "ink-0001").KindText);
        var other = foreign.Items.Single(item => item.Annotation.Subtype == "Line");
        Assert.True(other.IsOther);
        Assert.False(other.IsDrawing);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, language == "en" ? "Other: {0}" : "Sonstige: {0}", "Line"), other.KindText);
    }

    [AvaloniaFact]
    public async Task GroupsManyCommentlessStrokes()
    {
        // Page 1: six strokes without a comment and a highlight between them; page 2: four strokes.
        PdfAnnotation[] first = [.. Enumerable.Range(0, 3).Select(i => Stroke(0, i, 100 + i)), Highlight(0, 3, 150), .. Enumerable.Range(4, 3).Select(i => Stroke(0, i, 200 + i))];
        PdfAnnotation[] second = [.. Enumerable.Range(0, 4).Select(i => Stroke(1, i, 100 + i))];
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? first : second));
        var comments = await OpenCommentsAsync(tab);

        // One row for the six strokes, where the first of them is; the highlight on its own; page 2 with four rows.
        Assert.Equal(6, comments.Items.Count);
        var group = comments.Items[0];
        Assert.True(group.IsGroup);
        Assert.False(group.IsExpanded);
        Assert.Equal(6, group.GroupCount);
        Assert.Equal("Freehand, 6 strokes", group.KindText);
        Assert.Equal("1", group.PageText);
        Assert.True(group.IsInk);
        Assert.StartsWith("Page 1: Freehand, 6 strokes", group.AutomationName, StringComparison.Ordinal);
        Assert.Contains(Strings.CommentGroupCollapsed, group.AutomationName, StringComparison.Ordinal);
        Assert.Equal(AnnotationKind.Highlight, comments.Items[1].Kind);
        Assert.All(comments.Items.Skip(2), item => Assert.False(item.IsGroup));
        Assert.All(comments.Items.Skip(2), item => Assert.Equal(1, item.PageIndex));

        // The summary counts the comments, not the rows.
        Assert.Equal("11 comments on 2 pages", comments.Summary);

        // A group row is no entry to edit.
        comments.SelectedItem = group;
        Assert.False(comments.HasSelection);
        Assert.False(comments.SelectedIsEditable);
        Assert.False(comments.SelectedIsViewOnly);

        // In German.
        Strings.Culture = new CultureInfo("de");
        var german = await OpenCommentsAsync(await OpenAsync("annotations-foreign.pdf", "german.pdf", tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? first : second)));
        Assert.Equal("Freihand, 6 Striche", german.Items[0].KindText);
    }

    [AvaloniaFact]
    public async Task GroupRowExpandsAndCollapses()
    {
        PdfAnnotation[] strokes = [.. Enumerable.Range(0, 5).Select(i => Stroke(0, i, 100 + (i * 10))), Highlight(0, 5, 125)];
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? strokes : []));
        var comments = await OpenCommentsAsync(tab);
        var group = comments.Items[0];
        Assert.Equal(2, comments.Items.Count);

        // A click, Enter or Space on the row opens it: the row stays, the strokes follow it in the order of the page.
        comments.JumpCommand.Execute(group);
        Assert.True(group.IsExpanded);
        Assert.Contains(Strings.CommentGroupExpanded, group.AutomationName, StringComparison.Ordinal);
        Assert.Same(group, comments.Items[0]);
        Assert.Equal(7, comments.Items.Count);
        Assert.Equal(["s0", "s1", "s2", null, "s3", "s4"], comments.Items.Skip(1).Select(item => item.Kind == AnnotationKind.Ink ? item.Annotation.Name : null));
        Assert.Equal("6 comments, all on page 1", comments.Summary);

        // A stroke of the open group is an entry like any other: it can be selected and jumped to.
        var stroke = comments.Items[1];
        comments.SelectedItem = stroke;
        Assert.True(comments.HasSelection);
        Assert.True(comments.SelectedIsEditable);

        // Reading the page again keeps the group open (the same row).
        comments.Invalidate(new HashSet<int> { 0 });
        await WaitUntilAsync(() => !comments.IsReading);
        Assert.Same(group, comments.Items[0]);
        Assert.True(group.IsExpanded);
        Assert.Equal(7, comments.Items.Count);

        // A second time closes it again; the selected stroke is no longer listed.
        comments.JumpCommand.Execute(group);
        Assert.False(group.IsExpanded);
        Assert.Equal(2, comments.Items.Count);
        Assert.Null(comments.SelectedItem);

        // A filter that hides the strokes hides the row; "Drawings" shows it again.
        comments.Filter = CommentFilter.Highlight;
        Assert.Single(comments.Items);
        comments.Filter = CommentFilter.Drawings;
        Assert.Same(group, Assert.Single(comments.Items));
    }

    [AvaloniaFact]
    public async Task StrokesWithACommentAreNeverGrouped()
    {
        // Four strokes without a comment and two with one: no group (it needs five).
        PdfAnnotation[] few = [.. Enumerable.Range(0, 4).Select(i => Stroke(0, i, 100 + i)), Stroke(0, 4, 104, "Look"), Stroke(0, 5, 105, "Here")];
        var tab = await OpenAsync("annotations-foreign.pdf", configure: tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? few : []));
        var comments = await OpenCommentsAsync(tab);
        Assert.Equal(6, comments.Items.Count);
        Assert.All(comments.Items, item => Assert.False(item.IsGroup));

        // Five without a comment and two with one: one row for the five, the two on their own.
        PdfAnnotation[] many = [Stroke(0, 0, 100, "First"), .. Enumerable.Range(1, 5).Select(i => Stroke(0, i, 100 + i)), Stroke(0, 6, 106, "Last")];
        var other = await OpenCommentsAsync(await OpenAsync("annotations-foreign.pdf", "many.pdf", tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? many : [])));
        Assert.Equal(3, other.Items.Count);
        Assert.Equal("First", other.Items[0].Comment);
        Assert.True(other.Items[1].IsGroup);
        Assert.Equal(5, other.Items[1].GroupCount);
        Assert.Equal("Last", other.Items[2].Comment);

        // A stroke that gets a comment leaves the group (four left: no group any more).
        many[3] = Stroke(0, 3, 103, "Now with a comment");
        other.Invalidate(new HashSet<int> { 0 });
        await WaitUntilAsync(() => !other.IsReading && other.Items.Count == 7);
        Assert.All(other.Items, item => Assert.False(item.IsGroup));
    }

    [AvaloniaFact]
    public async Task FilterDrawings()
    {
        var tab = await OpenAsync("drawings-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);
        var all = comments.Items.ToArray();
        Assert.Contains(all, item => item.IsDrawing);
        Assert.Contains(all, item => item.IsOther);

        // "Drawings" lets every freehand drawing, rectangle, oval, line and arrow through, nothing else.
        comments.Filter = CommentFilter.Drawings;
        Assert.Equal(all.Where(item => item.IsDrawing), comments.Items);
        Assert.All(comments.Items, item => Assert.Contains(item.Kind, new[] { AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow }));
        Assert.Equal(Strings.CommentFilterDrawings, comments.SelectedFilterChoice!.Title);

        // "Other" no longer lists drawings.
        comments.Filter = CommentFilter.Other;
        Assert.Equal(all.Where(item => item.IsOther), comments.Items);
        Assert.DoesNotContain(comments.Items, item => item.IsDrawing);
    }

    [AvaloniaFact]
    public async Task LockedReasonIsShown()
    {
        var tab = await OpenAsync("drawings-foreign.pdf");

        // A drawing of Pagewing next to those of another program (in the temp copy).
        var own = await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(300, 200, 380, 260), 2), DrawPalette.ColorOf(DrawColor.Blue), null, null);
        var comments = await OpenCommentsAsync(tab);

        // A drawing of another program that Pagewing cannot redraw without loss: the reason in words.
        var locked = comments.Items.Single(item => item.Annotation.Name == "ink-0001");
        Assert.Equal(Strings.SelectionLockedForeign, locked.LockReason);
        Assert.False(locked.ShowsLockReason);
        Assert.All(comments.Items.Where(item => item.IsDrawing && item.Annotation.Editable && item.Annotation.Name != own!.Name), item => Assert.Equal(Strings.SelectionLockedForeign, item.LockReason));
        var free = comments.Items.Single(item => item.Annotation.Name == own!.Name);
        Assert.Null(free.LockReason);

        // A drawing with a reply is "View only", not locked.
        Assert.Null(comments.Items.Single(item => item.Annotation.Name == "ink-0008").LockReason);

        // In the mode the row and the edit controls say it; only the comment and deleting are offered, no colour.
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        Assert.True(locked.ShowsLockReason);
        Assert.False(free.ShowsLockReason);
        comments.SelectedItem = locked;
        Assert.True(comments.SelectedIsEditable);
        Assert.Equal(Strings.SelectionLockedForeign, comments.SelectedLockReason);
        Assert.False(comments.SelectedCanChangeColor);
        Assert.True(comments.DeleteCommand.CanExecute(null));
        comments.SelectedItem = free;
        Assert.Null(comments.SelectedLockReason);
        Assert.True(comments.SelectedCanChangeColor);
        Assert.True(comments.SelectedIsDrawing);
    }

    [AvaloniaFact]
    public async Task SelectingADrawingRowSelectsItInTheDocument()
    {
        var tab = await OpenAsync("text-basic.pdf");
        await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(40, 200, 140, 240), 2), DrawPalette.ColorOf(DrawColor.Red), null, null);
        var comments = await OpenCommentsAsync(tab);
        var entry = Assert.Single(comments.Items);

        // Outside the mode a jump only shows it.
        comments.JumpCommand.Execute(entry);
        Assert.Null(tab.SelectedAnnotation);

        // With the select tool the jump selects it in the document as well.
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        tab.SelectToolCommand.Execute(null);
        comments.JumpCommand.Execute(entry);
        Assert.Equal(entry.Annotation.Name, tab.SelectedAnnotation?.Name);
        Assert.True(tab.CanMoveSelection);
    }

    private static PdfAnnotation Stroke(int page, int index, double top, string? contents = null) =>
        new(page, index, $"s{index}", AnnotationKind.Ink, "Ink", DrawPalette.ColorOf(DrawColor.Red), [], [], new PageRect(40, top, 80, top + 4), contents, "Bot", null,
            Editable: true, Drawing: DrawingGeometry.Ink([[new DisplayPoint(40, top), new DisplayPoint(80, top + 4)]], 2), GeometryEditable: true);

    private static PdfAnnotation Highlight(int page, int index, double top) =>
        new(page, index, $"h{index}", AnnotationKind.Highlight, "Highlight", AnnotationPalette.ColorOf(MarkupColor.Yellow), [], [new PageRect(40, top, 200, top + 12)], new PageRect(40, top, 200, top + 12), null, "Bot", null, Editable: true);

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The view model did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Opens the panel "Comments" in the tab and waits until its list is read.</summary>
    private static async Task<CommentsViewModel> OpenCommentsAsync(TabViewModel tab)
    {
        tab.SidePanel = SidePanelKind.Comments;
        var comments = tab.Comments;
        await WaitUntilAsync(() => !comments.IsLoading);
        return comments;
    }

    private string Copy(string name, string? copyName = null)
    {
        var path = Path.Combine(_folder, copyName ?? name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>
    /// Opens a copy of a test file in a new tab without a window and, unless <paramref name="sharp"/> is false, marks its
    /// first sharp page as shown (as the document view does).
    /// </summary>
    private async Task<TabViewModel> OpenAsync(string name, string? copyName = null, Action<TabViewModel>? configure = null, bool sharp = true)
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        configure?.Invoke(tab);
        var path = Copy(name, copyName);
        await tab.OpenAsync(path);
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = sharp;
        return tab;
    }
}
