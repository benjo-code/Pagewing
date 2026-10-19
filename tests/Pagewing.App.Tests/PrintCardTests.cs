using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PrintCardTests : PrintTestHost
{
    private PrintViewModel Dialog(PrintSettings? defaults = null, int pageCount = 10, int currentPage = 3) =>
        new(Service, pageCount, currentPage, defaults ?? new PrintSettings());

    [Fact]
    public async Task TheCardListsThePrintersAndPicksTheLastUsed()
    {
        using var dialog = Dialog(new PrintSettings { Printer = "Office" });

        dialog.OnOpened();
        await Eventually(() => dialog.Printers.Count == 2);

        Assert.Equal("Office", dialog.SelectedPrinter!.Value);
        Assert.Equal("Office – Second floor", dialog.SelectedPrinter.Title);
        Assert.Equal("Plotter", dialog.Printers[1].Title);
        Assert.False(dialog.IsSearching);
    }

    [Fact]
    public async Task TheSystemDefaultIsPickedWhenTheLastPrinterIsGone()
    {
        using var dialog = Dialog(new PrintSettings { Printer = "Vanished" });

        dialog.OnOpened();
        await Eventually(() => dialog.Printers.Count == 2);

        Assert.Equal("Plotter", dialog.SelectedPrinter!.Value);
    }

    [Fact]
    public async Task WithoutPrintersTheCardSaysSoAndCannotPrint()
    {
        Service.Printers = [];
        using var dialog = Dialog();

        dialog.OnOpened();
        await Eventually(() => !dialog.IsSearching);

        Assert.True(dialog.HasNoPrinters);
        Assert.Null(dialog.SelectedPrinter);
        Assert.False(dialog.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task SearchingShowsAHintAndStaysUsable()
    {
        Service.PrintersGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dialog = Dialog();
        var cancelled = 0;
        dialog.Cancelled += () => cancelled++;

        dialog.OnOpened();

        Assert.True(dialog.IsSearching);
        Assert.False(dialog.HasNoPrinters);
        dialog.RangeText = "1-2";
        Assert.False(dialog.PrintCommand.CanExecute(null));
        dialog.CancelCommand.Execute(null);
        Assert.Equal(1, cancelled);

        Service.PrintersGate.SetResult();
        await Eventually(() => dialog.Printers.Count == 2);
        Assert.True(dialog.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnAnswerAfterTheDialogClosedIsDropped()
    {
        Service.PrintersGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = Dialog();
        dialog.OnOpened();

        dialog.Dispose();
        Service.PrintersGate.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(dialog.Printers);
        Assert.False(dialog.PrintCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("5-3", false)]
    [InlineData("abc", false)]
    [InlineData("1-", false)]
    [InlineData("11", false)]
    [InlineData("1-3,5", true)]
    [InlineData("10", true)]
    public async Task AWrongRangeShowsAMessageAndDisablesPrint(string range, bool valid)
    {
        using var dialog = Dialog();
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);

        dialog.RangeText = range;

        Assert.True(dialog.RangeSelected);
        Assert.Equal(valid, dialog.RangeError is null);
        Assert.Equal(valid, dialog.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARangeBeyondTheDocumentNamesThePage()
    {
        using var dialog = Dialog(pageCount: 10);
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);

        dialog.RangeText = "8-12";

        Assert.Equal("Page 12 does not exist. The document has 10 pages.", dialog.RangeError);
    }

    [Fact]
    public async Task AnInvalidRangeIsIgnoredWhileAnotherChoiceIsMade()
    {
        using var dialog = Dialog();
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);
        dialog.RangeText = "abc";

        dialog.AllPages = true;

        Assert.Null(dialog.RangeError);
        Assert.True(dialog.PrintCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1000", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(" 7 ", true)]
    [InlineData("999", true)]
    public async Task CopiesNeedANumberFromOneTo999(string copies, bool valid)
    {
        using var dialog = Dialog();
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);

        dialog.CopiesText = copies;

        Assert.Equal(valid, dialog.CopiesError is null);
        Assert.Equal(valid, dialog.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheCurrentPageChoiceUsesTheCurrentPage()
    {
        using var dialog = Dialog(currentPage: 6);
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);
        PrintTicket? ticket = null;
        dialog.Confirmed += confirmed => ticket = confirmed;

        dialog.CurrentPageSelected = true;
        dialog.PrintCommand.Execute(null);

        Assert.Equal(7, dialog.CurrentPageNumber);
        Assert.Equal(PrintPageChoice.CurrentPage, ticket!.Settings.Pages);
        Assert.Equal(string.Empty, ticket.Settings.Range);
    }

    [Fact]
    public async Task DuplexIsOnlyOfferedForAPrinterThatHasIt()
    {
        using var dialog = Dialog();
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);

        await Eventually(() => !dialog.SupportsDuplex);
        dialog.SelectedPrinter = dialog.Printers.Single(printer => printer.Value == "Office");
        await Eventually(() => dialog.SupportsDuplex);
        dialog.SelectedDuplex = dialog.DuplexChoices.Single(choice => choice.Value == PrintDuplex.LongEdge);
        dialog.SelectedPrinter = dialog.Printers.Single(printer => printer.Value == "Plotter");
        await Eventually(() => !dialog.SupportsDuplex);

        Assert.Equal(PrintDuplex.Default, dialog.SelectedDuplex.Value);
    }

    [Fact]
    public async Task AChosenDuplexIsOnlySentWhenThePrinterHasIt()
    {
        using var dialog = Dialog();
        dialog.OnOpened();
        await Eventually(() => dialog.SelectedPrinter is not null);
        PrintTicket? ticket = null;
        dialog.Confirmed += confirmed => ticket = confirmed;
        dialog.SelectedPrinter = dialog.Printers.Single(printer => printer.Value == "Office");
        await Eventually(() => dialog.SupportsDuplex);
        dialog.SelectedDuplex = dialog.DuplexChoices.Single(choice => choice.Value == PrintDuplex.ShortEdge);
        dialog.SelectedOrientation = dialog.OrientationChoices.Single(choice => choice.Value == PrintOrientation.Landscape);
        dialog.Collate = false;

        dialog.PrintCommand.Execute(null);

        Assert.Equal(PrintDuplex.ShortEdge, ticket!.Settings.Duplex);
        Assert.Equal(PrintOrientation.Landscape, ticket.Settings.Orientation);
        Assert.False(ticket.Settings.Collate);
    }

    [Fact]
    public void ActualSizeSaysThatEdgesAreCutOff()
    {
        using var dialog = Dialog();

        Assert.False(dialog.ActualSizeSelected);
        dialog.SelectedScaling = dialog.ScalingChoices.Single(choice => choice.Value == PrintScaling.ActualSize);

        Assert.True(dialog.ActualSizeSelected);
        Assert.Equal("What does not fit on the paper is cut off.", Strings.PrintScalingActualHint);
    }

    [Fact]
    public void TheDialogStartsWithTheDefaultsItWasGiven()
    {
        using var dialog = Dialog(new PrintSettings { Copies = 4, Collate = false, Scaling = PrintScaling.ActualSize, Pages = PrintPageChoice.Range, Range = "2-3" });

        Assert.Equal("4", dialog.CopiesText);
        Assert.False(dialog.Collate);
        Assert.True(dialog.ActualSizeSelected);
        Assert.True(dialog.RangeSelected);
        Assert.Equal("2-3", dialog.RangeText);
    }

    [AvaloniaFact]
    public async Task NothingOfTheCardIsBuiltBeforeTheFirstPrint()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        Assert.Null(TryFind<PrintCard>(window, "PrintCard"));
        Assert.Null(TryFind<PrintJobCard>(window, "PrintJobCard"));
        Assert.Null(viewModel.Print);
    }

    [AvaloniaFact]
    public async Task EnterPrintsOnlyWhenTheChoicesAreValid()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        view.Focus();
        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null });
        var field = Find<TextBox>(window, "RangeField");
        field.Focus();
        viewModel.Print!.RangeText = "9-1";
        Settle(window);

        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Empty(Service.Jobs);
        Assert.Equal(OverlayKind.Print, viewModel.Overlay);

        viewModel.Print.RangeText = "1-2";
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        await WaitUntilAsync(window, () => Service.Jobs.Count == 1);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal("1-2", Service.Jobs[0].Ticket.Settings.Range);
    }

    [AvaloniaFact]
    public async Task EscapeCancelsAndTheKeysGoBackToTheDocument()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        view.Focus();
        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "PrinterChoice" });

        Press(window, Key.Escape, PhysicalKey.Escape, None);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(Find<PrintCard>(window, "PrintCard").IsVisible);
        await WaitUntilAsync(window, () => view.IsFocused);
        Assert.Empty(Service.Jobs);
    }

    [AvaloniaFact]
    public async Task TheButtonsAndTheFieldsHaveNames()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null });

        var card = Find<PrintCard>(window, "PrintCard");

        Assert.Equal(Strings.PrintTitle, Avalonia.Automation.AutomationProperties.GetName(card));
        Assert.Equal(Strings.PrintStart, Avalonia.Automation.AutomationProperties.GetName(Find<Button>(window, "PrintStartButton")));
        Assert.Equal(Strings.PrintCancel, Avalonia.Automation.AutomationProperties.GetName(Find<Button>(window, "PrintCancelButton")));
        Assert.Equal(Strings.PrintCopies, Avalonia.Automation.AutomationProperties.GetName(Find<TextBox>(window, "CopiesField")));
        Assert.Equal(Strings.PrintPrinter, Avalonia.Automation.AutomationProperties.GetName(Find<ComboBox>(window, "PrinterChoice")));
    }

    [Fact]
    public void ThePageSizeForPrintingIsASettingOfTheSettingsCard()
    {
        var settings = Pagewing.App.Services.UserSettings.Detached();
        var card = new SettingsViewModel(settings);

        Assert.Equal(PrintScaling.FitToPaper, card.SelectedPrintScaling!.Value);
        card.SelectedPrintScaling = card.PrintScalingChoices.Single(choice => choice.Value == PrintScaling.ActualSize);

        Assert.Equal(PrintScaling.ActualSize, settings.Current.PrintScaling);
        Assert.Equal([Strings.PrintScalingFit, Strings.PrintScalingActual], card.PrintScalingChoices.Select(choice => choice.Title));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The dialog did not reach the expected state.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
