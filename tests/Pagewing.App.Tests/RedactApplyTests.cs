using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// Applying the redaction marks (milestone 14, task 13): the preview, the card with the numbers and every list, the refusals with
/// their pages, the questions before it (comments saved first), the work and what the tab shows afterwards, the check that finds
/// something, the locks against printing, Ctrl+S, Esc, closing and the update path, and the report. Every file is a copy in a temp
/// folder; nothing is printed and nothing leaves the temp folder. The analysis and the writer are replaced where a test needs a
/// result that real files do not give (every kind of line, a failing check); the others run the real pipeline.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class RedactApplyTests : IDisposable
{
    // Redacts end to end: the term hides in a form object, a note, a form field, invisible text, a bookmark, the properties, the XMP
    // and an attachment, and has no tiling pattern (PDFium cannot redact around one: redact-everywhere.pdf is the refused case).
    private const string Fixture = "redact-everywhere-nopattern.pdf";

    private readonly SaveCardHost _host = new();
    private readonly CultureInfo _originalCulture = Strings.Culture;

    public void Dispose()
    {
        _host.Dispose();
        Strings.Culture = _originalCulture;
    }

    // ------------------------------------------------------------------ the preview

    [AvaloniaFact]
    public async Task PreviewDrawsOpaqueAreasAndChangesNothing()
    {
        var (window, viewModel, view, tab, path) = await MarkedAsync();
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var document = tab.Document!;
        var analyzed = new List<(PdfDocument Document, string Path)>();
        viewModel.RedactionAnalyzer = (_, shown, file, _) =>
        {
            analyzed.Add((shown, file));
            return Task.FromResult(FakeAnalysis());
        };
        tab.RedactionPreviewDelay = TimeSpan.Zero;
        tab.ClearMarkSelection();
        var version = tab.RedactionHistory!.Version;
        var plan = tab.RedactionPlan;
        var overlay = view.RedactionOverlay!;
        Assert.False(overlay.IsPreview);
        Assert.Empty(window.SaveCards);

        // Without the preview the mark is a tint with a hatch: the page shows through.
        var area = MarkArea(view, Mark);
        using (var frame = FramePixels.Snapshot(LayersOf(view)))
        {
            Assert.NotEqual(Avalonia.Media.Colors.Black, Pixel(frame, area.Center));
        }

        tab.ToggleRedactionPreviewCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.RedactionPreviewText is not null && tab.RedactionPreviewText != Strings.RedactPreviewWorking);

        // The area is opaque black, exactly the rectangle of the mark; the numbers come from the analysis of the shown document.
        Assert.True(tab.IsRedactionPreview);
        Assert.True(overlay.IsPreview);
        Assert.Equal(Avalonia.Media.Colors.Black, overlay.PreviewFillColor);
        using (var frame = FramePixels.Snapshot(LayersOf(view)))
        {
            Assert.Equal(Avalonia.Media.Colors.Black, Pixel(frame, area.Center));
            Assert.Equal(Avalonia.Media.Colors.Black, Pixel(frame, new Point(area.Left + 3, area.Center.Y)));
        }

        Assert.Equal("Removes: 38 characters, 1 image, 2 annotations; 1 text object as a whole", tab.RedactionPreviewText);
        var toolbar = Find<RedactionToolbar>(window, "RedactionToolbar");
        Assert.True(Find<Border>(toolbar, "PreviewRow").IsVisible);
        Assert.Equal(tab.RedactionPreviewText, Find<TextBlock>(toolbar, "PreviewText").Text);
        Assert.All(analyzed, call => Assert.Same(document, call.Document));
        Assert.All(analyzed, call => Assert.Equal(path, call.Path));

        // White is the other fill.
        tab.RedactionFill = RedactionFill.White;
        Settle(window);
        Assert.Equal(Avalonia.Media.Colors.White, overlay.PreviewFillColor);

        // Nothing changed: the plan (but for the fill), the document, the file, the editor.
        tab.RedactionFill = RedactionFill.Black;
        Assert.Same(document, tab.Document);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(plan!.Marks, tab.RedactionPlan.Marks);
        Assert.Equal(version + 2, tab.RedactionHistory.Version);

        // The preview off: the tint is back.
        tab.ToggleRedactionPreviewCommand.Execute(null);
        Settle(window);
        Assert.False(overlay.IsPreview);
        Assert.Null(tab.RedactionPreviewText);
        Assert.False(Find<Border>(toolbar, "PreviewRow").IsVisible);
        using (var frame = FramePixels.Snapshot(LayersOf(view)))
        {
            Assert.NotEqual(Avalonia.Media.Colors.Black, Pixel(frame, area.Center));
        }

        // No card, no apply: the preview is only a view.
        Assert.Empty(window.SaveCards);
        Assert.Null(viewModel.PendingSave);
    }

    // ------------------------------------------------------------------ the questions before the card

    [AvaloniaFact]
    public async Task ApplyAsksToSaveAnnotationsFirstWhenDirty()
    {
        // The comment is made before the mode starts (the mode locks commenting); the marks come after it.
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, Fixture);
        await MarkAsync(window, tab);
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.True(tab.AddAreaMark(0, Mark));
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis());
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // "Cancel" on the card: nothing is written, the marks, the mode and the comment stay.
        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveAnnotationsFirst);
        Assert.Equal(Strings.RedactSaveFirstText, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Save, PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.Equal(PendingSaveChoice.Save, viewModel.PendingSave.DefaultChoice);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.True(tab.IsDirty);
        Assert.False(tab.IsSaving);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // "Save": the incremental save runs (the comment, no rewrite), then the card of the apply comes; "Back" there leaves the
        // file saved and the marks staged.
        apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveAnnotationsFirst);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Save));
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.False(tab.IsDirty);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.False(tab.IsSaving);
    }

    // ------------------------------------------------------------------ the card

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task CardShowsCountsFallbacksCleanupAndLosses(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(FakeAnalysis());

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var info = viewModel.PendingSave!.Redaction!;

        // The numbers of the whole file: characters, images, annotations, and the text objects that went as a whole.
        Assert.Equal(
            language == "en"
                ? "Removes: 38 characters, 1 image, 2 annotations; 1 text object as a whole"
                : "Entfernt: 38 Zeichen, 1 Bild, 2 Annotationen; 1 Textobjekt ganz",
            info.Summary);
        Assert.Equal(info.Summary, Find<TextBlock>(card, "RedactApplySummary").Text);

        // Every part of the card is on it: the fallbacks, the cleanup (this file's numbers), the losses, what is not cleaned.
        var shown = card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.All(info.AllTexts.Where(static text => text.Length > 0), text => Assert.Contains(text, shown));
        var headings = info.Sections.Select(static section => section.Heading).ToList();
        Assert.Equal(
            [Strings.RedactSecFallbacks, Strings.RedactSecCleaned, Strings.ApplyLossesHeading, Strings.RedactSecNotCleaned, Strings.RedactSecAfter],
            headings);
        Assert.Contains(headings, heading => heading.Contains(language == "en" ? "Will be cleaned" : "Wird bereinigt", StringComparison.Ordinal));
        var fallback = Assert.Single(info.Sections[0].Lines);
        Assert.Contains("1", fallback, StringComparison.Ordinal);
        var cleaned = info.Sections[1].Lines;
        Assert.Equal(7, cleaned.Count);
        Assert.Contains(cleaned, line => line.Contains("up to 3", StringComparison.Ordinal) || line.Contains("bis zu 3", StringComparison.Ordinal));
        Assert.Contains(cleaned, line => line.Contains('5'));
        var lost = info.Sections[2].Lines;
        Assert.Equal(4, lost.Count);
        Assert.Contains(lost, line => line.StartsWith("4 ", StringComparison.Ordinal));

        // The values of the properties are never repeated on the card: it names the field and says the text is not shown.
        Assert.DoesNotContain(info.AllTexts, text => text.Contains("Max Mustermann", StringComparison.Ordinal) || text.Contains("Annual report", StringComparison.Ordinal));
        Assert.Contains(cleaned, line => line.Contains(language == "en" ? "Author" : "Autor", StringComparison.Ordinal));

        // The sentence that it cannot be undone, and no button that does anything before the user chooses.
        Assert.Equal(language == "en" ? "Redacting cannot be undone." : "Das Schwärzen lässt sich nicht rückgängig machen.", viewModel.PendingSave.Message);
        Assert.Contains(viewModel.PendingSave.Message, shown);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
    }

    [AvaloniaFact]
    public async Task DefaultButtonIsSaveAsWithRedactedSuggestion()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync(copyName: "contract.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis());

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var question = viewModel.PendingSave!;

        // "Save as …" is the default: it has the focus and Enter; the original stays the safe choice.
        Assert.Equal(PendingSaveChoice.SaveAs, question.DefaultChoice);
        Assert.Equal([PendingSaveChoice.SaveAs, PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel], question.Choices);
        var saveAs = card.ButtonOf(PendingSaveChoice.SaveAs)!;
        Assert.True(saveAs.IsFocused);
        Assert.Contains("primary", saveAs.Classes);
        Assert.Equal(["Save as …", "Save in the original file", "Back"], ShownChoices(card));
        Assert.All(
            new[] { PendingSaveChoice.SaveAs, PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel },
            choice => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(card.ButtonOf(choice)!))));

        // Writing into the original is dangerous: not the default, locked for the first 400 ms, on the manual clock.
        Assert.True(PendingSave.IsDangerous(PendingSaveChoice.SaveInOriginal));
        var inOriginal = card.ButtonOf(PendingSaveChoice.SaveInOriginal)!;
        Assert.DoesNotContain("primary", inOriginal.Classes);
        Assert.False(inOriginal.IsEnabled);
        Assert.False(question.Choose(PendingSaveChoice.SaveInOriginal));
        _host.PassTheLock(window);
        Assert.True(inOriginal.IsEnabled);

        // Enter takes the default: the file dialog opens with "<name>-redacted.pdf" (cancelled here: nothing is written).
        _host.Picker.Result = null;
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(await apply);
        Assert.Equal(1, _host.Picker.Calls);
        Assert.Equal("contract-redacted.pdf", _host.Picker.LastSuggestedName);
        Assert.True(tab.HasPendingRedactions);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaTheory]
    [InlineData("signed-docmdp-none.pdf")]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    [InlineData("signed-docmdp-3.pdf")]
    public async Task SignedDocumentShowsTheWarningAndNeverSaysValid(string fixture)
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync(fixture);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // The real analysis of the dummy signature, for every level: the warning is on the card each time.
        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var lines = viewModel.PendingSave!.Redaction!.Sections.SelectMany(static section => section.Lines).ToList();
        Assert.Contains(Strings.RedactLossSignature, lines);
        Assert.Contains("invalid", Strings.RedactLossSignature, StringComparison.Ordinal);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.RedactLossSignature);
        Assert.All(lines.Concat(viewModel.PendingSave.Redaction.AllTexts).Append(viewModel.PendingSave.Message), line => Assert.DoesNotMatch(ValidWord(), line));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ the refusals

    [AvaloniaFact]
    public async Task EncryptedRefusalCardNamesTheReason()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var rewrites = 0;
        viewModel.RedactionRewriter = (_, _, _, _, _) =>
        {
            rewrites++;
            throw new InvalidOperationException("must not be called");
        };
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis([new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "The file is encrypted.")]));

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);

        // The kind and the reason; no "Apply", no "Save as": "Back" alone; the plan stays.
        var info = viewModel.PendingSave!.Redaction!;
        var section = Assert.Single(info.Sections);
        Assert.Equal(Strings.RedactRefusalHeadingEncrypted, section.Heading);
        Assert.Equal([Strings.RedactRefusalEncrypted], section.Lines);
        Assert.Equal(["Back"], ShownChoices(card));
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.Equal(Strings.RedactRefusedNothing, viewModel.PendingSave.Message);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.RedactRefusalEncrypted);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.Equal(0, _host.Picker.Calls);
        Assert.True(tab.HasPendingRedactions);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task PageKeyRefusalCardNamesTheKeysAndPages()
    {
        // The real analysis of a file whose first page carries all four keys PDFium cannot remove: refused, with the pages.
        var (window, viewModel, _, tab, path) = await MarkedAsync("redact-structure.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);

        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections);
        Assert.Equal(Strings.RedactRefusalHeadingKeys, section.Heading);
        Assert.Contains(section.Lines, line => line.Contains("page 1", StringComparison.Ordinal));
        var keys = Assert.Single(section.Lines, line => line.StartsWith("Keys:", StringComparison.Ordinal));
        foreach (var key in new[] { "/Thumb", "/PieceInfo", "/Metadata", "/AA" })
        {
            Assert.Contains(key, keys, StringComparison.Ordinal);
        }

        Assert.Equal(["Back"], ShownChoices(card));
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);

        // Nothing was written, nothing was created, the marks stay.
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.Equal(0, _host.Picker.Calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.Single(Directory.GetFiles(_host.Folder, "*.pdf"));

        // The keys that could not be checked are told apart by the flag, not by a text.
        var unverified = RedactionApplyInfo.ForRefusals([new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [], "The page keys could not be checked.", PageKeysUnverified: true)]);
        Assert.Equal(Strings.RedactRefusalHeadingKeysUnverified, unverified.Sections[0].Heading);
        Assert.Equal([Strings.RedactRefusalKeysUnverified], unverified.Sections[0].Lines);
    }

    [AvaloniaFact]
    public async Task PageNotRedactableCardNamesPageAndReasonWithoutPromisingAFutureVersion()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis(
        [
            new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [2], RenderCheckReason),
        ]));

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);

        // "This page cannot be redacted, because ...": the page and the reason of the check; no greyed offer.
        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections);
        Assert.Equal(Strings.RedactRefusalHeadingPage, section.Heading);
        Assert.Equal(
            "Page 3 cannot be redacted, because redacting it would change content outside the marked areas (PDFium cannot write this page again without changing it). The file is not redacted.",
            Assert.Single(section.Lines));
        Assert.Equal(["Back"], ShownChoices(card));
        Assert.All(card.GetVisualDescendants().OfType<Button>(), button => Assert.True(button.IsEnabled || !button.IsVisible));

        // Nothing in any language promises a later version or another way: that belongs to a later milestone.
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var texts = RedactionApplyInfo.ForRefusals(
            [
                new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "x"),
                new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [0], "Page keys that cannot be removed: /Thumb on page 1."),
                new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [2], "reason"),
            ]).AllTexts.ToList();
            Assert.All(texts, text => Assert.DoesNotMatch(FutureWords(), text));
        }

        Strings.Culture = new CultureInfo("en");
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
    }

    // The reason the render check of the rewritten document gives (PdfDocument.Redaction.cs).
    private const string RenderCheckReason = "The page looks different outside the marked areas after it is written again (12 pixels, around x 1 to 2 pt and y 3 to 4 pt from the top left): PDFium cannot write this page without changing it.";

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task ThePageReasonIsSaidPerCauseInTheLanguageOfTheInterface(string language)
    {
        Strings.Culture = new CultureInfo(language);
        await Task.CompletedTask;
        (RedactionPageCause Cause, string Reason)[] reasons =
        [
            (RedactionPageCause.ChangesOutsideAreas, RenderCheckReason),
            (RedactionPageCause.SizeChanged, "The page changed its size when it was written again."),
            (RedactionPageCause.RenderFailed, "The page could not be rendered for the check, so it cannot be shown that nothing outside the marked areas changed."),
            (RedactionPageCause.ReopenFailed, "The rewritten document could not be opened again for the check (NotAPdf)."),
            (RedactionPageCause.TooLarge, "The page has 90000 objects; at most 50000 are handled."),
            (RedactionPageCause.Unreadable, "The geometry of the page could not be read."),
            (RedactionPageCause.RewriteUnreliable, "PDFium could not write the content of the page."),
            (RedactionPageCause.BigShape, "A shape that covers more than half of the page and is not a plain filled rectangle is under a rectangle; it cannot be cut and is never removed as a whole."),
            (RedactionPageCause.AnnotationsTooComplex, "The page has 30000 annotations; at most 20000 are handled."),
        ];
        Assert.Equal(Enum.GetValues<RedactionPageCause>().Where(static cause => cause != RedactionPageCause.Unknown).Order(), reasons.Select(static r => r.Cause).Order());

        var lines = new Dictionary<RedactionPageCause, IReadOnlyList<string>>();
        foreach (var (cause, reason) in reasons)
        {
            Assert.Equal(cause, RedactionPageCauses.Classify(reason));
            var section = Assert.Single(RedactionApplyInfo.ForRefusals([new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [4], reason)]).Sections);
            lines[cause] = section.Lines;

            // The sentence is the interface's own: the page, the cause in its language, none of the English of the engine.
            var first = section.Lines[0];
            Assert.StartsWith(language == "en" ? "Page 5 cannot be redacted, because " : "Seite 5 lässt sich nicht schwärzen, weil ", first, StringComparison.Ordinal);
            Assert.DoesNotContain(reason, string.Join(' ', section.Lines), StringComparison.Ordinal);

            // The hint belongs to the one cause it is true for.
            Assert.Equal(cause == RedactionPageCause.AnnotationsTooComplex, section.Lines.Contains(Strings.RedactRefusalPageHint));
            Assert.Equal(cause == RedactionPageCause.AnnotationsTooComplex ? 2 : 1, section.Lines.Count);
        }

        // Each cause says its own thing: a reopen that failed is not "would change content outside the marked areas".
        Assert.Equal(reasons.Length, lines.Values.Select(static l => l[0]).Distinct().Count());
        Assert.DoesNotContain(language == "en" ? "outside the marked areas" : "außerhalb der markierten Bereiche", lines[RedactionPageCause.ReopenFailed][0], StringComparison.Ordinal);

        // A reason in no group stays English, is named so, and gets no hint and no guess.
        var unknown = Assert.Single(RedactionApplyInfo.ForRefusals([new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [4], "Something new happened.")]).Sections);
        Assert.Equal(language == "en" ? "Page 5 cannot be redacted. The file is not redacted." : "Seite 5 lässt sich nicht schwärzen. Die Datei wird nicht geschwärzt.", unknown.Lines[0]);
        Assert.Contains(string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.RedactRefusalPageReason), "Something new happened."), unknown.Lines);
        Assert.DoesNotContain(Strings.RedactRefusalPageHint, unknown.Lines);
        Assert.DoesNotContain(unknown.Lines.Concat(lines.Values.SelectMany(static l => l)), line => FutureWords().IsMatch(line));
        Strings.Culture = new CultureInfo("en");
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task RefusalCardsAreEnglishAndGerman(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        RedactionRefusal[] refusals =
        [
            new(RedactionRefusalKind.Encrypted, [], "x"),
            new(RedactionRefusalKind.UnremovablePageKeys, [0, 1], "Page keys that cannot be removed: /Thumb on pages 1, 2."),
            new(RedactionRefusalKind.UnremovablePageKeys, [], "The page keys could not be checked.", PageKeysUnverified: true),
            new(RedactionRefusalKind.PageNotRedactable, [4], "reason"),
        ];
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis(refusals));

        var apply = viewModel.ApplyRedactionsAsync(tab);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);
        var shown = card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        // One part per reason, in the language of the interface: the heading, the sentence, the pages as page numbers.
        var sections = viewModel.PendingSave!.Redaction!.Sections;
        Assert.Equal(4, sections.Count);
        Assert.Equal(language == "en" ? "The file is encrypted" : "Die Datei ist verschlüsselt", sections[0].Heading);
        Assert.Contains(sections[1].Lines, line => line.Contains(language == "en" ? "pages 1, 2" : "Seiten 1, 2", StringComparison.Ordinal));
        Assert.Contains(language == "en" ? "could not be checked" : "nicht geprüft werden", sections[2].Heading, StringComparison.Ordinal);
        Assert.StartsWith(language == "en" ? "Page 5 cannot be redacted" : "Seite 5 lässt sich nicht schwärzen", sections[3].Lines[0], StringComparison.Ordinal);
        Assert.All(sections.SelectMany(static section => section.Lines.Prepend(section.Heading)), text => Assert.Contains(text, shown));
        Assert.Equal(language == "en" ? "Nothing was written. The marks stay." : "Es wurde nichts geschrieben. Die Markierungen bleiben.", viewModel.PendingSave.Message);
        Assert.Equal([language == "en" ? "Back" : "Zurück"], ShownChoices(card));
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
    }

    // ------------------------------------------------------------------ the work

    [AvaloniaFact]
    public async Task ApplyWritesAndTheTabShowsTheRedactedFileAtTheNewPath()
    {
        var (window, viewModel, view, tab, path) = await MarkedAsync(area: false);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var oldDocument = tab.Document!;
        var headline = await HeadlineAreaAsync(oldDocument);
        Assert.True(tab.AddAreaMark(0, headline));
        tab.ZoomMode = ZoomMode.Free;
        tab.ApplyFitZoom(1.25);
        var zoom = tab.Zoom;
        var target = Path.Combine(_host.Folder, "redacted.pdf");
        _host.Picker.Result = target;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        // The tab shows the redacted file at the new path, with the view it had; the marks and the mode are gone; the original is
        // byte for byte as it was.
        Assert.Equal(Path.GetFullPath(target), tab.FullPath);
        Assert.NotSame(oldDocument, tab.Document);
        Assert.Same(tab.Session.Document, tab.Document);
        Assert.Equal(2, tab.Document!.PageCount);
        Assert.False(tab.HasPendingRedactions);
        Assert.Null(tab.RedactionPlan);
        Assert.Null(tab.RedactionHistory);
        Assert.False(tab.IsRedacting);
        Assert.False(tab.IsDirty);
        Assert.False(tab.IsSaving);
        Assert.Equal(zoom, tab.Zoom);
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(target));
        Assert.DoesNotContain("Redaction fixture", await TextOfAsync(tab.Document, 0), StringComparison.Ordinal);
        Assert.Contains("Seite zwei", await TextOfAsync(tab.Document, 1), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));

        // The report is shown: what was removed, cleaned and checked, and what was not; "Close" alone.
        var card = await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);
        var report = viewModel.PendingSave!.Redaction!;
        Assert.Equal("“redacted.pdf” was redacted", viewModel.PendingSave.Title);
        Assert.Contains(Strings.RedactSecRemoved, report.Sections.Select(static section => section.Heading));
        Assert.Contains(Strings.RedactSecChecked, report.Sections.Select(static section => section.Heading));
        Assert.Contains(Strings.RedactSecNotChecked, report.Sections.Select(static section => section.Heading));
        Assert.Equal(["Close"], ShownChoices(card));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Close));
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);

        // The marks can start again on the new file.
        Assert.True(tab.RedactCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SaveInTheOriginalRedactsTheFileAtItsPath()
    {
        var (window, viewModel, view, tab, path) = await MarkedAsync(area: false);
        var oldDocument = tab.Document!;
        Assert.True(tab.AddAreaMark(0, await HeadlineAreaAsync(oldDocument)));

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        _host.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveInOriginal));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        // The file at the path of the tab is the redacted one now; nothing was asked for a name, nothing is left over.
        Assert.Equal(path, tab.FullPath);
        Assert.Equal(0, _host.Picker.Calls);
        Assert.NotSame(oldDocument, tab.Document);
        Assert.DoesNotContain("Redaction fixture", await TextOfAsync(tab.Document!, 0), StringComparison.Ordinal);
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.IsRedacting);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.old"));
        await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);
        viewModel.PendingSave!.Abandon();
    }

    [AvaloniaFact]
    public async Task AFailureAfterTheWriteSaysTheFileIsWrittenAndReopensIt()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var target = Path.Combine(_host.Folder, "written.pdf");
        _host.Picker.Result = target;

        // The checked file is written, but the tab cannot take it over: the card says it is written (never "not saved").
        viewModel.RedactionRewriter = async (session, engine, request, progress, token) =>
        {
            var result = await RedactionApplier.WriteAsync(engine, tab.Document!, path, null, null, request, progress, token);
            await result.Saved.Reopened.DisposeAsync();
            throw new RedactionAdoptException(request.TargetPath, new InvalidOperationException("injected"));
        };
        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        Assert.Contains(Path.GetFullPath(target), viewModel.PendingSave!.Message, StringComparison.Ordinal);
        Assert.Equal("The redacted file was written to “written.pdf”", viewModel.PendingSave.Title);
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.True(await apply);

        // The marks are given up (the file has them), the written file opens in the tab, the original is as it was.
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.IsRedacting);
        await WaitUntilAsync(window, () => tab.FullPath == Path.GetFullPath(target) && tab.HasShownSharpPage);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task AFailureOfTheTabAfterTheWriteSaysTheFileIsWrittenNeverNotSaved()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var target = Path.Combine(_host.Folder, "written-late.pdf");
        _host.Picker.Result = target;

        // The real pipeline writes and adopts the file; then the tab fails to show it (a seam after the write).
        viewModel.RedactionAfterWrite = static _ => throw new InvalidOperationException("injected");
        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        Assert.Equal("The redacted file was written to “written-late.pdf”", viewModel.PendingSave!.Title);
        Assert.Contains(Path.GetFullPath(target), viewModel.PendingSave.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not", viewModel.PendingSave.Title, StringComparison.OrdinalIgnoreCase);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.True(await apply);

        Assert.False(tab.HasPendingRedactions);
        await WaitUntilAsync(window, () => tab.FullPath == Path.GetFullPath(target) && tab.HasShownSharpPage);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(target));
    }

    [AvaloniaFact]
    public async Task SaveAsWithTheNameOfTheOriginalAsksTheLockedConfirmationFirst()
    {
        var (window, viewModel, view, tab, path) = await MarkedAsync(area: false);
        var oldDocument = tab.Document!;
        Assert.True(tab.AddAreaMark(0, await HeadlineAreaAsync(oldDocument)));
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        _host.Picker.Result = path;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var first = viewModel.PendingSave!;
        Assert.True(first.Resolve(PendingSaveChoice.SaveAs));

        // The file dialog named the original: the same lock as "Save in the original file" stands in front of the replacing.
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } next && !ReferenceEquals(next, first) && next.Kind == PendingSaveKind.ApplyRedactions);
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var question = viewModel.PendingSave!;
        Assert.Equal([PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel], question.Choices);
        Assert.Equal(PendingSaveChoice.Cancel, question.DefaultChoice);
        Assert.False(card.ButtonOf(PendingSaveChoice.SaveInOriginal)!.IsEnabled);
        Assert.False(question.Choose(PendingSaveChoice.SaveInOriginal));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        _host.PassTheLock(window);
        Assert.True(question.Resolve(PendingSaveChoice.SaveInOriginal));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Redaction fixture", await TextOfAsync(tab.Document!, 0), StringComparison.Ordinal);
        await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);
        viewModel.PendingSave!.Abandon();
    }

    [AvaloniaFact]
    public async Task BackOnTheConfirmationOfTheOriginalLeavesTheFileAndTheMarks()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        _host.Picker.Result = path;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        var first = viewModel.PendingSave!;
        Assert.True(first.Resolve(PendingSaveChoice.SaveAs));
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } next && !ReferenceEquals(next, first));
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));

        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task VerificationFailureShowsFindingAndKeepsPlanAndFiles()
    {
        // The real pipeline fails its check: the engine "forgets" the place of a marked text (a test hook of the core), so the page
        // text of the written file still holds it. The saver, not a stand-in, refuses to replace anything.
        var (window, viewModel, _, tab, path) = await MarkedAsync(area: false);
        var headline = await HeadlineAreaAsync(tab.Document!);
        Assert.True(tab.AddRedactionMarks([new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, 0, [headline], "Redaction fixture", Everywhere: false)]));
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var document = tab.Document;
        var hooks = new RedactionApplyHooks { AreasToRedact = static (_, _) => [] };
        viewModel.RedactionRewriter = (session, engine, request, progress, token) => RedactionApplier.ApplyAsync(session, engine, request, progress, hooks, token);
        var target = Path.Combine(_host.Folder, "redacted.pdf");
        _host.Picker.Result = target;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionFailed);

        // The finding names its kind and page, never the text; "Back" alone; the real saver wrote nothing.
        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections);
        Assert.Equal(Strings.RedactSecFindings, section.Heading);
        Assert.Contains(section.Lines, line => line.Contains("page text of the result (page 1)", StringComparison.Ordinal));
        Assert.All(section.Lines, line => Assert.DoesNotContain("Redaction fixture", line, StringComparison.Ordinal));
        Assert.Equal(["Back"], ShownChoices(card));
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);

        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.Same(document, tab.Document);
        Assert.Equal(path, tab.FullPath);
        Assert.False(tab.IsSaving);
        Assert.False(File.Exists(target));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.Single(Directory.GetFiles(_host.Folder, "*.pdf"));

        // A check that could not be completed has no details: the card says so instead of "found nothing".
        Assert.Equal([Strings.RedactFailureNotCompleted], RedactionApplyInfo.ForFailure(null).Sections[0].Lines);
    }

    // Only the mapping of a failure of the writer to its card: the writer is a stand-in that throws, so what it says about files is
    // not evidence (VerificationFailureShowsFindingAndKeepsPlanAndFiles runs the real pipeline). The core pins "nothing is written"
    // with RedactionApplierTests.ALeakMakesTheApplyFailAndWritesNothing.
    [AvaloniaFact]
    public async Task AFailedCheckIsMappedToACardWithKindAndPage()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis());
        viewModel.RedactionRewriter = (_, _, _, _, _) => throw new SaveFailureException(SaveFailureReason.RedactionFailed, "injected")
        {
            Details = new RedactionCheck([new RedactionFinding(RedactionFindingKind.TextStillPresent, 1, "secret detail")], []),
        };
        var target = Path.Combine(_host.Folder, "redacted.pdf");
        _host.Picker.Result = target;

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        var card = await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionFailed);

        // The kind and the page of the finding, never the text itself; "Back" alone.
        var section = Assert.Single(viewModel.PendingSave!.Redaction!.Sections);
        Assert.Equal(Strings.RedactSecFindings, section.Heading);
        var line = Assert.Single(section.Lines);
        Assert.Contains("page text of the result (page 2)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        Assert.Equal(Strings.RedactFailedNothing, viewModel.PendingSave.Message);
        Assert.Equal(["Back"], ShownChoices(card));
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceChangedMeanwhileEndsInACardWithCancel(bool duringTheWrite)
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var target = Path.Combine(_host.Folder, "redacted.pdf");
        _host.Picker.Result = target;
        void ChangeTheFile()
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.WriteByte((byte)'\n');
        }

        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        if (duringTheWrite)
        {
            // The real pipeline: another program appends to the opened file while the checked copy is being written (a test hook of the
            // saver runs after the temporary file is written). The saver itself finds the source changed and replaces nothing.
            var hooks = new RedactionApplyHooks { Faults = new SaveFaults { AfterWrite = ChangeTheFile } };
            viewModel.RedactionRewriter = (session, engine, request, progress, token) => RedactionApplier.ApplyAsync(session, engine, request, progress, hooks, token);
        }
        else
        {
            // Another program rewrote the opened file in place: the analysis refuses before anything is built.
            ChangeTheFile();
        }

        var apply = viewModel.ApplyRedactionsAsync(tab);
        if (duringTheWrite)
        {
            await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
            Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        }

        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        // The card says the file changed; "Cancel" alone, and nothing can overwrite it. The marks stay.
        Assert.Equal(Strings.RedactErrorFileChanged, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.DoesNotContain(PendingSaveChoice.Overwrite, viewModel.PendingSave.Choices);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);

        // The file is the one the other program left (the original and its one byte); the target does not exist; nothing is left over.
        var left = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original.Append((byte)'\n').ToArray(), left);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.Single(Directory.GetFiles(_host.Folder, "*.pdf"));
    }

    // ------------------------------------------------------------------ keys, locks and the way out

    [AvaloniaFact]
    public async Task CtrlSOpensTheApplyCardWhileMarksArePending()
    {
        var (window, viewModel, view, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(FakeAnalysis());

        // Marks make the tab saveable although nothing else is unsaved; Ctrl+S opens the card of "Apply", not the save of comments.
        Assert.False(tab.IsDirty);
        Assert.True(tab.SaveCommand.CanExecute(null));
        view.Focus();
        Press(window, Key.S, PhysicalKey.S, RawInputModifiers.Control);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.Contains("38", viewModel.PendingSave!.Redaction!.Summary, StringComparison.Ordinal);

        // The keys of the document do nothing behind the card, and Esc answers "Back".
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.HasPendingRedactions);

        // "Save as" (Ctrl+Shift+S) leads to the same card.
        Press(window, Key.S, PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        viewModel.PendingSave!.Abandon();
    }

    [AvaloniaFact]
    public async Task ApplyIsBlockedWhilePrintingAndViceVersa()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync();
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis());
        Assert.True(tab.ApplyRedactionsCommand.CanExecute(null));

        // A print job runs: no apply, no Ctrl+S card, the reason is the one of saving.
        tab.IsPrinting = true;
        Assert.False(tab.ApplyRedactionsCommand.CanExecute(null));
        Assert.False(tab.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.SaveBlockedPrinting, tab.SaveBlockedReason);
        Assert.False(await viewModel.ApplyRedactionsAsync(tab));
        Assert.Null(viewModel.PendingSave);
        tab.IsPrinting = false;
        Assert.True(tab.ApplyRedactionsCommand.CanExecute(null));

        // While the apply asks or runs, printing and editing the marks wait.
        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.False(viewModel.PrintCommand.CanExecute(null));
        Assert.False(tab.ApplyRedactionsCommand.CanExecute(null));
        Assert.False(tab.AddAreaMark(0, new PageRect(300, 300, 340, 320)));
        Assert.Single(tab.RedactionPlan!.Marks);
        viewModel.PendingSave!.Abandon();
        Assert.False(await apply);
        Assert.True(viewModel.PrintCommand.CanExecute(null));
        Assert.True(tab.ApplyRedactionsCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task EscCancelsTheCardNotThePlan()
    {
        var (window, viewModel, view, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(FakeAnalysis());

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.False(await apply);
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, _host.Picker.Calls);
        Assert.Same(tab.Session.Document, view.Document);

        // The same for the card of a refusal and for the report.
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis([new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "x")]));
        apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.RedactionRefused);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(await apply);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
    }

    [AvaloniaFact]
    public async Task CloseWithPendingMarksAsks()
    {
        var (window, viewModel, _, tab, path) = await MarkedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(tab.AsksBeforeClosing);

        // Nothing was removed from the file, and "Save" would not apply the marks: the question says that the marks are lost.
        var resolve = viewModel.ResolveUnsavedChangesAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        Assert.Equal(Strings.DiscardRedactionsText, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Discard, PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.Equal(PendingSaveChoice.Cancel, viewModel.PendingSave.DefaultChoice);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await resolve);
        Assert.True(tab.HasPendingRedactions);

        // "Close and discard": the marks go for good, the file is as it was.
        resolve = viewModel.ResolveUnsavedChangesAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Discard));
        Assert.True(await resolve);
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.IsRedacting);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ the texts

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task NoTextCallsAnythingLegallyBindingValidOrGuaranteed(string language)
    {
        Strings.Culture = new CultureInfo(language);
        using var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        var path = Path.Combine(_host.Folder, "a.pdf");
        var analysis = FakeAnalysis();
        var check = new RedactionCheck([], ["Raw bytes: something was not looked at."]);
        var infos = new List<RedactionApplyInfo>
        {
            RedactionApplyInfo.Create(analysis),
            RedactionApplyInfo.Create(CleanAnalysis()),
            RedactionApplyInfo.ForReport(analysis, check),
            RedactionApplyInfo.ForReport(CleanAnalysis(), new RedactionCheck([], [])),
            RedactionApplyInfo.ForFailure(null),
            RedactionApplyInfo.ForFailure(new RedactionCheck(Enum.GetValues<RedactionFindingKind>().Select(static kind => new RedactionFinding(kind, 0, "x", [0, 1])).ToList(), [])),
            RedactionApplyInfo.ForRefusals(
            [
                new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "x"),
                new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [0], "/Thumb on page 1"),
                new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [], "x", PageKeysUnverified: true),
                new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [1, 2], "reason"),
            ]),
        };
        var texts = infos.SelectMany(static info => info.AllTexts).ToList();
        foreach (var info in infos.Take(1))
        {
            foreach (var question in new[]
            {
                PendingSave.ApplyRedactions(tab, path, info),
                PendingSave.RedactionRefused(tab, path, info),
                PendingSave.RedactionFailed(tab, path, info),
                PendingSave.RedactionReport(tab, path, info),
                PendingSave.SaveAnnotationsFirst(tab, path, static () => Strings.RedactSaveFirstText),
                PendingSave.DiscardRedactions(tab, path),
                PendingSave.PageApplyError(tab, path, static () => Strings.RedactErrorNotPrepared),
                PendingSave.PageApplyError(tab, path, static () => Strings.RedactErrorDraft),
                PendingSave.PageApplyError(tab, path, static () => Strings.RedactErrorFileChanged),
            })
            {
                texts.Add(question.Title);
                texts.Add(question.Message);
            }
        }

        texts.AddRange(Enum.GetValues<PendingSaveChoice>().Select(PendingSave.LabelOf));
        texts.AddRange(AllRedactionTexts());
        await Task.CompletedTask;

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("garantiert", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guarantee", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(ValidWord(), text);
            Assert.DoesNotMatch(PromiseWords(), text);
        }

        // The signature line says "invalid", the one thing it may claim.
        Assert.Matches(language == "en" ? "invalid" : "ungültig", Strings.RedactLossSignature);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task TheReportNamesWhatWasNotChecked(string language)
    {
        Strings.Culture = new CultureInfo(language);
        await Task.CompletedTask;
        var analysis = FakeAnalysis();
        var check = new RedactionCheck([], ["Raw bytes: text in fonts with their own code mapping is not readable.", "Another program may read the file differently."]);

        var report = RedactionApplyInfo.ForReport(analysis, check);

        // What the check did, and what it did not: the lines of the check itself and the ones the file adds (marks without a term).
        var notChecked = Assert.Single(report.Sections, section => section.Heading == Strings.RedactSecNotChecked);
        Assert.Contains("Raw bytes: text in fonts with their own code mapping is not readable.", notChecked.Lines);
        Assert.Contains("Another program may read the file differently.", notChecked.Lines);
        Assert.Contains(notChecked.Lines, line => line.StartsWith("2 ", StringComparison.Ordinal));
        Assert.Equal(language != "en", notChecked.Lines.Contains(Strings.RedactSecNotCheckedNote));
        var checkedLines = Assert.Single(report.Sections, section => section.Heading == Strings.RedactSecChecked).Lines;
        Assert.Equal(6, checkedLines.Count);

        // The cleaned list tells only what was read back: XMP and scripts are under "not cleaned", not called cleaned.
        var cleaned = Assert.Single(report.Sections, section => section.Heading == Strings.RedactSecCleanedDone).Lines;
        Assert.DoesNotContain(cleaned, line => line.Contains("XMP", StringComparison.Ordinal) || line.Contains("JavaScript", StringComparison.Ordinal));
        Assert.Equal(5, cleaned.Count);

        // A check with findings has nothing to say about "checked" or "cleaned".
        var failing = RedactionApplyInfo.ForReport(analysis, new RedactionCheck([new RedactionFinding(RedactionFindingKind.TextStillPresent, 0, "x")], []));
        Assert.DoesNotContain(failing.Sections, section => section.Heading == Strings.RedactSecChecked || section.Heading == Strings.RedactSecCleanedDone);
        Assert.Contains(failing.Sections, section => section.Heading == Strings.RedactSecNotChecked);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task NoLineSaysThatPagesTurnedIntoImagesWereComparedWithTheOriginal(string language)
    {
        Strings.Culture = new CultureInfo(language);
        await Task.CompletedTask;
        var rasterPrefix = language == "en" ? "Pages turned into images (" : "In Bilder umgewandelte Seiten (";
        var inPlace = new PageRedactionImpact(0, 30, 2, 1, 1, 0, 0, 0, 0, 2, [], ["a"], null);
        var plain = FakeAnalysis() with { Raster = null };
        var rasterOnly = FakeAnalysis() with { Pages = [], Raster = [new RasterPageInfo(1, 200, 100, 100, false, 1000)] };
        var mixed = FakeAnalysis() with { Pages = [inPlace], Raster = [new RasterPageInfo(2, 200, 100, 100, false, 1000)] };
        var rasterMarked = FakeAnalysis() with { Pages = [inPlace with { Page = 2 }], Raster = [new RasterPageInfo(2, 200, 100, 100, false, 1000)] };

        // (c) Without pages turned into images nothing changes: the old lines, the old preview.
        var plainReport = Lines(RedactionApplyInfo.ForReport(plain, RenderCheck(0)), Strings.RedactSecChecked);
        Assert.Contains(Strings.RedactCheckedPages, plainReport);
        Assert.DoesNotContain(plainReport, line => line.StartsWith(rasterPrefix, StringComparison.Ordinal));
        Assert.Contains(Strings.RedactNcApplyRender, Lines(RedactionApplyInfo.ForReport(plain, RenderCheck(0)), Strings.RedactSecNotChecked));
        Assert.Contains(Strings.RedactCheckList, Lines(RedactionApplyInfo.Create(plain), Strings.RedactSecAfter));

        // (a) Only pages turned into images: no line says that a page was rendered and looks like the original.
        var onlyReport = RedactionApplyInfo.ForReport(rasterOnly, RenderCheck(1));
        var onlyChecked = Lines(onlyReport, Strings.RedactSecChecked);
        Assert.DoesNotContain(Strings.RedactCheckedPages, onlyChecked);
        Assert.DoesNotContain(Strings.RedactCheckedPagesInPlace, onlyChecked);
        Assert.Contains(onlyChecked, line => line.StartsWith(rasterPrefix, StringComparison.Ordinal));
        Assert.Contains(Strings.RedactNcApplyRenderRaster, Lines(onlyReport, Strings.RedactSecNotChecked));
        Assert.DoesNotContain(Strings.RedactNcApplyRender, Lines(onlyReport, Strings.RedactSecNotChecked));
        var onlyAfter = Lines(RedactionApplyInfo.Create(rasterOnly), Strings.RedactSecAfter);
        Assert.Contains(Strings.RedactCheckListRasterOnly, onlyAfter);
        Assert.DoesNotContain(Strings.RedactCheckList, onlyAfter);

        // (b) Both kinds: the render claim is for the page redacted in place only, and the raster pages have their own line.
        // A page that is in the list of pages and turned into an image (a mark on it) does not count as redacted in place.
        foreach (var analysis in new[] { mixed })
        {
            var report = RedactionApplyInfo.ForReport(analysis, RenderCheck(1));
            var checkedLines = Lines(report, Strings.RedactSecChecked);
            Assert.DoesNotContain(Strings.RedactCheckedPages, checkedLines);
            Assert.Contains(Strings.RedactCheckedPagesInPlace, checkedLines);
            Assert.Contains(checkedLines, line => line.StartsWith(rasterPrefix, StringComparison.Ordinal));
            Assert.Contains(Strings.RedactNcApplyRenderRaster, Lines(report, Strings.RedactSecNotChecked));
            Assert.Contains(Strings.RedactCheckListRaster, Lines(RedactionApplyInfo.Create(analysis), Strings.RedactSecAfter));
        }

        var markedReport = Lines(RedactionApplyInfo.ForReport(rasterMarked, RenderCheck(1)), Strings.RedactSecChecked);
        Assert.DoesNotContain(Strings.RedactCheckedPagesInPlace, markedReport);
        Assert.DoesNotContain(Strings.RedactCheckedPages, markedReport);

        static RedactionCheck RenderCheck(int rasterPages) =>
            new([], ["x"], [new RedactionNotChecked(RedactionNotCheckedKind.ApplyRender, "x", rasterPages)]);

        static IReadOnlyList<string> Lines(RedactionApplyInfo info, string heading) =>
            Assert.Single(info.Sections, section => section.Heading == heading).Lines;
    }

    [AvaloniaFact]
    public async Task EveryKindOfWhatWasNotCheckedHasASentenceInBothLanguages()
    {
        await Task.CompletedTask;
        var english = new Dictionary<RedactionNotCheckedKind, string>();
        foreach (var kind in Enum.GetValues<RedactionNotCheckedKind>().Where(static kind => kind != RedactionNotCheckedKind.Other))
        {
            var item = new RedactionNotChecked(kind, "English line", 2, 3, "detail", 40);
            Strings.Culture = new CultureInfo("en");
            var en = RedactionApplyInfo.SayNotChecked(item);
            Strings.Culture = new CultureInfo("de");
            var de = RedactionApplyInfo.SayNotChecked(item);

            // A kind without a sentence fails here: add it to RedactionApplyInfo.SayNotChecked and to both resx files.
            Assert.False(string.IsNullOrWhiteSpace(en), $"{kind} has no English sentence");
            Assert.False(string.IsNullOrWhiteSpace(de), $"{kind} has no German sentence");
            Assert.NotEqual(en, de);
            Assert.DoesNotContain("{0}", de, StringComparison.Ordinal);
            Assert.DoesNotMatch(ValidWord(), de!);
            english[kind] = en!;
        }

        // The ones with numbers say them, singular and plural.
        Strings.Culture = new CultureInfo("de");
        Assert.StartsWith("1 entfernter Text", RedactionApplyInfo.SayNotChecked(new RedactionNotChecked(RedactionNotCheckedKind.BlankTexts, "x", 1))!, StringComparison.Ordinal);
        Assert.StartsWith("2 entfernte Texte", RedactionApplyInfo.SayNotChecked(new RedactionNotChecked(RedactionNotCheckedKind.BlankTexts, "x", 2))!, StringComparison.Ordinal);
        Assert.Contains("3 Streams, der erste bei Byte 40", RedactionApplyInfo.SayNotChecked(new RedactionNotChecked(RedactionNotCheckedKind.RawPredictor, "x", 3, 0, "", 40))!, StringComparison.Ordinal);
        Assert.Contains("Stream bei Byte 40", RedactionApplyInfo.SayNotChecked(new RedactionNotChecked(RedactionNotCheckedKind.RawPredictor, "x", 1, 0, "", 40))!, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("en");

        // A line with no kind of its own has no sentence: the report shows the English text and says so.
        Assert.Null(RedactionApplyInfo.SayNotChecked(new RedactionNotChecked(RedactionNotCheckedKind.Other, "Raw bytes: a detail.")));
        Assert.Equal(Enum.GetValues<RedactionNotCheckedKind>().Length - 1, english.Count);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task TheReportOfARealRedactionIsSaidInTheLanguageOfTheInterface(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view, tab, _) = await MarkedAsync(area: false);
        Assert.True(tab.AddAreaMark(0, await HeadlineAreaAsync(tab.Document!)));
        _host.Picker.Result = Path.Combine(_host.Folder, "redacted.pdf");

        var apply = viewModel.ApplyRedactionsAsync(tab);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitForCardAsync<RedactReportCard>(window, viewModel, PendingSaveKind.RedactionReport);

        // The lines of the real check (the verifier's and the apply step's) are the interface's own sentences, none English in the
        // German report, and the note about English lines does not appear because there are none.
        var notChecked = Assert.Single(viewModel.PendingSave!.Redaction!.Sections, section => section.Heading == Strings.RedactSecNotChecked).Lines;
        Assert.Contains(Strings.RedactNcRawFont, notChecked);
        Assert.Contains(Strings.RedactNcOtherViewer, notChecked);
        Assert.Contains(Strings.RedactNcApplyRender, notChecked);
        Assert.Contains(Strings.RedactNcApplyKeys, notChecked);
        Assert.Contains(Strings.RedactNcApplyXmp, notChecked);
        Assert.DoesNotContain(Strings.RedactSecNotCheckedNote, notChecked);
        if (language == "de")
        {
            Assert.All(notChecked, line => Assert.DoesNotMatch(EnglishStart(), line));
        }
        else
        {
            // English stays what the engine says.
            Assert.Contains("Another program may read the file differently; open the result in a second viewer to be sure.", notChecked);
        }

        viewModel.PendingSave.Abandon();
        Assert.NotNull(view);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task CardAndReportNameWhatIsNotCleaned(string language)
    {
        Strings.Culture = new CultureInfo(language);
        await Task.CompletedTask;
        var analysis = FakeAnalysis();

        foreach (var info in new[] { RedactionApplyInfo.Create(analysis), RedactionApplyInfo.ForReport(analysis, new RedactionCheck([], [])) })
        {
            var section = Assert.Single(info.Sections, section => section.Heading == Strings.RedactSecNotCleaned);

            // Objects of the pages and actions of annotations that come with the pages, field values, signature data, marks without a term.
            Assert.Contains(Strings.RedactNotCleanedObjects, section.Lines);
            Assert.Contains(Strings.RedactNotCleanedActions, section.Lines);
            Assert.Contains(Strings.RedactNotCleanedFieldValues, section.Lines);
            Assert.Contains(Strings.RedactNotCleanedSignature, section.Lines);
            // The annotations that come along unchanged, and the paperclips (up to 2 files) whose files stay in the file.
            Assert.Contains(Strings.RedactNotCleanedAnnotations, section.Lines);
            Assert.Contains(Strings.RedactNotCleanedAnnotationFiles(2), section.Lines);
            Assert.Equal(7, section.Lines.Count);
            Assert.Contains("2", Strings.RedactNotCleanedAnnotationFiles(2), StringComparison.Ordinal);
            Assert.Contains(language == "en" ? "up to 2" : "bis zu 2", Strings.RedactNotCleanedAnnotationFiles(2), StringComparison.Ordinal);
            Assert.Contains("/Metadata", Strings.RedactNotCleanedObjects, StringComparison.Ordinal);
            Assert.Contains("/PieceInfo", Strings.RedactNotCleanedObjects, StringComparison.Ordinal);
            Assert.Contains("/AA", Strings.RedactNotCleanedActions, StringComparison.Ordinal);
        }

        // Both headings stay: "Will be cleaned" on the card, "Cleaned" in the report.
        Assert.StartsWith(language == "en" ? "Will be cleaned" : "Wird bereinigt", Strings.RedactSecCleaned, StringComparison.Ordinal);
        Assert.StartsWith(language == "en" ? "Cleaned" : "Bereinigt", Strings.RedactSecCleanedDone, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task EveryNewTextExistsInBothLanguages()
    {
        var keys = AllRedactionKeys();
        Assert.True(keys.Count >= 100);
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var key in keys)
            {
                var value = (string)typeof(Strings).GetProperty(key)!.GetValue(null)!;
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} in {language}");
                Assert.NotEqual(key, value);
                Assert.DoesNotMatch(ValidWord(), value);
            }
        }

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the toolbar

    [AvaloniaFact]
    public async Task TheToolbarOffersApplyAndPreviewAndNamesThem()
    {
        var (window, viewModel, _, tab, _) = await MarkedAsync(area: false);
        var toolbar = Find<RedactionToolbar>(window, "RedactionToolbar");

        // Without a mark "Done" is the primary button and there is nothing to apply.
        var apply = Find<Button>(toolbar, "ApplyButton");
        var done = Find<Button>(toolbar, "DoneButton");
        var preview = Find<Button>(toolbar, "PreviewButton");
        Assert.False(apply.IsVisible);
        Assert.Contains("primary", done.Classes);
        Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(preview)));

        // With a mark "Apply …" is the primary button and opens the card; "Done" waits for the marks to be dealt with.
        viewModel.RedactionAnalyzer = (_, _, _, _) => Task.FromResult(CleanAnalysis());
        Assert.True(tab.AddAreaMark(0, Mark));
        Settle(window);
        Assert.True(apply.IsVisible);
        Assert.Contains("primary", apply.Classes);
        Assert.DoesNotContain("primary", done.Classes);
        Assert.Equal(Strings.RedactApplyButton, Avalonia.Automation.AutomationProperties.GetName(apply));
        Assert.True(apply.Command!.CanExecute(null));
        apply.Command.Execute(null);
        await WaitForCardAsync<RedactApplyCard>(window, viewModel, PendingSaveKind.ApplyRedactions);
        Assert.False(apply.Command.CanExecute(null));
        viewModel.PendingSave!.Abandon();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(tab.HasPendingRedactions);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly PageRect Mark = new(20, 275, 200, 300);

    private async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View, TabViewModel Tab, string Path)> MarkedAsync(
        string fixture = Fixture, string? copyName = null, bool area = true)
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, fixture, copyName);
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        if (area)
        {
            Assert.True(tab.AddAreaMark(0, Mark));
        }

        Settle(window);
        return (window, viewModel, view, tab, path);
    }

    /// <summary>The first line of the first page: its words are gone from the redacted file.</summary>
    private static async Task<PageRect> HeadlineAreaAsync(PdfDocument document)
    {
        var text = await document.GetPageTextAsync(0, TestContext.Current.CancellationToken);
        var match = TextMatcher.Find(text.Chars, "Redaction fixture", new SearchOptions(MatchCase: true))[0];
        var boxes = text.Chars.Skip(match.FirstChar).Take(match.LastChar - match.FirstChar + 1).Where(static c => !c.IsGenerated).Select(static c => c.Box).ToList();
        return new PageRect(boxes.Min(static b => b.Left), boxes.Min(static b => b.Top), boxes.Max(static b => b.Right), boxes.Max(static b => b.Bottom));
    }

    private static async Task<string> TextOfAsync(PdfDocument document, int page) =>
        string.Concat((await document.GetPageTextAsync(page, TestContext.Current.CancellationToken)).Chars.Select(static c => c.Text));

    // The buttons of the choices that are shown, from left to right, with their text (a card with a long list also has the buttons of its scroll bar).
    private static List<string> ShownChoices(SaveCard card) =>
        card.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible && button.Name is { } name && name.StartsWith("Redact", StringComparison.Ordinal) && name.EndsWith("Button", StringComparison.Ordinal))
            .OrderBy(button => button.TranslatePoint(default, card)!.Value.X)
            .Select(button => (string)button.Content!)
            .ToList();

    private static Visual LayersOf(DocumentView view) => view.Canvas.GetVisualParent() ?? throw new InvalidOperationException("No layers.");

    private static Avalonia.Media.Color Pixel(FramePixels frame, Point canvasPoint) =>
        frame.GetColor((int)Math.Floor(canvasPoint.X), (int)Math.Floor(canvasPoint.Y));

    // Where the mark lies in the canvas (the layers share its origin).
    private static Rect MarkArea(DocumentView view, PageRect rect)
    {
        var (page, scale) = view.Canvas.PagePlacement(0) ?? throw new InvalidOperationException("No page.");
        return new Rect(
            new Point(page.X + (rect.Left * scale), page.Y + (rect.Top * scale)),
            new Point(page.X + (rect.Right * scale), page.Y + (rect.Bottom * scale)));
    }

    /// <summary>An analysis with a line of every kind: characters, images, annotations, a text object as a whole, a fallback, the cleanup and the losses.</summary>
    private static RedactionAnalysis FakeAnalysis() => new(
        [
            new PageRedactionImpact(0, 30, 2, 1, 1, 0, 0, 0, 0, 2, [new RedactionFallback(RedactionFallbackKind.TextObjectRemovedWhole, 0, "reason")], ["a"], null),
            new PageRedactionImpact(2, 8, 1, 0, 0, 0, 0, 0, 0, 0, [], ["b"], null),
        ],
        [
            new CleanupItem(CleanupKind.Author, "Max Mustermann"),
            new CleanupItem(CleanupKind.Title, "Annual report"),
            new CleanupItem(CleanupKind.Xmp, "3"),
            new CleanupItem(CleanupKind.Bookmarks, "5"),
            new CleanupItem(CleanupKind.Attachments, "1"),
            new CleanupItem(CleanupKind.JavaScript, "2"),
            new CleanupItem(CleanupKind.PageLabels, "1"),
        ],
        CleanupComplete: true,
        [
            new RedactionLoss(RedactionLossKind.InternalLinksLoseTheirTarget, 4),
            new RedactionLoss(RedactionLossKind.FormFieldsNotFillable, 2),
            new RedactionLoss(RedactionLossKind.ColorSpaceBecomesRgb, 1),
            new RedactionLoss(RedactionLossKind.SignatureInvalidated, 1),
        ],
        [],
        PageKeysChecked: true,
        Marks: 3,
        MarksWithoutTerm: 2,
        AttachmentAnnotations: 2);

    /// <summary>An analysis of a file with nothing to clean, optionally refused.</summary>
    private static RedactionAnalysis CleanAnalysis(IReadOnlyList<RedactionRefusal>? refusals = null) =>
        new([], [], CleanupComplete: true, [], refusals ?? [], PageKeysChecked: true, Marks: 1, MarksWithoutTerm: 1);

    // The keys this task added to the resx files (the commands, the toolbar, the preview, the cards, the refusals, the findings, the report).
    private static List<string> AllRedactionKeys() =>
    [
            "CommandRedactApply", "CommandRedactPreview", "RedactApplyButton", "RedactApplyTip", "RedactPreviewButton", "RedactPreviewTip",
            "RedactPreviewBarName", "RedactPreviewWorking", "RedactPreviewFailed", "RedactPreviewRefused", "RedactPreviewEmpty",
            "RedactPreviewNote", "RedactSumFormat", "RedactSumNothing", "RedactSumCharOne", "RedactSumChars", "RedactSumImageOne",
            "RedactSumImages", "RedactSumAnnotationOne", "RedactSumAnnotations", "RedactSumPathOne", "RedactSumPaths",
            "RedactSumFormObjectOne", "RedactSumFormObjects", "RedactSumTextObjectWholeOne", "RedactSumTextObjectsWhole",
            "RedactSecFallbacks", "RedactSecCleaned", "RedactSecCleanedDone", "RedactSecNotCleaned", "RedactSecAfter", "RedactSecRemoved",
            "RedactSecChecked", "RedactSecNotChecked", "RedactSecNotCheckedNote", "RedactSecFindings", "RedactCheckList",
            "RedactCheckNothingWritten", "RedactFallbackText", "RedactFallbackImage", "RedactFallbackPath", "RedactFallbackForm",
            "RedactNothingUnderMarks", "RedactPageRemoved", "RedactRemovedAnnotationsNote", "RedactFieldAuthor", "RedactFieldTitle",
            "RedactFieldSubject", "RedactFieldKeywords", "RedactFieldCreator", "RedactFieldProducer", "RedactCleanField", "RedactCleanXmp",
            "RedactCleanBookmarks", "RedactCleanAttachments", "RedactCleanScripts", "RedactCleanLabels", "RedactCleanNone",
            "RedactCleanNoneIncomplete", "RedactCleanIncomplete", "RedactCleanSeeBelow", "RedactLossLinksOne", "RedactLossLinks",
            "RedactLossFieldsOne", "RedactLossFields", "RedactLossRgbOne", "RedactLossRgb", "RedactLossSignature",
            "RedactNotCleanedObjects", "RedactNotCleanedActions", "RedactNotCleanedFieldValues", "RedactNotCleanedSignature", "RedactNotCleanedAnnotations", "RedactNotCleanedAnnotationFilesFormat",
            "RedactNotCheckedNoTermOne", "RedactNotCheckedNoTerm", "RedactCheckedText", "RedactCheckedBytes", "RedactCheckedAreas",
            "RedactCheckedPages", "RedactCheckedKeys", "RedactCheckedCleanup", "RedactApplyTitle", "RedactIrreversible",
            "RedactRefusedTitle", "RedactRefusedNothing", "RedactFailedTitle", "RedactFailedNothing", "RedactReportTitle",
            "RedactReportIntro", "RedactViewFailedTitle", "SaveChoiceClose", "RedactReportName", "RedactRefusalHeadingEncrypted",
            "RedactRefusalHeadingKeys", "RedactRefusalHeadingKeysUnverified", "RedactRefusalHeadingPage", "RedactRefusalEncrypted",
            "RedactRefusalKeysUnverified", "RedactRefusalKeys", "RedactRefusalKeyNames", "RedactRefusalPage", "RedactRefusalPageReason",
            "RedactRefusalPageHint", "RedactRefusalOther", "RedactPageOne", "RedactPageMany", "RedactPageFile",
            "RedactFailureNotCompleted", "RedactFailureUnremovable", "RedactFailureText", "RedactFailureChar", "RedactFailureImage",
            "RedactFailureUnreadable", "RedactFailureKeys", "RedactFailurePage", "RedactFailureUnverifiable", "RedactFailureCleanup",
            "RedactErrorNotPrepared", "RedactErrorDraft", "RedactErrorFileChanged", "RedactSaveFirstText", "RedactApplyBlocked",
            "RedactFailureRaster", "RedactNcRasterOutside", "RedactNcApplyRenderRaster", "RedactCheckListRaster", "RedactCheckListRasterOnly",
            "RedactCheckedPagesInPlace", "RedactCheckedRaster"
    ];

    // Every text of the apply flow that the interface shows, in the language that is set.
    private static IEnumerable<string> AllRedactionTexts() =>
        AllRedactionKeys().Select(static key => (string)typeof(Strings).GetProperty(key)!.GetValue(null)!);

    // A line of the check that starts with an English word of the engine.
    [GeneratedRegex(@"^(Raw bytes|Pixels:|Another program|Render comparison|Page keys:|XMP metadata|\d+ removed|\d+ further)")]
    private static partial Regex EnglishStart();

    // Words that promise another way or a later version of the program.
    [GeneratedRegex(@"later version|future|coming|rescue|as an image|as image|spätere|späteren|künftig|zukünftig|demnächst|Rettung|als Bild|M14\.1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FutureWords();

    // "safe" or "sicher" as a promise: nothing here says the result is certain.
    [GeneratedRegex(@"\b(sicher\w*|secure\w*|safe\w*|100 ?%)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromiseWords();

    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();
}

/// <summary>
/// The update path and redaction marks that wait (milestone 14, task 13): "Install" asks about them like about unsaved changes
/// (<see cref="Workspace.ResolveUnsavedChangesAsync"/> via <see cref="MainViewModel.ResolveUnsavedChangesAsync()"/>), with the card
/// "Discard the redaction marks?", and "Cancel" stops the installation before anything is fetched or staged. Network and installer
/// are fakes; files lie in temporary folders, never in the user's.
/// </summary>
[Collection("Localization")]
public sealed class RedactApplyUpdateTests : UpdateTestHost
{
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    private readonly SaveCardHost _files = new();

    public RedactApplyUpdateTests() => Own(_files);

    [AvaloniaFact]
    public async Task UpdatePathAsksAboutPendingMarks()
    {
        var (_, coordinator, viewModel, window, tab, path) = await MarksInAWindowAsync();
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // Nothing but the marks is unsaved, and the update asks about them before anything is fetched.
        var fetches = Source.FetchCalls;
        var install = coordinator.BeginInstallAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        Assert.Same(tab, viewModel.PendingSave!.Tab);
        Assert.Equal(Strings.DiscardRedactionsText, viewModel.PendingSave.Message);
        Assert.True(coordinator.IsAwaitingAnswer);
        Assert.Equal(fetches, Source.FetchCalls);
        Assert.Equal(0, Source.DownloadCalls);

        // "Cancel": the installation stops, the marks and the mode stay.
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.True(tab.HasPendingRedactions);
        Assert.True(tab.IsRedacting);
        Assert.Null(viewModel.PendingSave);

        // "Discard": the marks go for good, the installer takes over, and nothing was written to the file.
        install = coordinator.BeginInstallAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Discard);
        await WaitUntilAsync(window, () => install.IsCompleted);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.IsRedacting);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    private async Task<(Workspace Workspace, UpdateCoordinator Coordinator, MainViewModel ViewModel, MainWindow Window, TabViewModel Tab, string Path)> MarksInAWindowAsync()
    {
        Source.Offer("0.2.0");
        UpdateCoordinator? made = null;
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _files.Picker,
            updateFactory: () => made ??= NewCoordinator());
        Own(workspace);
        var coordinator = workspace.UpdateCoordinator!;
        await coordinator.CheckNowAsync();
        await PumpUntilAsync(() => coordinator.State == UpdateState.Offer(Offered));

        var viewModel = workspace.OpenWindow();
        viewModel.Clock = _files.Clock;
        var window = (MainWindow)workspace.WindowOf(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        var path = _files.Copy("redact-everywhere-nopattern.pdf", "marks.pdf");
        var tab = await SaveCardHost.OpenAsync(window, viewModel, view, path);
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.True(tab.AddAreaMark(0, new PageRect(20, 275, 200, 300)));
        Assert.True(tab.HasPendingRedactions);
        Assert.False(tab.IsDirty);
        await WaitUntilAsync(window, () => viewModel.Update is not null);
        return (workspace, coordinator, viewModel, window, tab, path);
    }
}
