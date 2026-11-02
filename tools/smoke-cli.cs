// Smoke test for the published (Native AOT) CLI binary.
//   dotnet run tools/smoke-cli.cs -- <path-to-pagewing-binary>
// Run from the repository root: the test PDF is testdata/pdfs/text-basic.pdf relative to the current directory.
// Checks `--version`, the usage message, and `info`, `render` and `text` against the test PDF. The last three
// need the native PDFium library next to the binary, so they prove the published package is complete.
// Since M14 it also runs `redact` on testdata/pdfs/redact-everywhere-nopattern.pdf (a copy in a temp folder):
// a real redaction (exit 0, report on stderr, the term gone from `text`), a --dry-run (no output file) and a
// term without a match (exit 1, no output file).
// Since M14.1 it also runs `redact` with --pattern iban (redact-patterns.pdf), --image-fill pixelate (redact-image-blocks.pdf),
// --overlay-text (the embedded font and the glyph tables, in a --dry-run and a real run) and --rasterize-unredactable
// (redact-everywhere.pdf: a page that cannot be redacted in place becomes an image), so the published binary runs the new code.
// Since M15 it also runs `ocr`: a --dry-run (no library needed: the decision and the resolution of each page of ocr-mixed.pdf) and,
// when the OCR library is next to the binary (the published package has it), a real run on ocr-scan.pdf whose output `text` finds the words.

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

