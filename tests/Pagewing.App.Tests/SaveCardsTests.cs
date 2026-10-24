using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// The cards of the save flow (milestone 9, task 14; design system 7.14): the signature warning and note, another file at
/// the path, a failed save; their defaults, the lock of the dangerous choices, keyboard, names, colours and texts, and that
/// none of them is built before it is needed.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class SaveCardsTests : IDisposable
{
    private readonly SaveCardHost _host = new();

    public void Dispose() => _host.Dispose();

    /// <summary>Every reason of a failed save the error card explains.</summary>
    public static TheoryData<SaveFailureReason> Reasons =>
    [
        SaveFailureReason.NotWritable, SaveFailureReason.DiskFull, SaveFailureReason.WriteFailed, SaveFailureReason.VerificationFailed,
        SaveFailureReason.SourceModified, SaveFailureReason.EncryptedNotSupported,
    ];

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task SignatureCardAtLevelOneDefaultsToCancelAndNeverSaysValid(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-1.pdf");
        await MarkAsync(window, tab);

        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);

        // Warning symbol and text (never colour alone), "Cancel" primary with the focus, then "Save as" and "Save anyway".
        Assert.True(Find<StrokeIcon>(card, "SignatureWarningIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(card, "SignatureNoteIcon").IsEffectivelyVisible);
        Assert.Equal(Strings.SignatureWarningTitle, Find<TextBlock>(card, "SignatureTitle").Text);
        Assert.Equal(Strings.SignatureWarningText, Find<TextBlock>(card, "SignatureText").Text);
        Assert.Equal([Strings.SaveChoiceCancel, Strings.SaveChoiceSaveAs, Strings.SaveChoiceSaveAnyway], ShownButtons(card));
        var cancel = card.ButtonOf(PendingSaveChoice.Cancel)!;
        Assert.True(cancel.IsFocused);
        Assert.Contains("primary", cancel.Classes);
        Assert.DoesNotContain("primary", card.ButtonOf(PendingSaveChoice.SaveAnyway)!.Classes);
        if (language == "de")
        {
            Assert.Equal("Diese Signatur erlaubt keine Kommentare. Nach dem Speichern wird sie in Programmen wie Acrobat als ungültig angezeigt.",
                $"{Strings.SignatureWarningTitle}. {Strings.SignatureWarningText}");
        }

        // No text of the card says the signature is valid.
        foreach (var text in card.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).Append(AutomationProperties.GetName(card)))
        {
            Assert.DoesNotMatch(ValidWord(), text ?? string.Empty);
        }

        // Enter cancels: nothing written.
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(await save);
        Assert.True(tab.IsDirty);
        Assert.False(card.IsVisible);
    }

    [AvaloniaFact]
    public async Task SignatureCardAtLevelThreeIsOnlyANote()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-3.pdf");
        var before = (await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count;
        await MarkAsync(window, tab);

        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureNote);

        // A note, no warning: the info symbol, "Save" (primary, focus) and "Cancel"; nothing is locked.
        Assert.True(Find<StrokeIcon>(card, "SignatureNoteIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(card, "SignatureWarningIcon").IsEffectivelyVisible);
        Assert.Equal(Strings.SignatureNoteTitle, Find<TextBlock>(card, "SignatureTitle").Text);
        Assert.Equal(Strings.SignatureNoteText, Find<TextBlock>(card, "SignatureText").Text);
        Assert.Equal([Strings.SaveChoiceSave, Strings.SaveChoiceCancel], ShownButtons(card));
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.Save)!.Classes);
        Assert.All(card.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible), button => Assert.True(button.IsEffectivelyEnabled));

        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.True(await save);
        Assert.False(tab.IsDirty);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, (await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);
    }

    [AvaloniaFact]
    public async Task FileChangedCardDefaultsToSaveAs()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);
        ReplaceAtPath(path);
        var other = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<FileChangedCard>(window, viewModel, PendingSaveKind.FileChanged);
        Assert.Equal(viewModel.PendingSave!.Title, Find<TextBlock>(card, "FileChangedTitle").Text);
        Assert.Equal(Strings.FileChangedText, Find<TextBlock>(card, "FileChangedText").Text);
        Assert.True(Find<StrokeIcon>(card, "FileChangedIcon").IsEffectivelyVisible);
        Assert.Equal([Strings.SaveChoiceOverwrite, Strings.SaveChoiceSaveAs, Strings.SaveChoiceCancel], ShownButtons(card));
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.SaveAs)!.Classes);
        Assert.False(card.ButtonOf(PendingSaveChoice.Overwrite)!.IsEffectivelyEnabled);

        // Enter takes "Save as": the dialog; cancelled there, the other file stays as it is.
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(await save);
        Assert.Equal(1, _host.Picker.Calls);
        Assert.Equal(other, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task SaveErrorCardOffersSaveAs()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);
        _host.Picker.Result = Path.Combine(_host.Folder, "missing", "out.pdf");

        var save = viewModel.SaveActiveTabAsAsync();
        var card = await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Assert.Equal(SaveFailureReason.NotWritable, viewModel.PendingSave!.Reason);
        Assert.Equal("“out.pdf” could not be saved", Find<TextBlock>(card, "SaveErrorTitle").Text);
        Assert.Equal(Strings.SaveErrorNotWritable, Find<TextBlock>(card, "SaveErrorText").Text);
        Assert.Equal([Strings.SaveChoiceSaveAs, Strings.SaveChoiceCancel], ShownButtons(card));
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.SaveAs)!.Classes);

        // "Save as" again, into a folder that exists.
        var target = Path.Combine(_host.Folder, "out.pdf");
        _host.Picker.Result = target;
        Click(window, card.ButtonOf(PendingSaveChoice.SaveAs)!);
        Assert.True(await save);
        Assert.Equal(target, tab.FullPath);
        Assert.False(card.IsVisible);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Reasons))]
    public void SaveErrorCardExplainsEveryReason(SaveFailureReason reason)
    {
        // Each reason has its own text in both languages; another name is offered where it can help.
        var texts = new List<string>();
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            using var tab = new TabViewModel(new Core.Viewing.ViewerSession(new PdfEngine()));
            var question = PendingSave.SaveError(tab, Path.Combine(_host.Folder, "a.pdf"), reason);
            Assert.False(string.IsNullOrWhiteSpace(question.Message));
            Assert.NotEqual(Strings.SaveErrorUnknown, question.Message);
            Assert.True(question.IsWarning);
            texts.Add(question.Message);
            var onlyCancel = reason is SaveFailureReason.SourceModified or SaveFailureReason.EncryptedNotSupported;
            Assert.Equal(onlyCancel ? [PendingSaveChoice.Cancel] : [PendingSaveChoice.SaveAs, PendingSaveChoice.Cancel], question.Choices);
        }

        Assert.NotEqual(texts[0], texts[1]);
        if (reason == SaveFailureReason.WriteFailed)
        {
            // Mostly a file another program holds open.
            Assert.Contains("another program", texts[0], StringComparison.Ordinal);
            Assert.Contains("anderes Programm", texts[1], StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task SourceModifiedCardOffersOnlyCancel()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));

        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Assert.Equal(SaveFailureReason.SourceModified, viewModel.PendingSave!.Reason);
        Assert.Equal(Strings.SaveErrorSourceModified, Find<TextBlock>(card, "SaveErrorText").Text);
        Assert.Equal([Strings.SaveChoiceCancel], ShownButtons(card));
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.Cancel)!.Classes);

        Press(window, Key.Space, PhysicalKey.Space);
        Assert.False(await save);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.IsDirty);
        Assert.Equal(SaveFailureReason.SourceModified, tab.UnsaveableReason);
    }

    [AvaloniaFact]
    public async Task DangerousChoicesAreLockedForTheFirst400Ms()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);

        // "Don't save" before the tab closes.
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        var question = viewModel.PendingSave!;
        var dontSave = card.ButtonOf(PendingSaveChoice.DontSave)!;
        Assert.False(PendingSave.IsDangerous(question.DefaultChoice));
        Assert.False(dontSave.IsEffectivelyEnabled);
        Assert.True(card.ButtonOf(PendingSaveChoice.Cancel)!.IsEffectivelyEnabled);
        Click(window, dontSave);
        Assert.False(question.Choose(PendingSaveChoice.DontSave));
        Assert.False(question.IsResolved);

        _host.Clock.Advance(PendingSave.ArmingDelay - TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.False(dontSave.IsEffectivelyEnabled);
        _host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.True(dontSave.IsEffectivelyEnabled);

        // A second click right after the first is a double click and never reaches a button; armed, a click of its own takes it.
        Click(window, dontSave);
        Assert.False(question.IsResolved);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Click(window, dontSave);
        Assert.Equal(PendingSaveChoice.DontSave, await question.Choice.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);

        // "Overwrite" and "Save anyway" the same way.
        var (other, otherPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf", "other.pdf");
        await MarkAsync(window, other);
        ReplaceAtPath(otherPath);
        var save = viewModel.SaveActiveTabAsync();
        var fileCard = await WaitForCardAsync<FileChangedCard>(window, viewModel, PendingSaveKind.FileChanged);
        var overwrite = fileCard.ButtonOf(PendingSaveChoice.Overwrite)!;
        Assert.False(overwrite.IsEffectivelyEnabled);

        // Another tab shown and back: the lock starts again.
        _host.Clock.Advance(TimeSpan.FromMilliseconds(300));
        var third = await SaveCardHost.OpenAsync(window, viewModel, view, _host.Copy("hello_world.pdf"));
        Assert.False(fileCard.IsVisible);
        viewModel.ActivateTabCommand.Execute(other);
        await WaitForCardAsync<FileChangedCard>(window, viewModel, PendingSaveKind.FileChanged);
        _host.Clock.Advance(TimeSpan.FromMilliseconds(300));
        Settle(window);
        Assert.False(overwrite.IsEffectivelyEnabled);
        _host.Clock.Advance(TimeSpan.FromMilliseconds(100));
        Settle(window);
        Assert.True(overwrite.IsEffectivelyEnabled);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        Assert.NotNull(third);
        Assert.NotNull(path);
    }

    [AvaloniaFact]
    public async Task SaveAnywayIsLockedAtFirst()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-2.pdf");
        await MarkAsync(window, tab);

        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);
        var anyway = card.ButtonOf(PendingSaveChoice.SaveAnyway)!;
        Assert.False(anyway.IsEffectivelyEnabled);
        Assert.False(viewModel.PendingSave!.Choose(PendingSaveChoice.SaveAnyway));
        _host.PassTheLock(window);
        Assert.True(anyway.IsEffectivelyEnabled);
        Click(window, anyway);
        Assert.True(await save);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task CardsAreKeyboardOperableAndNamed()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);

        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        var question = viewModel.PendingSave!;

        // For screen readers: the card is named by its title and described by its text; title and text are a polite live
        // region (announced when the card appears); every button is named by its text.
        var peer = ControlAutomationPeer.CreatePeerForElement(card);
        Assert.Equal(question.Title, peer.GetName());
        Assert.Equal(question.Message, AutomationProperties.GetHelpText(card));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(card, "SaveChangesTitle")));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(card, "SaveChangesText")));
        foreach (var button in card.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible))
        {
            Assert.Equal(button.Content, ControlAutomationPeer.CreatePeerForElement(button).GetName());
        }

        // Tab stays on the card: during the lock "Don't save" is skipped, afterwards it is in the order of the buttons.
        Assert.True(card.ButtonOf(PendingSaveChoice.Save)!.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(card.ButtonOf(PendingSaveChoice.Cancel)!.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(card.ButtonOf(PendingSaveChoice.Save)!.IsFocused);
        _host.PassTheLock(window);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(card.ButtonOf(PendingSaveChoice.DontSave)!.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(card.ButtonOf(PendingSaveChoice.Cancel)!.IsFocused);

        // The page keys of the document do nothing behind the card.
        var page = tab.CurrentPage;
        Press(window, Key.PageDown, PhysicalKey.PageDown);
        Assert.Equal(page, tab.CurrentPage);

        // Space on "Cancel": answered, the keys go back to the document.
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.Equal(PendingSaveChoice.Cancel, await question.Choice);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.True(view.IsKeyboardFocusWithin);
        Assert.Contains(tab, viewModel.Tabs);
    }

    [AvaloniaTheory]
    [MemberData(nameof(TokenTests.AllThemes), MemberType = typeof(TokenTests))]
    public async Task CardColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) =>
            Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // Texts on surface.raised (4.5:1), the symbols, the edges of the buttons and the focus (3:1), the primary button.
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5, $"{theme}: title {Ratio("Text.Primary", "Surface.Raised"):F2}");
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 4.5, $"{theme}: text {Ratio("Text.Secondary", "Surface.Raised"):F2}");
        Assert.True(Ratio("Danger.Default", "Surface.Raised") >= 3, $"{theme}: warning {Ratio("Danger.Default", "Surface.Raised"):F2}");
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 3);
        Assert.True(Ratio("Border.Strong", "Surface.Raised") >= 3);
        Assert.True(Ratio("Focus.Ring", "Surface.Raised") >= 3);
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, $"{theme}: primary {Ratio("Accent.On", "Accent.Default"):F2}");

        if (variant == HighContrast.Variant)
        {
            // High contrast has the same keys (HighContrastTests.TheVariantHasEveryTokenOfTheOtherThemes).
            return;
        }

        var (window, viewModel, view) = _host.Show();
        window.RequestedThemeVariant = variant;
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-1.pdf");
        await MarkAsync(window, tab);
        var save = viewModel.SaveActiveTabAsync();
        var card = await WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);

        // The card draws exactly these tokens.
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Brush(Find<Border>(card, "SignatureBox").Background));
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Brush(Find<TextBlock>(card, "SignatureText").Foreground));
        Assert.Equal(TokenTests.Token("Danger.Default", variant), Brush(Find<StrokeIcon>(card, "SignatureWarningIcon").Brush));
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Brush(Find<StrokeIcon>(card, "SignatureNoteIcon").Brush));
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void NoUiTextCallsAnythingLegallyBinding(string language)
    {
        Strings.Culture = new CultureInfo(language);
        using var tab = new TabViewModel(new Core.Viewing.ViewerSession(new PdfEngine()));
        var path = Path.Combine(_host.Folder, "a.pdf");
        var questions = new List<PendingSave>
        {
            PendingSave.SaveChanges(tab, path),
            PendingSave.DiscardChanges(tab, path, SaveFailureReason.SourceModified),
            PendingSave.DiscardChanges(tab, path, SaveFailureReason.EncryptedNotSupported),
            PendingSave.SignatureWarning(tab, path, DocMdpLevel.NoChanges),
            PendingSave.SignatureWarning(tab, path, DocMdpLevel.FormsOnly),
            PendingSave.SignatureNote(tab, path, DocMdpLevel.FormsAndAnnotations),
            PendingSave.SignatureNote(tab, path, DocMdpLevel.NotSet),
            PendingSave.FileChanged(tab, path),
        };
        foreach (var reason in Reasons)
        {
            questions.Add(PendingSave.SaveError(tab, path, reason));
        }

        var texts = questions.SelectMany(question => new[] { question.Title, question.Message })
            .Concat(Enum.GetValues<PendingSaveChoice>().Select(PendingSave.LabelOf))
            .ToList();
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally binding", text, StringComparison.OrdinalIgnoreCase);

            // "invalid" / "ungültig" (what Acrobat will show) is allowed; "valid" / "gültig" as a claim is not.
            Assert.DoesNotMatch(ValidWord(), text);
        }
    }

    [AvaloniaFact]
    public async Task EscClosesTheCardWithCancel()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);

        // With the focus on the card.
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        var question = viewModel.PendingSave!;
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(PendingSaveChoice.Cancel, await question.Choice);
        await WaitUntilAsync(window, () => !card.IsVisible);
        Assert.Contains(tab, viewModel.Tabs);

        // With the focus elsewhere (the title bar): Esc still answers "Cancel".
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        question = viewModel.PendingSave!;
        Find<Button>(window, "PaletteButton").Focus();
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(PendingSaveChoice.Cancel, await question.Choice);

        // The order: an overlay with the focus closes first (its own Esc), then the card.
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        question = viewModel.PendingSave!;
        viewModel.ShowHelpCommand.Execute(null);
        await WaitUntilAsync(window, () => Find<HelpCard>(window, "HelpCard").IsKeyboardFocusWithin);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(question.IsResolved);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(PendingSaveChoice.Cancel, await question.Choice);

        // In the catalog Esc means: the link card, the save cards, an overlay, the update card.
        Assert.Equal(
            ["link.cancel", "save.cancel", "overlay.hide", "update.dismiss"],
            viewModel.Commands.All.Where(command => command.Gesture?.Equals(new KeyGesture(Key.Escape)) == true).Select(command => command.Id));
    }

    [AvaloniaFact]
    public async Task CardsAreNotCreatedBeforeTheyAreShown()
    {
        var (window, viewModel, view) = _host.Show();
        Assert.Empty(window.SaveCards);
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-1.pdf");

        // Open, first sharp page, a change: no card.
        await MarkAsync(window, tab);
        Settle(window);
        Assert.Empty(window.SaveCards);
        Assert.Empty(window.GetVisualDescendants().OfType<SaveCard>());

        // The first question makes its own card only.
        var save = viewModel.SaveActiveTabAsync();
        await WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);
        Assert.IsType<SignatureWarningCard>(Assert.Single(window.SaveCards));
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
    }

    // "valid" or "gültig" as a word of its own ("invalid", "ungültig" do not match).
    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>Puts another file at <paramref name="path"/> (the opened one moves away, untouched).</summary>
    private static void ReplaceAtPath(string path)
    {
        File.Move(path, path + ".moved");
        File.Copy(TestData.Pdf("hello_world.pdf"), path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
    }
}
