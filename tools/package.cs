#:property PublishAot=false

// Packaging tool of Pagewing (grows with Milestone 8; further subcommands are added to Commands).
//   dotnet run tools/package.cs -- check-binary <file>... [--max-glibc 2.35]
//   dotnet run tools/package.cs -- layout --rid <win-x64|linux-x64> --app <dir> --cli <dir> --out <dir> [--repo <root>]
//   dotnet run tools/package.cs -- zip --layout <dir> --version <x.y.z> --out <file>
//   dotnet run tools/package.cs -- sums <file>... --out <SHA256SUMS.txt>
//   dotnet run tools/package.cs -- selftest
// Run from the repository root. The tool never uses the network.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

return Cli.Run(args);

static class Cli
{
    private const string Usage =
        "Aufruf: dotnet run tools/package.cs -- <Befehl>\n" +
        "  check-binary <Datei>... [--max-glibc 2.35]   prueft GLIBC-Versionen und NEEDED-Bibliotheken (ELF)\n" +
        "  layout --rid <rid> --app <Ordner> --cli <Ordner> --out <Ordner>   legt das Paketlayout an (App, cli/, LICENSE, THIRD-PARTY-NOTICES.txt)\n" +
        "  zip --layout <Ordner> --version <x.y.z> --out <Datei>             reproduzierbare ZIP-Datei, oberster Ordner Pagewing-<Version>/\n" +
        "  sums <Datei>... --out <Datei>                                     SHA-256-Summen (<sha256>  <Dateiname>)\n" +
        "  selftest                                     prueft die Werkzeugteile";

    // Command name -> handler (receives the arguments after the command name). Add new subcommands here.
    private static readonly Dictionary<string, Func<string[], int>> Commands = new(StringComparer.Ordinal)
    {
        ["check-binary"] = CheckBinary.Run,
        ["layout"] = Layout.Run,
        ["zip"] = ZipCommand.Run,
        ["sums"] = Sums.Run,
        ["selftest"] = SelfTest.Run,
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!Commands.TryGetValue(args[0], out var handler))
        {
            Console.Error.WriteLine($"unbekannter Befehl: {args[0]}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return handler(args[1..]);
    }
}

static partial class ElfRules
{
    public const string DefaultMaxGlibc = "2.35";

    public static readonly string[] AllowedNeeded =
    [
        "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "libz.so.1",
        "ld-linux-x86-64.so.2", "libstdc++.so.6", "libgcc_s.so.1",
    ];

    // objdump -T: "0000000000000000      DF *UND*	0000000000000000 (GLIBC_2.2.5) printf"
    [GeneratedRegex(@"\*UND\*.*\(GLIBC_(?<v>\d+(?:\.\d+)*)\)")]
    private static partial Regex UndefinedSymbolVersion();

    // objdump -p: "  NEEDED               libc.so.6"
    [GeneratedRegex(@"^\s*NEEDED\s+(?<n>\S+)\s*$")]
    private static partial Regex NeededLine();

