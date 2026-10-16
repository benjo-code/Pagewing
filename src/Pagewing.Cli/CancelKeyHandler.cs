namespace Pagewing.Cli;

/// <summary>
/// Decides what Ctrl+C does: the first press cancels the command gracefully (it finishes its cleanup and
/// reports "Canceled."), every later press lets the runtime terminate the process at once. A single native
/// PDFium step cannot be interrupted, so without the second press a hostile PDF could keep the process alive.
/// </summary>
internal sealed class CancelKeyHandler(CancellationTokenSource cancellation)
{
    private int presses;

    /// <summary>Handles one press of Ctrl+C (or Ctrl+Break).</summary>
    /// <returns>
    /// <see langword="true"/> to keep the process running (the value for <see cref="ConsoleCancelEventArgs.Cancel"/>),
    /// <see langword="false"/> to let the runtime terminate it.
    /// </returns>
    public bool OnPress()
    {
        if (Interlocked.Increment(ref presses) != 1)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The command has already finished and the program is exiting; nothing is left to cancel gracefully.
            return false;
        }
    }
}
