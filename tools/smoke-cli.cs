// Smoke test for the published (Native AOT) CLI binary.
//   dotnet run tools/smoke-cli.cs -- <path-to-pagewing-binary>
// Run from the repository root: the test PDF is testdata/pdfs/text-basic.pdf relative to the current directory.
// Checks `--version`, the usage message, and `info`, `render` and `text` against the test PDF. The last three
// need the native PDFium library next to the binary, so they prove the published package is complete.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("Aufruf: dotnet run tools/smoke-cli.cs -- <Pfad zum pagewing-Binary>");
    return 2;
}

var binary = Path.GetFullPath(args[0]);
var pdf = Path.GetFullPath(Path.Combine("testdata", "pdfs", "text-basic.pdf"));
if (!File.Exists(pdf))
{
    Console.Error.WriteLine($"Test-PDF nicht gefunden: {pdf}. Das Skript muss im Repo-Root laufen.");
    return 2;
}

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

var info = Run(binary, "info", pdf);
if (info.ExitCode != 0 || !info.Output.Contains("Pages: 2", StringComparison.Ordinal))
{
    failures.Add($"info: erwartet Exit 0 und 'Pages: 2', erhalten Exit {info.ExitCode} mit '{info.Output.Trim()}' / '{info.Error.Trim()}'");
}

var outputDirectory = Path.Combine(Path.GetTempPath(), "pagewing-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var render = Run(binary, "render", pdf, "--pages", "1", "--output", outputDirectory);
    var png = Path.Combine(outputDirectory, "text-basic-p001.png");
    byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    if (render.ExitCode != 0 || !File.Exists(png))
    {
        failures.Add($"render: erwartet Exit 0 und {png}, erhalten Exit {render.ExitCode} mit '{render.Output.Trim()}' / '{render.Error.Trim()}'");
    }
    else if (!File.ReadAllBytes(png).AsSpan().StartsWith(signature))
    {
        failures.Add($"render: {png} beginnt nicht mit der PNG-Signatur");
    }
}
finally
{
    if (Directory.Exists(outputDirectory))
    {
        Directory.Delete(outputDirectory, recursive: true);
    }
}

var text = Run(binary, "text", pdf);
if (text.ExitCode != 0 || !text.Output.Contains("Hello, Pagewing", StringComparison.Ordinal))
{
    failures.Add($"text: erwartet Exit 0 und 'Hello, Pagewing', erhalten Exit {text.ExitCode} mit '{text.Output.Trim()}' / '{text.Error.Trim()}'");
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

Console.WriteLine(failures.Count == 0 ? $"Smoke-Test bestanden ({version.Output.Trim()})." : "Smoke-Test fehlgeschlagen.");
return failures.Count == 0 ? 0 : 1;

static (int ExitCode, string Output, string Error) Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
    };
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} konnte nicht gestartet werden.");

    // Read both streams at the same time so a full pipe cannot block the process.
    var errorTask = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    var error = errorTask.GetAwaiter().GetResult();
    process.WaitForExit();
    return (process.ExitCode, output, error);
}