    public static bool TryParseVersion(string text, out int[] version)
    {
        var parts = text.Split('.');
        version = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out version[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Numeric tuple comparison (2.9 < 2.35), missing components count as 0.
    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    public static string? HighestGlibc(string dynamicSymbols, out int[]? version)
    {
        string? text = null;
        version = null;
        foreach (var line in dynamicSymbols.Split('\n'))
        {
            var m = UndefinedSymbolVersion().Match(line);
            if (m.Success && TryParseVersion(m.Groups["v"].Value, out var v) && (version is null || Compare(v, version) > 0))
            {
                version = v;
                text = m.Groups["v"].Value;
            }
        }

        return text;
    }

    public static List<string> Needed(string programHeaders)
    {
        var result = new List<string>();
        foreach (var line in programHeaders.Split('\n'))
        {
            var m = NeededLine().Match(line);
            if (m.Success)
            {
                result.Add(m.Groups["n"].Value);
            }
        }

        return result;
    }

    // Returns the violations for one file, given the objdump -T and objdump -p outputs.
    public static List<string> Violations(string dynamicSymbols, string programHeaders, int[] max, string maxText)
    {
        var problems = new List<string>();
        var highest = HighestGlibc(dynamicSymbols, out var version);
        if (highest is not null && Compare(version!, max) > 0)
        {
            problems.Add($"verlangt GLIBC_{highest} (erlaubt hoechstens GLIBC_{maxText})");
        }

        foreach (var name in Needed(programHeaders))
        {
            if (!AllowedNeeded.Contains(name, StringComparer.Ordinal))
            {
                problems.Add($"unerlaubte NEEDED-Bibliothek {name}");
            }
        }

        return problems;
    }
}

static class CheckBinary
{
    public static int Run(string[] args)
    {
        var files = new List<string>();
        var maxText = ElfRules.DefaultMaxGlibc;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--max-glibc" && i + 1 < args.Length)
            {
                maxText = args[++i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unbekannte Option: {args[i]}");
                return 2;
            }
            else
            {
                files.Add(args[i]);
            }
        }

        if (files.Count == 0 || !ElfRules.TryParseVersion(maxText, out var max))
        {
            Console.Error.WriteLine("Aufruf: check-binary <Datei>... [--max-glibc 2.35]");
            return 2;
        }

        var failed = false;
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"{file}: Datei nicht gefunden");
                failed = true;
                continue;
            }

            var symbols = Objdump(["-T", file]);
            var headers = Objdump(["-p", file]);
            if (symbols is null || headers is null)
            {
                failed = true;
                continue;
            }

            var problems = ElfRules.Violations(symbols, headers, max, maxText);
            if (problems.Count == 0)
            {
                Console.WriteLine($"{file}: ok");
                continue;
            }

            failed = true;
            foreach (var p in problems)
            {
                Console.Error.WriteLine($"{file}: {p}");
            }
        }

        return failed ? 1 : 0;
    }

    // objdump -T prints "not a dynamic object" for a static file; that is not an error here (empty text, no GLIBC need).
    private static string? Objdump(string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("objdump") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var stderr = stderrTask.Result;
            if (p.ExitCode != 0 && !stderr.Contains("not a dynamic object", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{arguments[^1]}: objdump {arguments[0]} fehlgeschlagen: {stderr.Trim()}");
                return null;
            }

            return stdout;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("objdump nicht gefunden: Paket binutils installieren (sudo apt-get install -y binutils)");
            return null;
        }
    }
}

static class Options
{
    // Parses "--name value" pairs; everything else is positional. Returns null (message written) on an unknown or valueless option.
    public static (Dictionary<string, string> Named, List<string> Positional)? Parse(string[] args, params string[] known)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
            }
            else if (known.Contains(args[i], StringComparer.Ordinal) && i + 1 < args.Length)
            {
                named[args[i]] = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"unbekannte Option oder fehlender Wert: {args[i]}");
                return null;
            }
        }

        return (named, positional);
    }
}

static class Layout
{
    private static readonly string[] SkippedExtensions = [".pdb", ".dbg"];

