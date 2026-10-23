using Pagewing.Cli;

// Native AOT: AppContext must be initialized before any other thread starts (Docs/DECISIONS.md, entry
// "Startabsturz 0xC0000602: AppContext vor dem PDFium-Thread initialisieren (M3)"). Keep this the first statement.
_ = AppContext.GetData("System.GC.Server");

using var cancellation = new CancellationTokenSource();
var cancelKey = new CancelKeyHandler(cancellation);

// First press: the running command finishes its cleanup and reports "Canceled.". Second press: terminate now.
Console.CancelKeyPress += (_, e) => e.Cancel = cancelKey.OnPress();

using var stdout = Console.OpenStandardOutput();
return await CliApp.RunAsync(args, stdout, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
