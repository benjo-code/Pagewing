using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// Pages that become images, chosen in the card (milestone 14.1, task 5; spec 4.3): a file that is refused for pages alone offers each
/// of them as an image, never preselected, "Continue" waits for a chosen page, the choice goes into the plan (one step each) and the
/// analysis runs again, a page that is refused even as an image is not offered again, and an encrypted file or page keys offer nothing.
/// The analysis is replaced where a test needs a result that real files do not give; one test runs the real pipeline on the fixture
/// that PDFium cannot redact around.
/// </summary>
public sealed partial class RedactApplyTests
{
    private static readonly RedactionRefusal PageOneRefused = new(RedactionRefusalKind.PageNotRedactable, [0], RenderCheckReason);

    // ------------------------------------------------------------------ the offer

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task RejectedPageOffersTheRescueNeverPreselected(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, []);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);

        // One check box for the page, not checked; the sentence of what the choice costs; the resolution 200 first; the estimate says "up to".
        var offer = viewModel.PendingSave!.Rescue!;
        var page = Assert.Single(offer.Pages);
        Assert.False(page.IsChosen);
        Assert.Equal(200, offer.Dpi);
        Assert.Empty(offer.ChosenPages);
        var check = Assert.Single(card.GetVisualDescendants().OfType<CheckBox>(), box => box.Name == "RedactRescueCheck");
        Assert.False(check.IsChecked);
        Assert.Equal(language == "en" ? "Convert this page into an image instead" : "Diese Seite stattdessen in ein Bild umwandeln", check.Content);
        Assert.Equal(language == "en" ? "Page 1: convert this page into an image instead" : "Seite 1: Diese Seite stattdessen in ein Bild umwandeln", Avalonia.Automation.AutomationProperties.GetName(check));
        var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(Strings.RedactRescueIntro, texts);
        Assert.Contains(texts, text => text is not null && text.Contains(language == "en" ? "up to about" : "bis zu etwa", StringComparison.Ordinal) && text.Contains("200 dpi", StringComparison.Ordinal));
        Assert.True(Find<RadioButton>(card, "RedactRescueDpi200").IsChecked);
        Assert.False(Find<RadioButton>(card, "RedactRescueDpi150").IsChecked);
        Assert.False(Find<RadioButton>(card, "RedactRescueDpi300").IsChecked);

        // The refusal is told as before (kind, page, reason); "Continue" waits for a choice; "Back" is the default; nothing else is offered.
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith(language == "en" ? "Page 1 cannot be redacted" : "Seite 1", StringComparison.Ordinal) == true);
        Assert.Equal(language == "en" ? ["Continue", "Back"] : ["Weiter", "Zurück"], ShownChoices(card));
        Assert.False(card.ButtonOf(PendingSaveChoice.Continue)!.IsEnabled);
        Assert.Equal([PendingSaveChoice.Continue, PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.Equal(PendingSaveChoice.Cancel, viewModel.PendingSave.DefaultChoice);
        Assert.Equal(Strings.RedactRefusedNothing, viewModel.PendingSave.Message);

        // "Back": nothing was written, nothing is in the plan but the mark.
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Empty(tab.RedactionPlan!.RasterPages);
        Assert.True(tab.HasPendingRedactions);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ApplyStaysBlockedWhileARejectedPageIsNotChosen()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var rewrites = 0;
        viewModel.RedactionRewriter = (_, _, _, _, _) =>
        {
            rewrites++;
            throw new InvalidOperationException("must not be called");
        };
        var seen = new List<RedactionPlan>();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, seen);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var question = viewModel.PendingSave!;

        // Without a chosen page "Continue" does nothing: not by the button, not by the command, not by the model; no card of "Apply" follows.
        Assert.False(question.IsArmed(PendingSaveChoice.Continue));
        Assert.False(question.Choose(PendingSaveChoice.Continue));
        Assert.False(question.Resolve(PendingSaveChoice.Continue));
        Assert.False(question.IsResolved);
        Assert.False(card.ButtonOf(PendingSaveChoice.Continue)!.IsEnabled);
        Assert.DoesNotContain(PendingSaveChoice.SaveAs, question.Choices);
        Assert.DoesNotContain(PendingSaveChoice.SaveInOriginal, question.Choices);
        Assert.False(question.Resolve(PendingSaveChoice.SaveAs));

        // Choosing a page arms it; taking the choice back disarms it again.
        question.Rescue!.Pages[0].IsChosen = true;
        Settle(window);
        Assert.True(question.IsArmed(PendingSaveChoice.Continue));
        Assert.True(card.ButtonOf(PendingSaveChoice.Continue)!.IsEnabled);
        question.Rescue.Pages[0].IsChosen = false;
        Settle(window);
        Assert.False(card.ButtonOf(PendingSaveChoice.Continue)!.IsEnabled);

        Assert.True(question.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.Equal(0, _host.Picker.Calls);
        Assert.Single(seen);
        Assert.Empty(seen[0].RasterPages);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // A second try offers it again, as unchosen as the first time.
        apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        Assert.False(viewModel.PendingSave!.Rescue!.Pages[0].IsChosen);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
    }

    [AvaloniaFact]
    public async Task ChoosingThePageAnalysesAgainAndApplies()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        var seen = new List<RedactionPlan>();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, seen);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var offer = viewModel.PendingSave!.Rescue!;
        offer.Pages[0].IsChosen = true;
        offer.Dpi = 300;
        Settle(window);
        var version = tab.RedactionHistory!.Version;
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));

        // The choice is in the plan (one step), the analysis ran again on that plan, and the card of "Apply" follows with the page in it.
        var next = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.Equal([0], tab.RedactionPlan!.RasterPages.Order());
        Assert.Equal(300, tab.RedactionPlan.RasterDpi);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);
        Assert.Equal(2, seen.Count);
        Assert.Equal([0], seen[1].RasterPages.Order());
        Assert.Equal(300, seen[1].RasterDpi);
        Assert.Null(viewModel.PendingSave!.Rescue);
        Assert.Equal(Strings.RedactSecRaster, viewModel.PendingSave.Redaction!.Sections[0].Heading);
        Assert.Equal([PendingSaveChoice.SaveAs, PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.NotNull(next);

        // The mark of the plan stays; "Back" leaves the choice in the plan (it is a staged change like a mark) and writes nothing.
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Single(tab.RedactionPlan.Marks);
        Assert.True(tab.HasPendingRedactions);
        Assert.False(tab.IsSaving);
    }

    [AvaloniaFact]
    public async Task CardNamesWhatIsLostBeforeApplyingEnglish() => await CardNamesWhatIsLostAsync("en", "200 dpi", "1,654 × 2,339", "up to about 11.1 MiB", "all of its text", "2 links", "5 annotations", "3 form fields");

    [AvaloniaFact]
    public async Task CardNamesWhatIsLostBeforeApplyingGerman() => await CardNamesWhatIsLostAsync("de", "200 dpi", "1.654 × 2.339", "bis zu etwa 11,1 MiB", "ihren gesamten Text", "2 Links", "5 Annotationen", "3 Formularfelder");

    private async Task CardNamesWhatIsLostAsync(string language, params string[] expected)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, []);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        viewModel.PendingSave!.Rescue!.Pages[0].IsChosen = true;
        Settle(window);
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);

        // Before anything is applied the card says it, per page: the text, the links, the comments (annotations), the form fields, the
        // resolution and the size the file grows by at most; and the sentence that the other pages keep their text.
        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections, s => s.Heading == Strings.RedactSecRaster);
        Assert.Equal(Strings.RedactRasterIntro, section.Lines[0]);
        var all = string.Join(" ", section.Lines);
        Assert.All(expected, part => Assert.Contains(part, all, StringComparison.Ordinal));
        Assert.Contains(Strings.RedactRasterOthers, section.Lines);
        var shown = card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.All(section.Lines.Prepend(section.Heading), line => Assert.Contains(line, shown));
        Assert.Equal(language == "en" ? "Turned into images" : "Wird in ein Bild umgewandelt", section.Heading);

        // The summary line counts the page, and the honest check lines for pages that are images come with it.
        Assert.Contains(language == "en" ? "1 page turned into an image" : "1 Seite in ein Bild umgewandelt", viewModel.PendingSave.Redaction.Summary, StringComparison.Ordinal);
        Assert.Contains(Strings.RedactCheckListRasterOnly, viewModel.PendingSave.Redaction.AllTexts);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
    }

    [AvaloniaFact]
    public async Task UndoRestoresTheChoice()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, []);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        viewModel.PendingSave!.Rescue!.Pages[0].IsChosen = true;
        viewModel.PendingSave.Rescue.Dpi = 150;
        Settle(window);
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal([0], tab.RedactionPlan!.RasterPages.Order());
        Assert.Equal(150, tab.RedactionPlan.RasterDpi);
        Assert.Contains(tab.RedactionPanel.MarkRows, row => row.IsRaster && row.Page == 0 && row.RasterDpi == 150);

        // One undo takes the choice back, with its resolution; the mark stays; redo brings it again.
        Assert.True(tab.UndoRedactionCommand.CanExecute(null));
        tab.UndoRedactionCommand.Execute(null);
        Assert.Empty(tab.RedactionPlan!.RasterPages);
        Assert.Equal(200, tab.RedactionPlan.RasterDpi);
        Assert.Single(tab.RedactionPlan.Marks);
        Assert.DoesNotContain(tab.RedactionPanel.MarkRows, row => row.IsRaster);
        tab.RedoRedactionCommand.Execute(null);
        Assert.Equal([0], tab.RedactionPlan.RasterPages.Order());
        Assert.Equal(150, tab.RedactionPlan.RasterDpi);
    }

    [AvaloniaFact]
    public async Task ARejectedFixturePageIsRescuedEndToEndWithTheRealPipeline()
    {
        // redact-everywhere.pdf has a tiling pattern PDFium cannot redact around: refused without the choice, applied with it.
        var (window, viewModel, view, tab, path) = await MarkedAsync("redact-everywhere.pdf", area: false);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(tab.AddAreaMark(0, await HeadlineAreaAsync(tab.Document!)));
        var target = Path.Combine(_host.Folder, "rescued.pdf");
        _host.Picker.Result = target;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var offer = viewModel.PendingSave!.Rescue!;
        var page = Assert.Single(offer.Pages);
        Assert.Equal(0, page.Page);
        Assert.False(page.IsChosen);
        Assert.Contains("up to about", page.EstimateText, StringComparison.Ordinal);

        // Nothing is written without the choice; with it the card of "Apply" names the page, and the file is written and shows no text there.
        page.IsChosen = true;
        Settle(window);
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections, s => s.Heading == Strings.RedactSecRaster);
        Assert.Contains(section.Lines, line => line.StartsWith("Page 1 becomes an image at 200 dpi", StringComparison.Ordinal));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        Assert.Equal(Path.GetFullPath(target), tab.FullPath);
        Assert.Equal(string.Empty, await TextOfAsync(tab.Document!, 0));
        Assert.Contains("Seite zwei", await TextOfAsync(tab.Document!, 1), StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var report = await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);
        Assert.Contains(viewModel.PendingSave!.Redaction!.Sections, s => s.Heading == Strings.RedactSecRaster);
        Assert.NotNull(report);
        viewModel.PendingSave.Abandon();
    }

    // ------------------------------------------------------------------ no rescue

    [AvaloniaFact]
    public async Task OfferStartsAtTheResolutionOfThePlan()
    {
        // The resolution is plan-wide: pages the user already set to 300 dpi are not reset to 200 by "Continue".
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        tab.SetRasterDpiCommand.Execute(300);
        var seen = new List<RedactionPlan>();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, seen);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var offer = viewModel.PendingSave!.Rescue!;
        Assert.Equal(300, offer.Dpi);
        Assert.True(Find<RadioButton>(card, "RedactRescueDpi300").IsChecked);
        Assert.False(Find<RadioButton>(card, "RedactRescueDpi200").IsChecked);
        Assert.StartsWith("At 300 dpi", offer.Pages[0].EstimateText, StringComparison.Ordinal);
        Assert.Contains("every page that becomes an image", Strings.RedactRescueDpiLabel, StringComparison.Ordinal);

        offer.Pages[0].IsChosen = true;
        Settle(window);
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.Equal(300, tab.RedactionPlan!.RasterDpi);
        Assert.Equal(300, seen[1].RasterDpi);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
    }

    [AvaloniaFact]
    public async Task AnOfferablePageStaysOfferedNextToOnesThatCannotBeTurnedIntoImages()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        var reads = new List<int>();

        // Page 1 is an ordinary page, page 2 is far too large, page 3 cannot be read. None of them was ever chosen.
        viewModel.RescuePageSizeReader = (_, page, _) =>
        {
            reads.Add(page);
            return page switch
            {
                0 => Task.FromResult(new PdfPageSize(595, 842)),
                1 => Task.FromResult(new PdfPageSize(14400, 14400)),
                _ => throw new InvalidOperationException("unreadable"),
            };
        };
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis(
        [
            new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [0], RenderCheckReason),
            new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [1], RenderCheckReason),
            new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [2], RenderCheckReason),
        ]));

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);

        // Only the ordinary page is offered; the others are named under "cannot be turned into images", each with its own true sentence.
        Assert.Equal([0, 1, 2], reads);
        Assert.Equal([0], viewModel.PendingSave!.Rescue!.Pages.Select(static page => page.Page));
        var stuck = Assert.Single(viewModel.PendingSave.Redaction!.Sections, section => section.Heading == Strings.RedactRescueHeadingStuck);
        Assert.Equal(
            ["Page 2 is too large to be turned into an image, even at 72 dpi.", "Page 3 cannot be read, so it cannot be turned into an image."],
            stuck.Lines);
        Assert.All(stuck.Lines, line => Assert.DoesNotContain("Undo", line, StringComparison.OrdinalIgnoreCase));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);

        // Nothing offerable: the plain refusal follows, naming the unreadable page without advice to undo a choice that was never made.
        viewModel.RescuePageSizeReader = (_, _, _) => throw new InvalidOperationException("unreadable");
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis([PageOneRefused]));
        apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);
        Assert.Null(viewModel.PendingSave!.Rescue);
        var plain = Assert.Single(viewModel.PendingSave.Redaction!.Sections, section => section.Heading == Strings.RedactRescueHeadingStuck);
        Assert.Equal(["Page 1 cannot be read, so it cannot be turned into an image."], plain.Lines);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Empty(tab.RedactionPlan!.RasterPages);
    }

    [AvaloniaFact]
    public async Task AppliedRasterOnlyPlanIsNoLongerAnUnsavedChange()
    {
        // A plan of one page as an image and no mark, through the real pipeline.
        var (window, viewModel, view, tab, _) = await MarkedAsync("redact-everywhere-nopattern.pdf", area: false);
        tab.CurrentPage = 1;
        tab.ToggleRasterPageCommand.Execute(null);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.AsksBeforeClosing);
        _host.Picker.Result = Path.Combine(_host.Folder, "raster-only.pdf");

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.AsksBeforeClosing);
        Assert.Null(tab.RedactionPlan);
        Assert.Equal(string.Empty, await TextOfAsync(tab.Document!, 0));
        await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);
        viewModel.PendingSave!.Abandon();
        await WaitUntilAsync(window, () => viewModel.PendingSave is null && tab.RedactCommand.CanExecute(null));

        // Discarding a new raster-only plan ends the unsaved state as well.
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        tab.CurrentPage = 1;
        tab.ToggleRasterPageCommand.Execute(null);
        Assert.True(tab.AsksBeforeClosing);
        tab.DiscardRedactionsCommand.Execute(null);
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.AsksBeforeClosing);
    }

    [AvaloniaFact]
    public async Task EncryptedOrPageKeyRefusalOffersNoRescue()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        RedactionRefusal[][] cases =
        [
            [new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "The file is encrypted.")],
            [new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [0], "/Thumb")],

            // A page that could be rescued next to a refusal of the file that no page choice helps: the file stays refused.
            [PageOneRefused, new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [1], "/Thumb")],
            [PageOneRefused, new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "The file is encrypted.")],
        ];
        foreach (var refusals in cases)
        {
            viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis(refusals));
            var apply = viewModel.ApplyRedactionsAsync(tab);
            var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);
            Assert.Null(viewModel.PendingSave!.Rescue);
            Assert.DoesNotContain(card.GetVisualDescendants().OfType<CheckBox>(), box => box.Name == "RedactRescueCheck" && box.IsEffectivelyVisible);
            Assert.Equal(["Back"], ShownChoices(card));
            Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
            Assert.False(await apply);
        }

        Assert.Empty(tab.RedactionPlan!.RasterPages);
    }

    [AvaloniaFact]
    public async Task APageThatIsRefusedAfterTheChoiceIsNotOfferedAgain()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        var calls = 0;

        // The page is refused with and without the choice (it cannot be rendered, say).
        viewModel.RedactionAnalyzer = (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(CleanAnalysis([PageOneRefused]));
        };

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        viewModel.PendingSave!.Rescue!.Pages[0].IsChosen = true;
        Settle(window);
        Assert.True(viewModel.PendingSave.Choose(PendingSaveChoice.Continue));

        // The second analysis refuses it again: the plain card follows, without an offer, naming the page that cannot be turned into an image.
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);
        Assert.Equal(2, calls);
        Assert.Null(viewModel.PendingSave!.Rescue);
        Assert.Equal(["Back"], ShownChoices(card));
        var stuck = Assert.Single(viewModel.PendingSave.Redaction!.Sections, section => section.Heading == Strings.RedactRescueHeadingStuck);
        Assert.Equal(string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.RedactRescueStuck), "1"), Assert.Single(stuck.Lines));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(2, calls);

        // The choice is a step like a mark: it can be undone, and then the page is offered again.
        tab.UndoRedactionCommand.Execute(null);
        Assert.Empty(tab.RedactionPlan!.RasterPages);
        apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
    }

    [AvaloniaTheory]
    [InlineData(612, 792, 200, false, false)]
    [InlineData(5000, 5000, 200, true, false)]
    [InlineData(14400, 14400, 200, true, true)]
    public void EstimateNamesTheResolutionItReallyGetsAndRefusesWhatIsTooLarge(double width, double height, int dpi, bool lowered, bool tooLarge)
    {
        Strings.Culture = new CultureInfo("en");
        var text = RedactionApplyInfo.RasterEstimate(width, height, dpi);
        if (tooLarge)
        {
            Assert.Null(text);
            return;
        }

        Assert.NotNull(text);
        Assert.Contains("up to about", text, StringComparison.Ordinal);
        Assert.Equal(lowered, text.Contains("lower than the 200 dpi chosen", StringComparison.Ordinal));
        if (!lowered)
        {
            Assert.StartsWith("At 200 dpi: 1,700 × 2,200 pixels", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ keyboard

    [AvaloniaFact]
    public async Task RasterCardIsAccessibleByKeyboard()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, []);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var check = Find<CheckBox>(card, "RedactRescueCheck");
        var back = card.ButtonOf(PendingSaveChoice.Cancel)!;
        var next = card.ButtonOf(PendingSaveChoice.Continue)!;
        Assert.True(back.IsFocused);

        // Tab walks the card (the check box is reachable), the space bar chooses the page, and Continue becomes available.
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(check.IsFocused || card.GetVisualDescendants().OfType<Control>().Any(control => control.IsFocused && control != back));
        for (var i = 0; i < 6 && !check.IsFocused; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab);
        }

        Assert.True(check.IsFocused);
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.True(viewModel.PendingSave!.Rescue!.Pages[0].IsChosen);
        Assert.True(next.IsEnabled);

        // Further Tabs reach the buttons ("Continue" is one of them); Shift+Tab goes back; Esc leaves the card (it is no trap).
        var reached = new HashSet<Control>();
        for (var i = 0; i < 12; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab);
            if (card.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.IsFocused) is { } focused)
            {
                reached.Add(focused);
            }
        }

        Assert.Contains(next, reached);
        Assert.Contains(back, reached);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(await apply);
        Assert.True(viewModel.PendingSave is null || viewModel.PendingSave.Kind != PendingSaveKind.RedactionRescue);
        Assert.Empty(tab.RedactionPlan!.RasterPages);
    }

    [AvaloniaFact]
    public async Task ContinueByKeyboardPutsTheChoiceIntoThePlan()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = RefusesPageUnlessImage(0, []);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRescue);
        var check = Find<CheckBox>(card, "RedactRescueCheck");
        check.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Space, PhysicalKey.Space);
        var next = card.ButtonOf(PendingSaveChoice.Continue)!;
        next.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);

        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.Equal([0], tab.RedactionPlan!.RasterPages.Order());
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
    }

    // ------------------------------------------------------------------ helpers

    // Page 1 (index 0) cannot be redacted, unless the plan turns it into an image; every plan seen is kept.
    private static Func<RedactionPlan, PdfDocument, string, CancellationToken, Task<RedactionAnalysis>> RefusesPageUnlessImage(int page, List<RedactionPlan> seen) =>
        (plan, _, _, _) =>
        {
            seen.Add(plan);
            var asImage = plan.RasterPages.Contains(page);
            return Task.FromResult(RasterAnalysis(plan, asImage ? [] : [new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [page], RenderCheckReason)]));
        };

    // An A4 page at 200 dpi: 1,654 by 2,339 pixels, 11,606,118 bytes at most; with text, 2 links, 5 annotations and 3 form fields.
    private static RedactionAnalysis RasterAnalysis(RedactionPlan plan, IReadOnlyList<RedactionRefusal> refusals)
    {
        var raster = plan.RasterPages.Order()
            .Select(page => new RasterPageInfo(page, plan.RasterDpi, 1654, 2339, Lowered: false, 11_606_118, Annotations: 5, FormFields: 3, Links: 2, HasText: true))
            .ToList();
        return new RedactionAnalysis(
            [],
            [],
            CleanupComplete: true,
            [],
            refusals,
            PageKeysChecked: true,
            plan.Marks.Count,
            plan.Marks.Count(static mark => string.IsNullOrWhiteSpace(mark.Term)),
            Raster: raster.Count > 0 ? raster : null);
    }
}