    public static string NativeLibrary(string rid) => rid switch
    {
        "win-x64" => "pdfium.dll",
        "linux-x64" => "libpdfium.so",
        _ => throw new ArgumentException($"unbekannte RID: {rid}", nameof(rid)),
    };

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--rid", "--app", "--cli", "--out", "--repo");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--rid", out var rid) || !p.Named.TryGetValue("--app", out var app) ||
            !p.Named.TryGetValue("--cli", out var cli) || !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: layout --rid <win-x64|linux-x64> --app <Ordner> --cli <Ordner> --out <Ordner>");
            return 2;
        }

        if (rid is not ("win-x64" or "linux-x64"))
        {
            Console.Error.WriteLine($"unbekannte RID: {rid}");
            return 2;
        }

        var repo = p.Named.GetValueOrDefault("--repo", ".");
        var native = NativeLibrary(rid);
        var license = Path.Combine(repo, "LICENSE");
        var notices = Path.Combine(repo, "src", "Pagewing.App", "Resources", "ThirdPartyNotices.txt");
        var problems = new List<string>();
        foreach (var (label, dir) in new[] { ("App", app), ("CLI", cli) })
        {
            if (!Directory.Exists(dir))
            {
                problems.Add($"{label}-Ordner fehlt: {dir}");
            }
            else if (!File.Exists(Path.Combine(dir, native)))
            {
                problems.Add($"{label}-Ordner enthaelt {native} nicht: {dir}");
            }
        }

        foreach (var file in new[] { license, notices })
        {
            if (!File.Exists(file))
            {
                problems.Add($"Datei fehlt: {file}");
            }
        }

        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            problems.Add($"Ausgabeordner ist nicht leer: {output}");
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"layout: {problem}");
            }

            return 1;
        }

        Directory.CreateDirectory(output);
        CopyTree(app, output, rid);
        CopyTree(cli, Path.Combine(output, "cli"), rid);
        Copy(license, Path.Combine(output, "LICENSE"), false);
        Copy(notices, Path.Combine(output, "THIRD-PARTY-NOTICES.txt"), false);
        Console.WriteLine($"layout: {Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count()} Dateien in {output}");
        return 0;
    }

    private static void CopyTree(string source, string target, string rid)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (SkippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var name = Path.GetFileName(file);
            var executable = rid == "linux-x64" && string.Equals(Path.GetDirectoryName(relative), string.Empty, StringComparison.Ordinal) &&
                (name == "Pagewing" || name == "pagewing");
            Copy(file, Path.Combine(target, relative), executable);
        }
    }

    // Executables get 0755, everything else 0644, so the ZIP does not depend on the build machine's umask.
    private static void Copy(string from, string to, bool executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(to, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}

static class ZipCommand
{
    // Fixed timestamp so the archive does not depend on the build time (ZIP stores local time, no zone).
    public static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--layout", "--version", "--out");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--layout", out var layout) || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: zip --layout <Ordner> --version <x.y.z> --out <Datei>");
            return 2;
        }

        if (!Directory.Exists(layout))
        {
            Console.Error.WriteLine($"zip: Layout-Ordner fehlt: {layout}");
            return 1;
        }

        var files = Directory.EnumerateFiles(layout, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Name: Path.GetRelativePath(layout, f).Replace('\\', '/')))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"zip: Layout-Ordner ist leer: {layout}");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.Delete(output);
        using (var stream = File.Create(output))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (path, name) in files)
            {
                var entry = archive.CreateEntry($"Pagewing-{version}/{name}", CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTime;
                var executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
                entry.ExternalAttributes = unchecked((int)((executable ? 0x81EDu : 0x81A4u) << 16)); // regular file, 0755 / 0644
                using var target = entry.Open();
                using var source = File.OpenRead(path);
                source.CopyTo(target);
            }
        }

        Console.WriteLine($"zip: {files.Count} Dateien in {output}");
        return 0;
    }
}

static class Sums
{
    public static string Line(string file)
    {
        using var stream = File.OpenRead(file);
        return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {Path.GetFileName(file)}\n";
    }

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--out");
        if (parsed is not { } p || p.Positional.Count == 0 || !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: sums <Datei>... --out <SHA256SUMS.txt>");
            return 2;
        }

        var missing = p.Positional.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            foreach (var f in missing)
            {
                Console.Error.WriteLine($"sums: Datei nicht gefunden: {f}");
            }

            return 1;
        }

        File.WriteAllText(output, string.Concat(p.Positional.Select(Line)), new UTF8Encoding(false));
        return 0;
    }
}

static class SelfTest
{
    private static int s_checks;
    private static int s_failures;

    private const string Header = "\nDYNAMIC SYMBOL TABLE:\n";

    private static string Symbols(params string[] versions) =>
        Header + string.Concat(versions.Select((v, i) =>
            $"0000000000000000      DF *UND*\t0000000000000000 (GLIBC_{v}) sym{i}\n")) +
        "0000000000001139 g    DF .text\t000000000000000b  Base        main\n";

    private static string Needs(params string[] libs) =>
        "\nDynamic Section:\n" + string.Concat(libs.Select(l => $"  NEEDED               {l}\n")) +
        "  SONAME               libx.so\n";

