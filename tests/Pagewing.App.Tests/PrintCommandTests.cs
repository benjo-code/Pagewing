using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Printing;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PrintCommandTests : PrintTestHost
{
    [AvaloniaFact]
    public async Task CtrlPOpensTheDialogForAnOpenDocument()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        view.Focus();

        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Print);

        var card = Find<PrintCard>(window, "PrintCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(viewModel.Print, card.DataContext);
        await WaitUntilAsync(window, () => viewModel.Print!.Printers.Count == 2);
        Assert.Equal("Plotter", viewModel.Print!.SelectedPrinter!.Value);
        Assert.Single(Service.Printers, printer => printer.IsDefault);
    }

    [AvaloniaFact]
    public void CtrlPDoesNothingWithoutADocument()
    {
        var (window, viewModel, _) = Show();
        window.Focus();

        Press(window, Key.P, PhysicalKey.P, Ctrl);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.PrintJob);
        Assert.False(viewModel.Commands.ById("file.print")!.CanExecute());
    }

    [AvaloniaFact]
    public void ThePrintCommandIsListedInTheHelpAndThePalette()
    {
        var (_, viewModel, _) = Show();

        var command = viewModel.Commands.ById("file.print")!;

        Assert.Equal("Ctrl+P", GestureText.Of(command.Gesture!));
        Assert.Equal(CommandCatalog.FileGroup, command.Group);
        Assert.Equal(Strings.CommandPrint, command.Title());
        Assert.True(command.Listed);
    }

    [AvaloniaFact]
    public async Task NoPrintServiceIsCreatedAtStartup()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        Assert.Equal(0, ServicesMade);

        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Print);

        Assert.Equal(1, ServicesMade);
    }

    [AvaloniaFact]
    public async Task APlatformWithoutAServiceShowsAMessage()
    {
        var (window, viewModel, view) = Show();
        Service.Available = false;
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is not null);

        Assert.Equal(Strings.PrintNoService, viewModel.PrintJob!.Text);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Empty(Service.Jobs);
    }

    [AvaloniaFact]
    public async Task ADocumentThatForbidsPrintingShowsTheReasonAndSendsNothing()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData(window, "encrypted_hello_world_r3.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.IsPasswordPrompt);
        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsReady);

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is not null);

        Assert.Equal(Strings.PrintForbidden, viewModel.PrintJob!.Text);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Empty(Service.Jobs);
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public async Task ConfirmingTheDialogPrintsAndRemembersThePrinterAndScaling()
    {
        var settings = UserSettings.Detached();
        var (window, viewModel, view) = Show(settings);
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null });
        var dialog = viewModel.Print!;
        dialog.SelectedPrinter = dialog.Printers.Single(printer => printer.Value == "Office");
        dialog.RangeText = "2-3";
        dialog.CopiesText = "2";
        dialog.SelectedScaling = dialog.ScalingChoices.Single(choice => choice.Value == PrintScaling.ActualSize);

        dialog.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => Service.Jobs.Count == 1);

        var job = Service.Jobs.Single();
        Assert.Equal("Office", job.Ticket.Settings.Printer);
        Assert.Equal(PrintPageChoice.Range, job.Ticket.Settings.Pages);
        Assert.Equal("2-3", job.Ticket.Settings.Range);
        Assert.Equal(2, job.Ticket.Settings.Copies);
        Assert.Equal(PrintScaling.ActualSize, job.Ticket.Settings.Scaling);
        Assert.Equal("print-mixed.pdf", job.DocumentName);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal("Office", settings.Current.LastPrinter);
        Assert.Equal(PrintScaling.ActualSize, settings.Current.PrintScaling);
    }

    [AvaloniaFact]
    public async Task TheSystemDialogAsksWithTheDocumentAndPrintsWhatItReturns()
    {
        Service.SystemDialog = true;
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        viewModel.ActiveTab!.GoToPage(3);
        Service.DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter", Copies = 3 });

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => Service.Jobs.Count == 1);

        Assert.Equal(4, Service.LastRequest!.PageCount);
        Assert.Equal(2, Service.LastRequest.CurrentPage);
        Assert.Equal("print-mixed.pdf", Service.LastRequest.DocumentName);
        Assert.Equal(3, Service.Jobs.Single().Ticket.Settings.Copies);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    [AvaloniaFact]
    public async Task CancellingTheSystemDialogPrintsNothing()
    {
        Service.SystemDialog = true;
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        Service.DialogTicket = null;

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => Service.LastRequest is not null);

        Assert.Empty(Service.Jobs);
        Assert.Null(viewModel.PrintJob);
    }

    [AvaloniaFact]
    public async Task PrintingNeverChangesTheViewState()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        var zoom = tab.Zoom;
        var page = tab.CurrentPage;
        var layout = tab.LayoutMode;

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null });
        viewModel.Print!.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => Service.Jobs.Count == 1);

        Assert.Equal(zoom, tab.Zoom);
        Assert.Equal(page, tab.CurrentPage);
        Assert.Equal(layout, tab.LayoutMode);
    }

    [AvaloniaFact]
    public async Task ThePrintButtonOfTheActionBarOpensTheDialog()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        view.Focus();
        var button = Find<Button>(window, "PrintButton");
        var settings = Find<Button>(window, "SettingsButton");

        Assert.True(button.IsEffectivelyEnabled);
        Assert.Equal(Strings.PrintTitle, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal($"{Strings.PrintTitle} (Ctrl+P)", ToolTip.GetTip(button));
        Assert.Same(TokenTests.Resource("Pw.Icon.Printer", Avalonia.Styling.ThemeVariant.Light), button.GetVisualDescendants().OfType<StrokeIcon>().Single().Data);

        // Above the settings button, same size, in the same column.
        var above = button.TranslatePoint(default, window)!.Value;
        var below = settings.TranslatePoint(default, window)!.Value;
        Assert.True(above.Y + button.Bounds.Height < below.Y);
        Assert.Equal(above.X, below.X);
        Assert.Equal(settings.Bounds.Size, button.Bounds.Size);

        Click(window, button);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Print);
        Assert.True(Find<PrintCard>(window, "PrintCard").IsEffectivelyVisible);

        // Esc closes it and the keys go back to the document, not to the button that was pressed with the mouse.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    [AvaloniaFact]
    public async Task ThePrintButtonIsDisabledForATabWithoutAnOpenDocument()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(Pagewing.TestSupport.TestData.Pdf("not-a-pdf.pdf"));
        await WaitUntilAsync(window, () => viewModel.ActiveTab!.IsFailed);
        var button = Find<Button>(window, "PrintButton");

        Assert.False(button.IsEffectivelyEnabled);

        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await WaitUntilAsync(window, () => button.IsEffectivelyEnabled);
        viewModel.Tabs.Activate(viewModel.Tabs[0]);
        await WaitUntilAsync(window, () => !button.IsEffectivelyEnabled);
    }

    [Fact]
    public void TheServiceOfThePlatformIsChosenByTheOperatingSystem()
    {
        var service = PlatformPrintServices.Create();

        Assert.NotNull(service);
        Assert.Equal(OperatingSystem.IsWindows(), service.HasSystemDialog);
    }

    private static string TestData(Avalonia.Controls.Window window, string name)
    {
        Assert.NotNull(window);
        return Pagewing.TestSupport.TestData.Pdf(name);
    }
}
