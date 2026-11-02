using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The panel "Redact" of the mode (milestone 14, task 12b): the marks of the plan grouped by page (a click shows the mark, Delete
/// removes it), and "find and redact": a search of the text, the annotations and the form values that lists its hits with their
/// source and "invisible text", marks them all or the checked ones, says what it could not search, and is never taken for "nothing
/// found" when it was stopped or left something out. Every file is a copy or a generated file in a temp folder; settings are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class RedactPanelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-redactpanel-" + Guid.NewGuid().ToString("N"))).FullName;

    public RedactPanelTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
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
    public async Task PanelListsMarksGroupedByPageAndJumps()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(100, 300, 220, 360)));
        Assert.True(tab.AddAreaMark(1, new PageRect(50, 50, 150, 90)));
        Assert.True(tab.AddAreaMark(0, new PageRect(100, 100, 220, 130)));

        // Nothing of the panel is made before it opens.
        Assert.False(tab.IsRedactionPanelCreated);
        var panel = await OpenPanelAsync(window, tab);
        Assert.True(tab.IsRedactionPanelCreated);
        Assert.Equal(SidePanelKind.Redact, tab.SidePanel);
        var rows = tab.RedactionPanel.MarkRows;

        // Grouped by page, the marks of a page in the order they were made: kind, term, page.
        Assert.Equal([true, false, false, true, false], rows.Select(row => row.IsHeader));
        Assert.Equal("Page 1", rows[0].HeaderText);
        Assert.Equal("Page 2", rows[3].HeaderText);
        Assert.Equal(tab.RedactionPlan!.Marks[0].Id, rows[1].Mark!.Id);
        Assert.Equal(tab.RedactionPlan.Marks[2].Id, rows[2].Mark!.Id);
        Assert.Equal(tab.RedactionPlan.Marks[1].Id, rows[4].Mark!.Id);
        Assert.Equal("Rectangle", rows[1].KindText);
        Assert.Equal("1", rows[1].PageText);
        Assert.Equal("2", rows[4].PageText);

        // A text mark shows its term.
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var first = text.GetText(0, text.Chars.Count).IndexOf("Pagewing", StringComparison.Ordinal);
        tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + 8));
        await tab.MarkSelectionAsync();
        Settle(window);
        var textRow = tab.RedactionPanel.MarkRows.Single(row => row.Mark?.Kind == RedactionKind.Text);
        Assert.Equal("Selected text", textRow.KindText);
        Assert.Equal("Pagewing", textRow.TermText);
        tab.ClearMarkSelection();

        // A click on the mark of page 2 selects it and shows its page.
        var target = tab.RedactionPanel.MarkRows.Single(row => row.Mark?.Id == tab.RedactionPlan.Marks[1].Id);
        Assert.False(target.IsSelected);
        Click(window, CenterOf(panel.MarkList, window, tab.RedactionPanel.MarkRows.IndexOf(target)));
        Assert.Equal([target.Mark!.Id], tab.SelectedMarks);
        Assert.True(target.IsSelected);
        await WaitUntilAsync(window, () => tab.CurrentPage == 2);

        // Page Down in the list moves its cursor, not the document; Delete removes the selected mark.
        var before = tab.CurrentPage;
        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(before, tab.CurrentPage);
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.DoesNotContain(tab.RedactionPlan.Marks, mark => mark.Id == target.Mark.Id);
        Assert.DoesNotContain(tab.RedactionPanel.MarkRows, row => row.IsHeader && row.HeaderText == "Page 2");
        Assert.Equal(3, tab.RedactionPlan.Marks.Count);

        // The selection of the plan shows in the list when it changes elsewhere.
        var other = tab.RedactionPlan.Marks[0].Id;
        tab.SelectMarks([other]);
        Assert.True(tab.RedactionPanel.MarkRows.Single(row => row.Mark?.Id == other).IsSelected);
        tab.ClearMarkSelection();
        Assert.All(tab.RedactionPanel.MarkRows, row => Assert.False(row.IsSelected));
    }

    [AvaloniaFact]
    public async Task SearchListsHitsWithSourceAndInvisibleFlag()
    {
        var path = WritePdf(
            "BT /F1 12 Tf 20 150 Td 3 Tr (Hidden Zephyr) Tj ET BT /F1 12 Tf 20 100 Td 0 Tr (Shown Zephyr) Tj ET",
            ("Zephyr note", "10 20 40 40"),
            ("ab", "60 20 90 40"));
        var (window, viewModel, view) = Show();
        var tab = await OpenFileAsync(window, viewModel, view, path);
        await EnterAsync(window, tab);
        var panel = await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;

        search.Query = "Zephyr";
        search.SearchCommand.Execute(null);
        Assert.Equal(RedactionSearchState.Running, search.State);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

        // Page text twice (one of it invisible), then the comment.
        Assert.Equal(3, search.Hits.Count);
        var hidden = search.Hits.Single(hit => hit.IsInvisible);
        Assert.Equal(RedactionHitSource.PageText, hidden.Hit.Source);
        Assert.Equal("Invisible text", Strings.RedactHitInvisible);
        Assert.Equal("Page text", hidden.SourceLabel);
        Assert.True(hidden.IsChecked);
        var shown = search.Hits.Single(hit => hit.Hit.Source == RedactionHitSource.PageText && !hit.IsInvisible);
        Assert.Equal("1", shown.PageText);
        var comment = search.Hits.Single(hit => hit.Hit.Source == RedactionHitSource.Annotation && hit.CanCheck);
        Assert.Equal("Comment", comment.SourceLabel);
        Assert.Contains("Text", comment.DetailLabel, StringComparison.Ordinal);
        Assert.False(comment.IsInvisible);
        Assert.Null(comment.NotMarkableReason);

        // The summary counts the hits; the list of the panel shows them.
        Assert.Equal("3 hits", search.Summary);
        Settle(window);
        Assert.Equal(3, panel.HitList.ItemCount);

        // A short text in a comment is not looked for in the raw bytes of the result: the panel says so next to it. A hit in the page text is not flagged.
        search.Query = "ab";
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        var short1 = Assert.Single(search.Hits);
        Assert.True(short1.HasShortNote);
        Assert.Contains("raw", Strings.RedactHitShort, StringComparison.OrdinalIgnoreCase);
        search.Query = "Zephyr";
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.All(search.Hits, hit => Assert.False(hit.HasShortNote));

        // A form value is its own source with the name of the field.
        var form = await OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        await EnterAsync(window, form);
        await OpenPanelAsync(window, form);
        var formSearch = form.RedactionPanel;
        formSearch.Query = "Fest";
        formSearch.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => formSearch.State == RedactionSearchState.Finished);
        var field = Assert.Single(formSearch.Hits, hit => hit.Hit.Source == RedactionHitSource.FormField);
        Assert.Equal("Form field", field.SourceLabel);
        Assert.Equal("Gesperrt", field.DetailLabel);
        Assert.False(field.HasShortNote);

        // Three characters of a form value are short.
        formSearch.Query = "Fes";
        formSearch.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => formSearch.State == RedactionSearchState.Finished);
        Assert.True(Assert.Single(formSearch.Hits, hit => hit.Hit.Source == RedactionHitSource.FormField).HasShortNote);
    }

    [AvaloniaFact]
    public async Task MarkAllSetsEverywhere()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;

        // Case ignored: four hits; "Mark all" marks every one with the text exactly as the page has it, gone from the whole document.
        search.Query = "pagewing";
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Equal(4, search.Hits.Count);
        Assert.True(search.MarkAllCommand.CanExecute(null));
        var version = tab.RedactionHistory!.Version;
        search.MarkAllCommand.Execute(null);
        Assert.Equal(4, tab.RedactionPlan!.Marks.Count);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);
        Assert.All(tab.RedactionPlan.Marks, mark =>
        {
            Assert.Equal(RedactionKind.Hit, mark.Kind);
            Assert.True(mark.Everywhere);
            Assert.NotEmpty(mark.Areas);
            Assert.Equal("pagewing", mark.Term!, ignoreCase: true);
        });
        Assert.Equal(4, tab.SelectedMarks.Count);

        // The hits are used up; the list of marks shows them under their page; one undo takes them all back.
        Assert.Empty(search.Hits);
        Assert.Equal("Marked 4 places.", search.MarkedNote);
        Assert.Equal(4, search.MarkRows.Count(row => row.Mark?.Kind == RedactionKind.Hit));
        Assert.Equal("Search hit", search.MarkRows.First(row => row.Mark is not null).KindText);
        tab.UndoCommand.Execute(null);
        Assert.True(tab.RedactionPlan.IsEmpty);

        // Whole words: the marks are not "everywhere" (the check counts substrings), but every hit is marked.
        search.Query = "Pagewing";
        search.WholeWord = true;
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.NotEmpty(search.Hits);
        var count = search.Hits.Count;
        search.MarkAllCommand.Execute(null);
        Assert.Equal(count, tab.RedactionPlan.Marks.Count);
        Assert.All(tab.RedactionPlan.Marks, mark => Assert.False(mark.Everywhere));

        // The checked hits only: not "everywhere", the others stay in the list.
        search.WholeWord = false;
        search.Query = "Pagewing";
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        var total = search.Hits.Count;
        search.Hits[0].IsChecked = false;
        var marksBefore = tab.RedactionPlan.Marks.Count;
        search.MarkCheckedCommand.Execute(null);
        Assert.Equal(marksBefore + total - 1, tab.RedactionPlan.Marks.Count);
        Assert.All(tab.RedactionPlan.Marks.Skip(marksBefore), mark => Assert.False(mark.Everywhere));
        Assert.Single(search.Hits);
        Assert.False(search.MarkCheckedCommand.CanExecute(null));
        search.CheckAllCommand.Execute(null);
        Assert.True(search.Hits[0].IsChecked);
        search.UncheckAllCommand.Execute(null);
        Assert.False(search.Hits[0].IsChecked);
    }

    [AvaloniaFact]
    public async Task AMovedOrResizedMarkIsShownAtItsCurrentPlaceFromTheList()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(100, 300, 220, 360)));
        var id = tab.RedactionPlan!.Marks[0].Id;
        await OpenPanelAsync(window, tab);
        var panel = tab.RedactionPanel;
        var revealed = new List<(int Page, PageRect Rect)>();
        tab.RevealRequested += (page, rect) => revealed.Add((page, rect));

        void JumpAndExpect(PageRect expected)
        {
            revealed.Clear();
            var row = panel.MarkRows.Single(candidate => candidate.Mark?.Id == id);
            Assert.Equal(expected, Assert.Single(row.Mark!.Areas));
            panel.ChooseMark(row);
            var shown = Assert.Single(revealed);
            Assert.Equal(expected, shown.Rect);
        }

        JumpAndExpect(new PageRect(100, 300, 220, 360));

        // Moved and resized: the row holds the new rectangle, and the jump goes there.
        var moved = new PageRect(120, 310, 240, 370);
        Assert.True(tab.SetMarkAreas([(id, moved)]));
        JumpAndExpect(moved);
        var resized = new PageRect(120, 310, 300, 400);
        Assert.True(tab.SetMarkAreas([(id, resized)]));
        JumpAndExpect(resized);

        // Undo and redo bring the rectangles back, and the list follows.
        tab.UndoCommand.Execute(null);
        JumpAndExpect(moved);
        tab.UndoCommand.Execute(null);
        JumpAndExpect(new PageRect(100, 300, 220, 360));
        tab.RedoCommand.Execute(null);
        JumpAndExpect(moved);
        tab.RedoCommand.Execute(null);
        JumpAndExpect(resized);
    }

    [AvaloniaFact]
    public async Task AutomationNamesExistForEveryButton()
    {
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var (window, viewModel, view) = Show();
            var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
            await EnterAsync(window, tab);
            Assert.True(tab.AddAreaMark(0, new PageRect(100, 300, 220, 360)));
            var panel = await OpenPanelAsync(window, tab);
            var search = tab.RedactionPanel;
            search.Query = "Pagewing";
            search.SearchCommand.Execute(null);
            await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
            Settle(window);

            Assert.Equal(Strings.RedactPanelTitle, AutomationProperties.GetName(panel));
            Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(panel));
            var buttons = panel.GetVisualDescendants().OfType<Button>().Where(button => button is not CheckBox).ToList();
            Assert.True(buttons.Count >= 6, $"{buttons.Count} buttons");
            foreach (var button in buttons)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{language}: {button.Name} has no automation name");
            }

            foreach (var box in panel.GetVisualDescendants().OfType<CheckBox>().Where(box => box.Name is not null))
            {
                Assert.True(
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)) || box.Content is string { Length: > 0 },
                    $"{language}: {box.Name} has no name");
            }

            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(panel.QueryField)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(panel.HitList)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(panel.MarkList)));

            // The button of the toolbar that shows the panel, too.
            var find = window.GetVisualDescendants().OfType<Control>().First(control => control.Name == "FindButton");
            Assert.Equal(Strings.CommandRedactFind, AutomationProperties.GetName(find));

            // Every row of a list is named; a hit and a mark say what they are, a header says its page.
            var hitRow = panel.HitList.GetVisualDescendants().OfType<ListBoxItem>().First();
            Assert.Contains("1", AutomationProperties.GetName(hitRow), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(panel.MarkList.GetVisualDescendants().OfType<ListBoxItem>().First())));
        }
    }

    [AvaloniaFact]
    public void TextsExistInBothLanguages()
    {
        string[] keys =
        [
            "RedactPanelTitle", "CommandRedactFind", "RedactFindTip", "RedactMarksHeader", "RedactQueryPlaceholder", "RedactMatchCase",
            "RedactWholeWord", "RedactFindAction", "RedactStopAction", "RedactSearchingOf", "RedactNoHits", "RedactNoHitsSearched",
            "RedactHitsOne", "RedactHitsMany", "RedactStopped", "RedactFailed", "RedactIncompletePages", "RedactMorePages",
            "RedactLimitReached", "RedactAcknowledge", "RedactNotSearchedToggle", "RedactNotSearchedReplies", "RedactNotSearchedAuthor",
            "RedactNotSearchedTooltips", "RedactNotSearchedRichText", "RedactNotSearchedAppearance", "RedactNotSearchedCovered", "RedactNotSearchedNoForm",
            "RedactNotSearchedPatterns", "RedactMarkAll", "RedactMarkAllTip", "RedactMarkChecked", "RedactCheckAll", "RedactUncheckAll", "RedactHitSourceText",
            "RedactHitSourceComment", "RedactHitSourceField", "RedactHitInvisible", "RedactHitShort", "RedactHitNameOf",
            "RedactHitChecked", "RedactHitUnchecked", "RedactHitNotMarkable", "RedactReasonNoArea", "RedactReasonLossy",
            "RedactReasonTooManyLines", "RedactReasonBlank", "RedactReasonTooLong", "RedactMarkedOf", "RedactSkippedOf",
            "RedactKindArea", "RedactKindText", "RedactKindHit", "RedactPageHeader", "RedactMarkRowNameOf", "RedactMarksEmpty",
            "RedactHitListName", "RedactNotSearchedIntro", "RedactHitCheckName",
        ];
        var english = new ResourceManager("Pagewing.App.Resources.Strings.en", typeof(Strings).Assembly);
        var german = new ResourceManager("Pagewing.App.Resources.Strings.de", typeof(Strings).Assembly);
        foreach (var key in keys)
        {
            var en = english.GetString(key, CultureInfo.InvariantCulture);
            var de = german.GetString(key, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} is missing in English");
            Assert.False(string.IsNullOrWhiteSpace(de), $"{key} is missing in German");

            // Every text is translated, and the placeholders of a format are the same in both texts.
            Assert.NotEqual(en, de);
            Assert.Equal(Placeholders(en!), Placeholders(de!));

            // No text claims a guarantee, a legal effect or that a document is clean.
            foreach (var word in new[] { "safe", "secure", "guarantee", "legally", "valid", "sicher", "garantie", "rechtsgültig", "gültig" })
            {
                Assert.DoesNotContain(word, en!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, de!, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The places the search never looks at are told one by one, as many as the core lists.
        Assert.Equal(RedactionSearch.NotSearched.Count, Strings.RedactNotSearchedLines.Count);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Seite 3", Strings.RedactPageHeaderOf(3));
        Assert.Equal("Schwärzen", Strings.RedactPanelTitle);
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Page 3", Strings.RedactPageHeaderOf(3));
    }

    [AvaloniaFact]
    public async Task HundredPagesDocumentStaysResponsiveWhileSearching()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-dense.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        var expected = 0;
        await foreach (var _ in RedactionSearch.FindAsync(tab.Document!, "Needle", new SearchOptions(), CancellationToken.None))
        {
            expected++;
        }

        Assert.True(expected > 0);

        // The search starts and the call returns at once: it runs on pool threads, and the UI thread only takes its batches.
        search.Query = "Needle";
        search.SearchCommand.Execute(null);
        Assert.Equal(RedactionSearchState.Running, search.State);
        Assert.True(search.StopCommand.CanExecute(null));

        // A change of the query or an option stops it: nothing is left of it, and it never says "no hits".
        search.MatchCase = true;
        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.Empty(search.Hits);
        Assert.False(search.NoHitsVisible);
        Assert.False(search.CanMark);
        search.MatchCase = false;
        search.SearchCommand.Execute(null);
        Assert.Equal(RedactionSearchState.Running, search.State);

        // Stop: the same; the hits that came in are not shown as a result.
        search.StopCommand.Execute(null);
        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.Empty(search.Hits);
        Assert.False(search.NoHitsVisible);
        Assert.Equal(Strings.RedactStopped, search.Summary);

        // The batches of the stopped run that are still on their way change nothing.
        for (var i = 0; i < 20; i++)
        {
            Settle(window);
            await Task.Delay(10);
        }

        Assert.Empty(search.Hits);
        Assert.Equal(RedactionSearchState.Idle, search.State);

        // A run left alone ends with all its hits and is complete.
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Equal(expected, search.Hits.Count);
        Assert.False(search.RequiresAcknowledgement);
        Assert.True(search.MarkAllCommand.CanExecute(null));

        // Leaving the panel stops a run; a run that has ended keeps its result.
        search.SearchCommand.Execute(null);
        Assert.Equal(RedactionSearchState.Running, search.State);
        tab.SidePanel = SidePanelKind.None;
        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.Empty(search.Hits);
    }

    [AvaloniaFact]
    public async Task AnIncompleteSearchNeedsAnAcknowledgementBeforeMarking()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.Searcher = (_, _, _, report, _, token) => Fake(report, incompletePage: 4, limit: false, withHit: true, token);

        search.Query = "Pagewing";
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

        // One hit, but page 5 was not searched completely: the note names it, and marking waits for the user's word.
        Assert.Single(search.Hits);
        Assert.True(search.RequiresAcknowledgement);
        Assert.Contains("5", search.IncompleteNote, StringComparison.Ordinal);
        Assert.False(search.IncompleteAcknowledged);
        Assert.False(search.MarkAllCommand.CanExecute(null));
        Assert.False(search.MarkCheckedCommand.CanExecute(null));
        Assert.False(search.CanMark);
        search.IncompleteAcknowledged = true;
        Assert.True(search.MarkAllCommand.CanExecute(null));
        search.MarkAllCommand.Execute(null);
        Assert.Single(tab.RedactionPlan!.Marks);

        // The acknowledgement belongs to one search: the next one asks again.
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.False(search.IncompleteAcknowledged);
        Assert.False(search.MarkAllCommand.CanExecute(null));

        // No hit and an incomplete search is not "no hits" of the document.
        search.Searcher = (_, _, _, report, _, token) => Fake(report, incompletePage: 1, limit: false, withHit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Empty(search.Hits);
        Assert.Equal(Strings.RedactNoHitsSearched, search.Summary);
        Assert.NotEqual(Strings.RedactNoHits, search.Summary);

        // The limit of hits asks the same.
        search.Searcher = (_, _, _, report, _, token) => Fake(report, incompletePage: null, limit: true, withHit: true, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.True(search.RequiresAcknowledgement);
        Assert.NotNull(search.LimitNote);

        // A complete search without a hit says so, and a search that fails is never complete.
        search.Searcher = (_, _, _, report, _, token) => Fake(report, incompletePage: null, limit: false, withHit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Equal(Strings.RedactNoHits, search.Summary);
        Assert.True(search.NoHitsVisible);
        Assert.False(search.RequiresAcknowledgement);
        search.Searcher = (_, _, _, _, _, _) => throw new InvalidOperationException("The document went.");
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Failed);
        Assert.False(search.NoHitsVisible);
        Assert.True(search.RequiresAcknowledgement);
        Assert.Equal(Strings.RedactFailed, search.Summary);

        // The places the search never looks at can be shown, in the language of the interface.
        Assert.Equal(RedactionSearch.NotSearched.Count, Strings.RedactNotSearchedLines.Count);
        Assert.False(search.ShowNotSearched);
        search.ToggleNotSearchedCommand.Execute(null);
        Assert.True(search.ShowNotSearched);
    }

    [AvaloniaFact]
    public async Task AStoppedSearchNeverSaysNoHits()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        var gate = new TaskCompletionSource();
        search.Searcher = (_, _, _, _, _, token) => Waiting(gate.Task, token);

        search.Query = "anything";
        search.SearchCommand.Execute(null);
        await Task.Delay(50);
        Settle(window);
        Assert.Equal(RedactionSearchState.Running, search.State);
        Assert.False(search.NoHitsVisible);
        search.StopCommand.Execute(null);

        // Even when the old run would end by itself afterwards, its result is dropped.
        gate.SetResult();
        for (var i = 0; i < 20; i++)
        {
            Settle(window);
            await Task.Delay(10);
        }

        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.False(search.NoHitsVisible);
        Assert.NotEqual(Strings.RedactNoHits, search.Summary);
        Assert.Empty(search.Hits);
    }

    [AvaloniaFact]
    public async Task TheModeEndingDropsThePanelAndItsSearch()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var panel = await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.Query = "Pagewing";
        search.SearchCommand.Execute(null);

        // Discarding ends the mode: the panel goes, and so does the search.
        tab.DiscardRedactionsCommand.Execute(null);
        Settle(window);
        Assert.False(tab.IsRedacting);
        Assert.NotEqual(SidePanelKind.Redact, tab.SidePanel);
        Assert.False(tab.IsRedactionPanelCreated);
        Assert.False(tab.ToggleRedactionPanelCommand.CanExecute(null));
        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.Empty(search.Hits);
        _ = panel;
    }

    [AvaloniaFact]
    public async Task HitsThatCannotBeMarkedShowTheirReasonAndCannotBeChecked()
    {
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var (window, viewModel, view) = Show();
            var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
            await EnterAsync(window, tab);
            await OpenPanelAsync(window, tab);
            var search = tab.RedactionPanel;
            var good = new PageRect(10, 10, 60, 30);
            RedactionHit Hit(List<PageRect> areas, string text, bool lossy = false) =>
                RedactionSearch.Make(RedactionHitSource.Annotation, 0, areas, new SearchSnippet(string.Empty, text, string.Empty), "Text", false, lossy);
            RedactionHit[] hits =
            [
                Hit([good], "fine"),
                Hit([], "no area"),
                Hit([good], "lossy", lossy: true),
                Hit([good], "  "),
                Hit([.. Enumerable.Range(0, RedactionLimits.MaxAreasPerMark + 1).Select(i => new PageRect(0, i, 10, i + 0.5))], "many lines"),
                Hit([good], new string('x', RedactionLimits.MaxTermLength + 1)),
            ];
            search.Searcher = (_, _, _, _, _, token) => Listing(hits, token);

            search.Query = "anything";
            search.SearchCommand.Execute(null);
            await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

            Assert.Equal(6, search.Hits.Count);
            Assert.True(search.Hits[0].CanCheck);
            Assert.True(search.Hits[0].IsChecked);
            Assert.Null(search.Hits[0].NotMarkableReason);
            string[] reasons = [Strings.RedactReasonNoArea, Strings.RedactReasonLossy, Strings.RedactReasonBlank, Strings.RedactReasonTooManyLines, Strings.RedactReasonTooLong];
            for (var i = 1; i < hits.Length; i++)
            {
                var row = search.Hits[i];
                Assert.False(row.CanCheck);
                Assert.False(row.IsChecked);
                row.IsChecked = true;
                Assert.False(row.IsChecked);
                Assert.Equal(reasons[i - 1], row.NotMarkableReason);
                Assert.Contains(reasons[i - 1], row.NotMarkableText, StringComparison.Ordinal);
                Assert.Contains(reasons[i - 1], row.AutomationName, StringComparison.Ordinal);
            }

            // Only the one that can be marked is marked; the others stay in the list with their reason.
            search.MarkAllCommand.Execute(null);
            Assert.Single(tab.RedactionPlan!.Marks);
            Assert.Equal(5, search.Hits.Count);
            Assert.EndsWith(Strings.RedactSkippedCount(5), search.MarkedNote, StringComparison.Ordinal);
            Assert.False(search.MarkAllCommand.CanExecute(null));
            Assert.False(search.CheckAllCommand.CanExecute(null));
        }
    }

    [AvaloniaFact]
    public async Task APanelWithoutAModeOrDocumentMakesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // Outside the mode the command of the toolbar does nothing, and the panel is not offered through the catalog.
        Assert.False(tab.ToggleRedactionPanelCommand.CanExecute(null));
        Assert.False(viewModel.Commands.ById("redact.find")!.CanExecute());
        tab.ToggleRedactionPanelCommand.Execute(null);
        Assert.NotEqual(SidePanelKind.Redact, tab.SidePanel);
        Assert.False(tab.IsRedactionPanelCreated);

        // In the mode the catalog command shows and hides it; closing returns to the panel that was open before.
        await EnterAsync(window, tab);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.True(viewModel.Commands.ById("redact.find")!.CanExecute());
        viewModel.Commands.ById("redact.find")!.Execute();
        Assert.Equal(SidePanelKind.Redact, tab.SidePanel);
        Assert.True(tab.IsRedactionPanelOpen);
        await OpenPanelAsync(window, tab);
        Assert.True(tab.IsRedactionSearchFocusPending || FindOrNull<RedactionPanel>(window, "RedactContent")!.QueryField.IsFocused);
        viewModel.Commands.ById("redact.find")!.Execute();
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.False(tab.IsRedactionPanelOpen);
    }

    private static async IAsyncEnumerable<RedactionHit> Listing(RedactionHit[] hits, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        foreach (var hit in hits)
        {
            token.ThrowIfCancellationRequested();
            yield return hit;
        }
    }

    private static async IAsyncEnumerable<RedactionHit> Fake(
        RedactionSearchReport? report,
        int? incompletePage,
        bool limit,
        bool withHit,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        if (withHit)
        {
            yield return new RedactionHit(RedactionHitSource.PageText, 0, [new PageRect(10, 10, 60, 30)], "Pagewing", false)
            {
                Snippet = new SearchSnippet("a ", "Pagewing", " b"),
            };
        }

        if (incompletePage is { } page)
        {
            report!.MarkIncomplete(page);
        }

        report!.LimitReached = limit;
    }

    private static async IAsyncEnumerable<RedactionHit> Waiting(Task gate, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.WhenAny(gate, Task.Delay(System.Threading.Timeout.Infinite, token));
        await Task.Yield();
        yield break;
    }

    private static Regex PlaceholderPattern { get; } = new(@"\{\d+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string[] Placeholders(string text) =>
        [.. PlaceholderPattern.Matches(text).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>The center of the row at an index of a list, in window coordinates (the row is realized: the lists are short in these tests).</summary>
    private static Point CenterOf(ListBox list, Window window, int index)
    {
        list.ScrollIntoView(index);
        Settle(window);
        var container = list.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Row {index} is not realized.");
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
    }

    private static void Click(Window window, Point point)
    {
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    /// <summary>Starts the mode of the tab through its command and waits until it is on and its toolbar shows.</summary>
    private static async Task EnterAsync(Window window, TabViewModel tab)
    {
        if (!tab.IsRedacting)
        {
            tab.RedactCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsRedacting);
        }

        Settle(window);
    }

    /// <summary>Shows the panel through the command of the toolbar button and waits until its content stands in the window.</summary>
    private static async Task<RedactionPanel> OpenPanelAsync(Window window, TabViewModel tab)
    {
        if (tab.SidePanel != SidePanelKind.Redact)
        {
            tab.ToggleRedactionPanelCommand.Execute(null);
        }

        RedactionPanel? panel = null;
        await WaitUntilAsync(window, () => (panel = FindOrNull<RedactionPanel>(window, "RedactContent")) is { IsVisible: true });
        return panel!;
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private string Copy(string fixture, string? name = null)
    {
        var path = System.IO.Path.Combine(_folder, name ?? fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>
    /// Writes a one-page PDF (300 x 200, Helvetica) with the given content stream and one note annotation per entry (its comment and its
    /// rectangle "x0 y0 x1 y1"); all text is ASCII.
    /// </summary>
    private string WritePdf(string content, params (string Contents, string Rect)[] notes)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            string.Empty,
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var annots = new List<string>();
        foreach (var (contents, rect) in notes)
        {
            objects.Add($"<< /Type /Annot /Subtype /Text /Rect [{rect}] /Contents {Utf16(contents)} /F 4 >>");
            annots.Add($"{objects.Count} 0 R");
        }

        objects[2] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [{string.Join(' ', annots)}] >>";
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = System.IO.Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-generated.pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    private static string Utf16(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, FindOrNull<DocumentView>(window, "DocumentView")!);
    }

    /// <summary>Opens a copy of a test file (in the temp folder) in a new tab and waits for its first sharp page.</summary>
    private Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name) =>
        OpenFileAsync(window, viewModel, view, Copy(name, Guid.NewGuid().ToString("N")[..8] + "-" + name));

    private static async Task<TabViewModel> OpenFileAsync(Window window, MainViewModel viewModel, DocumentView view, string path)
    {
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