    public static int Run(string[] args)
    {
        _ = args;
        var max = new[] { 2, 35 };
        const string maxText = "2.35";
        var libc = Needs("libc.so.6");

        Expect("2.34 besteht", ElfRules.Violations(Symbols("2.34"), libc, max, maxText).Count, 0);
        Expect("2.35 besteht (Grenze)", ElfRules.Violations(Symbols("2.35"), libc, max, maxText).Count, 0);
        Expect("2.38 faellt durch", ElfRules.Violations(Symbols("2.2.5", "2.38"), libc, max, maxText).Count, 1);
        Expect("2.9 ist kleiner als 2.35 (Zahlentupel, nicht Text)", ElfRules.Violations(Symbols("2.9"), libc, max, maxText).Count, 0);
        Expect("2.100 ist groesser als 2.35", ElfRules.Violations(Symbols("2.100"), libc, max, maxText).Count, 1);
        Expect("unbekanntes NEEDED faellt durch", ElfRules.Violations(Symbols("2.2.5"), Needs("libc.so.6", "libfoo.so.1"), max, maxText).Count, 1);
        Expect("alle erlaubten NEEDED bestehen", ElfRules.Violations(Symbols("2.2.5"), Needs(ElfRules.AllowedNeeded), max, maxText).Count, 0);
        Expect("SONAME ist kein NEEDED", ElfRules.Needed(Needs("libc.so.6")).Count, 1);
        Expect("definiertes Symbol zaehlt nicht", ElfRules.HighestGlibc("0000 g DF .text 000b (GLIBC_2.99) x\n", out _) ?? "none", "none");
        Expect("Version des hoechsten Symbols", ElfRules.HighestGlibc(Symbols("2.2.5", "2.34", "2.17"), out _) ?? "none", "2.34");
        Expect("Vergleich 2.35 = 2.35.0", ElfRules.Compare([2, 35], [2, 35, 0]), 0);

        PackagingCommands();
        RealElf();
        Expect("unbekannter Befehl liefert 2", Cli.Run(["gibt-es-nicht"]), 2);
        Expect("check-binary ohne Datei liefert 2", Cli.Run(["check-binary"]), 2);

        Console.WriteLine(s_failures == 0
            ? $"selftest: {s_checks} Pruefungen bestanden."
            : $"selftest: {s_failures} von {s_checks} Pruefungen fehlgeschlagen.");
        return s_failures == 0 ? 0 : 1;
    }

    private static void PackagingCommands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pagewing-package-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = Path.Combine(dir, "repo");
            var app = Path.Combine(dir, "app");
            var cli = Path.Combine(dir, "cli");
            Directory.CreateDirectory(Path.Combine(repo, "src", "Pagewing.App", "Resources"));
            File.WriteAllText(Path.Combine(repo, "LICENSE"), "license");
            File.WriteAllText(Path.Combine(repo, "src", "Pagewing.App", "Resources", "ThirdPartyNotices.txt"), "notices");
            foreach (var d in new[] { app, Path.Combine(app, "sub"), cli })
            {
                Directory.CreateDirectory(d);
            }

            File.WriteAllText(Path.Combine(app, "Pagewing"), "app");
            File.WriteAllText(Path.Combine(app, "Pagewing.pdb"), "symbols");
            File.WriteAllText(Path.Combine(app, "Pagewing.dbg"), "symbols");
            File.WriteAllText(Path.Combine(app, "libpdfium.so"), "pdfium");
            File.WriteAllText(Path.Combine(app, "sub", "x.txt"), "x");
            File.WriteAllText(Path.Combine(cli, "pagewing"), "cli");
            File.WriteAllText(Path.Combine(cli, "libpdfium.so"), "pdfium");

            string[] LayoutArgs(string output) =>
                ["layout", "--rid", "linux-x64", "--app", app, "--cli", cli, "--out", output, "--repo", repo];

