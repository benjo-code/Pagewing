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
using Pagewing.App.Controls;
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

    [AvaloniaFact]
    public async Task PatternModeListsGroupsInOrder()
    {
        (string Ok, string Shape, string Failed, string Email, string Iban, string Phone, string Card)[] languages =
        [
            ("Checksum matches", "Format matches", "Checksum does not match", "E-mail", "IBAN", "Phone", "Credit card"),
            ("Prüfsumme stimmt", "Format passt", "Prüfsumme stimmt nicht", "E-Mail", "IBAN", "Telefon", "Kreditkarte"),
        ];
        string[] codes = ["en", "de"];
        for (var round = 0; round < codes.Length; round++)
        {
            Strings.Culture = new CultureInfo(codes[round]);
            var names = languages[round];
            var (window, viewModel, view) = Show();
            var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
            await EnterAsync(window, tab);
            var panel = await OpenPanelAsync(window, tab);
            var search = tab.RedactionPanel;

            // The panel starts as the word search; the pattern mode is the second way, and nothing is searched until "Find".
            Assert.Equal(RedactionSearchMode.Word, search.Mode);
            Assert.True(search.IsWordMode);
            Assert.False(search.CanSearch);
            search.IsPatternMode = true;
            Assert.Equal(RedactionSearchMode.Pattern, search.Mode);
            Assert.Equal([RedactionPatternKind.Email, RedactionPatternKind.Iban, RedactionPatternKind.Phone, RedactionPatternKind.Card], search.PatternKinds.Select(kind => kind.Kind));
            Assert.All(search.PatternKinds, kind => Assert.True(kind.IsChecked));
            Assert.True(search.CanSearch);
            Assert.Equal([names.Email, names.Iban, names.Phone, names.Card], search.PatternKinds.Select(kind => kind.Label));

            // The scanner finds them in any order; the list is by the words, never by colour alone.
            RedactionHit[] found =
            [
                PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 0),
                PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed, "DE89 3704 0044 0532 0130 01", 1),
                PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumOk, "DE89 3704 0044 0532 0130 00", 2),
                PatternHit(RedactionPatternKind.Card, PatternCertainty.ChecksumFailed, "4111 1111 1111 1112", 3),
                PatternHit(RedactionPatternKind.Card, PatternCertainty.ChecksumOk, "4111 1111 1111 1111", 4),
                PatternHit(RedactionPatternKind.Phone, PatternCertainty.ShapeOnly, "+49 711 1234567", 5),
            ];
            RedactionPatternKind[]? asked = null;
            search.PatternSearcher = (_, kinds, report, _, token) =>
            {
                asked = [.. kinds.Order()];
                return PatternListing(found, report, null, limit: false, token);
            };
            search.SearchCommand.Execute(null);
            await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
            Assert.Equal([RedactionPatternKind.Email, RedactionPatternKind.Iban, RedactionPatternKind.Phone, RedactionPatternKind.Card], asked);

            Assert.Equal(
                [PatternCertainty.ChecksumOk, PatternCertainty.ChecksumOk, PatternCertainty.ShapeOnly, PatternCertainty.ShapeOnly, PatternCertainty.ChecksumFailed, PatternCertainty.ChecksumFailed],
                search.Hits.Select(hit => hit.Hit.Certainty!.Value));

            // The first row of each group carries its heading with the count; the others none.
            string?[] expectedHeadings = [$"{names.Ok} (2)", null, $"{names.Shape} (2)", null, $"{names.Failed} (2)", null];
            Assert.Equal(expectedHeadings, search.Hits.Select(hit => hit.GroupHeading));
            Assert.Equal([true, false, true, false, true, false], search.Hits.Select(hit => hit.HasGroupHeading));

            // One counter per kind, after the search; the name of a row says kind, source and certainty in words.
            Assert.Equal([$"{names.Email} (1)", $"{names.Iban} (2)", $"{names.Phone} (1)", $"{names.Card} (2)"], search.PatternKinds.Select(kind => kind.Label));
            var failed = search.Hits[4];
            Assert.Contains(names.Failed, failed.AutomationName, StringComparison.Ordinal);
            Assert.Contains(failed.PatternLabel!, failed.AutomationName, StringComparison.Ordinal);

            // The panel shows the headings and the list as rows.
            Settle(window);
            Assert.Equal(6, panel.HitList.ItemCount);
            for (var index = 0; index < expectedHeadings.Length; index++)
            {
                panel.HitList.ScrollIntoView(index);
                Settle(window);
                var container = panel.HitList.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Row {index} is not realized.");
                var heading = container.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "HitGroupHeading");
                Assert.Equal(expectedHeadings[index] is not null, heading.IsVisible);
                if (expectedHeadings[index] is { } text)
                {
                    Assert.Equal(text, heading.Text);
                }
            }

            // A change of a kind, of the mode or of the field ends the search: its hits belong to the old choice.
            search.PatternKinds.Single(kind => kind.Kind == RedactionPatternKind.Phone).IsChecked = false;
            Assert.Equal(RedactionSearchState.Idle, search.State);
            Assert.Empty(search.Hits);
            Assert.Equal([names.Email, names.Iban, names.Phone, names.Card], search.PatternKinds.Select(kind => kind.Label));
            search.SearchCommand.Execute(null);
            await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
            Assert.Equal([RedactionPatternKind.Email, RedactionPatternKind.Iban, RedactionPatternKind.Card], asked);
            search.IsWordMode = true;
            Assert.Equal(RedactionSearchState.Idle, search.State);
            Assert.Empty(search.Hits);

            // No kind chosen: nothing to search.
            search.IsPatternMode = true;
            foreach (var kind in search.PatternKinds)
            {
                kind.IsChecked = false;
            }

            Assert.False(search.CanSearch);
            Assert.False(search.SearchCommand.CanExecute(null));
        }
    }

    [AvaloniaFact]
    public async Task ChecksumFailedIsNotPreselected()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.IsPatternMode = true;
        RedactionHit[] found =
        [
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed, "DE89 3704 0044 0532 0130 01", 0),
            PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 1),
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumOk, "DE89 3704 0044 0532 0130 00", 2),
            PatternHit(RedactionPatternKind.Card, PatternCertainty.ChecksumFailed, "4111 1111 1111 1112", 3),
        ];
        search.PatternSearcher = (_, _, report, _, token) => PatternListing(found, report, null, limit: false, token);
        Assert.False(search.HasChecksumFailedHits);
        Assert.False(search.CheckFailedCommand.CanExecute(null));
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

        // The checksum and the shape are checked; a failed checksum is listed apart and is not.
        Assert.All(search.Hits.Where(hit => hit.Hit.Certainty != PatternCertainty.ChecksumFailed), hit => Assert.True(hit.IsChecked));
        Assert.All(search.Hits.Where(hit => hit.Hit.Certainty == PatternCertainty.ChecksumFailed), hit => Assert.False(hit.IsChecked));
        Assert.All(search.Hits.Where(hit => hit.Hit.Certainty == PatternCertainty.ChecksumFailed), hit => Assert.True(hit.CanCheck));
        Assert.Equal(2, search.CheckedCount);
        Assert.True(search.HasChecksumFailedHits);
        Assert.Equal(Strings.RedactAddFailed(2), search.CheckFailedLabel);
        Assert.Contains("2", search.CheckFailedLabel, StringComparison.Ordinal);

        // One click adds them all; then there is nothing left to add.
        Assert.True(search.CheckFailedCommand.CanExecute(null));
        search.CheckFailedCommand.Execute(null);
        Assert.Equal(4, search.CheckedCount);
        Assert.All(search.Hits, hit => Assert.True(hit.IsChecked));
        Assert.False(search.CheckFailedCommand.CanExecute(null));

        // A single one can be taken out again by hand.
        search.Hits.Last().IsChecked = false;
        Assert.Equal(3, search.CheckedCount);
        Assert.True(search.CheckFailedCommand.CanExecute(null));

        // The word search has no groups, no heading and no such command to run.
        search.IsWordMode = true;
        Assert.False(search.HasChecksumFailedHits);
        Assert.False(search.CheckFailedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task MarkCheckedAddsPatternMarksWithTheirKind()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var panel = await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.IsPatternMode = true;
        RedactionHit[] found =
        [
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed, "DE89 3704 0044 0532 0130 01", 0),
            PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 1),
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumOk, "DE89 3704 0044 0532 0130 00", 1),
        ];
        search.PatternSearcher = (_, _, report, _, token) => PatternListing(found, report, null, limit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

        // "Mark all" is the word search's "everywhere"; a pattern search has only the checked ones.
        Assert.False(search.MarkAllCommand.CanExecute(null));
        Assert.True(search.MarkCheckedCommand.CanExecute(null));
        search.MarkCheckedCommand.Execute(null);

        Assert.Equal(2, tab.RedactionPlan!.Marks.Count);
        Assert.All(tab.RedactionPlan.Marks, mark =>
        {
            Assert.Equal(RedactionKind.Hit, mark.Kind);
            Assert.False(mark.Everywhere);
        });
        Assert.Equal(
            [(RedactionPatternKind.Email, "ann@example.org"), (RedactionPatternKind.Iban, "DE89 3704 0044 0532 0130 00")],
            tab.RedactionPlan.Marks.Select(mark => (mark.Pattern!.Value, mark.Term!)).Order());
        Assert.Equal("Marked 2 places.", search.MarkedNote);

        // What was marked leaves the list; the failed one stays, not checked, and can be marked afterwards.
        var rest = Assert.Single(search.Hits);
        Assert.Equal(PatternCertainty.ChecksumFailed, rest.Hit.Certainty);
        Assert.False(rest.IsChecked);
        Assert.False(search.MarkCheckedCommand.CanExecute(null));
        search.CheckFailedCommand.Execute(null);
        search.MarkCheckedCommand.Execute(null);
        Assert.Equal(3, tab.RedactionPlan.Marks.Count);
        Assert.Equal(RedactionPatternKind.Iban, tab.RedactionPlan.Marks[2].Pattern);
        Assert.False(tab.RedactionPlan.Marks[2].Everywhere);

        // The list of marks says which kind of pattern a mark came from, in words.
        Settle(window);
        Assert.Equal(
            ["Pattern: E-mail", "Pattern: IBAN", "Pattern: IBAN"],
            tab.RedactionPanel.MarkRows.Where(row => !row.IsHeader).Select(row => row.KindText).Order(StringComparer.Ordinal));
        Assert.All(tab.RedactionPanel.MarkRows.Where(row => row.Mark is not null), row => Assert.Contains("Pattern", row.AutomationName, StringComparison.Ordinal));
        _ = panel;
    }

    [AvaloniaFact]
    public async Task IncompletePagesNeedTheUsersWord()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.IsPatternMode = true;
        RedactionHit[] one = [PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 0)];

        // A page the scanner could not read completely: the hit is listed, the note names the page, and marking waits for the word.
        search.PatternSearcher = (_, _, report, _, token) => PatternListing(one, report, incompletePage: 4, limit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Single(search.Hits);
        Assert.True(search.RequiresAcknowledgement);
        Assert.Contains("5", search.IncompleteNote, StringComparison.Ordinal);
        Assert.False(search.MarkCheckedCommand.CanExecute(null));
        Assert.False(search.CanMark);
        search.IncompleteAcknowledged = true;
        Assert.True(search.MarkCheckedCommand.CanExecute(null));
        search.MarkCheckedCommand.Execute(null);
        var mark = Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(RedactionPatternKind.Email, mark.Pattern);

        // The word belongs to one search: the next asks again.
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.False(search.IncompleteAcknowledged);
        Assert.False(search.MarkCheckedCommand.CanExecute(null));

        // No hit from an incomplete search is not "no hits"; a complete one without a hit says so.
        search.PatternSearcher = (_, _, report, _, token) => PatternListing([], report, incompletePage: 1, limit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Empty(search.Hits);
        Assert.Equal(Strings.RedactNoHitsSearched, search.Summary);
        Assert.NotEqual(Strings.RedactNoHits, search.Summary);
        search.PatternSearcher = (_, _, report, _, token) => PatternListing([], report, incompletePage: null, limit: false, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.Equal(Strings.RedactNoHits, search.Summary);
        Assert.False(search.RequiresAcknowledgement);

        // The limit of hits asks the same, a search that fails is never complete, and the places it never looks at stay listed.
        search.PatternSearcher = (_, _, report, _, token) => PatternListing(one, report, incompletePage: null, limit: true, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
        Assert.True(search.RequiresAcknowledgement);
        Assert.NotNull(search.LimitNote);
        search.PatternSearcher = (_, _, _, _, _) => throw new InvalidOperationException("The document went.");
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Failed);
        Assert.True(search.RequiresAcknowledgement);
        Assert.False(search.NoHitsVisible);
        Assert.Equal(RedactionSearch.NotSearched.Count, Strings.RedactNotSearchedLines.Count);
        search.ToggleNotSearchedCommand.Execute(null);
        Assert.True(search.ShowNotSearched);
    }

    [AvaloniaFact]
    public async Task TheFailedChecksumRowFollowsTheListWhenTheSearchEnds()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var panel = await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.IsPatternMode = true;
        RedactionHit[] withFailed =
        [
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed, "DE89 3704 0044 0532 0130 01", 0),
            PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumFailed, "DE89 3704 0044 0532 0130 02", 1),
            PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 1),
        ];
        RedactionHit[] withoutFailed = [withFailed[2]];
        var raised = new List<string?>();
        search.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The area of the result is built when the first search shows it.
        StackPanel? found = null;
        StackPanel Row() => found ??= FindOrNull<StackPanel>(panel, "CheckFailedRow") ?? throw new InvalidOperationException("The row is not built.");

        async Task SearchAsync(RedactionHit[] hits)
        {
            search.PatternSearcher = (_, _, report, _, token) => PatternListing(hits, report, null, limit: false, token);
            search.SearchCommand.Execute(null);
            await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);
            Settle(window);
        }

        // What the row shows is what its bindings were told: visibility and label follow every change of the list.
        void AssertShown(int failed)
        {
            Assert.True(search.HasChecksumFailedHits);
            Assert.True(Row().IsVisible);
            Assert.Equal(Strings.RedactAddFailed(failed), search.CheckFailedLabel);
        }

        void AssertGoneAndTold()
        {
            Assert.Contains(nameof(RedactionViewModel.HasChecksumFailedHits), raised);
            Assert.Contains(nameof(RedactionViewModel.CheckFailedLabel), raised);
            Assert.False(search.HasChecksumFailedHits);
            Assert.False(Row().IsVisible);
            raised.Clear();
        }

        // (a) The switch to the word search empties the list: the row goes with it.
        await SearchAsync(withFailed);
        AssertShown(2);
        raised.Clear();
        search.IsWordMode = true;
        Settle(window);
        AssertGoneAndTold();

        // (b) A kind that is ticked off ends the search the same way.
        search.IsPatternMode = true;
        await SearchAsync(withFailed);
        AssertShown(2);
        raised.Clear();
        search.PatternKinds.Single(kind => kind.Kind == RedactionPatternKind.Phone).IsChecked = false;
        Settle(window);
        AssertGoneAndTold();

        // (c) A running search that is stopped drops what it found, the row included.
        var gate = new TaskCompletionSource();
        search.PatternSearcher = (_, _, report, _, token) => PatternWaiting(withFailed, report, gate.Task, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.Hits.Count == 3 && search.IsRunning);
        Settle(window);
        AssertShown(2);
        raised.Clear();
        search.StopCommand.Execute(null);
        Settle(window);
        AssertGoneAndTold();
        gate.SetResult();

        // (d) A new search that finds no checksum that fails starts with an empty list and ends without the row.
        await SearchAsync(withFailed);
        AssertShown(2);
        raised.Clear();
        await SearchAsync(withoutFailed);
        Assert.Contains(nameof(RedactionViewModel.HasChecksumFailedHits), raised);
        Assert.Contains(nameof(RedactionViewModel.CheckFailedLabel), raised);
        Assert.False(search.HasChecksumFailedHits);
        Assert.False(Row().IsVisible);
        Assert.Single(search.Hits);
    }

    [AvaloniaFact]
    public async Task AnUntickedHitStaysUntickedWhenMoreHitsComeIn()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        search.IsPatternMode = true;
        RedactionHit first = PatternHit(RedactionPatternKind.Email, PatternCertainty.ShapeOnly, "ann@example.org", 0);
        RedactionHit second = PatternHit(RedactionPatternKind.Iban, PatternCertainty.ChecksumOk, "DE89 3704 0044 0532 0130 00", 0);
        RedactionHit third = PatternHit(RedactionPatternKind.Phone, PatternCertainty.ShapeOnly, "+49 711 1234567", 1);
        RedactionHit fourth = PatternHit(RedactionPatternKind.Card, PatternCertainty.ChecksumFailed, "4111 1111 1111 1112", 1);
        var gate = new TaskCompletionSource();

        // Two batches: the first stands in the list while the search still runs, the second comes in after the user has unticked a hit.
        search.PatternSearcher = (_, _, _, _, token) => TwoBatches([first, second], [third, fourth], gate.Task, token);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => search.Hits.Count == 2);
        Assert.True(search.IsRunning);
        var early = search.Hits.ToArray();
        Assert.All(early, hit => Assert.True(hit.IsChecked));
        early.Single(hit => hit.Hit == first).IsChecked = false;
        gate.SetResult();
        await WaitUntilAsync(window, () => search.State == RedactionSearchState.Finished);

        Assert.Equal(4, search.Hits.Count);

        // The unticked hit and the hit that was left alone are the same rows as before: the second batch did not reset them.
        Assert.Contains(early[0], search.Hits);
        Assert.Contains(early[1], search.Hits);
        Assert.False(search.Hits.Single(hit => hit.Hit == first).IsChecked);
        Assert.True(search.Hits.Single(hit => hit.Hit == second).IsChecked);

        // Only the new rows get the preselection: a shape is ticked, a failed checksum is not.
        Assert.True(search.Hits.Single(hit => hit.Hit == third).IsChecked);
        Assert.False(search.Hits.Single(hit => hit.Hit == fourth).IsChecked);
        Assert.Equal(2, search.CheckedCount);
    }

    private static async IAsyncEnumerable<RedactionHit> PatternWaiting(
        RedactionHit[] hits,
        RedactionSearchReport? report,
        Task gate,
        [EnumeratorCancellation] CancellationToken token)
    {
        // A pause before each hit lets the time limit of a batch pass, so every hit goes to the UI thread on its own; then the search waits until it is stopped.
        foreach (var hit in hits)
        {
            await Task.Delay(RedactionViewModel.BatchInterval + TimeSpan.FromMilliseconds(40), token);
            yield return hit;
        }

        await Task.WhenAny(gate, Task.Delay(System.Threading.Timeout.Infinite, token));
        report!.LimitReached = false;
    }

    private static async IAsyncEnumerable<RedactionHit> TwoBatches(
        RedactionHit[] first,
        RedactionHit[] second,
        Task gate,
        [EnumeratorCancellation] CancellationToken token)
    {
        // A hit goes to the UI thread when it comes in after BatchInterval, so a pause before each hit makes every hit its own batch.
        foreach (var hit in first)
        {
            await Task.Delay(RedactionViewModel.BatchInterval + TimeSpan.FromMilliseconds(40), token);
            yield return hit;
        }

        await Task.WhenAny(gate, Task.Delay(System.Threading.Timeout.Infinite, token));
        foreach (var hit in second)
        {
            await Task.Delay(RedactionViewModel.BatchInterval + TimeSpan.FromMilliseconds(40), token);
            yield return hit;
        }
    }

    [AvaloniaFact]
    public async Task ModeSwitchIsReachableByKeyboardAndTabNeverTraps()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var panel = await OpenPanelAsync(window, tab);
        var search = tab.RedactionPanel;
        var word = FindOrNull<RadioButton>(panel, "ModeWordButton")!;
        var pattern = FindOrNull<RadioButton>(panel, "ModePatternButton")!;
        Assert.NotNull(word);
        Assert.NotNull(pattern);
        Assert.True(word.IsChecked);
        Assert.True(word.Focusable && pattern.Focusable);
        Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(word)));
        Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(pattern)));

        // Focus goes to the switch with the keyboard, the space bar chooses the pattern mode, and the field of the words goes.
        Assert.True(FindOrNull<TextBox>(panel, "QueryField")!.IsVisible);
        Assert.True(pattern.Focus(NavigationMethod.Tab));
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(search.IsPatternMode);
        Assert.True(pattern.IsChecked);
        Assert.False(FindOrNull<TextBox>(panel, "QueryField")!.IsVisible);
        var boxes = panel.GetVisualDescendants().OfType<CheckBox>().Where(box => box.Name == "PatternKindBox").ToArray();
        Assert.Equal(4, boxes.Length);
        Assert.All(boxes, box => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(box))));
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(search.IsPatternMode);

        // The word switch is reachable the same way and brings the field back.
        Assert.True(word.Focus(NavigationMethod.Tab));
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(search.IsWordMode);
        Assert.True(FindOrNull<TextBox>(panel, "QueryField")!.IsVisible);
        search.IsPatternMode = true;
        Settle(window);

        // Tab never holds the focus: it moves on with every press, through the kinds and the buttons, and leaves the panel.
        Assert.True(pattern.Focus(NavigationMethod.Tab));
        var visited = new List<object?>();
        var left = false;
        for (var i = 0; i < 40 && !left; i++)
        {
            var before = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
            Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
            var after = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
            Assert.NotSame(before, after);
            visited.Add(after);
            left = after is Visual visual && !ReferenceEquals(visual, panel) && !panel.IsVisualAncestorOf(visual);
        }

        Assert.True(left, "Tab did not leave the panel.");
        Assert.Contains(boxes[0], visited);
        Assert.Contains(boxes[3], visited);
    }

    // ---- the text on the areas (milestone 14.1, task 16) ----

    private static HashSet<Guid> Ids(params Guid[] ids) => [.. ids];

    private static TextBox OverlayField(RedactionPanel panel) =>
        FindOrNull<TextBox>(panel, "OverlayTextBox") ?? throw new InvalidOperationException("The field of the text on the areas is not there.");

    /// <summary>A panel of a tab in the mode with two rectangles, the second one selected (a new mark is the selection).</summary>
    private async Task<(Window Window, TabViewModel Tab, RedactionPanel Panel, Guid First, Guid Second)> OverlayPanelAsync()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(100, 300, 220, 360)));
        Assert.True(tab.AddAreaMark(1, new PageRect(50, 50, 150, 90)));
        var panel = await OpenPanelAsync(window, tab);
        Settle(window);
        return (window, tab, panel, tab.RedactionPlan!.Marks[0].Id, tab.RedactionPlan.Marks[1].Id);
    }

    [AvaloniaFact]
    public async Task OverlayFieldAppearsOnlyInTheSelectedMarkRow()
    {
        var (window, tab, panel, first, second) = await OverlayPanelAsync();

        // Nothing selected: no editor and no control of it (the model is dropped with the last selected mark).
        tab.ClearMarkSelection();
        Settle(window);
        Assert.Null(tab.RedactionPanel.OverlayEditor);
        Assert.Null(FindOrNull<TextBox>(panel, "OverlayTextBox"));
        Assert.Null(FindOrNull<StackPanel>(panel, "OverlayEditor"));

        // A selected mark brings the field, named, reachable by Tab, empty, with the options and the snippets.
        tab.SelectMarks([first]);
        Settle(window);
        var field = OverlayField(panel);
        Assert.True(field.IsVisible && field.Focusable && field.IsEffectivelyEnabled);
        Assert.Equal(Strings.RedactOverlayHeading, AutomationProperties.GetName(field));
        Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(FindOrNull<StackPanel>(panel, "OverlayEditor")!));
        Assert.Equal(RedactionLimits.MaxOverlayTextLength, field.MaxLength);
        Assert.True(string.IsNullOrEmpty(field.Text));
        foreach (var name in new[] { "OverlayRepeatBox", "OverlayAutoSizeBox", "OverlayColorAutoBox" })
        {
            Assert.NotNull(FindOrNull<CheckBox>(panel, name));
        }

        // The field shows the text of the selected mark; another mark shows its own.
        Assert.True(tab.SetOverlayText(Ids(first), "Akte 17"));
        Settle(window);
        Assert.Equal("Akte 17", OverlayField(panel).Text);
        tab.SelectMarks([second]);
        Settle(window);
        Assert.True(string.IsNullOrEmpty(OverlayField(panel).Text));

        // Marks with different texts: an empty field that says so, and any edit then reaches all of them.
        tab.SelectMarks([first, second]);
        Settle(window);
        var editor = tab.RedactionPanel.OverlayEditor!;
        Assert.Equal(Strings.RedactOverlayMixed, editor.Placeholder);
        Assert.Equal(Strings.RedactOverlayMixed, OverlayField(panel).PlaceholderText);
        editor.Text = "Alle";
        Assert.True(editor.Commit());
        Assert.All(tab.RedactionPlan!.Marks, mark => Assert.Equal("Alle", mark.OverlayText));

        // The mode ends: the panel and the editor go with the plan.
        tab.DiscardRedactionsCommand.Execute(null);
        Settle(window);
        Assert.False(tab.IsRedactionPanelCreated);
        Assert.Null(FindOrNull<TextBox>(panel, "OverlayTextBox"));
    }

    [AvaloniaFact]
    public async Task OverlayTextIsOneHistoryStepAppliedOnEnterOrOnLeavingTheField()
    {
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        var field = OverlayField(panel);
        var history = tab.RedactionHistory!;
        var version = history.Version;

        // Typing is a draft: no step of the history, no change of the plan, and the editor knows it is not applied.
        Assert.True(field.Focus(NavigationMethod.Tab));
        foreach (var text in new[] { "a", "ab", "abc", "abcd" })
        {
            field.Text = text;
        }

        Assert.Equal(version, history.Version);
        Assert.Null(tab.RedactionPlan!.Marks[0].OverlayText);
        Assert.True(tab.RedactionPanel.OverlayEditor!.IsDirty);

        // Enter applies it as one step; undo takes the whole text back.
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(version + 1, history.Version);
        Assert.Equal("abcd", tab.RedactionPlan.Marks[0].OverlayText);
        Assert.False(tab.RedactionPanel.OverlayEditor.IsDirty);
        tab.UndoCommand.Execute(null);
        Assert.Null(tab.RedactionPlan.Marks[0].OverlayText);
        tab.RedoCommand.Execute(null);
        Assert.Equal("abcd", tab.RedactionPlan.Marks[0].OverlayText);

        // Enter with nothing new is no step.
        Settle(window);
        Assert.True(OverlayField(panel).Focus(NavigationMethod.Tab));
        var before = history.Version;
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(before, history.Version);

        // Leaving the field applies it, too.
        OverlayField(panel).Text = "Neu";
        Assert.Equal("abcd", tab.RedactionPlan.Marks[0].OverlayText);
        Assert.True(FindOrNull<Button>(panel, "AddSnippetButton")!.Focus(NavigationMethod.Tab));
        Settle(window);
        Assert.Equal("Neu", tab.RedactionPlan.Marks[0].OverlayText);
        Assert.Equal(before + 1, history.Version);

        // White space alone clears the text (one step).
        Assert.True(OverlayField(panel).Focus(NavigationMethod.Tab));
        OverlayField(panel).Text = "   ";
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Null(tab.RedactionPlan.Marks[0].OverlayText);
        Assert.Equal(before + 2, history.Version);
        Assert.True(string.IsNullOrEmpty(OverlayField(panel).Text));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task CharactersTheFontLacksRefuseTheWholeTextAndAreTold(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        var editor = tab.RedactionPanel.OverlayEditor!;
        var version = tab.RedactionHistory!.Version;
        Assert.True(OverlayField(panel).Focus(NavigationMethod.Tab));

        // A snowman is no glyph of the font: the position is told in words (with an icon, never colour alone), the text is not used.
        OverlayField(panel).Text = "ok ☃ text";
        Settle(window);
        Assert.True(editor.HasInvalid);
        Assert.Equal(Strings.RedactOverlayInvalidAt(4), editor.InvalidText);
        Assert.Contains("4", editor.InvalidText, StringComparison.Ordinal);
        var row = FindOrNull<Grid>(panel, "OverlayInvalidRow")!;
        Assert.True(row.IsVisible);
        Assert.Equal(editor.InvalidText, FindOrNull<TextBlock>(panel, "OverlayInvalidNote")!.Text);
        Assert.NotEmpty(row.GetVisualDescendants().OfType<StrokeIcon>());
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(version, tab.RedactionHistory.Version);
        Assert.Null(tab.RedactionPlan!.Marks[0].OverlayText);
        Assert.Equal("ok ☃ text", OverlayField(panel).Text);

        // The plan itself refuses such a text too and tells the notice (a path other than the field), and nothing changes.
        Assert.False(tab.SetOverlayText(Ids(first), "x ☃"));
        Assert.Equal(Strings.RedactOverlayInvalid, tab.RedactionNotice);
        Assert.Null(tab.RedactionPlan.Marks[0].OverlayText);
        tab.DismissRedactionNoticeCommand.Execute(null);

        // Without the character it goes through.
        OverlayField(panel).Text = "ok text";
        Settle(window);
        Assert.False(editor.HasInvalid);
        Assert.False(row.IsVisible);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("ok text", tab.RedactionPlan.Marks[0].OverlayText);
    }

    [AvaloniaFact]
    public async Task TermInTheOverlayWarnsWhileTyping()
    {
        var (window, tab, panel, _, _) = await OverlayPanelAsync();
        var mark = new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 0, [new PageRect(100, 100, 160, 120)], "Pagewing", Everywhere: false);
        Assert.True(tab.AddRedactionMarks([mark]));
        Settle(window);
        var editor = tab.RedactionPanel.OverlayEditor!;
        var version = tab.RedactionHistory!.Version;
        var row = FindOrNull<Grid>(panel, "OverlayWarningRow")!;
        Assert.False(editor.HasWarning);
        Assert.False(row.IsVisible);

        // While it is typed, whatever the case: a sentence with an icon, never the term, and nothing changed in the plan.
        Assert.True(OverlayField(panel).Focus(NavigationMethod.Tab));
        OverlayField(panel).Text = "see PAGE wind here";
        Settle(window);
        Assert.False(editor.HasWarning);
        OverlayField(panel).Text = "see PAGEWING here";
        Settle(window);
        Assert.True(editor.HasWarning);

        // Spaces and hyphens do not hide it.
        OverlayField(panel).Text = "see PAGE - wing here";
        Settle(window);
        Assert.True(editor.HasWarning);
        OverlayField(panel).Text = "see PAGEWING here";
        Settle(window);
        Assert.Equal(Strings.RedactOverlayRepeatsTerm, editor.Warning);
        Assert.True(row.IsVisible);
        Assert.Equal(Strings.RedactOverlayRepeatsTerm, FindOrNull<TextBlock>(panel, "OverlayWarningNote")!.Text);
        Assert.NotEmpty(row.GetVisualDescendants().OfType<StrokeIcon>());
        Assert.DoesNotContain("Pagewing", editor.Warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(version, tab.RedactionHistory.Version);

        // The warning goes with the text.
        OverlayField(panel).Text = "harmless";
        Settle(window);
        Assert.False(editor.HasWarning);
        Assert.False(row.IsVisible);

        // Applying the text is not forbidden here (the analysis refuses later): the plan takes it, and the warning stays while the plan holds it.
        OverlayField(panel).Text = "see PAGEWING here";
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("see PAGEWING here", tab.RedactionPlan!.Marks.Single(m => m.Id == mark.Id).OverlayText);
        Settle(window);
        Assert.True(editor.HasWarning);

        // A term marked AFTER the text counts as well: the text of the other mark now holds a term.
        var other = tab.RedactionPlan.Marks[0].Id;
        tab.SelectMarks([other]);
        Settle(window);
        tab.SetOverlayText(Ids(other), "a harmless text");
        var later = new RedactionMark(Guid.NewGuid(), RedactionKind.Text, 1, [new PageRect(10, 10, 60, 30)], "harmless", Everywhere: false);
        Assert.True(tab.AddRedactionMarks([later]));
        tab.SelectMarks([other]);
        Settle(window);
        Assert.True(tab.RedactionPanel.OverlayEditor!.HasWarning);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task ColourBelowTheContrastIsWarnedInWordsNotForbidden(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        var editor = tab.RedactionPanel.OverlayEditor!;
        var color = FindOrNull<CheckBox>(panel, "OverlayColorAutoBox")!;
        var version = tab.RedactionHistory!.Version;

        // Automatic: black or white by contrast, no field for a colour, no warning.
        Assert.True(editor.IsColorAuto);
        Assert.True(color.IsChecked);
        Assert.False(FindOrNull<TextBox>(panel, "OverlayColorBox")!.IsVisible);
        Assert.False(editor.HasContrastWarning);

        // Choosing a colour starts from the one that would have been chosen (white on black): enough contrast, no warning.
        color.IsChecked = false;
        Settle(window);
        Assert.False(editor.IsColorAuto);
        Assert.Equal(RedactionFill.White, tab.RedactionOverlayStyle.TextColor);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);
        var box = FindOrNull<TextBox>(panel, "OverlayColorBox")!;
        Assert.True(box.IsVisible);
        Assert.Equal("#FFFFFF", box.Text);
        Assert.False(editor.HasContrastWarning);

        // A dark grey on the black fill: allowed, but told in words with the ratio and an icon.
        box.Text = "#202020";
        Assert.True(editor.CommitColor());
        Settle(window);
        Assert.Equal(new RedactionFill(0x20, 0x20, 0x20), tab.RedactionOverlayStyle.TextColor);
        Assert.True(editor.HasContrastWarning);
        var ratio = RedactionOverlayColor.Contrast(new RedactionFill(0x20, 0x20, 0x20), RedactionFill.Black);
        Assert.True(ratio < RedactionOverlayColor.MinContrast);
        Assert.Equal(Strings.RedactOverlayContrastLow(ratio), editor.ContrastWarning);
        var row = FindOrNull<Grid>(panel, "OverlayContrastRow")!;
        Assert.True(row.IsVisible);
        Assert.NotEmpty(row.GetVisualDescendants().OfType<StrokeIcon>());
        Assert.Equal(editor.ContrastWarning, FindOrNull<TextBlock>(panel, "OverlayContrastNote")!.Text);

        // A text that is no colour is told; the colour of the plan stays.
        box.Text = "#12";
        Assert.False(editor.CommitColor());
        Assert.True(editor.HasColorError);
        Assert.Equal(Strings.RedactFillHexError, editor.ColorError);
        Assert.Equal(new RedactionFill(0x20, 0x20, 0x20), tab.RedactionOverlayStyle.TextColor);

        // A good contrast again: the warning goes.
        box.Text = "#ffffff";
        Assert.True(editor.CommitColor());
        Assert.False(editor.HasContrastWarning);

        // Back to automatic is a step too, and undo works through all of them.
        color.IsChecked = true;
        Settle(window);
        Assert.Null(tab.RedactionOverlayStyle.TextColor);
        tab.UndoCommand.Execute(null);
        Assert.Equal(RedactionFill.White, tab.RedactionOverlayStyle.TextColor);
    }

    [AvaloniaFact]
    public async Task RepeatAndFitSizeAreStepsOfThePlan()
    {
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        var repeat = FindOrNull<CheckBox>(panel, "OverlayRepeatBox")!;
        var fit = FindOrNull<CheckBox>(panel, "OverlayAutoSizeBox")!;
        var version = tab.RedactionHistory!.Version;
        Assert.False(repeat.IsChecked);
        Assert.True(fit.IsChecked);

        repeat.IsChecked = true;
        fit.IsChecked = false;
        Settle(window);
        Assert.Equal(new RedactionOverlayStyle(Repeat: true, AutoSize: false), tab.RedactionOverlayStyle);
        Assert.Equal(version + 2, tab.RedactionHistory.Version);
        Assert.Same(tab.RedactionOverlayStyle, tab.RedactionPlan!.Overlay);

        // Undo takes them back one by one, and the boxes follow.
        tab.UndoCommand.Execute(null);
        Settle(window);
        Assert.True(repeat.IsChecked);
        Assert.True(fit.IsChecked);
        tab.UndoCommand.Execute(null);
        Settle(window);
        Assert.False(repeat.IsChecked);
        Assert.Equal(new RedactionOverlayStyle(), tab.RedactionOverlayStyle);
    }

    [AvaloniaFact]
    public async Task CodesAreOfferedFromTheSettingsAndLimited()
    {
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        var editor = tab.RedactionPanel.OverlayEditor!;
        var settings = tab.AnnotationSettings;

        // Nothing stored: the module of the language is the only suggestion, and the settings stay untouched.
        Assert.Equal(["redacted"], editor.Snippets.Select(static s => s.Text));
        Assert.Null(settings.Current.RedactionCodes);
        var use = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SnippetUseButton");
        Assert.Equal(Strings.RedactOverlayUseSnippet("redacted"), AutomationProperties.GetName(use));
        var remove = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SnippetRemoveButton");
        Assert.Equal(Strings.RedactOverlayRemoveSnippet("redacted"), AutomationProperties.GetName(remove));

        // A snippet goes on the selected areas in one step.
        var version = tab.RedactionHistory!.Version;
        editor.Snippets[0].UseCommand.Execute(null);
        Assert.Equal("redacted", tab.RedactionPlan!.Marks[0].OverlayText);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);
        Assert.Null(settings.Current.RedactionCodes);

        // The text of the field is saved as another one: now the settings keep the list (the first one included).
        Assert.False(editor.CanAddCode);
        editor.Text = "File 4711";
        Assert.True(editor.CanAddCode);
        Assert.True(editor.AddCodeCommand.CanExecute(null));
        editor.AddCodeCommand.Execute(null);
        Assert.Equal(["redacted", "File 4711"], settings.Current.RedactionCodes);
        Assert.Equal(["redacted", "File 4711"], editor.Snippets.Select(static s => s.Text));
        Settle(window);
        Assert.Equal(2, panel.GetVisualDescendants().OfType<Button>().Count(b => b.Name == "SnippetUseButton"));

        // The same text again is not saved twice; the field says why.
        Assert.False(editor.CanAddCode);
        Assert.Equal(Strings.RedactOverlayCodeKnown, editor.CodeNote);
        Assert.True(FindOrNull<TextBlock>(panel, "SnippetNote")!.IsVisible);

        // Taking one out is a step of the settings, not of the plan.
        editor.Snippets[0].RemoveCommand.Execute(null);
        Assert.Equal(["File 4711"], settings.Current.RedactionCodes);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);

        // At most 40 characters per module, and only characters the font has.
        editor.Text = new string('a', AppSettings.MaxRedactionCodeLength + 1);
        Assert.False(editor.CanAddCode);
        Assert.Equal(Strings.RedactOverlayCodeTooLong(AppSettings.MaxRedactionCodeLength), editor.CodeNote);
        editor.Text = new string('a', AppSettings.MaxRedactionCodeLength);
        Assert.True(editor.CanAddCode);
        editor.Text = "Akte ☃";
        Assert.False(editor.CanAddCode);

        // At most 20 modules: the 21st is refused with the reason, nothing is dropped silently.
        for (var i = settings.Current.RedactionCodes!.Count; i < AppSettings.MaxRedactionCodes; i++)
        {
            editor.Text = $"code {i}";
            Assert.True(editor.CanAddCode);
            editor.AddCodeCommand.Execute(null);
        }

        Assert.Equal(AppSettings.MaxRedactionCodes, settings.Current.RedactionCodes!.Count);
        editor.Text = "one too many";
        Assert.False(editor.CanAddCode);
        Assert.False(editor.AddCodeCommand.CanExecute(null));
        Assert.Equal(Strings.RedactOverlayCodesFull(AppSettings.MaxRedactionCodes), editor.CodeNote);
        Assert.Equal(AppSettings.MaxRedactionCodes, editor.Snippets.Count);

        // Taking them all away leaves an empty list (the user's choice), and the default does not come back.
        foreach (var snippet in editor.Snippets.ToList())
        {
            snippet.RemoveCommand.Execute(null);
        }

        Assert.NotNull(settings.Current.RedactionCodes);
        Assert.Empty(settings.Current.RedactionCodes!);
        Assert.Empty(editor.Snippets);
    }

    [AvaloniaFact]
    public async Task OverlayEditorIsReachableByKeyboardAndTabNeverTraps()
    {
        var (window, tab, panel, first, _) = await OverlayPanelAsync();
        tab.SelectMarks([first]);
        Settle(window);
        Assert.True(OverlayField(panel).Focus(NavigationMethod.Tab));

        // A text in the field makes "Save as snippet" a stop (a button that cannot act is no stop).
        OverlayField(panel).Text = "File 4711";

        // Tab moves with every press through the controls of the editor and on, and leaves the panel in the end.
        var visited = new List<object?>();
        var left = false;
        for (var i = 0; i < 60 && !left; i++)
        {
            var before = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
            Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
            var after = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
            Assert.NotSame(before, after);
            visited.Add(after);
            left = after is Visual visual && !ReferenceEquals(visual, panel) && !panel.IsVisualAncestorOf(visual);
        }

        Assert.True(left, "Tab did not leave the panel.");
        Assert.Contains(FindOrNull<Button>(panel, "AddSnippetButton"), visited);
        Assert.Contains(FindOrNull<CheckBox>(panel, "OverlayRepeatBox"), visited);
        Assert.Contains(FindOrNull<CheckBox>(panel, "OverlayColorAutoBox"), visited);
        Assert.Contains(panel.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SnippetUseButton"), visited);
        Assert.Contains(panel.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SnippetRemoveButton"), visited);

        // Every control of the editor has a name for screen readers.
        var editor = FindOrNull<StackPanel>(panel, "OverlayEditor")!;
        foreach (var button in editor.GetVisualDescendants().OfType<Button>().Where(static b => b is not CheckBox))
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{button.Name} has no automation name");
        }

        foreach (var box in editor.GetVisualDescendants().OfType<CheckBox>())
        {
            Assert.True(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)) || box.Content is string { Length: > 0 }, $"{box.Name} has no name");
        }

        foreach (var text in editor.GetVisualDescendants().OfType<TextBox>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(text)), $"{text.Name} has no automation name");
        }
    }

    [AvaloniaFact]
    public void TextsOfTheTextOnTheAreasExistInBothLanguages()
    {
        string[] keys =
        [
            "RedactOverlayHeading", "RedactOverlayPlaceholder", "RedactOverlayMixed", "RedactOverlayHint", "RedactOverlayInvalidAtFormat",
            "RedactOverlayInvalid", "RedactOverlayRepeatsTerm", "RedactOverlaySnippets", "RedactOverlayUseSnippetFormat",
            "RedactOverlayRemoveSnippetFormat", "RedactOverlayAddSnippet", "RedactOverlayCodeTooLongFormat", "RedactOverlayCodesFullFormat",
            "RedactOverlayCodeKnown", "RedactOverlayCodeDefault", "RedactOverlayRepeat", "RedactOverlayAutoSize", "RedactOverlayColorAuto",
            "RedactOverlayColorLabel", "RedactOverlayContrastLowFormat",
        ];
        var english = new ResourceManager("Pagewing.App.Resources.Strings.en", typeof(Strings).Assembly);
        var german = new ResourceManager("Pagewing.App.Resources.Strings.de", typeof(Strings).Assembly);
        foreach (var key in keys)
        {
            var en = english.GetString(key, CultureInfo.InvariantCulture);
            var de = german.GetString(key, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} is missing in English");
            Assert.False(string.IsNullOrWhiteSpace(de), $"{key} is missing in German");
            Assert.NotEqual(en, de);
            Assert.Equal(Placeholders(en!), Placeholders(de!));

            // No text claims a guarantee, a legal effect or that something is valid.
            foreach (var word in new[] { "safe", "secure", "guarantee", "legally", "valid", "sicher", "garantie", "rechtsgültig", "gültig" })
            {
                Assert.DoesNotContain(word, en!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, de!, StringComparison.OrdinalIgnoreCase);
            }
        }

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("geschwärzt", Strings.RedactOverlayCodeDefault);
        Assert.Contains("2,1", Strings.RedactOverlayContrastLow(2.1), StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("redacted", Strings.RedactOverlayCodeDefault);
        Assert.Contains("2.1", Strings.RedactOverlayContrastLow(2.1), StringComparison.Ordinal);
    }

    private static RedactionHit PatternHit(RedactionPatternKind kind, PatternCertainty certainty, string text, int page) =>
        new(RedactionHitSource.PageText, page, [new PageRect(10, 10, 160, 30)], text, false)
        {
            Snippet = new SearchSnippet("a ", text, " b"),
            Pattern = kind,
            Certainty = certainty,
        };

    private static async IAsyncEnumerable<RedactionHit> PatternListing(
        RedactionHit[] hits,
        RedactionSearchReport? report,
        int? incompletePage,
        bool limit,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        foreach (var hit in hits)
        {
            token.ThrowIfCancellationRequested();
            yield return hit;
        }

        if (incompletePage is { } page)
        {
            report!.MarkIncomplete(page);
        }

        report!.LimitReached = limit;
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
