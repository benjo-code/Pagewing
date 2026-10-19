#:property PublishAot=false

// Packaging tool of Pagewing (grows with Milestone 8; further subcommands are added to Commands).
//   dotnet run tools/package.cs -- check-binary <file>... [--max-glibc 2.35]
//   dotnet run tools/package.cs -- selftest
// Run from the repository root. The tool never uses the network.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

return Cli.Run(args);

static class Cli
{
    private const string Usage =
        "Aufruf: dotnet run tools/package.cs -- <Befehl>\n" +
        "  check-binary <Datei>... [--max-glibc 2.35]   prueft GLIBC-Versionen und NEEDED-Bibliotheken (ELF)\n" +
        "  selftest                                     prueft die Werkzeugteile";

    // Command name -> handler (receives the arguments after the command name). Add new subcommands here.
    private static readonly Dictionary<string, Func<string[], int>> Commands = new(StringComparer.Ordinal)
    {
        ["check-binary"] = CheckBinary.Run,
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

        RealElf();
        Expect("unbekannter Befehl liefert 2", Cli.Run(["gibt-es-nicht"]), 2);
        Expect("check-binary ohne Datei liefert 2", Cli.Run(["check-binary"]), 2);

        Console.WriteLine(s_failures == 0
            ? $"selftest: {s_checks} Pruefungen bestanden."
            : $"selftest: {s_failures} von {s_checks} Pruefungen fehlgeschlagen.");
        return s_failures == 0 ? 0 : 1;
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