            var layout = Path.Combine(dir, "layout");
            Expect("layout: Erfolg", Cli.Run(LayoutArgs(layout)), 0);
            Expect("layout: App liegt in der Wurzel", File.Exists(Path.Combine(layout, "Pagewing")), true);
            Expect("layout: Unterordner bleiben", File.Exists(Path.Combine(layout, "sub", "x.txt")), true);
            Expect("layout: .pdb fehlt", File.Exists(Path.Combine(layout, "Pagewing.pdb")), false);
            Expect("layout: .dbg fehlt", File.Exists(Path.Combine(layout, "Pagewing.dbg")), false);
            Expect("layout: cli/ mit CLI und Bibliothek", File.Exists(Path.Combine(layout, "cli", "pagewing")) && File.Exists(Path.Combine(layout, "cli", "libpdfium.so")), true);
            Expect("layout: LICENSE", File.ReadAllText(Path.Combine(layout, "LICENSE")), "license");
            Expect("layout: THIRD-PARTY-NOTICES.txt", File.ReadAllText(Path.Combine(layout, "THIRD-PARTY-NOTICES.txt")), "notices");
            if (!OperatingSystem.IsWindows())
            {
                Expect("layout: Programm ausfuehrbar", (File.GetUnixFileMode(Path.Combine(layout, "Pagewing")) & UnixFileMode.UserExecute) != 0, true);
                Expect("layout: Bibliothek nicht ausfuehrbar", (File.GetUnixFileMode(Path.Combine(layout, "libpdfium.so")) & UnixFileMode.UserExecute) != 0, false);
            }

            Expect("layout: nicht leerer Ausgabeordner bricht ab", Cli.Run(LayoutArgs(layout)), 1);

            File.Delete(Path.Combine(cli, "libpdfium.so"));
            Expect("layout: ohne PDFium in der CLI bricht ab", Cli.Run(LayoutArgs(Path.Combine(dir, "layout2"))), 1);
            Expect("layout: bei Abbruch entsteht nichts", Directory.Exists(Path.Combine(dir, "layout2")), false);
            File.WriteAllText(Path.Combine(cli, "libpdfium.so"), "pdfium");
            File.Delete(Path.Combine(app, "libpdfium.so"));
            Expect("layout: ohne PDFium in der App bricht ab", Cli.Run(LayoutArgs(Path.Combine(dir, "layout3"))), 1);
            Expect("layout: win-x64 verlangt pdfium.dll", Cli.Run(["layout", "--rid", "win-x64", "--app", app, "--cli", cli, "--out", Path.Combine(dir, "layout4"), "--repo", repo]), 1);
            Expect("layout: unbekannte RID liefert 2", Cli.Run(["layout", "--rid", "mac", "--app", app, "--cli", cli, "--out", dir]), 2);
            Expect("layout: fehlende Option liefert 2", Cli.Run(["layout", "--rid", "linux-x64"]), 2);