// redact: the term sits in the page text, a form object, invisible text, a note and its reply, a form field, a bookmark, the
// information dictionary, the XMP and an attachment; the area over the note and its reply is part of the call (replies are not searched).
var redactSource = Path.GetFullPath(Path.Combine("testdata", "pdfs", "redact-everywhere-nopattern.pdf"));
var redactDirectory = Path.Combine(Path.GetTempPath(), "pagewing-smoke-redact-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(redactDirectory);
try
{
    var redactInput = Path.Combine(redactDirectory, "input.pdf");
    File.Copy(redactSource, redactInput);

    var redactedFile = Path.Combine(redactDirectory, "out.pdf");
    var redact = Run(binary, "redact", redactInput, "--output", redactedFile, "--text", "Zephyrstraße 17", "--rect", "1:248,40,42,22");
    if (redact.ExitCode != 0 || !File.Exists(redactedFile) || !string.Equals(redact.Output.Trim(), redactedFile, StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"redact: erwartet Exit 0, {redactedFile} und den Pfad auf stdout, erhalten Exit {redact.ExitCode} mit '{redact.Output.Trim()}' / '{redact.Error.Trim()}'");
    }
    else
    {
        if (!redact.Error.Contains("Cleaned (read back", StringComparison.Ordinal) || !redact.Error.Contains("Not checked:", StringComparison.Ordinal))
        {
            failures.Add($"redact: der Bericht auf stderr nennt 'Cleaned (read back' und 'Not checked:' nicht: '{redact.Error.Trim()}'");
        }

        var redactedText = Run(binary, "text", redactedFile);
        if (redactedText.ExitCode != 0 || redactedText.Output.Contains("Zephyr", StringComparison.Ordinal) || !redactedText.Output.Contains("Seite zwei", StringComparison.Ordinal))
        {
            failures.Add($"redact: 'text' der Ausgabe soll 'Zephyr' nicht und 'Seite zwei' enthalten, erhalten Exit {redactedText.ExitCode} mit '{redactedText.Output.Trim()}' / '{redactedText.Error.Trim()}'");
        }
    }

    var dryRunFile = Path.Combine(redactDirectory, "dry.pdf");
    var dryRun = Run(binary, "redact", redactInput, "--output", dryRunFile, "--text", "Zephyr", "--rect", "1:248,40,42,22", "--dry-run");
    if (dryRun.ExitCode != 0 || File.Exists(dryRunFile))
    {
        failures.Add($"redact --dry-run: erwartet Exit 0 und keine Ausgabedatei, erhalten Exit {dryRun.ExitCode}, Datei vorhanden: {File.Exists(dryRunFile)}, '{dryRun.Error.Trim()}'");
    }

    var noMatchFile = Path.Combine(redactDirectory, "nomatch.pdf");
    var noMatch = Run(binary, "redact", redactInput, "--output", noMatchFile, "--text", "gibt es nicht");
    if (noMatch.ExitCode != 1 || File.Exists(noMatchFile))
    {
        failures.Add($"redact ohne Treffer: erwartet Exit 1 und keine Ausgabedatei, erhalten Exit {noMatch.ExitCode}, Datei vorhanden: {File.Exists(noMatchFile)}, '{noMatch.Error.Trim()}'");
    }

    // --pattern iban: the valid IBANs of the fixture go (the one split across two lines is not found), the card number stays.
    var patternInput = Path.Combine(redactDirectory, "patterns.pdf");
    File.Copy(Path.GetFullPath(Path.Combine("testdata", "pdfs", "redact-patterns.pdf")), patternInput);
    var patternFile = Path.Combine(redactDirectory, "patterns-out.pdf");
    var pattern = Run(binary, "redact", patternInput, "--output", patternFile, "--pattern", "iban");
    if (pattern.ExitCode != 0 || !File.Exists(patternFile))
    {
        failures.Add($"redact --pattern iban: erwartet Exit 0 und {patternFile}, erhalten Exit {pattern.ExitCode} mit '{pattern.Output.Trim()}' / '{pattern.Error.Trim()}'");
    }
    else
    {
        var patternText = Run(binary, "text", patternFile);
        var flat = patternText.Output.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (patternText.ExitCode != 0 || flat.Contains("DE89370400440532013000", StringComparison.Ordinal) || !flat.Contains("4111111111111111", StringComparison.Ordinal))
        {
            failures.Add($"redact --pattern iban: 'text' der Ausgabe soll die IBAN nicht und die Kartennummer noch enthalten, erhalten Exit {patternText.ExitCode} mit '{patternText.Output.Trim()}' / '{patternText.Error.Trim()}'");
        }
    }

    // --image-fill pixelate: the report names the treated image areas, and the result still opens.
    var imageInput = Path.Combine(redactDirectory, "image-blocks.pdf");
    File.Copy(Path.GetFullPath(Path.Combine("testdata", "pdfs", "redact-image-blocks.pdf")), imageInput);
    var imageFile = Path.Combine(redactDirectory, "image-out.pdf");
    var pixelate = Run(binary, "redact", imageInput, "--output", imageFile, "--rect", "1:46,56,72,72", "--image-fill", "pixelate", "--image-strength", "coarse");
    if (pixelate.ExitCode != 0 || !File.Exists(imageFile) || !pixelate.Error.Contains("Image areas", StringComparison.Ordinal) || !pixelate.Error.Contains("pixelated", StringComparison.Ordinal))
    {
        failures.Add($"redact --image-fill pixelate: erwartet Exit 0, {imageFile} und 'Image areas' / 'pixelated' im Bericht, erhalten Exit {pixelate.ExitCode} mit '{pixelate.Output.Trim()}' / '{pixelate.Error.Trim()}'");
    }
    else
    {
        var imageInfo = Run(binary, "info", imageFile);
        if (imageInfo.ExitCode != 0 || !imageInfo.Output.Contains("Pages: 1", StringComparison.Ordinal))
        {
            failures.Add($"redact --image-fill pixelate: das Ergebnis lässt sich nicht öffnen, erhalten Exit {imageInfo.ExitCode} mit '{imageInfo.Output.Trim()}' / '{imageInfo.Error.Trim()}'");
        }
    }

    // --overlay-text: the text is laid out with the embedded font (dry run counts the area), drawn as shapes (never as text).
    var overlayDry = Run(binary, "redact", redactInput, "--rect", "1:20,275,180,25", "--overlay-text", "REDACTED", "--dry-run");
    if (overlayDry.ExitCode != 0 || !overlayDry.Error.Contains("Overlay text: 1 area gets the overlay text", StringComparison.Ordinal))
    {
        failures.Add($"redact --overlay-text --dry-run: erwartet Exit 0 und 'Overlay text: 1 area gets the overlay text', erhalten Exit {overlayDry.ExitCode} mit '{overlayDry.Error.Trim()}'");
    }

    var overlayFile = Path.Combine(redactDirectory, "overlay-out.pdf");
    var overlay = Run(binary, "redact", redactInput, "--output", overlayFile, "--rect", "1:20,275,180,25", "--overlay-text", "REDACTED");
    if (overlay.ExitCode != 0 || !File.Exists(overlayFile))
    {
        failures.Add($"redact --overlay-text: erwartet Exit 0 und {overlayFile}, erhalten Exit {overlay.ExitCode} mit '{overlay.Output.Trim()}' / '{overlay.Error.Trim()}'");
    }
    else
    {
        var overlayText = Run(binary, "text", overlayFile);
        if (overlayText.ExitCode != 0 || overlayText.Output.Contains("REDACTED", StringComparison.Ordinal))
        {
            failures.Add($"redact --overlay-text: der Text darf in 'text' der Ausgabe nicht stehen (er wird als Formen gezeichnet), erhalten Exit {overlayText.ExitCode} mit '{overlayText.Output.Trim()}'");
        }
    }

    // --rasterize-unredactable: page 1 of redact-everywhere.pdf (tiling pattern) cannot be redacted in place and becomes an image.
    var rasterInput = Path.Combine(redactDirectory, "everywhere.pdf");
    File.Copy(Path.GetFullPath(Path.Combine("testdata", "pdfs", "redact-everywhere.pdf")), rasterInput);
    var rasterFile = Path.Combine(redactDirectory, "raster-out.pdf");
    var rasterRefused = Run(binary, "redact", rasterInput, "--output", Path.Combine(redactDirectory, "refused.pdf"), "--text", "Zephyrstraße 17");
    if (rasterRefused.ExitCode != 1 || File.Exists(Path.Combine(redactDirectory, "refused.pdf")))
    {
        failures.Add($"redact ohne --rasterize-unredactable: die Seite mit Muster soll abgelehnt werden (Exit 1, keine Ausgabedatei), erhalten Exit {rasterRefused.ExitCode} mit '{rasterRefused.Error.Trim()}'");
    }

    var raster = Run(binary, "redact", rasterInput, "--output", rasterFile, "--text", "Zephyrstraße 17", "--rasterize-unredactable");
    if (raster.ExitCode != 0 || !File.Exists(rasterFile) || !raster.Error.Contains("Pages turned into images", StringComparison.Ordinal))
    {
        failures.Add($"redact --rasterize-unredactable: erwartet Exit 0, {rasterFile} und 'Pages turned into images', erhalten Exit {raster.ExitCode} mit '{raster.Output.Trim()}' / '{raster.Error.Trim()}'");
    }
    else
    {
        var rasterText = Run(binary, "text", rasterFile);
        if (rasterText.ExitCode != 0 || rasterText.Output.Contains("Zephyr", StringComparison.Ordinal) || !rasterText.Output.Contains("Seite zwei", StringComparison.Ordinal))
        {
            failures.Add($"redact --rasterize-unredactable: 'text' der Ausgabe soll 'Zephyr' nicht und 'Seite zwei' enthalten, erhalten Exit {rasterText.ExitCode} mit '{rasterText.Output.Trim()}' / '{rasterText.Error.Trim()}'");
        }
    }
}
finally
{
    Directory.Delete(redactDirectory, recursive: true);
}

// ocr: the dry run lists what would be recognized (it loads no library, so the language folder only has to exist).
var ocrDirectory = Path.Combine(Path.GetTempPath(), "pagewing-smoke-ocr-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ocrDirectory);
try
{
    var dummyData = Path.Combine(ocrDirectory, "tessdata");
    Directory.CreateDirectory(dummyData);
    File.WriteAllBytes(Path.Combine(dummyData, "eng.traineddata"), [1]);
    var ocrMixed = Path.GetFullPath(Path.Combine("testdata", "pdfs", "ocr-mixed.pdf"));
    var ocrDry = Run(binary, "ocr", ocrMixed, "--dry-run", "--tessdata", dummyData, "--language", "eng");
    if (ocrDry.ExitCode != 0 || !ocrDry.Output.Contains("page 1: skip (has text of its own)", StringComparison.Ordinal) || !ocrDry.Output.Contains("would recognize 2 pages", StringComparison.Ordinal))
    {
        failures.Add($"ocr --dry-run: erwartet Exit 0, 'page 1: skip (has text of its own)' und 'would recognize 2 pages', erhalten Exit {ocrDry.ExitCode} mit '{ocrDry.Output.Trim()}' / '{ocrDry.Error.Trim()}'");
    }

    // A real run, when the package has the library and the language data next to the binary.
    var binaryFolder = Path.GetDirectoryName(binary)!;
    var libraryName = OperatingSystem.IsWindows() ? "tesseract.dll" : "libtesseract.so";
    var shippedData = new[] { Path.Combine(binaryFolder, "tessdata"), Path.Combine(binaryFolder, "..", "tessdata") }.FirstOrDefault(Directory.Exists);
    if (File.Exists(Path.Combine(binaryFolder, libraryName)) && shippedData is not null)
    {
        var ocrInput = Path.Combine(ocrDirectory, "scan.pdf");
        File.Copy(Path.GetFullPath(Path.Combine("testdata", "pdfs", "ocr-scan.pdf")), ocrInput);
        var ocrFile = Path.Combine(ocrDirectory, "scan-ocr.pdf");
        var ocr = Run(binary, "ocr", ocrInput, "--output", ocrFile, "--pages", "1", "--language", "eng");
        if (ocr.ExitCode != 0 || !File.Exists(ocrFile) || !ocr.Error.Contains("Recognized 1 page", StringComparison.Ordinal))
        {
            failures.Add($"ocr: erwartet Exit 0, {ocrFile} und 'Recognized 1 page', erhalten Exit {ocr.ExitCode} mit '{ocr.Output.Trim()}' / '{ocr.Error.Trim()}'");
        }
        else
        {
            var ocrText = Run(binary, "text", ocrFile, "--pages", "1");
            if (ocrText.ExitCode != 0 || !ocrText.Output.Contains("Invoice", StringComparison.Ordinal))
            {
                failures.Add($"ocr: 'text' der Ausgabe soll 'Invoice' enthalten, erhalten Exit {ocrText.ExitCode} mit '{ocrText.Output.Trim()}' / '{ocrText.Error.Trim()}'");
            }
        }
    }
    else
    {
        Console.WriteLine($"ocr: ohne Bibliothek und Sprachdaten neben dem Binary nur der Trockenlauf geprüft ({libraryName} fehlt).");
    }
}
finally
{
    Directory.Delete(ocrDirectory, recursive: true);
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
