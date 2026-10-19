using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PrintProgressTests : PrintTestHost
{
    private sealed class WatchedTicket(PrintSettings settings) : PrintTicket(settings)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [AvaloniaFact]
    public async Task ProgressShowsPagesDoneOfTotal()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service.OnPrint = async (_, progress, token) =>
        {
            progress.Report(new PrintProgress(1, 4, []));
            await gate.Task.WaitAsync(token);
        };
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { Done: 1 });

        var job = viewModel.PrintJob!;
        Assert.True(job.IsRunning);
        Assert.Equal(4, job.Total);
        Assert.Equal("Printing page 2 of 4 …", job.Text);
        Assert.True(job.ShowsProgress);
        var card = Find<PrintJobCard>(window, "PrintJobCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(Strings.PrintJobCancel, Find<Button>(window, "PrintJobButton").Content);
        gate.SetResult();
        await WaitUntilAsync(window, () => viewModel.PrintJob is { State: PrintJobState.Done });
        Assert.Equal(Strings.PrintDone, viewModel.PrintJob!.Text);
        Assert.Equal(Strings.PrintJobClose, Find<Button>(window, "PrintJobButton").Content);
    }

    [AvaloniaFact]
    public async Task CancelStopsTheJobAndTheCardSaysSo()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service.OnPrint = async (_, progress, token) =>
        {
            progress.Report(new PrintProgress(1, 4, []));
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => started.Task.IsCompleted);

        viewModel.PrintJob!.ActionCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });

        Assert.Equal(Strings.PrintCancelled, viewModel.PrintJob!.Text);
        Assert.Equal(PrintJobState.Notice, viewModel.PrintJob.State);
    }

    [AvaloniaFact]
    public async Task ClosingTheTabCancelsTheJob()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service.OnPrint = async (_, _, token) =>
        {
            seen = token;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => started.Task.IsCompleted);

        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(PrintJobState.Notice, viewModel.PrintJob!.State);
    }

    [AvaloniaFact]
    public async Task ASecondJobInTheSameWindowIsRefusedWithAHint()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service.OnPrint = (_, _, token) => gate.Task.WaitAsync(token);
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);
        var first = viewModel.PrintJob;

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => !ReferenceEquals(viewModel.PrintJob, first) || viewModel.Overlay != OverlayKind.None);

        Assert.Equal(Strings.PrintBusy, viewModel.PrintJob!.Text);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Single(Service.Jobs);
        gate.SetResult();
    }

    [AvaloniaFact]
    public async Task AFailedPageIsNamedAtTheEnd()
    {
        Service.OnPrint = (_, progress, _) =>
        {
            progress.Report(new PrintProgress(4, 4, [6, 2]));
            return Task.CompletedTask;
        };
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });

        Assert.Equal(PrintJobState.Failed, viewModel.PrintJob!.State);
        Assert.Equal("Pages 3, 7 could not be printed.", viewModel.PrintJob.Text);
        Assert.True(viewModel.PrintJob.IsFailed);
    }

    [AvaloniaFact]
    public async Task APrinterErrorStopsAndShowsTheMessageOfTheSystem()
    {
        Service.OnPrint = (_, _, _) => throw new PrintException("The printer is out of paper.");
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });

        Assert.Equal("The printer is out of paper.", viewModel.PrintJob!.Text);
        Assert.Equal(PrintJobState.Failed, viewModel.PrintJob.State);
        Assert.Equal(Strings.PrintJobClose, viewModel.PrintJob.ActionText);
    }

    [AvaloniaTheory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task TheTicketIsDisposedAfterSuccessFailureAndCancel(string outcome)
    {
        var ticket = new WatchedTicket(new PrintSettings { Printer = "Office" });
        Service.SystemDialog = true;
        Service.DialogTicket = ticket;
        Service.OnPrint = (_, _, _) => outcome switch
        {
            "failed" => throw new PrintException("boom"),
            "cancelled" => throw new OperationCanceledException(),
            _ => Task.CompletedTask,
        };
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => ticket.Disposed);

        Assert.True(ticket.Disposed);
    }

    [AvaloniaFact]
    public async Task TheUiThreadNeverWaitsOnTheJob()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service.OnPrint = (_, _, token) => gate.Task.WaitAsync(token);
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);

        // The window goes on: a command runs while the job is still going.
        tab.ZoomInCommand.Execute(null);
        Settle(window);

        Assert.True(viewModel.PrintJob!.IsRunning);
        gate.SetResult();
        await WaitUntilAsync(window, () => !viewModel.PrintJob!.IsRunning);
    }

    [AvaloniaFact]
    public async Task AFinishedJobGoesByItself()
    {
        var clock = new PrintClock();
        var (window, viewModel, view) = Show();
        viewModel.Clock = clock;
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });
        Assert.NotNull(viewModel.PrintJob);

        clock.Advance(MainViewModel.PrintCardLifetime + TimeSpan.FromSeconds(1));
        await WaitUntilAsync(window, () => viewModel.PrintJob is null);

        Assert.False(Find<PrintJobCard>(window, "PrintJobCard").IsVisible);
    }

    [AvaloniaFact]
    public async Task AFailureStaysUntilItIsClosed()
    {
        var clock = new PrintClock();
        Service.OnPrint = (_, _, _) => throw new PrintException("The printer is off.");
        var (window, viewModel, view) = Show();
        viewModel.Clock = clock;
        await OpenAsync(window, viewModel, view, "print-mixed.pdf");
        await PrintNowAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsFailed: true });

        clock.Advance(TimeSpan.FromMinutes(5));
        Settle(window);
        Assert.NotNull(viewModel.PrintJob);

        viewModel.PrintJob!.ActionCommand.Execute(null);
        Assert.Null(viewModel.PrintJob);
    }

    private static async Task PrintNowAsync(Window window, MainViewModel viewModel)
    {
        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null });
        viewModel.Print!.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is not null);
    }
}
