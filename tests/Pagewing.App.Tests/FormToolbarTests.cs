using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The window of the mode "Fill in form" (milestone 11, task 11): the commands and their keys, the toolbar of the mode, the
/// hint bar after the first sharp page and the notice of a refused value, Esc, names for screen readers, contrast and a
/// narrow window. The files are only read: no test saves, and no value is written into a document.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class FormToolbarTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public FormToolbarTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task ToolbarExistsOnlyWhileFilling()
    {
        var (window, viewModel, tab, view) = await ShowAsync("forms-basic.pdf");

        // Nothing of the toolbar before the mode.
        Assert.Null(FindOrNull<FormToolbar>(window, "FormToolbar"));

        // Ctrl+Shift+F in the document starts the mode: the toolbar is over the document, which moves down by its height.
        await FocusAsync(window, view);
        Press(window, Key.F, PhysicalKey.F, Ctrl | Shift);
        await WaitUntilAsync(window, () => tab.IsFilling);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible && toolbar.Bounds.Height > 0);
        Assert.Same(tab, toolbar.DataContext);
        Assert.Equal(44, toolbar.Bounds.Height);
        Assert.Equal(toolbar.Bounds.Height, view.Margin.Top);
        Assert.Equal(Strings.FormModeName, Find<TextBlock>(toolbar, "ModeName").Text);
        Assert.Equal(Strings.FormToolbarName, AutomationProperties.GetName(toolbar));
        Assert.Equal("form.fill", viewModel.Commands.ForGesture(new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift))?.Id);

        // "Comment" takes over: only its toolbar is shown, at the same place.
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Settle(window);
        Assert.False(tab.IsFilling);
        Assert.False(toolbar.IsVisible);
        var annotation = Find<AnnotationToolbar>(window, "AnnotationToolbar");
        Assert.True(annotation.IsEffectivelyVisible);
        Assert.Equal(annotation.Bounds.Height, view.Margin.Top);

        // And back: the same toolbar (made once), the annotation toolbar hidden.
        Press(window, Key.F, PhysicalKey.F, Ctrl | Shift);
        await WaitUntilAsync(window, () => tab.IsFilling && toolbar.IsEffectivelyVisible);
        Assert.False(tab.IsAnnotating);
        Assert.False(annotation.IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<FormToolbar>());
        Assert.Equal(toolbar.Bounds.Height, view.Margin.Top);

        // "Done" with the keyboard ends the mode; the toolbar goes, the keys go back to the document, which takes the space again.
        var done = Find<Button>(toolbar, "DoneButton");
        done.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.False(tab.IsFilling);
        Assert.False(toolbar.IsVisible);
        Assert.Null(toolbar.DataContext);
        await WaitUntilAsync(window, () => view.IsFocused);
        Assert.Equal(0, view.Margin.Top);

        // Another tab without the mode shows no toolbar; back on the tab in the mode it is there again.
        tab.ToggleFillCommand.Execute(null);
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible);
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var other = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => other.HasShownSharpPage);
        Assert.False(toolbar.IsVisible);
        Assert.Equal(0, view.Margin.Top);
        viewModel.ActivateTabCommand.Execute(tab);
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible);
        Assert.Same(tab, toolbar.DataContext);
    }

    [AvaloniaFact]
    public async Task HintBarOffersTheModeOnceAfterTheFirstSharpPage()
    {
        var viewModel = NewViewModel();
        var window = NewWindow(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        Assert.Null(FindOrNull<FormHintBar>(window, "FormHintBar"));

        // The bar is never there before the first sharp page of its tab.
        await viewModel.OpenAsync(TestData.Pdf("forms-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        FormHintBar? bar = null;
        await WaitUntilAsync(window, () =>
        {
            bar = FindOrNull<FormHintBar>(window, "FormHintBar");
            Assert.True(bar is not { IsVisible: true } || tab.HasShownSharpPage, "The hint bar was shown before the first sharp page.");
            return bar is { IsEffectivelyVisible: true } && bar.Bounds.Height > 0;
        });
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);

        // One line: what the document has, "Fill in form" and the close button; the information symbol, not the warning.
        Assert.Same(tab, bar!.DataContext);
        Assert.Equal(Strings.FormHintOffer, Find<TextBlock>(bar, "HintText").Text);
        Assert.Equal(Strings.FormHintOffer, AutomationProperties.GetName(bar));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(bar, "HintText")));
        Assert.True(Find<StrokeIcon>(bar, "HintIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(bar, "NoticeIcon").IsVisible);
        var fill = Find<Button>(bar, "FillButton");
        Assert.True(fill.IsEffectivelyVisible);
        Assert.Equal(Strings.FormModeName, AutomationProperties.GetName(fill));
        Assert.Equal(Strings.FormHintClose, AutomationProperties.GetName(Find<Button>(bar, "CloseHintButton")));

        // The bar lies over the top of the document area; the document moves down by its height.
        Assert.Equal(0, bar.Margin.Top);
        Assert.Equal(bar.Bounds.Height, view.Margin.Top);

        // "Fill in form" starts the mode; the offer is done with, also after the mode ended.
        Click(window, fill);
        await WaitUntilAsync(window, () => tab.IsFilling);
        Assert.False(bar.IsVisible);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");
        Assert.Equal(toolbar.Bounds.Height, view.Margin.Top);
        tab.EndFillCommand.Execute(null);
        Settle(window);
        Assert.False(tab.OffersFill);
        Assert.False(bar.IsVisible);
        Assert.Equal(0, view.Margin.Top);

        // A form with scripts: the first hint stands with the offer, closing it closes both; then the next hint alone.
        await viewModel.OpenAsync(TestData.Pdf("forms-calc.pdf"));
        var calc = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => calc.PendingHints != FormHint.None && bar.IsEffectivelyVisible && ReferenceEquals(bar.DataContext, calc));
        Assert.Equal(FormHint.Calculated | FormHint.FormatScripts, calc.PendingHints);
        Assert.Equal(TabViewModel.HintTextOf(FormHint.Calculated), Find<TextBlock>(bar, "HintText").Text);
        Assert.True(Find<Button>(bar, "FillButton").IsEffectivelyVisible);
        Click(window, Find<Button>(bar, "CloseHintButton"));
        Assert.False(calc.OffersFill);
        Assert.Equal(FormHint.FormatScripts, calc.PendingHints);
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(TabViewModel.HintTextOf(FormHint.FormatScripts), Find<TextBlock>(bar, "HintText").Text);
        Assert.False(Find<Button>(bar, "FillButton").IsVisible);
        Click(window, Find<Button>(bar, "CloseHintButton"));
        Assert.Equal(FormHint.None, calc.PendingHints);
        Assert.False(bar.IsVisible);
        Assert.Equal(0, view.Margin.Top);

        // Back on the first tab: nothing to show either; one bar for the window.
        viewModel.ActivateTabCommand.Execute(tab);
        Settle(window);
        Assert.False(bar.IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<FormHintBar>());
    }

    [AvaloniaFact]
    public async Task HintBarIsNotCreatedForADocumentWithoutAForm()
    {
        var (window, viewModel, tab, view) = await ShowAsync("text-basic.pdf");

        // Some time for anything that would come after the first sharp page.
        for (var i = 0; i < 10; i++)
        {
            Settle(window);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(tab.HasForm);
        Assert.False(tab.OffersFill);
        Assert.Null(FindOrNull<FormHintBar>(window, "FormHintBar"));

        // The command is not offered and its key does nothing.
        var fill = viewModel.Commands.ById("form.fill")!;
        Assert.False(fill.CanExecute());
        Assert.False(viewModel.Commands.ById("form.highlight")!.CanExecute());
        await FocusAsync(window, view);
        Press(window, Key.F, PhysicalKey.F, Ctrl | Shift);
        Assert.False(tab.IsFilling);
        Assert.Null(FindOrNull<FormToolbar>(window, "FormToolbar"));
        Assert.Equal(0, view.Margin.Top);
    }

    [AvaloniaFact]
    public async Task EscEndsTheModeAfterTheOpenFieldAndBeforeTheSearch()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        var search = tab.Search;
        search.Query = "Name";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.IsActive);

        await tab.EnterFillAsync();
        var key = tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Name").Key;
        tab.FocusedField = key;
        Assert.True(tab.EditDraft(key, "Neu"));
        await FocusAsync(window, view);

        // The open value first (the field keeps the focus), then the focus of the field, then the mode, then the search.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(tab.CurrentDraft);
        Assert.Equal(key, tab.FocusedField);
        Assert.True(tab.IsFilling);
        Assert.True(search.IsActive);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(tab.FocusedField);
        Assert.True(tab.IsFilling);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsFilling);
        Assert.True(search.IsActive);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(search.IsActive);
        Assert.False(tab.IsDirty);

        // Esc in the toolbar ends the mode too, and the keys go back to the document.
        tab.ToggleFillCommand.Execute(null);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible);
        Find<Button>(toolbar, "NextFieldButton").Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsFilling);
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    [AvaloniaTheory]
    [InlineData("en", "Fill in form", "Highlight form fields", "Ctrl+Shift+F", "Ctrl+Shift+J")]
    [InlineData("de", "Formular ausfüllen", "Felder hervorheben", "Strg+Umschalt+F", "Strg+Umschalt+J")]
    public void HelpListsTheNewCommands(string language, string fill, string highlight, string fillKeys, string highlightKeys)
    {
        Strings.Culture = new CultureInfo(language);
        var viewModel = NewViewModel();
        var window = NewWindow(viewModel);
        Press(window, Key.F1, PhysicalKey.F1, None);
        var card = Find<HelpCard>(window, "HelpCard");

        foreach (var (id, title, keys) in new[] { ("form.fill", fill, fillKeys), ("form.highlight", highlight, highlightKeys) })
        {
            var command = viewModel.Commands.ById(id)!;
            Assert.Equal(CommandCatalog.EditGroup, command.Group);
            Assert.True(command.Listed);
            Assert.Equal(title, command.Title());
            var row = card.GetVisualDescendants().OfType<Control>().Single(control => control.Classes.Contains("helpRow") && Equals(control.Tag, id));
            var texts = row.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(title, texts[0].Text);
            Assert.Equal(keys, texts.Single(text => text.Classes.Contains("mono")).Text);
        }

        // Next and previous field are buttons of the toolbar only: Tab belongs to the field.
        Assert.Null(viewModel.Commands.ById("form.nextField"));
        Assert.Null(viewModel.Commands.ById("form.previousField"));
    }

    [AvaloniaFact]
    public async Task ButtonsHaveAutomationNamesAndStateIsNotOnlyColour()
    {
        var (window, viewModel, tab, _) = await ShowAsync("forms-basic.pdf");
        viewModel.Commands.ById("form.fill")!.Execute();
        await WaitUntilAsync(window, () => tab.IsFilling);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible);

        // Every button has a name; the toolbar is named for screen readers.
        var buttons = toolbar.GetVisualDescendants().OfType<Button>().Cast<Control>()
            .Concat(toolbar.GetVisualDescendants().OfType<ActionToggleButton>()).Distinct().ToArray();
        Assert.True(buttons.Length >= 6);
        Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), button.Name));
        Assert.All(buttons, button => Assert.True(button.Bounds.Height >= 24 && button.Bounds.Width >= 24, button.Name));
        Assert.Equal(Strings.FormPreviousField, AutomationProperties.GetName(Find<Button>(toolbar, "PreviousFieldButton")));
        Assert.Equal(Strings.FormNextField, AutomationProperties.GetName(Find<Button>(toolbar, "NextFieldButton")));
        Assert.Equal(Strings.FormDone, AutomationProperties.GetName(Find<Button>(toolbar, "DoneButton")));
        Assert.Equal(Strings.FormDoneTooltip, ToolTip.GetTip(Find<Button>(toolbar, "DoneButton")));
        Assert.Equal("Undo (Ctrl+Z)", ToolTip.GetTip(Find<Button>(toolbar, "UndoButton")));
        Assert.Equal("Redo (Ctrl+Shift+Z)", ToolTip.GetTip(Find<Button>(toolbar, "RedoButton")));

        // Highlighting is on in the mode: a toggle that is on for screen readers, with an accent frame and label.
        var highlight = Find<ActionToggleButton>(toolbar, "HighlightToggle");
        Assert.Equal(Strings.FormHighlightFields, AutomationProperties.GetName(highlight));
        Assert.Equal("Highlight fields (Ctrl+Shift+J)", ToolTip.GetTip(highlight));
        Assert.Equal("Ctrl+Shift+J", AutomationProperties.GetAcceleratorKey(highlight));
        Assert.IsType<ToggleButtonAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(highlight));
        Assert.Equal(ToggleState.On, ToggleStateOf(highlight));
        Assert.Contains("active", highlight.Classes);
        Assert.Equal(new Thickness(1), highlight.BorderThickness);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), ((ISolidColorBrush)highlight.BorderBrush!).Color);

        // Ctrl+Shift+J switches it off: off for screen readers, no frame; a click switches it on again.
        Press(window, Key.J, PhysicalKey.J, Ctrl | Shift);
        Assert.False(tab.HighlightFields);
        Assert.Equal(ToggleState.Off, ToggleStateOf(highlight));
        Assert.DoesNotContain("active", highlight.Classes);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)highlight.BorderBrush!).Color);
        Click(window, highlight);
        Assert.True(tab.HighlightFields);
        Assert.Equal(ToggleState.On, ToggleStateOf(highlight));

        // Next field moves the focus to the first field that can be changed.
        Click(window, Find<Button>(toolbar, "NextFieldButton"));
        await WaitUntilAsync(window, () => tab.FocusedField is not null);
        Assert.Equal("Name", tab.FocusedField!.Value.FieldName);

        // The keyboard reaches every button of the row in order: the toggle, previous, next, then "Done" (nothing to undo).
        highlight.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(Find<Button>(toolbar, "PreviousFieldButton").IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(Find<Button>(toolbar, "NextFieldButton").IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(Find<Button>(toolbar, "DoneButton").IsFocused);
    }

    [AvaloniaFact]
    public async Task RefusalNoticeAppearsOnTheActiveTab()
    {
        var (window, viewModel, first, view) = await ShowAsync("forms-basic.pdf");
        await first.EnterFillAsync();
        var bar = await BarAsync(window);
        Assert.False(bar.IsVisible);

        // A value the field refuses (it is read-only): the reason in the bar, with the warning symbol and as a polite live region.
        Assert.False(await first.CommitFieldAsync("Gesperrt", FormFieldState.OfText("x")));
        Settle(window);
        Assert.Equal(Strings.FormLockedReadOnly, first.FormNotice);
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(Strings.FormLockedReadOnly, Find<TextBlock>(bar, "HintText").Text);
        Assert.Equal(Strings.FormLockedReadOnly, AutomationProperties.GetName(bar));
        Assert.True(Find<StrokeIcon>(bar, "NoticeIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(bar, "HintIcon").IsVisible);
        Assert.False(Find<Button>(bar, "FillButton").IsVisible);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");

        // Under the toolbar; the document below both.
        Assert.Equal(toolbar.Bounds.Height, bar.Margin.Top);
        Assert.Equal(toolbar.Bounds.Height + bar.Bounds.Height, view.Margin.Top);

        // Another tab is active: its own state; the refusal of the first one stays with it until it is shown again.
        await viewModel.OpenAsync(TestData.Pdf("forms-signed.pdf"));
        var second = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => second.HasShownSharpPage && second.PendingHints != FormHint.None);
        Settle(window);
        Assert.Same(second, bar.DataContext);
        Assert.NotEqual(Strings.FormLockedReadOnly, Find<TextBlock>(bar, "HintText").Text);
        Assert.Null(second.FormNotice);
        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        Assert.Same(first, bar.DataContext);
        Assert.Equal(Strings.FormLockedReadOnly, Find<TextBlock>(bar, "HintText").Text);

        // The next value that is taken clears it (here a write that changes nothing in the file).
        first.FormValueWriter = (_, _, _) => Task.FromResult<FormChange?>(null);
        Assert.True(await first.CommitFieldAsync("Name", FormFieldState.OfText("Neu")));
        Settle(window);
        Assert.Null(first.FormNotice);
        Assert.False(bar.IsVisible);
        Assert.Equal(toolbar.Bounds.Height, view.Margin.Top);

        // The close button clears a notice as well.
        Assert.False(await first.CommitFieldAsync("Gesperrt", FormFieldState.OfText("y")));
        Settle(window);
        Assert.True(bar.IsEffectivelyVisible);
        Click(window, Find<Button>(bar, "CloseHintButton"));
        Assert.Null(first.FormNotice);
        Assert.False(bar.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task ToolbarFitsAtTheNarrowestWindow(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, _, tab, _) = await ShowAsync("forms-basic.pdf");
        window.Width = 640;
        Settle(window);
        tab.ToggleFillCommand.Execute(null);
        var toolbar = Find<FormToolbar>(window, "FormToolbar");
        await WaitUntilAsync(window, () => toolbar.IsEffectivelyVisible && toolbar.Bounds.Width > 0);
        Settle(window);

        // Every button lies wholly inside the toolbar, "Done" included.
        foreach (var button in toolbar.GetVisualDescendants().OfType<Button>().Cast<Control>().Concat(toolbar.GetVisualDescendants().OfType<ActionToggleButton>()))
        {
            if (!button.IsEffectivelyVisible)
            {
                continue;
            }

            var origin = button.TranslatePoint(default, toolbar)!.Value;
            Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= toolbar.Bounds.Width + 0.5, $"{button.Name} at {origin.X} + {button.Bounds.Width} of {toolbar.Bounds.Width}");
        }

        Assert.True(Find<Button>(toolbar, "DoneButton").IsEffectivelyVisible);
        Assert.True(Find<ActionToggleButton>(toolbar, "HighlightToggle").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void FormBarColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) => Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // Toolbar and hint bar share surface.toolbar: the texts, the icons of the mode and of the hint, the warning symbol of a
        // notice, the edge of the secondary button, the focus ring; the chosen toggle and "Done".
        Assert.True(Ratio("Text.Primary", "Surface.Toolbar") >= 4.5, "text.primary on surface.toolbar");
        Assert.True(Ratio("Text.Secondary", "Surface.Toolbar") >= 3, "text.secondary icon on surface.toolbar");
        Assert.True(Ratio("Accent.Default", "Surface.Toolbar") >= 3, "accent.default icon on surface.toolbar");
        Assert.True(Ratio("Danger.Default", "Surface.Toolbar") >= 3, "danger.default icon on surface.toolbar");
        Assert.True(Ratio("Border.Strong", "Surface.Toolbar") >= 3, "border.strong on surface.toolbar");
        Assert.True(Ratio("Focus.Ring", "Surface.Toolbar") >= 3, "focus.ring on surface.toolbar");
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5, "text.primary on surface.raised (secondary button)");
        Assert.True(Ratio("Accent.Default", "Accent.Subtle") >= 4.5, "accent.default label on accent.subtle");
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, "accent.on on accent.default");
    }

    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;
    private const RawInputModifiers Shift = RawInputModifiers.Shift;

    private static ToggleState ToggleStateOf(Control control) =>
        ((IToggleProvider)ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IToggleProvider>()!).ToggleState;

    private static async Task<FormHintBar> BarAsync(Window window)
    {
        FormHintBar? bar = null;
        await WaitUntilAsync(window, () => (bar = FindOrNull<FormHintBar>(window, "FormHintBar")) is not null);
        return bar!;
    }

    private static async Task FocusAsync(Window window, DocumentView view)
    {
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    private static void Click(Window window, Control control)
    {
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
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

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private MainViewModel NewViewModel()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        return viewModel;
    }

    private MainWindow NewWindow(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>A window with a test file open (read only), its first sharp page shown and the panel it opened closed again.</summary>
    private async Task<(MainWindow Window, MainViewModel ViewModel, TabViewModel Tab, DocumentView View)> ShowAsync(string fixture)
    {
        var viewModel = NewViewModel();
        var window = NewWindow(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        await viewModel.OpenAsync(TestData.Pdf(fixture));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        // The offer of the mode (after the first sharp page of a form) is closed, so a test starts with the document at the top.
        if (tab.HasForm)
        {
            await WaitUntilAsync(window, () => tab.OffersFill);
            tab.CloseFillOfferCommand.Execute(null);
            Settle(window);
        }

        return (window, viewModel, tab, view);
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
