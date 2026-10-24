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
        Assert.True(ink.IsOther);
        Assert.Equal("Other: Ink", ink.KindText);
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
        Assert.Equal(["ink-0001", "ft-0001", "note-0001"], comments.Items.Select(item => item.Annotation.Name));

        // The choices of the list are the filters, in this order, and choosing one sets the filter.
        Assert.Equal([CommentFilter.All, CommentFilter.Highlight, CommentFilter.Underline, CommentFilter.StrikeOut, CommentFilter.Other], comments.FilterChoices.Select(choice => choice.Value));
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
        Assert.Equal(language == "en" ? "3 comments, all on page 1" : "3 Kommentare, alle auf Seite 1", comments.Summary);
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