            var zip1 = Path.Combine(dir, "a.zip");
            var zip2 = Path.Combine(dir, "b.zip");
            Expect("zip: Erfolg", Cli.Run(["zip", "--layout", layout, "--version", "1.2.3", "--out", zip1]), 0);
            Thread.Sleep(1100);
            File.SetLastWriteTimeUtc(Path.Combine(layout, "LICENSE"), DateTime.UtcNow);
            Expect("zip: zweiter Lauf", Cli.Run(["zip", "--layout", layout, "--version", "1.2.3", "--out", zip2]), 0);
            Expect("zip: reproduzierbar (gleiche SHA-256)", Sums.Line(zip1)[..64], Sums.Line(zip2)[..64]);
            using (var archive = ZipFile.OpenRead(zip1))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                Expect("zip: oberster Ordner Pagewing-<Version>/", names.All(n => n.StartsWith("Pagewing-1.2.3/", StringComparison.Ordinal)), true);
                Expect("zip: stabile Reihenfolge", names.SequenceEqual(names.Order(StringComparer.Ordinal)), true);
                Expect("zip: feste Zeitstempel", archive.Entries.All(e => e.LastWriteTime.DateTime == new DateTime(2000, 1, 1)), true);
                Expect("zip: cli/ enthalten", names.Contains("Pagewing-1.2.3/cli/pagewing"), true);
                Expect("zip: Programm 0755", archive.GetEntry("Pagewing-1.2.3/Pagewing")!.ExternalAttributes >> 16 & 0xFFFF, OperatingSystem.IsWindows() ? 0x81A4 : 0x81ED);
                Expect("zip: Datei 0644", archive.GetEntry("Pagewing-1.2.3/LICENSE")!.ExternalAttributes >> 16 & 0xFFFF, 0x81A4);
            }

            Expect("zip: fehlendes Layout liefert 1", Cli.Run(["zip", "--layout", Path.Combine(dir, "nope"), "--version", "1", "--out", zip1]), 1);
            Expect("zip: ohne Version liefert 2", Cli.Run(["zip", "--layout", layout, "--out", zip1]), 2);

            var sums = Path.Combine(dir, "SHA256SUMS-test.txt");
            Expect("sums: Erfolg", Cli.Run(["sums", zip1, zip2, "--out", sums]), 0);
            var lines = File.ReadAllText(sums).Split('\n');
            var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip1)));
            Expect("sums: Format <sha256>  <Dateiname>", lines[0], $"{expected}  a.zip");
            Expect("sums: eine Zeile je Datei, LF am Ende", lines.Length, 3);
            Expect("sums: fehlende Datei liefert 1", Cli.Run(["sums", Path.Combine(dir, "nope"), "--out", sums]), 1);
            Expect("sums: ohne Datei liefert 2", Cli.Run(["sums", "--out", sums]), 2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Builds tiny ELF files with clang in a temp directory and runs the real command on them.
    private static void RealElf()
    {
        if (!OperatingSystem.IsLinux() || Exec("clang", "--version") != 0 || Exec("objdump", "--version") != 0)
        {
            Console.WriteLine("selftest: clang oder objdump fehlt, echte ELF-Dateien uebersprungen.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "pagewing-package-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ok.c"), "#include <stdio.h>\nint main(void){puts(\"x\");return 0;}\n");
            File.WriteAllText(Path.Combine(dir, "foo.c"), "int foo(void){return 1;}\n");
            File.WriteAllText(Path.Combine(dir, "bar.c"), "int foo(void);\nint main(void){return foo();}\n");
            var ok = Path.Combine(dir, "ok");
            var bar = Path.Combine(dir, "bar");
            var built =
                Exec("clang", $"\"{Path.Combine(dir, "ok.c")}\" -o \"{ok}\"") == 0 &&
                Exec("clang", $"-shared -fPIC -Wl,-soname,libfoo.so.1 \"{Path.Combine(dir, "foo.c")}\" -o \"{Path.Combine(dir, "libfoo.so.1")}\"") == 0 &&
                Exec("clang", $"\"{Path.Combine(dir, "bar.c")}\" \"{Path.Combine(dir, "libfoo.so.1")}\" -o \"{bar}\"") == 0;
            Expect("clang erzeugt die Test-ELF-Dateien", built, true);
            if (!built)
            {
                return;
            }

            // A simple C program needs a GLIBC version far below any limit we use; a limit of 2.0 must fail it.
            Expect("echte ELF: ok bei 2.35 (alle Bibliotheken erlaubt)", Cli.Run(["check-binary", ok, "--max-glibc", "2.35"]), 0);
            Expect("echte ELF: Grenze 2.0 faellt durch", Cli.Run(["check-binary", ok, "--max-glibc", "2.0"]), 1);
            Expect("echte ELF: libfoo.so.1 als NEEDED faellt durch", Cli.Run(["check-binary", bar]), 1);
            Expect("echte ELF: eine schlechte Datei genuegt", Cli.Run(["check-binary", ok, bar]), 1);
            Expect("fehlende Datei faellt durch", Cli.Run(["check-binary", Path.Combine(dir, "nope")]), 1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static int Exec(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    private static void Expect<T>(string name, T actual, T expected)
    {
        s_checks++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            s_failures++;
            Console.Error.WriteLine($"selftest: {name}: erwartet {expected}, erhalten {actual}");
        }
    }
}
