using Pagewing.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let the running command finish its cleanup and report "Canceled." instead of killing the process.
    e.Cancel = true;
    cancellation.Cancel();
};

using var stdout = Console.OpenStandardOutput();
return await CliApp.RunAsync(args, stdout, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
