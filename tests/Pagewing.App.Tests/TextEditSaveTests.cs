using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.TextEdit;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>What the save flow says about edited text (milestone 16): the warning of a signed file names it, the first save per document adds a note on the old text.</summary>
public sealed class TextEditSaveTests : IDisposable
{
    private readonly SaveCardHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static async Task EditAsync(TabViewModel tab, string oldText, string newText)
    {
        var runs = await tab.Document!.GetTextRunsAsync(0, Ct);
        var run = runs.Single(r => r.Text.TrimEnd() == oldText);
        _ = await tab.Editor.ApplyTextEditAsync(run, newText, TextEditMode.KeepFont);
    }

    [AvaloniaFact]
    public async Task SignedDocumentWarnsAndNamesChangedText()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "signed-docmdp-2.pdf");
        var runs = await tab.Document!.GetTextRunsAsync(0, Ct);
        var run = runs.First(static r => !r.IsLocked && r.Text.Trim().Length > 2);
        _ = await tab.Editor.ApplyTextEditAsync(run, run.Text.TrimEnd() + "x", TextEditMode.KeepFont);
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsDirty);
        Assert.True(tab.HasTextEdits);

        var save = viewModel.SaveActiveTabAsync();
        var card = await SaveCardHost.WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);

        Assert.Contains(Strings.SignatureWarningTextEdit, viewModel.PendingSave!.Message, StringComparison.Ordinal);
        Assert.Contains("changed text", viewModel.PendingSave.Message, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("de");
        Assert.Contains("geänderte Text", viewModel.PendingSave.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(" gültig", viewModel.PendingSave.Message.Replace("ungültig", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("en");
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(await save);
        Assert.True(tab.IsDirty);
        Assert.False(card.IsVisible);
        Assert.NotNull(path);
    }

    [AvaloniaFact]
    public async Task SaveNotesTheOldTextOnceAndNeverSaysRemoved()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-edit-standard.pdf");
        Assert.False(tab.HasTextEditSaveNote);
        await EditAsync(tab, "Quarterly report 2026", "Quarterly report 2027");
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsDirty);

        Assert.True(await viewModel.SaveActiveTabAsync());

        // No question: the save ran through, and the status bar says what stays in the file.
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.HasTextEditSaveNote);
        Assert.Equal(Strings.TextEditSaveNote, tab.TextEditSaveNote);
        Assert.Contains("earlier versions of the file", tab.TextEditSaveNote, StringComparison.Ordinal);
        Assert.Contains("Redact", tab.TextEditSaveNote, StringComparison.Ordinal);
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var note = Strings.TextEditSaveNote;
            Assert.DoesNotContain("removed", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("deleted", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secure", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("entfernt", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gelöscht", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sicher", note, StringComparison.OrdinalIgnoreCase);
        }

        Strings.Culture = new CultureInfo("en");

        // The next change takes the note away; the second save of the same document does not bring it back (once per tab and document).
        await EditAsync(tab, "Quarterly report 2027", "Quarterly report 2028");
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsDirty && !tab.HasTextEditSaveNote);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.False(tab.HasTextEditSaveNote);
        Assert.True(tab.TextEditNoticeShown);
        Assert.NotNull(path);
    }
}
