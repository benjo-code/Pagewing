// Smoke test for the published (Native AOT) CLI binary.
//   dotnet run tools/smoke-cli.cs -- <path-to-pagewing-binary>
// Exit code 0 when `--version` prints a semantic version and a call without arguments fails with usage.

using System.Diagnostics;
using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("Aufruf: dotnet run tools/smoke-cli.cs -- <Pfad zum pagewing-Binary>");
    return 2;
}

var binary = Path.GetFullPath(args[0]);
var failures = new List<string>();

var version = Run(binary, "--version");
if (version.ExitCode != 0 || !Regex.IsMatch(version.Output.Trim(), @"^\d+\.\d+\.\d+"))
{
    failures.Add($"--version: erwartet Exit 0 und eine Version, erhalten Exit {version.ExitCode} mit '{version.Output.Trim()}'");
}

var usage = Run(binary);
if (usage.ExitCode != 2 || !usage.Error.Contains("Usage", StringComparison.Ordinal))
{
    failures.Add($"ohne Argumente: erwartet Exit 2 und 'Usage' auf stderr, erhalten Exit {usage.ExitCode} mit '{usage.Error.Trim()}'");
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

Console.WriteLine(failures.Count == 0 ? $"Smoke-Test bestanden ({version.Output.Trim()})." : "Smoke-Test fehlgeschlagen.");
return failures.Count == 0 ? 0 : 1;

static (int ExitCode, string Output, string Error) Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} konnte nicht gestartet werden.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error);
}
