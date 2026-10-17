using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;

namespace Pagewing.App.Tests;

/// <summary>
/// For window tests about the document view: a wide window opens the "Pages" panel after the first sharp tile of the
/// window (<see cref="MainViewModel.OnFirstSharpTileDrawn"/>, see PanelTests), which narrows the viewport at a moment
/// that depends on timing. <see cref="CloseAsync"/> waits for it and closes the panel again, so such a test starts
/// from the layout it was written for: the document with the whole width right of the action bar.
/// </summary>
internal static class AutoOpenedPanel
{
    /// <summary>Name of the element in MainWindow.axaml that holds the side panel; hidden while no panel is shown.</summary>
    public const string HostName = "PanelHost";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the first sharp tile of page 1 of the shown document and for the panel the window may open after it,
    /// then closes an open panel and waits until it is gone.
    /// </summary>
    public static async Task CloseAsync(Window window, MainViewModel viewModel, DocumentView view)
    {
        await WaitUntilAsync(window, () => view.Canvas.Bitmaps.Keys.Any(key => key.Page == 0 && !key.IsPreview));

        // The tile is drawn in the next frame, the window learns of it in the frame after and opens the panel in a
        // background job after that.
        Settle(window);
        Settle(window);
        if (viewModel.ActivePanel != SidePanelKind.None)
        {
            viewModel.CloseSidePanelCommand.Execute(null);
            var host = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == HostName);
            await WaitUntilAsync(window, () => !host.IsVisible);
        }
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
}
