using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Findings of the review of the whole branch of milestone 16 in the window (Docs/DECISIONS.md, "Text bearbeiten: Befunde der Abschlussprüfung (M16)").</summary>
[Collection("Localization")]
public sealed class TextEditReviewUiTests : IDisposable
{
    private readonly SaveCardHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View, TabViewModel Tab)> OpenInModeAsync(string file)
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, file);
        tab.ToggleTextEditCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsTextEditing && tab.TextRunsOf(0).Count > 0 && view.TextEditOverlay is not null);
        return (window, viewModel, view, tab);
    }

    private static async Task OpenFieldAsync(Window window, DocumentView view, TabViewModel tab, string text)
    {
        var run = tab.TextRunsOf(0).Single(r => r.Text.TrimEnd() == text);
        Assert.True(tab.OpenTextRunField(run));
        await SaveCardHost.WaitUntilAsync(window, () => view.TextRunEditor is { } editor && editor.Input.IsKeyboardFocusWithin);
    }

    private static async Task TypeAsync(Window window, DocumentView view, TabViewModel tab, string text)
    {
        view.TextRunEditor!.Input.Text = text;
        await SaveCardHost.WaitUntilAsync(window, () => string.Equals(tab.TextEditDraft, text, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task TheFieldOfWhiteTextIsReadable()
    {
        var (_, _, _, tab) = await OpenInModeAsync("text-edit-standard.pdf");
        var run = tab.TextRunsOf(0)[0];
        var editor = new TextRunEditor();

        editor.Start(tab, run with { Color = new AnnotationColor(255, 255, 255) });
        var onWhite = ((ISolidColorBrush)editor.Input.Background!).Color;
        editor.Start(tab, run with { Color = new AnnotationColor(0, 0, 0) });
        var onBlack = ((ISolidColorBrush)editor.Input.Background!).Color;

        Assert.True(onWhite.R < 100, $"paper behind white text is {onWhite}");
        Assert.Equal(Colors.White, onBlack);
    }

    [AvaloniaFact]
    public async Task TheSwitchForTheWholeLineDoesNotApplyTheTextWhenItTakesTheFocus()
    {
        var (window, _, view, tab) = await OpenInModeAsync("text-edit-subset.pdf");
        await OpenFieldAsync(window, view, tab, "Invoice total: 1,250 EUR");
        await TypeAsync(window, view, tab, "Invoice total: 1,259 EUR");
        await SaveCardHost.WaitUntilAsync(window, () => tab.CanChooseWholeRun);

        var toggle = SaveCardHost.Find<ToggleButton>(window.TextEditHintBar!, "TextEditWholeRunButton");
        toggle.Focus();
        SaveCardHost.Settle(window);
        await Task.Delay(100, Ct);
        SaveCardHost.Settle(window);

        // The field is still open with its text: the choice lands on the line it is meant for.
        Assert.NotNull(tab.OpenTextRun);
        Assert.False(tab.CanUndo);
        tab.SetTextEditWholeRun(true);
        Assert.True(await tab.CommitTextEditAsync());
        await SaveCardHost.WaitUntilAsync(window, () => tab.CanUndo && !tab.HasEditsInFlight);
        var runs = await tab.Document!.GetTextRunsAsync(0, Ct);
        Assert.Single(runs, r => r.Text == "Invoice total: 1,259 EUR");
    }

    [AvaloniaFact]
    public async Task DoneWithATextInTheFieldAppliesItAndEndsTheModeInOneClick()
    {
        var (window, _, view, tab) = await OpenInModeAsync("text-edit-standard.pdf");
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");

        tab.ToggleTextEditCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => !tab.IsTextEditing && tab.CanUndo && !tab.HasEditsInFlight);

        Assert.Null(tab.OpenTextRun);
    }

    [AvaloniaFact]
    public async Task TheTextOfAnOpenFieldSurvivesAnotherTabBeingShown()
    {
        var (window, viewModel, view, tab) = await OpenInModeAsync("text-edit-standard.pdf");
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");

        var other = _host.Copy("text-edit-subset.pdf");
        await viewModel.OpenAsync(other);
        await SaveCardHost.WaitUntilAsync(window, () => !ReferenceEquals(viewModel.ActiveTab, tab) && view.TextRunEditor is null);
        viewModel.Tabs.Activate(tab);
        await SaveCardHost.WaitUntilAsync(window, () => view.TextRunEditor is not null);

        Assert.Equal("Quarterly report 2027", view.TextRunEditor!.Text);
        Assert.Equal("Quarterly report 2027", tab.TextEditDraft);
    }

    [AvaloniaFact]
    public async Task ACommitDoesNotPullTheFocusBackFromWhereTheUserWent()
    {
        var (window, _, view, tab) = await OpenInModeAsync("text-edit-standard.pdf");
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");
        var pageField = SaveCardHost.Find<TextBox>(window, "PageField");

        pageField.Focus();
        await SaveCardHost.WaitUntilAsync(window, () => tab.OpenTextRun is null && tab.CanUndo && !tab.HasEditsInFlight);

        Assert.True(pageField.IsFocused);
    }
}
