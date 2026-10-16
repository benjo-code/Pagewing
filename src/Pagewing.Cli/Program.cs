using Pagewing.Cli;

using var cancellation = new CancellationTokenSource();
var cancelKey = new CancelKeyHandler(cancellation);

// First press: the running command finishes its cleanup and reports "Canceled.". Second press: terminate now.
Console.CancelKeyPress += (_, e) => e.Cancel = cancelKey.OnPress();

using var stdout = Console.OpenStandardOutput();
return await CliApp.RunAsync(args, stdout, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
