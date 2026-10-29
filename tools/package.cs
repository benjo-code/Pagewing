#:property PublishAot=false

// Packaging tool of Pagewing (grows with Milestone 8; further subcommands are added to Commands).
//   dotnet run tools/package.cs -- check-binary <file>... [--max-glibc 2.35] [--max-glibcxx 3.4.30]
//   dotnet run tools/package.cs -- layout --rid <win-x64|linux-x64> --app <dir> --cli <dir> --out <dir> [--repo <root>]
//   dotnet run tools/package.cs -- zip --layout <dir> --version <x.y.z> --out <file>
//   dotnet run tools/package.cs -- sums <file>... --out <SHA256SUMS.txt>
//   dotnet run tools/package.cs -- sums-verify --file <SHA256SUMS.txt> --sha256 <hex>
//   dotnet run tools/package.cs -- inno-install [--dir <dir>]                       (Windows)
//   dotnet run tools/package.cs -- installer --layout <dir> --version <x.y.z> --out <file> [--iscc <ISCC.exe>] [--repo <root>]   (Windows)
//   dotnet run tools/package.cs -- installer-smoke <setup.exe> [--repo <root>]          (Windows)
//   dotnet run tools/package.cs -- update-smoke <setup-old.exe> <setup-new.exe> --key <test-key.txt> [--key-id <id>]   (Windows, test build)
//   dotnet run tools/package.cs -- update-keygen --id <id> --private-out <file>   (the owner, once; never in a repository)
//   dotnet run tools/package.cs -- update-manifest --version <x.y.z> --sums <SHA256SUMS-windows.txt> <SHA256SUMS-linux.txt> --sizes <assets.json> --out <update-manifest.json>
//   dotnet run tools/package.cs -- update-sign --manifest <file> --key-env PAGEWING_UPDATE_SIGNING_KEY --id <id> --out <file.sig> [--source <UpdateTrust.cs>]
//   dotnet run tools/package.cs -- update-verify [--manifest <file> --sig <file>] (--keys-from-source | --source <file>)
//   dotnet run tools/package.cs -- deb --layout <dir> --version <x.y.z> --out <pagewing_x.y.z_amd64.deb> [--repo <root>]   (Linux)
//   dotnet run tools/package.cs -- deb-smoke <file.deb> [--repo <root>]                  (Linux)
//   dotnet run tools/package.cs -- runtime-fetch [--out <file>]                         (Linux)
//   dotnet run tools/package.cs -- appimage --layout <dir> --version <x.y.z> --out <Pagewing-x.y.z-x86_64.AppImage> [--runtime <file>] [--repo <root>]   (Linux)
//   dotnet run tools/package.cs -- appimage-smoke <file.AppImage> [--repo <root>]        (Linux)
//   dotnet run tools/package.cs -- selftest
// Run from the repository root. Only inno-install and runtime-fetch use the network (one download each, with a pinned SHA-256).

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
        "  check-binary <Datei>... [--max-glibc 2.35] [--max-glibcxx 3.4.30]   prueft GLIBC- und GLIBCXX-Versionen und NEEDED-Bibliotheken (ELF)\n" +
        "  layout --rid <rid> --app <Ordner> --cli <Ordner> --out <Ordner>   legt das Paketlayout an (App, cli/, LICENSE, THIRD-PARTY-NOTICES.txt)\n" +
        "  zip --layout <Ordner> --version <x.y.z> --out <Datei>             reproduzierbare ZIP-Datei, oberster Ordner Pagewing-<Version>/\n" +
        "  sums <Datei>... --out <Datei>                                     SHA-256-Summen (<sha256>  <Dateiname>)\n" +
        "  sums-verify --file <Datei> --sha256 <Hex>                         prueft eine SHA256SUMS-Datei gegen die Summe des Bau-Jobs (leer oder falsch: Fehler)\n" +
        "  inno-install [--dir <Ordner>]                                     laedt Inno Setup (feste Version, SHA-256 geprueft) ins Benutzerprofil (Windows)\n" +
        "  installer --layout <Ordner> --version <x.y.z> --out <Datei> [--iscc <ISCC.exe>]   baut Pagewing-<Version>-win-x64-setup.exe (Windows)\n" +
        "  installer-smoke <Setup.exe>                                       installiert still, prueft, installiert erneut, deinstalliert (Windows)\n" +
        "  update-smoke <Setup-alt.exe> <Setup-neu.exe> --key <Testschluessel.txt> [--key-id <Kennung>]   Update ueber den Helfermodus (Windows, Testaufbau)\n" +
        "  update-keygen --id <Kennung> --private-out <Datei>                erzeugt den Signaturschluessel: privat nur in die Datei, oeffentlich als Eintrag fuer UpdateTrust.Keys\n" +
        "  update-manifest --version <x.y.z> --sums <SHA256SUMS-windows.txt> <SHA256SUMS-linux.txt> --sizes <assets.json> --out <Datei>   Update-Manifest des Releases\n" +
        "  update-sign --manifest <Datei> --key-env <Variable> --id <Kennung> --out <Datei.sig> [--source <UpdateTrust.cs>]   signiert (Schluessel nur aus der Umgebung) und prueft\n" +
        "  update-verify [--manifest <Datei> --sig <Datei>] (--keys-from-source | --source <Datei>)   prueft Signatur und Inhalt wie die App (ohne Manifest nur die Schluessel)\n" +
        "  deb --layout <Ordner> --version <x.y.z> --out <Datei>             baut pagewing_<Version>_amd64.deb (Linux, dpkg-deb)\n" +
        "  deb-smoke <Datei.deb>                                             entpackt, prueft Starter, Symlink, ruft die CLI auf, lintian/desktop-file-validate wenn vorhanden (Linux)\n" +
        "  runtime-fetch [--out <Datei>]                                     laedt die type2-Runtime (feste Version, SHA-256 geprueft) nach ~/.cache/PagewingBuildTools\n" +
        "  runtime-sources --out <Ordner> [--from <Ordner>]                  laedt die Quellen der AppImage-Runtime (libfuse, type2-runtime; SHA-256 geprueft) fuer das Release\n" +
        "  appimage --layout <Ordner> --version <x.y.z> --out <Datei> [--runtime <Datei>]   baut Pagewing-<Version>-x86_64.AppImage (Linux, mksquashfs)\n" +
        "  appimage-smoke <Datei.AppImage>                                   prueft Runtime, entpackt, ruft die CLI auf, startet die App ohne FUSE (Linux)\n" +
        "  selftest                                     prueft die Werkzeugteile";

    // Command name -> handler (receives the arguments after the command name). Add new subcommands here.
    private static readonly Dictionary<string, Func<string[], int>> Commands = new(StringComparer.Ordinal)
    {
        ["check-binary"] = CheckBinary.Run,
        ["layout"] = Layout.Run,
        ["zip"] = ZipCommand.Run,
        ["sums"] = Sums.Run,
        ["sums-verify"] = SumsVerify.Run,
        ["inno-install"] = InnoInstall.Run,
        ["installer"] = Installer.Run,
        ["installer-smoke"] = InstallerSmoke.Run,
        ["update-smoke"] = UpdateSmoke.Run,
        ["update-keygen"] = UpdateKeygen.Run,
        ["update-manifest"] = UpdateManifestCommand.Run,
        ["update-sign"] = UpdateSign.Run,
        ["update-verify"] = UpdateVerify.Run,
        ["deb"] = Deb.Run,
        ["deb-smoke"] = DebSmoke.Run,
        ["runtime-fetch"] = RuntimeFetch.Run,
        ["runtime-sources"] = RuntimeSourcesCommand.Run,
        ["appimage"] = AppImage.Run,
        ["appimage-smoke"] = AppImageSmoke.Run,
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

    // libstdc++ of Ubuntu 22.04 and Debian 12 (GCC 12) exports GLIBCXX up to 3.4.30.
    public const string DefaultMaxGlibcxx = "3.4.30";

    public static readonly string[] AllowedNeeded =
    [
        "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "libz.so.1",
        "ld-linux-x86-64.so.2", "libstdc++.so.6", "libgcc_s.so.1",
        // libSkiaSharp.so (real linux-x64 publish, Milestone 8 task 11): the only NEEDED library beyond the glibc core.
        // Covered by the .deb Depends (packaging/linux/control.template: libfontconfig1) and, for the AppImage, by the
        // documented host-library assumption (packaging/README.md: libfontconfig1 and the X11 libraries come from the host).
        "libfontconfig.so.1",
    ];

    // objdump -T: "0000000000000000      DF *UND*	0000000000000000 (GLIBC_2.2.5) printf"
    [GeneratedRegex(@"\*UND\*.*\(GLIBC_(?<v>\d+(?:\.\d+)*)\)")]
    private static partial Regex UndefinedSymbolVersion();

    // objdump -T: "0000000000000000      DF *UND*	0000000000000000 (GLIBCXX_3.4.30) _ZSt..."
    [GeneratedRegex(@"\*UND\*.*\(GLIBCXX_(?<v>\d+(?:\.\d+)*)\)")]
    private static partial Regex UndefinedCxxSymbolVersion();

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

    public static string? HighestGlibc(string dynamicSymbols, out int[]? version) =>
        Highest(UndefinedSymbolVersion(), dynamicSymbols, out version);

    public static string? HighestGlibcxx(string dynamicSymbols, out int[]? version) =>
        Highest(UndefinedCxxSymbolVersion(), dynamicSymbols, out version);

    private static string? Highest(Regex symbolVersion, string dynamicSymbols, out int[]? version)
    {
        string? text = null;
        version = null;
        foreach (var line in dynamicSymbols.Split('\n'))
        {
            var m = symbolVersion.Match(line);
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
    public static List<string> Violations(string dynamicSymbols, string programHeaders, int[] max, string maxText,
        string maxGlibcxxText = DefaultMaxGlibcxx)
    {
        var problems = new List<string>();
        var highest = HighestGlibc(dynamicSymbols, out var version);
        if (highest is not null && Compare(version!, max) > 0)
        {
            problems.Add($"verlangt GLIBC_{highest} (erlaubt hoechstens GLIBC_{maxText})");
        }

        var highestCxx = HighestGlibcxx(dynamicSymbols, out var cxxVersion);
        if (highestCxx is not null && TryParseVersion(maxGlibcxxText, out var maxCxx) && Compare(cxxVersion!, maxCxx) > 0)
        {
            problems.Add($"verlangt GLIBCXX_{highestCxx} (erlaubt hoechstens GLIBCXX_{maxGlibcxxText})");
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
        var maxCxxText = ElfRules.DefaultMaxGlibcxx;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--max-glibc" && i + 1 < args.Length)
            {
                maxText = args[++i];
            }
            else if (args[i] == "--max-glibcxx" && i + 1 < args.Length)
            {
                maxCxxText = args[++i];
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

        if (files.Count == 0 || !ElfRules.TryParseVersion(maxText, out var max) || !ElfRules.TryParseVersion(maxCxxText, out _))
        {
            Console.Error.WriteLine("Aufruf: check-binary <Datei>... [--max-glibc 2.35] [--max-glibcxx 3.4.30]");
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

            var problems = ElfRules.Violations(symbols, headers, max, maxText, maxCxxText);
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

// sums-verify --file <path> --sha256 <hex>: the release workflow compares each downloaded SHA256SUMS file with the hash
// that the build job reported as a job output, before anything is signed. Fails closed: a missing, empty or malformed
// expectation is a failure, never a pass.
static class SumsVerify
{
    // null when the file has the expected SHA-256 (hex, either case); otherwise why not.
    public static string? Problem(string file, string expected)
    {
        var hex = expected.Trim();
        if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit))
        {
            return hex.Length == 0
                ? "die erwartete SHA-256 ist leer (der Bau-Job hat keine geliefert)"
                : "die erwartete SHA-256 ist keine 64-stellige Hexzahl";
        }

        if (!File.Exists(file))
        {
            return $"Datei nicht gefunden: {file}";
        }

        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actual, hex, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SHA-256 von {file} ist {actual}, der Bau-Job meldete {hex.ToLowerInvariant()}";
    }

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--file", "--sha256");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--file", out var file) || !p.Named.TryGetValue("--sha256", out var sha256))
        {
            Console.Error.WriteLine("Aufruf: sums-verify --file <Datei> --sha256 <Hex>");
            return 2;
        }

        if (Problem(file, sha256) is { } problem)
        {
            Console.Error.WriteLine($"sums-verify: {problem}");
            return 1;
        }

        Console.WriteLine($"sums-verify: {file} stimmt mit der Summe des Bau-Jobs ueberein");
        return 0;
    }
}

// Downloads of build inputs with a size and SHA-256 fixed in this file (Inno Setup, the AppImage runtime).
static class Pinned
{
    // null when the value is a SHA-256 in lower-case hex; otherwise why it is not pinned (name = the constant in this file).
    public static string? PinProblem(string pinned, string name) =>
        pinned.Length == 64 && pinned.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? null
            : $"Pruefsumme nicht festgehalten (hash not pinned): {name} in tools/package.cs muss eine SHA-256 in Kleinbuchstaben sein";

    // null when the stream has the expected size and the pinned SHA-256; otherwise the reason.
    public static string? VerifyDownload(Stream stream, long expectedSize, string pinned, string name)
    {
        if (PinProblem(pinned, name) is { } problem)
        {
            return problem;
        }

        if (stream.Length != expectedSize)
        {
            return $"Groesse {stream.Length} statt {expectedSize} Bytes";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return hash == pinned ? null : $"SHA-256 {hash} statt {pinned}";
    }

    // The same check for a file; a missing file is a reason too.
    public static string? VerifyFile(string file, long expectedSize, string pinned, string name)
    {
        if (!File.Exists(file))
        {
            return $"Datei fehlt: {file}";
        }

        using var stream = File.OpenRead(file);
        return VerifyDownload(stream, expectedSize, pinned, name);
    }

    // Downloads url to target (over HTTPS, through the configured proxy); the caller verifies before using the file.
    public static void Fetch(string url, string target)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var source = response.Content.ReadAsStream();
        using var file = File.Create(target);
        source.CopyTo(file);
    }
}

// The pinned Inno Setup 6 (build tool only, see Docs/DECISIONS.md "Windows-Installer mit Inno Setup 6.7.3 (M8)").
static class InnoSetup
{
    public const string Version = "6.7.3";
    public const string Url = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe";

    // From the release's own signature file innosetup-6.7.3.exe.issig (format issig-v2, ECDSA P-256 signature checked
    // against the project key def02.ispublickey of tag is-6_7_3 in jrsoftware/issrc).
    public const string Sha256 = "9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732";
    public const long Size = 10592232;

    public static string DefaultDir(string localAppData) => Path.Combine(localAppData, "PagewingBuildTools", $"InnoSetup-{Version}");

    public static string? PinProblem(string pinned) => Pinned.PinProblem(pinned, "InnoSetup.Sha256");

    public static string? VerifyDownload(Stream stream, long expectedSize, string pinned) =>
        Pinned.VerifyDownload(stream, expectedSize, pinned, "InnoSetup.Sha256");

    // Silent, current user only, portable mode of Inno Setup's own installer (no uninstall entry, no .iss association).
    public static string InstallArguments(string dir) =>
        InnoArguments.Join(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/PORTABLE=1", "/NOICONS", "/DIR=" + dir]);
}

// Command lines for programs built with Inno Setup (iscc.exe, Setup, the uninstaller). They do not split like the
// C runtime: outside quotes a character <= ' ' separates, a '"' toggles quoting and is dropped, a backslash is literal
// (GetParamStr in Projects/Src/Shared.CommonFunc.pas of jrsoftware/issrc). So an argument can never contain '"'.
static class InnoArguments
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Argument mit Anfuehrungszeichen ist fuer Inno Setup nicht darstellbar: {argument}", nameof(argument));
        }

        return argument.Length > 0 && argument.All(c => c > ' ') ? argument : $"\"{argument}\"";
    }

    // The same splitting as Inno Setup, for the self-test.
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var i = 0;
        while (i < commandLine.Length && commandLine[i] <= ' ')
        {
            i++;
        }

        while (i < commandLine.Length)
        {
            var current = new StringBuilder();
            var inQuote = false;
            while (i < commandLine.Length && (commandLine[i] > ' ' || inQuote))
            {
                if (commandLine[i] == '"')
                {
                    inQuote = !inQuote;
                }
                else
                {
                    current.Append(commandLine[i]);
                }

                i++;
            }

            result.Add(current.ToString());
            while (i < commandLine.Length && commandLine[i] <= ' ')
            {
                i++;
            }
        }

        return result;
    }
}

static class InnoInstall
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--dir");
        if (parsed is not { } p || p.Positional.Count != 0)
        {
            Console.Error.WriteLine("Aufruf: inno-install [--dir <Ordner>]");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("inno-install: Inno Setup laeuft nur unter Windows");
            return 1;
        }

        if (InnoSetup.PinProblem(InnoSetup.Sha256) is { } pinProblem)
        {
            Console.Error.WriteLine($"inno-install: {pinProblem}");
            return 1;
        }

        var dir = Path.GetFullPath(p.Named.GetValueOrDefault("--dir") ??
            InnoSetup.DefaultDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        var iscc = Path.Combine(dir, "ISCC.exe");
        if (File.Exists(iscc))
        {
            Console.WriteLine($"inno-install: schon vorhanden: {iscc}");
            return 0;
        }

        var download = Path.Combine(Path.GetTempPath(), $"innosetup-{InnoSetup.Version}-{Guid.NewGuid():N}.exe");
        try
        {
            Console.WriteLine($"inno-install: lade {InnoSetup.Url}");
            Pinned.Fetch(InnoSetup.Url, download);
            if (Pinned.VerifyFile(download, InnoSetup.Size, InnoSetup.Sha256, "InnoSetup.Sha256") is { } problem)
            {
                Console.Error.WriteLine($"inno-install: Download abgelehnt, nichts ausgefuehrt: {problem}");
                return 1;
            }

            Console.WriteLine($"inno-install: SHA-256 stimmt, installiere nach {dir}");
            var exitCode = ProcessRunner.RunInno(download, InnoSetup.InstallArguments(dir), TimeSpan.FromMinutes(10));
            if (exitCode != 0 || !File.Exists(iscc))
            {
                Console.Error.WriteLine($"inno-install: Installation fehlgeschlagen (Exit {exitCode}), {iscc} fehlt");
                return 1;
            }

            Console.WriteLine($"inno-install: Inno Setup {InnoSetup.Version} in {dir}");
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"inno-install: {e.Message}");
            return 1;
        }
        finally
        {
            File.Delete(download);
        }
    }
}

static class ProcessRunner
{
    // Starts a program built with Inno Setup with an Inno command line (no shell, so '&' is an ordinary character).
    // Returns the exit code, or -1 after the timeout (the process is killed).
    public static int RunInno(string file, string arguments, TimeSpan timeout, StringBuilder? output = null)
    {
        var psi = new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = output is not null, RedirectStandardError = output is not null };
        using var process = Process.Start(psi) ?? throw new IOException($"{file} konnte nicht gestartet werden");
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        if (output is not null)
        {
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
        }

        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return -1;
        }

        if (output is not null)
        {
            output.Append(stdout!.Result).Append(stderr!.Result);
        }

        return process.ExitCode;
    }
}

static partial class Installer
{
    // Generated once (2026-10-19), never change: Setup finds earlier installations and the uninstaller by it.
    // packaging/windows/pagewing.iss has it as AppId={{<guid>} (IssRules checks that both agree).
    public const string AppId = "472398B4-29BA-4DDB-8475-7928DA355D74";

    // Inno Setup appends "_is1" to the AppId for the uninstall key (HKCU or HKLM, by install mode).
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppId + "}_is1";

    public static readonly string[] RequiredLayoutFiles =
        ["Pagewing.exe", "pdfium.dll", "cli/pagewing.exe", "cli/pdfium.dll", "LICENSE", "THIRD-PARTY-NOTICES.txt"];

    [GeneratedRegex(@"^(?<a>0|[1-9]\d*)\.(?<b>0|[1-9]\d*)\.(?<c>0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();

    public static string SetupFileName(string version) => $"Pagewing-{version}-win-x64-setup.exe";

    // x.y.z[-pre] -> "x.y.z.0" for the Windows file version (each part at most 65535); null if it is no such version.
    public static string? NumericVersion(string version)
    {
        var m = SemVer().Match(version);
        if (!m.Success)
        {
            return null;
        }

        var parts = new[] { m.Groups["a"].Value, m.Groups["b"].Value, m.Groups["c"].Value };
        return parts.All(x => x.Length <= 5 && int.Parse(x, CultureInfo.InvariantCulture) <= 65535) ? string.Join('.', parts) + ".0" : null;
    }

    public static List<string> MissingLayoutFiles(string layout) =>
        RequiredLayoutFiles.Where(f => !File.Exists(Path.Combine(layout, f))).ToList();

    public static List<string> IsccArguments(string script, string layout, string repo, string version, string outputDir) =>
    [
        "/DAppVersion=" + version,
        "/DNumericVersion=" + NumericVersion(version),
        "/DLayoutDir=" + Path.TrimEndingDirectorySeparator(layout),
        "/DIconFile=" + Path.Combine(repo, "src", "Pagewing.App", "Assets", "pagewing.ico"),
        "/O" + Path.TrimEndingDirectorySeparator(outputDir),
        "/F" + Path.GetFileNameWithoutExtension(SetupFileName(version)),
        script,
    ];

    // iscc prints warnings on stdout as "Warning: ..." (SCompilerStatusWarning); here a warning is an error.
    public static List<string> Warnings(string isccOutput) =>
        isccOutput.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Warning:", StringComparison.Ordinal)).ToList();

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--layout", "--version", "--out", "--iscc", "--repo");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--layout", out var layout) || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: installer --layout <Ordner> --version <x.y.z> --out <Pagewing-<Version>-win-x64-setup.exe> [--iscc <ISCC.exe>] [--repo <Wurzel>]");
            return 2;
        }

        if (NumericVersion(version) is null)
        {
            Console.Error.WriteLine($"installer: ungueltige Version (erwartet x.y.z, optional -vorab): {version}");
            return 2;
        }

        if (Path.GetFileName(output) != SetupFileName(version))
        {
            Console.Error.WriteLine($"installer: die Ausgabedatei muss {SetupFileName(version)} heissen: {output}");
            return 2;
        }

        var repo = Path.GetFullPath(p.Named.GetValueOrDefault("--repo", "."));
        var script = Path.Combine(repo, "packaging", "windows", "pagewing.iss");
        var problems = MissingLayoutFiles(layout).Select(f => $"Layout enthaelt {f} nicht: {layout}").ToList();
        foreach (var file in new[] { script, Path.Combine(repo, "src", "Pagewing.App", "Assets", "pagewing.ico") })
        {
            if (!File.Exists(file))
            {
                problems.Add($"Datei fehlt: {file}");
            }
        }

        if (problems.Count > 0)
        {
            problems.ForEach(x => Console.Error.WriteLine($"installer: {x}"));
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("installer: iscc.exe laeuft nur unter Windows");
            return 1;
        }

        var iscc = p.Named.GetValueOrDefault("--iscc") ??
            Path.Combine(InnoSetup.DefaultDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "ISCC.exe");
        if (!File.Exists(iscc))
        {
            Console.Error.WriteLine($"installer: {iscc} fehlt (dotnet run tools/package.cs -- inno-install)");
            return 1;
        }

        var fullOutput = Path.GetFullPath(output);
        var outputDir = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(outputDir);
        File.Delete(fullOutput);
        var arguments = InnoArguments.Join(IsccArguments(script, Path.GetFullPath(layout), repo, version, outputDir));
        Console.WriteLine($"installer: {iscc} {arguments}");
        var log = new StringBuilder();
        var exitCode = ProcessRunner.RunInno(iscc, arguments, TimeSpan.FromMinutes(20), log);
        Console.WriteLine(log.ToString().TrimEnd());
        var warnings = Warnings(log.ToString());
        if (exitCode != 0 || warnings.Count > 0 || !File.Exists(fullOutput))
        {
            Console.Error.WriteLine($"installer: fehlgeschlagen (iscc Exit {exitCode}, {warnings.Count} Warnung(en), Datei {(File.Exists(fullOutput) ? "da" : "fehlt")})");
            warnings.ForEach(w => Console.Error.WriteLine($"installer: {w}"));
            return 1;
        }

        Console.WriteLine($"installer: {fullOutput}");
        return 0;
    }
}

// Checks the rules of packaging/windows/pagewing.iss that the plan fixes (Milestone 8, task 8), without iscc.
static class IssRules
{
    private sealed record Script(Dictionary<string, string> Setup, Dictionary<string, List<Dictionary<string, string>>> Entries, string Code);

    private static Script Parse(string text)
    {
        var setup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var code = new StringBuilder();
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (section.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                if (!line.StartsWith("//", StringComparison.Ordinal))
                {
                    code.Append(line).Append('\n');
                }

                continue;
            }

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (section.Equals("Setup", StringComparison.OrdinalIgnoreCase))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    setup[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }

                continue;
            }

            if (!entries.TryGetValue(section, out var list))
            {
                entries[section] = list = [];
            }

            list.Add(Parameters(line));
        }

        return new Script(setup, entries, code.ToString());
    }

    // "Name: value; Name: "quoted; value"" -> parameters; inside quotes "" is a literal quote.
    private static Dictionary<string, string> Parameters(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < line.Length)
        {
            var colon = line.IndexOf(':', i);
            if (colon < 0)
            {
                break;
            }

            var name = line[i..colon].Trim();
            i = colon + 1;
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            var value = new StringBuilder();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (i < line.Length && !(line[i] == '"' && (i + 1 >= line.Length || line[i + 1] != '"')))
                {
                    value.Append(line[i]);
                    i += line[i] == '"' ? 2 : 1;
                }

                i++;
                while (i < line.Length && line[i] != ';')
                {
                    i++;
                }
            }
            else
            {
                while (i < line.Length && line[i] != ';')
                {
                    value.Append(line[i++]);
                }
            }

            result[name] = value.ToString().Trim();
            i++;
        }

        return result;
    }

    private static HashSet<string> Words(string? value) =>
        new((value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public static List<string> Problems(string text)
    {
        var problems = new List<string>();
        var s = Parse(text);

        void Require(string directive, string expected)
        {
            var actual = s.Setup.GetValueOrDefault(directive);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                problems.Add($"[Setup] {directive}={actual ?? "<fehlt>"}, erwartet {expected}");
            }
        }

        Require("AppId", "{{" + Installer.AppId + "}");
        Require("AppName", "Pagewing");
        Require("PrivilegesRequired", "lowest");
        Require("DefaultDirName", @"{autopf}\Pagewing");
        Require("CloseApplications", "yes");
        Require("ChangesAssociations", "yes");
        Require("ArchitecturesAllowed", "x64compatible");
        Require("ArchitecturesInstallIn64BitMode", "x64compatible");
        Require("LicenseFile", @"{#LayoutDir}\LICENSE");
        if (!Words(s.Setup.GetValueOrDefault("PrivilegesRequiredOverridesAllowed")).SetEquals(["commandline", "dialog"]))
        {
            problems.Add("[Setup] PrivilegesRequiredOverridesAllowed muss genau 'commandline dialog' sein");
        }

        // A silent update (the helper of the app passes neither /ALLUSERS, /CURRENTUSER nor /DIR) takes the install mode and
        // the folder of the installation it finds by the AppId; both directives default to yes and must stay so.
        foreach (var directive in new[] { "UsePreviousPrivileges", "UsePreviousAppDir" })
        {
            if (s.Setup.TryGetValue(directive, out var value) && !string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"[Setup] {directive}={value}: ein Update muss Modus und Ordner der vorhandenen Installation uebernehmen (yes oder weglassen)");
            }
        }

        if (string.Equals(s.Setup.GetValueOrDefault("DisableDirPage"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("[Setup] DisableDirPage=yes: der Zielordner muss waehlbar sein");
        }

        List<Dictionary<string, string>> Section(string name) => s.Entries.GetValueOrDefault(name) ?? [];

        var languages = Section("Languages").Select(e => e.GetValueOrDefault("MessagesFile")).ToList();
        if (!languages.SequenceEqual(["compiler:Default.isl", @"compiler:Languages\German.isl"]))
        {
            problems.Add(@"[Languages] erwartet genau Englisch (compiler:Default.isl) und Deutsch (compiler:Languages\German.isl)");
        }

        if (!Section("Files").Any(e => e.GetValueOrDefault("Source") == @"{#LayoutDir}\*" && e.GetValueOrDefault("DestDir") == "{app}" &&
            Words(e.GetValueOrDefault("Flags")).IsSupersetOf(["recursesubdirs", "createallsubdirs"])))
        {
            problems.Add(@"[Files] das ganze Layout ({#LayoutDir}\*, recursesubdirs createallsubdirs) nach {app}");
        }

        const string exe = @"{app}\Pagewing.exe";
        var run = Section("Run");
        var register = run.FindIndex(e => e.GetValueOrDefault("Filename") == exe &&
            e.GetValueOrDefault("Parameters") == "--register-file-types={code:FileTypeScope}");
        var launch = run.FindIndex(e => e.GetValueOrDefault("Filename") == exe && !e.ContainsKey("Parameters"));
        if (register < 0)
        {
            problems.Add("[Run] Eintrag --register-file-types={code:FileTypeScope} fehlt");
        }
        else
        {
            var flags = Words(run[register].GetValueOrDefault("Flags"));
            if (!flags.Contains("runhidden") || flags.Overlaps(["postinstall", "nowait", "skipifsilent"]))
            {
                problems.Add("[Run] die Registrierung laeuft versteckt (runhidden), immer und bis zum Ende (kein postinstall/nowait/skipifsilent)");
            }
        }

        if (launch < 0 || !Words(run[launch].GetValueOrDefault("Flags")).IsSupersetOf(["postinstall", "skipifsilent", "nowait"]))
        {
            problems.Add("[Run] 'Pagewing starten' braucht postinstall nowait skipifsilent (nur im grafischen Modus)");
        }
        else if (register > launch)
        {
            problems.Add("[Run] die Registrierung muss vor dem Start stehen");
        }

        var unregister = Section("UninstallRun").Find(e => e.GetValueOrDefault("Filename") == exe &&
            e.GetValueOrDefault("Parameters") == "--unregister-file-types={code:FileTypeScope}");
        if (unregister is null)
        {
            problems.Add("[UninstallRun] Eintrag --unregister-file-types={code:FileTypeScope} fehlt");
        }
        else
        {
            var flags = Words(unregister.GetValueOrDefault("Flags"));
            if (!flags.Contains("runhidden") || flags.Contains("nowait") || string.IsNullOrEmpty(unregister.GetValueOrDefault("RunOnceId")))
            {
                problems.Add("[UninstallRun] die Abmeldung laeuft versteckt, bis zum Ende (kein nowait) und mit RunOnceId");
            }
        }

        if (!s.Code.Contains("function FileTypeScope(Param: String): String;", StringComparison.Ordinal) ||
            !s.Code.Contains("IsAdminInstallMode", StringComparison.Ordinal) ||
            !s.Code.Contains("'machine'", StringComparison.Ordinal) || !s.Code.Contains("'user'", StringComparison.Ordinal))
        {
            problems.Add("[Code] FileTypeScope liefert 'machine' bei IsAdminInstallMode, sonst 'user'");
        }

        var content = string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith(';') && !l.StartsWith("//", StringComparison.Ordinal)));
        if (content.Contains("appdata}", StringComparison.OrdinalIgnoreCase) || content.Contains("%APPDATA", StringComparison.OrdinalIgnoreCase) ||
            s.Entries.ContainsKey("UninstallDelete"))
        {
            problems.Add("das Skript fasst Benutzerdaten an (appdata, [UninstallDelete]); %APPDATA%\\Pagewing muss erhalten bleiben");
        }

        return problems;
    }
}

static class InstallerSmoke
{
    private const string ProgIdKey = @"Software\Classes\Pagewing.PDF";
    private const string OpenWithKey = @"Software\Classes\.pdf\OpenWithProgids";

    public static string TestDir(string temp) => Path.Combine(temp, "Pagewing Test ä&");

    public static string SilentInstallArguments(string dir, string log) =>
        InnoArguments.Join(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + dir, "/LOG=" + log]);

    public static string SilentUninstallArguments(string log) =>
        InnoArguments.Join(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + log]);

    // One line per file: "<relative path> <size> <last write ticks>"; a missing folder is one line "<fehlt>".
    public static List<string> Snapshot(string dir) =>
        !Directory.Exists(dir)
            ? ["<fehlt>"]
            : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => $"{Path.GetRelativePath(dir, f)} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f).Ticks}")
                .Order(StringComparer.Ordinal).ToList();

    // One description per path that is new, gone or changed (path = the line without its last two fields).
    public static List<string> Differences(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        static Dictionary<string, string> ByPath(IEnumerable<string> lines) => lines.ToDictionary(
            l => l.Count(c => c == ' ') >= 2 ? l[..l.LastIndexOf(' ', l.LastIndexOf(' ') - 1)] : l, l => l, StringComparer.Ordinal);
        var old = ByPath(before);
        var now = ByPath(after);
        return old.Keys.Union(now.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(k => old.GetValueOrDefault(k) != now.GetValueOrDefault(k))
            .Select(k => $"{k}: {(old.TryGetValue(k, out var o) ? o : "<fehlte>")} -> {(now.TryGetValue(k, out var n) ? n : "<fehlt>")}")
            .ToList();
    }

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--repo");
        if (parsed is not { } p || p.Positional.Count != 1)
        {
            Console.Error.WriteLine("Aufruf: installer-smoke <Pagewing-<Version>-win-x64-setup.exe> [--repo <Wurzel>]");
            return 2;
        }

        var setup = Path.GetFullPath(p.Positional[0]);
        if (!File.Exists(setup))
        {
            Console.Error.WriteLine($"installer-smoke: Setup fehlt: {setup}");
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("installer-smoke: laeuft nur unter Windows");
            return 1;
        }

        var pdf = Path.GetFullPath(Path.Combine(p.Named.GetValueOrDefault("--repo", "."), "testdata", "pdfs", "text-basic.pdf"));
        if (!File.Exists(pdf))
        {
            Console.Error.WriteLine($"installer-smoke: Test-PDF fehlt: {pdf}");
            return 1;
        }

        var failures = RunOnWindows(setup, pdf);
        if (failures.Count == 0)
        {
            Console.WriteLine("installer-smoke: alle Schritte bestanden");
            return 0;
        }

        failures.ForEach(f => Console.Error.WriteLine($"installer-smoke: {f}"));
        return 1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool KeyExists(Microsoft.Win32.RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> RunOnWindows(string setup, string pdf)
    {
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        var hklm = Microsoft.Win32.Registry.LocalMachine;
        var failures = new List<string>();

        // (0) Never run over a real installation: the test uninstalls at the end and would remove it.
        foreach (var (root, path) in new[] { (hkcu, ProgIdKey), (hkcu, Installer.UninstallKey), (hklm, Installer.UninstallKey) })
        {
            if (KeyExists(root, path))
            {
                failures.Add($@"Pagewing ist schon installiert oder registriert ({root.Name}\{path}); erst deinstallieren, der Test wuerde es entfernen");
            }
        }

        if (failures.Count > 0)
        {
            return failures;
        }

        var temp = Path.GetTempPath();
        var dir = TestDir(temp);
        var logs = Path.Combine(temp, "pagewing-installer-smoke");
        Directory.CreateDirectory(logs);
        if (Directory.Exists(dir))
        {
            Console.WriteLine($"installer-smoke: entferne Reste eines frueheren Laufs: {dir}");
            Directory.Delete(dir, true);
        }

        // (d) %APPDATA%\Pagewing must stay as it is; without the folder a marker file stands in for the settings.
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pagewing");
        var marker = Path.Combine(appData, "installer-smoke-marker.txt");
        var createdAppData = !Directory.Exists(appData);
        if (createdAppData)
        {
            Directory.CreateDirectory(appData);
            File.WriteAllText(marker, "Pagewing installer-smoke: must survive the uninstallation\n");
        }

        var before = Snapshot(appData);
        var uninstaller = Path.Combine(dir, "unins000.exe");
        try
        {
            // (a) First silent installation, exactly the documented command line.
            if (!Install(setup, dir, Path.Combine(logs, "install-1.log"), "erste Installation", failures))
            {
                return failures;
            }

            // (b) Files, CLI and registration.
            CheckInstalled(dir, pdf, failures);
            if (failures.Count > 0)
            {
                return failures;
            }

            // (c) Second installation over the first.
            if (!Install(setup, dir, Path.Combine(logs, "install-2.log"), "zweite Installation ueber die erste", failures))
            {
                return failures;
            }

            CheckInstalled(dir, pdf, failures);
            if (failures.Count > 0)
            {
                return failures;
            }

            // (d) Silent uninstallation: folder and keys gone. The uninstaller hands over to a copy of itself in %TEMP%,
            // whose last steps may still run after the exit code arrives, so wait for the result.
            var uninstallLog = Path.Combine(logs, "uninstall.log");
            var code = ProcessRunner.RunInno(uninstaller, SilentUninstallArguments(uninstallLog), TimeSpan.FromMinutes(5));
            if (code != 0)
            {
                failures.Add($"Deinstallation: Exit {code} statt 0 (Protokoll: {uninstallLog})");
                return failures;
            }

            var remaining = new List<string>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            do
            {
                remaining.Clear();
                if (Directory.Exists(dir))
                {
                    remaining.Add($"Ordner {dir}");
                }

                foreach (var path in new[] { ProgIdKey, Installer.UninstallKey })
                {
                    if (KeyExists(hkcu, path))
                    {
                        remaining.Add($@"HKCU\{path}");
                    }
                }

                using (var openWith = hkcu.OpenSubKey(OpenWithKey))
                {
                    if (openWith?.GetValue("Pagewing.PDF") is not null)
                    {
                        remaining.Add($@"HKCU\{OpenWithKey} Wert Pagewing.PDF");
                    }
                }

                if (remaining.Count > 0)
                {
                    Thread.Sleep(500);
                }
            }
            while (remaining.Count > 0 && DateTime.UtcNow < deadline);
            failures.AddRange(remaining.Select(r => $"nach der Deinstallation noch vorhanden: {r} (Protokoll: {uninstallLog})"));

            failures.AddRange(Differences(before, Snapshot(appData)).Select(d => $@"%APPDATA%\Pagewing veraendert: {d}"));
            return failures;
        }
        finally
        {
            if (failures.Count > 0 && File.Exists(uninstaller))
            {
                Console.Error.WriteLine("installer-smoke: Fehler, deinstalliere zum Aufraeumen");
                ProcessRunner.RunInno(uninstaller, SilentUninstallArguments(Path.Combine(logs, "cleanup-uninstall.log")), TimeSpan.FromMinutes(5));
            }

            if (createdAppData && File.Exists(marker))
            {
                File.Delete(marker);
                if (!Directory.EnumerateFileSystemEntries(appData).Any())
                {
                    Directory.Delete(appData);
                }
            }
        }
    }

    private static bool Install(string setup, string dir, string log, string step, List<string> failures)
    {
        var code = ProcessRunner.RunInno(setup, SilentInstallArguments(dir, log), TimeSpan.FromMinutes(10));
        if (code != 0)
        {
            failures.Add($"{step}: Exit {code} statt 0 (Protokoll: {log})");
        }

        return code == 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckInstalled(string dir, string pdf, List<string> failures)
    {
        foreach (var file in new[] { "Pagewing.exe", @"cli\pagewing.exe", "unins000.exe" })
        {
            if (!File.Exists(Path.Combine(dir, file)))
            {
                failures.Add($@"Datei fehlt: {dir}\{file}");
            }
        }

        var cli = Path.Combine(dir, "cli", "pagewing.exe");
        if (File.Exists(cli))
        {
            var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add(pdf);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                failures.Add("cli\\pagewing.exe info: Zeitlimit 60 s");
            }
            else if (process.ExitCode != 0 || !stdout.Contains("Pages: 2", StringComparison.Ordinal))
            {
                failures.Add($"cli\\pagewing.exe info {pdf}: Exit {process.ExitCode}, erwartet 0 und 'Pages: 2'; Ausgabe '{stdout.Trim()}' / '{stderr.Result.Trim()}'");
            }
        }

        // The command must point at the installed Pagewing.exe ("<exe>" "%1"), path with space, umlaut and '&'.
        using var command = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ProgIdKey + @"\shell\open\command");
        var value = command?.GetValue(null) as string;
        if (value is null)
        {
            failures.Add($@"HKCU\{ProgIdKey} fehlt (die Registrierung im [Run]-Abschnitt lief nicht oder scheiterte)");
        }
        else
        {
            var end = value.IndexOf("\" \"%1\"", StringComparison.Ordinal);
            var registered = value.StartsWith('"') && end > 0 ? value[1..end] : null;
            if (registered is null || !File.Exists(registered) || Path.GetFileName(registered) != "Pagewing.exe" ||
                Path.GetFileName(Path.GetDirectoryName(registered)) != Path.GetFileName(dir))
            {
                failures.Add($@"HKCU\{ProgIdKey}\shell\open\command zeigt nicht auf {dir}\Pagewing.exe: {value}");
            }
        }

        if (!KeyExists(Microsoft.Win32.Registry.CurrentUser, Installer.UninstallKey))
        {
            failures.Add($@"HKCU\{Installer.UninstallKey} fehlt (keine Benutzerinstallation?)");
        }
    }
}

// End-to-end check of the Windows updater (Milestone 8.1, task 10): installs the old version silently into a test folder
// (like installer-smoke, with the same refusal over a real installation), puts the new setup program with a manifest
// signed by the test key and request.json into a staging folder below a fake %APPDATA%, starts a copy of the program in
// the helper mode (--finish-update) with a fake %APPDATA% and %LOCALAPPDATA% and then checks: Pagewing.exe reports the new
// version, the staging folder is gone, settings.json and the real %APPDATA%\Pagewing are unchanged. The helper verifies
// the signature with the keys compiled into the program: both setup programs must come from the test build
// (-p:PagewingUpdateTestBuild=true -p:PagewingUpdateTestKey=<public half of --key>, packaging/README.md, "Update pruefen"),
// whose one key has the id test-1; otherwise the helper refuses. --key is the private file of update-keygen (PKCS#8 as
// Base64; PEM is read too). Shows windows (needs a desktop session); like installer-smoke it writes the registration
// below HKCU (file types, uninstall entry) and removes it again.
static partial class UpdateSmoke
{
    // The id of the one key of the test build (src/Pagewing.Core/Updates/UpdateTrust.TestBuild.cs).
    public const string TestKeyId = "test-1";

    // A private P-256 key as update-keygen writes it (PKCS#8 as Base64) or as PEM; null if it is neither.
    public static ECDsa? ReadKey(string text)
    {
        var key = ECDsa.Create();
        try
        {
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                key.ImportFromPem(text);
            }
            else
            {
                var bytes = Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
                key.ImportPkcs8PrivateKey(bytes, out var read);
                CryptographicOperations.ZeroMemory(bytes);
                if (read != bytes.Length)
                {
                    throw new FormatException();
                }
            }

            if (key.KeySize == 256)
            {
                return key;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException)
        {
            // Neither form.
        }

        key.Dispose();
        return null;
    }

    [GeneratedRegex(@"^Pagewing-(?<v>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))-win-x64-setup\.exe$")]
    private static partial Regex SetupName();

    public static string TestDir(string temp) => Path.Combine(temp, "Pagewing Update Test ä&");

    // The version of a setup program by its release name, or null.
    public static string? VersionOf(string setup) => SetupName().Match(Path.GetFileName(setup)) is { Success: true } m ? m.Groups["v"].Value : null;

    // The manifest of the release as the release job writes it (one Windows setup program).
    public static byte[] Manifest(string version, string name, long size, string sha256) =>
        Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"" + version + "\",\"files\":[{\"kind\":\"win-x64-setup\",\"name\":\"" + name +
            "\",\"size\":" + size.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":\"" + sha256 + "\"}]}");

    // "<keyId>:<base64 of the IEEE P1363 ECDSA P-256 signature over the raw bytes>", as the app verifies it.
    public static string Signature(byte[] manifest, ECDsa key, string keyId) =>
        keyId + ":" + Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    // request.json as the app writes it (UpdateRequest: camelCase, the form by its name).
    public static string Request(string version, string fileName, string exePath, string targetDirectory) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = 1,
            ["version"] = version,
            ["fileName"] = fileName,
            ["form"] = "WindowsInstaller",
            ["exePath"] = exePath,
            ["targetDirectory"] = targetDirectory,
        }.ToJsonString();

    public static List<string> HelperArguments(string staging) => ["--finish-update", staging];

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--key", "--key-id");
        if (parsed is not { } p || p.Positional.Count != 2 || !p.Named.TryGetValue("--key", out var keyFile))
        {
            Console.Error.WriteLine("Aufruf: update-smoke <Pagewing-<alt>-win-x64-setup.exe> <Pagewing-<neu>-win-x64-setup.exe> --key <Testschluessel.pem> [--key-id <Kennung>]");
            return 2;
        }

        var oldSetup = Path.GetFullPath(p.Positional[0]);
        var newSetup = Path.GetFullPath(p.Positional[1]);
        if (VersionOf(oldSetup) is not { } oldVersion || VersionOf(newSetup) is not { } newVersion)
        {
            Console.Error.WriteLine("update-smoke: die Setups muessen Pagewing-<x.y.z>-win-x64-setup.exe heissen");
            return 2;
        }

        foreach (var file in new[] { oldSetup, newSetup, Path.GetFullPath(keyFile) })
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"update-smoke: Datei fehlt: {file}");
                return 1;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("update-smoke: laeuft nur unter Windows");
            return 1;
        }

        using var key = ReadKey(File.ReadAllText(keyFile));
        if (key is null)
        {
            Console.Error.WriteLine($"update-smoke: kein privater P-256-Schluessel (Base64 wie von update-keygen oder PEM): {keyFile}");
            return 1;
        }

        var failures = RunOnWindows(oldSetup, oldVersion, newSetup, newVersion, key, p.Named.GetValueOrDefault("--key-id", TestKeyId));
        if (failures.Count == 0)
        {
            Console.WriteLine($"update-smoke: {oldVersion} -> {newVersion} ueber den Helfermodus bestanden");
            return 0;
        }

        failures.ForEach(f => Console.Error.WriteLine($"update-smoke: {f}"));
        return 1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> RunOnWindows(string oldSetup, string oldVersion, string newSetup, string newVersion, ECDsa key, string keyId)
    {
        var failures = new List<string>();
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        var hklm = Microsoft.Win32.Registry.LocalMachine;

        // (0) Never over a real installation, as installer-smoke: the test uninstalls at the end.
        foreach (var (root, path) in new[] { (hkcu, @"Software\Classes\Pagewing.PDF"), (hkcu, Installer.UninstallKey), (hklm, Installer.UninstallKey) })
        {
            using var existing = root.OpenSubKey(path);
            if (existing is not null)
            {
                failures.Add($@"Pagewing ist schon installiert oder registriert ({root.Name}\{path}); erst deinstallieren, der Test wuerde es entfernen");
            }
        }

        if (failures.Count > 0)
        {
            return failures;
        }

        var temp = Path.GetTempPath();
        var dir = TestDir(temp);
        var work = Path.Combine(temp, "pagewing-update-smoke");
        foreach (var leftover in new[] { dir, work })
        {
            if (Directory.Exists(leftover))
            {
                Console.WriteLine($"update-smoke: entferne Reste eines frueheren Laufs: {leftover}");
                Directory.Delete(leftover, true);
            }
        }

        // A profile of its own: the helper and the restarted version take %APPDATA% and %LOCALAPPDATA% from the environment.
        var roaming = Path.Combine(work, "Roaming");
        var local = Path.Combine(work, "Local");
        var config = Path.Combine(roaming, "Pagewing");
        var logs = Path.Combine(work, "logs");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(config, "settings.json"), "{\n  \"updateCheck\": false\n}\n");
        var settingsBefore = SettingsLine(config);

        var realAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pagewing");
        var realBefore = InstallerSmoke.Snapshot(realAppData);
        var uninstaller = Path.Combine(dir, "unins000.exe");
        var exe = Path.Combine(dir, "Pagewing.exe");
        try
        {
            // (1) The old version, silently, exactly the documented command line.
            var code = ProcessRunner.RunInno(oldSetup, InstallerSmoke.SilentInstallArguments(dir, Path.Combine(logs, "install-old.log")), TimeSpan.FromMinutes(10));
            if (code != 0 || !File.Exists(exe))
            {
                failures.Add($"Installation der alten Version: Exit {code}, {exe} {(File.Exists(exe) ? "da" : "fehlt")}");
                return failures;
            }

            if (ProductVersion(exe) is not { } before || !before.StartsWith(oldVersion, StringComparison.Ordinal))
            {
                failures.Add($"{exe} meldet {ProductVersion(exe) ?? "keine Version"} statt {oldVersion}");
                return failures;
            }

            // (2) The staging folder as the app leaves it: setup program, signed manifest, signature, request.
            var staging = Path.Combine(config, "updates", newVersion);
            Directory.CreateDirectory(staging);
            var name = Path.GetFileName(newSetup);
            File.Copy(newSetup, Path.Combine(staging, name));
            var manifest = Manifest(newVersion, name, new FileInfo(newSetup).Length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(newSetup))));
            File.WriteAllBytes(Path.Combine(staging, "update-manifest.json"), manifest);
            File.WriteAllText(Path.Combine(staging, "update-manifest.json.sig"), Signature(manifest, key, keyId), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(staging, "request.json"), Request(newVersion, name, exe, dir), new UTF8Encoding(false));

            // (3) The helper runs from a copy of the program folder, never from the folder the setup program replaces.
            var copy = Path.Combine(local, "Pagewing", "updater", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            var psi = new ProcessStartInfo(Path.Combine(copy, "Pagewing.exe")) { UseShellExecute = false, WorkingDirectory = copy };
            HelperArguments(staging).ForEach(psi.ArgumentList.Add);
            psi.Environment["APPDATA"] = roaming;
            psi.Environment["LOCALAPPDATA"] = local;
            Console.WriteLine($"update-smoke: starte den Helfer {psi.FileName} --finish-update {staging}");
            using (var helper = Process.Start(psi)!)
            {
                if (!helper.WaitForExit(TimeSpan.FromMinutes(10)))
                {
                    helper.Kill(entireProcessTree: true);
                    failures.Add("der Helfer endete nicht in 10 Minuten (Signatur abgelehnt? Beide Setups muessen aus dem Testaufbau mit dem Schluessel von --key stammen)");
                }
                else if (helper.ExitCode != 0)
                {
                    failures.Add($"der Helfer endete mit Exit {helper.ExitCode}");
                }
            }

            // (4) The new version is installed, the update is gone, the settings are as they were.
            var after = ProductVersion(exe);
            if (after is null || !after.StartsWith(newVersion, StringComparison.Ordinal))
            {
                // The helper starts Inno Setup with "/LOG" without a name (never a path in the user-writable staging
                // folder): the log is "Setup Log <date> #<n>.txt" in the %TEMP% of the account that ran the setup program.
                failures.Add($"{exe} meldet nach dem Update {after ?? "keine Version"} statt {newVersion} (Protokoll: \"Setup Log *.txt\" in {temp})");
            }

            if (Directory.Exists(staging))
            {
                failures.Add($"der Staging-Ordner ist nach dem Update noch da: {staging}");
            }

            failures.AddRange(InstallerSmoke.Differences(settingsBefore, SettingsLine(config)).Select(d => $"settings.json veraendert: {d}"));
            return failures;
        }
        finally
        {
            // The helper starts the new version: end it (only the one from the test folder) before uninstalling.
            foreach (var process in Process.GetProcessesByName("Pagewing"))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(TimeSpan.FromSeconds(30));
                        }
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Not ours, or gone already.
                    }
                }
            }

            if (File.Exists(uninstaller))
            {
                var code = ProcessRunner.RunInno(uninstaller, InstallerSmoke.SilentUninstallArguments(Path.Combine(logs, "uninstall.log")), TimeSpan.FromMinutes(5));
                if (code != 0)
                {
                    failures.Add($"Deinstallation: Exit {code} (Protokoll: {Path.Combine(logs, "uninstall.log")})");
                }
            }

            failures.AddRange(InstallerSmoke.Differences(realBefore, InstallerSmoke.Snapshot(realAppData)).Select(d => $@"%APPDATA%\Pagewing veraendert: {d}"));
            if (failures.Count == 0)
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException)
                {
                    Console.WriteLine($"update-smoke: {work} wird beim naechsten Lauf entfernt");
                }
            }
            else
            {
                Console.Error.WriteLine($"update-smoke: Protokolle und Testprofil bleiben fuer die Fehlersuche: {work}");
            }
        }
    }

    // The line of settings.json in a snapshot of the folder (size and time of the last write).
    private static List<string> SettingsLine(string config) =>
        InstallerSmoke.Snapshot(config).Where(l => l.StartsWith("settings.json ", StringComparison.Ordinal)).ToList();

    private static string? ProductVersion(string exe) =>
        File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
}

// The update manifest of a release (Milestone 8.1) as Pagewing.Core.Updates reads it: UpdateManifest/UpdateFile
// (camelCase JSON), ManifestVerifier (checks). The tool is a script and cannot reference the app; the rules below are
// a copy, and testdata/updates plus ReleaseToolTests (Core) prove that the app accepts what the tool writes.
static partial class UpdateManifestRules
{
    public const int Format = 1;

    // ManifestVerifier.MaxFileSize (1 GiB) and MaxFiles.
    public const long MaxFileSize = 1L << 30;
    private const int MaxFiles = 64;

    // UpdateFileKinds.All in their order, with the release file name of a version (packaging/README.md, "Dateinamen").
    public static readonly (string Kind, Func<string, string> Name)[] Kinds =
    [
        ("win-x64-setup", v => $"Pagewing-{v}-win-x64-setup.exe"),
        ("win-x64-portable", v => $"Pagewing-{v}-win-x64-portable.zip"),
        ("linux-appimage", v => $"Pagewing-{v}-x86_64.AppImage"),
        ("linux-deb", v => $"pagewing_{v}_amd64.deb"),
    ];

    // The endings that make a release file one of the known kinds; a file with such an ending but another version is an
    // error, every other file (source archives, checksums) is not part of the manifest.
    public static readonly string[] KnownEndings = ["-win-x64-setup.exe", "-win-x64-portable.zip", "-x86_64.AppImage", "_amd64.deb"];

    // UpdateVersion.TryParse without the "v": the app reads only stable X.Y.Z versions.
    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z")]
    public static partial Regex StableVersion();

    // ManifestVerifier.SafeFileName and Sha256Hex.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\z")]
    public static partial Regex SafeFileName();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z")]
    private static partial Regex Sha256Hex();

    // Compact JSON, no line breaks (nothing for git or an editor to convert; the signature is over the raw bytes).
    public static byte[] Write(string version, IEnumerable<(string Kind, string Name, long Size, string Sha256)> files)
    {
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("format", Format);
            json.WriteString("version", version);
            json.WriteStartArray("files");
            foreach (var (kind, name, size, sha256) in files)
            {
                json.WriteStartObject();
                json.WriteString("kind", kind);
                json.WriteString("name", name);
                json.WriteNumber("size", size);
                json.WriteString("sha256", sha256);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // null when the bytes are a manifest the app reads (apart from "newer than the running version"); else why not.
    public static string? Problem(byte[] manifest)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(manifest, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("format", out var format) || !format.TryGetInt32(out var f) || f != Format)
            {
                return $"format ist nicht {Format}";
            }

            var version = root.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : string.Empty;
            if (!StableVersion().IsMatch(version.StartsWith('v') ? version[1..] : version))
            {
                return $"version ist keine Version X.Y.Z: '{version}'";
            }

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != System.Text.Json.JsonValueKind.Array ||
                files.GetArrayLength() is 0 or > MaxFiles)
            {
                return $"files fehlt, ist leer oder hat mehr als {MaxFiles} Eintraege";
            }

            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files.EnumerateArray())
            {
                var kind = file.ValueKind == System.Text.Json.JsonValueKind.Object && file.TryGetProperty("kind", out var k) &&
                    k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString()! : null;
                if (kind is null)
                {
                    return "ein Eintrag in files hat kein kind";
                }

                if (!Kinds.Any(x => x.Kind == kind))
                {
                    continue; // a later kind: the app skips it
                }

                var name = file.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString()! : string.Empty;
                var sha = file.TryGetProperty("sha256", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString()! : string.Empty;
                if (!kinds.Add(kind) || !Sha256Hex().IsMatch(sha))
                {
                    return $"{kind}: doppelt oder ohne gueltige sha256";
                }

                if (!SafeFileName().IsMatch(name))
                {
                    return $"{kind}: kein einfacher Dateiname: '{name}'";
                }

                if (!file.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes is <= 0 or > MaxFileSize)
                {
                    return $"{kind}: Groesse fehlt, ist 0 oder groesser als 1 GiB";
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return "kein gueltiges JSON";
        }
    }
}

// The public keys of a C# file such as src/Pagewing.Core/Updates/UpdateTrust.cs: every "new UpdateKey(<id>,
// Convert.FromBase64String("<SPKI>"))", the id as a string or as a "const string" of the same file. The placeholder of
// UpdateTrust.cs is never a usable key: its private half was discarded, and a release must not be signed for it.
static partial class TrustSource
{
    public const string DefaultPath = "src/Pagewing.Core/Updates/UpdateTrust.cs";
    public const string PlaceholderId = "placeholder";
    public const string PlaceholderKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==";

    [GeneratedRegex(@"new\s+UpdateKey\(\s*(?:""(?<id>[^""\\]*)""|(?<const>[A-Za-z_][A-Za-z0-9_]*))\s*,\s*Convert\.FromBase64String\(\s*""(?<key>[A-Za-z0-9+/=]+)""\s*\)\s*\)")]
    private static partial Regex KeyEntry();

    [GeneratedRegex(@"\bnew\s+UpdateKey\(")]
    private static partial Regex AnyKeyEntry();

    // The usable keys (id -> SPKI), how many placeholder entries were skipped, or why the file cannot be read.
    public static (Dictionary<string, byte[]> Keys, int Placeholders, string? Problem) Read(string path)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return (keys, 0, $"Datei fehlt: {path}");
        }

        var text = File.ReadAllText(path);

        // Conditional compilation (a test build with other keys) would make the text say something else than the
        // release build; refuse it instead of guessing which branch counts.
        if (Regex.IsMatch(text, @"(?m)^\s*#\s*if\b"))
        {
            return (keys, 0, $"{path}: bedingte Uebersetzung (#if) wird nicht unterstuetzt, die Schluessel waeren nicht eindeutig");
        }

        var entries = KeyEntry().Matches(text);
        if (entries.Count == 0 || entries.Count != AnyKeyEntry().Count(text))
        {
            return (keys, 0, $"{path}: kein oder ein nicht lesbarer Eintrag new UpdateKey(\"<Kennung>\", Convert.FromBase64String(\"<Schluessel>\"))");
        }

        var placeholders = 0;
        foreach (Match entry in entries)
        {
            var id = entry.Groups["id"].Success ? entry.Groups["id"].Value : Constant(text, entry.Groups["const"].Value);
            if (string.IsNullOrEmpty(id) || id.Contains(':'))
            {
                return (keys, 0, $"{path}: Kennung fehlt, ist leer, enthaelt ':' oder ist keine Konstante der Datei: {entry.Groups["const"].Value}");
            }

            var base64 = entry.Groups["key"].Value;
            if (id == PlaceholderId || base64 == PlaceholderKey)
            {
                placeholders++;
                continue;
            }

            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(base64);
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out var read);
                if (read != spki.Length || key.KeySize != 256)
                {
                    return (keys, 0, $"{path}: Schluessel {id} ist kein oeffentlicher P-256-Schluessel");
                }
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                return (keys, 0, $"{path}: Schluessel {id} ist kein oeffentlicher P-256-Schluessel");
            }

            if (!keys.TryAdd(id, spki))
            {
                return (keys, 0, $"{path}: Kennung {id} steht doppelt");
            }
        }

        return (keys, placeholders, null);
    }

    private static string? Constant(string text, string name)
    {
        var m = Regex.Match(text, @"\bconst\s+string\s+" + Regex.Escape(name) + @"\s*=\s*""(?<v>[^""\\]*)""\s*;");
        return m.Success ? m.Groups["v"].Value : null;
    }

    // The keys for signing or verifying; null (message written) when there is none to trust.
    public static Dictionary<string, byte[]>? Usable(string path, string command)
    {
        var (keys, placeholders, problem) = Read(path);
        if (problem is not null)
        {
            Console.Error.WriteLine($"{command}: {problem}");
            return null;
        }

        if (keys.Count == 0)
        {
            Console.Error.WriteLine(placeholders > 0
                ? $"{command}: {path} enthaelt nur den Platzhalter; zuerst den echten oeffentlichen Schluessel eintragen (packaging/README.md, Abschnitt \"Signaturschluessel fuer Updates\")"
                : $"{command}: {path} enthaelt keinen Schluessel");
            return null;
        }

        return keys;
    }
}

static class UpdateSignature
{
    // "<keyId>:<base64 of the 64-byte IEEE P1363 ECDSA P-256 signature over the raw bytes, SHA-256>" (ManifestVerifier).
    public static string Sign(byte[] manifest, ECDsa key, string keyId) =>
        keyId + ":" + Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    // null when the signature text verifies with one of the keys (by its id); else why not.
    public static string? Problem(byte[] manifest, string signatureText, IReadOnlyDictionary<string, byte[]> keys)
    {
        var text = signatureText.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return "Signatur hat nicht die Form <Kennung>:<Base64>";
        }

        var id = text[..colon];
        if (!keys.TryGetValue(id, out var spki))
        {
            return $"Schluesselkennung {id} ist nicht vertraut";
        }

        var signature = new byte[67];
        if (!Convert.TryFromBase64String(text[(colon + 1)..], signature, out var written) || written != 64)
        {
            return "Signatur ist kein Base64 von 64 Bytes";
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key.VerifyData(manifest, signature.AsSpan(0, 64), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? null
            : $"Signatur passt nicht zum Manifest (Schluessel {id})";
    }

    // The key id rule of UpdateKey: not empty, no colon; kept to a plain token so it fits release.yml and file names.
    public static bool IsValidId(string id) => Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z");
}

// update-keygen --id <id> --private-out <file>: the private key (PKCS#8, Base64) only into the file, readable by the
// user alone; the public key as an entry for UpdateTrust.Keys on stdout. The private key is never printed.
static class UpdateKeygen
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--id", "--private-out");
        if (parsed is not { } p || p.Positional.Count != 0 || !p.Named.TryGetValue("--id", out var id) ||
            !p.Named.TryGetValue("--private-out", out var output) || !UpdateSignature.IsValidId(id))
        {
            Console.Error.WriteLine("Aufruf: update-keygen --id <Kennung aus A-Z a-z 0-9 . _ -> --private-out <Datei ausserhalb jedes Git-Arbeitsverzeichnisses>");
            return 2;
        }

        var full = Path.GetFullPath(output);
        if (File.Exists(full) || Directory.Exists(full))
        {
            Console.Error.WriteLine($"update-keygen: {full} gibt es schon; ein Schluessel wird nie ueberschrieben");
            return 1;
        }

        if (GitWorkTree(full) is { } tree)
        {
            Console.Error.WriteLine($"update-keygen: {full} liegt im Git-Arbeitsverzeichnis {tree}; der private Schluessel darf nie in ein Repository");
            return 1;
        }

        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            Console.Error.WriteLine($"update-keygen: Ordner fehlt: {Path.GetDirectoryName(full)}");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var chars = new char[(pkcs8.Length + 2) / 3 * 4];
        try
        {
            Convert.TryToBase64Chars(pkcs8, chars, out var length);
            using (var stream = CreateUserOnly(full))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(chars, 0, length);
                writer.Write('\n');
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            Array.Clear(chars);
        }

        Console.WriteLine("        new UpdateKey(");
        Console.WriteLine($"            \"{id}\",");
        Console.WriteLine($"            Convert.FromBase64String(\"{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}\")),");
        Console.Error.WriteLine($"update-keygen: privater Schluessel in {full} (nur fuer diesen Benutzer lesbar). Weiter: packaging/README.md, Abschnitt \"Signaturschluessel fuer Updates\".");
        return 0;
    }

    // The folder (or an ancestor) that holds .git, or null.
    private static string? GitWorkTree(string file)
    {
        for (var dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }
        }

        return null;
    }

    // A new file (never an existing one) that only the current user can read: 0600 on Unix, on Windows an ACL with
    // inheritance cut and a single rule for the user.
    private static FileStream CreateUserOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}

// update-manifest --version <x.y.z> --sums <SHA256SUMS-windows.txt> <SHA256SUMS-linux.txt> --sizes <gh release view --json assets> --out <file>
static partial class UpdateManifestCommand
{
    // sums: "<sha256>  <file name>" (Sums.Line)
    [GeneratedRegex(@"^(?<h>[0-9a-f]{64})  (?<n>[^\s/\\]+)$")]
    private static partial Regex SumsLine();

    private const string Usage = "Aufruf: update-manifest --version <x.y.z> --sums <SHA256SUMS-windows.txt> <SHA256SUMS-linux.txt> --sizes <assets.json> --out <update-manifest.json>";

    public static int Run(string[] args)
    {
        // --sums takes several files: everything up to the next option.
        var sums = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--sums")
            {
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    sums.Add(args[++i]);
                }
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        var parsed = Options.Parse([.. rest], "--version", "--sizes", "--out");
        if (parsed is not { } p || p.Positional.Count != 0 || sums.Count == 0 || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--sizes", out var sizesFile) || !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!UpdateManifestRules.StableVersion().IsMatch(version))
        {
            Console.Error.WriteLine($"update-manifest: '{version}' ist keine Version X.Y.Z; die App liest nur stabile Versionen (Vorabversionen bekommen kein Manifest)");
            return 2;
        }

        var problems = new List<string>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in sums)
        {
            if (!File.Exists(file))
            {
                problems.Add($"Datei fehlt: {file}");
                continue;
            }

            foreach (var raw in File.ReadAllText(file).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                var m = SumsLine().Match(line);
                if (!m.Success)
                {
                    problems.Add($"{file}: Zeile ist nicht '<sha256>  <Dateiname>': {line}");
                }
                else if (!hashes.TryAdd(m.Groups["n"].Value, m.Groups["h"].Value))
                {
                    problems.Add($"{m.Groups["n"].Value} steht mehrfach in den Pruefsummen");
                }
            }
        }

        var sizes = ReadSizes(sizesFile, problems);

        // Every file with a known ending must be exactly the name of this version; every known kind must be there.
        foreach (var name in hashes.Keys.Where(n => UpdateManifestRules.KnownEndings.Any(e => n.EndsWith(e, StringComparison.Ordinal))))
        {
            if (!UpdateManifestRules.Kinds.Any(k => k.Name(version) == name))
            {
                problems.Add($"{name} gehoert nicht zur Version {version}");
            }
        }

        var files = new List<(string Kind, string Name, long Size, string Sha256)>();
        foreach (var (kind, nameOf) in UpdateManifestRules.Kinds)
        {
            var name = nameOf(version);
            if (!hashes.TryGetValue(name, out var hash))
            {
                problems.Add($"{kind}: {name} fehlt in den Pruefsummen");
            }
            else if (sizes is not null && !sizes.TryGetValue(name, out var size))
            {
                problems.Add($"{kind}: {name} fehlt in den Dateien des Releases (--sizes)");
            }
            else if (sizes is not null)
            {
                files.Add((kind, name, sizes[name], hash));
            }
        }

        foreach (var (kind, name, size, _) in files)
        {
            if (size is <= 0 or > UpdateManifestRules.MaxFileSize)
            {
                problems.Add($"{kind}: {name} hat die Groesse {size} (erlaubt 1 Byte bis 1 GiB)");
            }

            if (!UpdateManifestRules.SafeFileName().IsMatch(name))
            {
                problems.Add($"{kind}: {name} ist kein einfacher Dateiname");
            }
        }

        if (problems.Count > 0)
        {
            problems.ForEach(x => Console.Error.WriteLine($"update-manifest: {x}"));
            return 1;
        }

        var bytes = UpdateManifestRules.Write(version, files);
        if (UpdateManifestRules.Problem(bytes) is { } problem)
        {
            Console.Error.WriteLine($"update-manifest: das geschriebene Manifest waere ungueltig: {problem}");
            return 1;
        }

        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"update-manifest: {version}, {files.Count} Dateien in {output}");
        return 0;
    }

    // name -> size from "gh release view <tag> --json assets"; only assets that are completely uploaded count.
    private static Dictionary<string, long>? ReadSizes(string file, List<string> problems)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                var uploaded = !asset.TryGetProperty("state", out var state) || state.GetString() == "uploaded";
                if (uploaded && !result.TryAdd(name, asset.GetProperty("size").GetInt64()))
                {
                    problems.Add($"{file}: {name} steht mehrfach");
                }
            }

            return result;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"{file}: keine Ausgabe von 'gh release view <Tag> --json assets' ({e.GetType().Name})");
            return null;
        }
    }
}

// update-sign --manifest <file> --key-env <VARIABLE> --id <id> --out <file.sig> [--source <UpdateTrust.cs>]
// The private key comes only from the environment variable (never an argument, never printed, never written to disk).
// The id must be a key of UpdateTrust.cs (never the placeholder), the private key must belong to it, and the signature
// is verified with that public key before it is written.
static class UpdateSign
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--manifest", "--key-env", "--id", "--out", "--source");
        if (parsed is not { } p || p.Positional.Count != 0 || !p.Named.TryGetValue("--manifest", out var manifestFile) ||
            !p.Named.TryGetValue("--key-env", out var keyEnv) || !p.Named.TryGetValue("--id", out var id) ||
            !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: update-sign --manifest <Datei> --key-env <Umgebungsvariable> --id <Kennung> --out <Datei.sig> [--source <UpdateTrust.cs>]   (der Schluessel kommt nur aus der Umgebungsvariablen)");
            return 2;
        }

        var source = p.Named.GetValueOrDefault("--source", TrustSource.DefaultPath);
        if (TrustSource.Usable(source, "update-sign") is not { } keys)
        {
            return 1;
        }

        if (!keys.TryGetValue(id, out var trusted))
        {
            Console.Error.WriteLine($"update-sign: Kennung {id} steht nicht in {source} (vorhanden: {string.Join(", ", keys.Keys)})");
            return 1;
        }

        if (!File.Exists(manifestFile))
        {
            Console.Error.WriteLine($"update-sign: Datei fehlt: {manifestFile}");
            return 1;
        }

        var manifest = File.ReadAllBytes(manifestFile);
        if (UpdateManifestRules.Problem(manifest) is { } problem)
        {
            Console.Error.WriteLine($"update-sign: {manifestFile} ist kein gueltiges Manifest: {problem}");
            return 1;
        }

        using var key = ReadKey(keyEnv);
        if (key is null)
        {
            return 1;
        }

        if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(trusted))
        {
            Console.Error.WriteLine($"update-sign: der Schluessel in {keyEnv} gehoert nicht zur Kennung {id} in {source}");
            return 1;
        }

        var signature = UpdateSignature.Sign(manifest, key, id);
        if (UpdateSignature.Problem(manifest, signature, new Dictionary<string, byte[]> { [id] = trusted }) is { } check)
        {
            Console.Error.WriteLine($"update-sign: die neue Signatur prueft nicht: {check}");
            return 1;
        }

        File.WriteAllText(output, signature, new UTF8Encoding(false));
        Console.WriteLine($"update-sign: {manifestFile} mit {id} signiert und geprueft: {output}");
        return 0;
    }

    // The P-256 private key from the environment (PKCS#8 as Base64, as update-keygen writes it); null with a message
    // that never contains the value.
    private static ECDsa? ReadKey(string variable)
    {
        var text = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine($"update-sign: Umgebungsvariable {variable} fehlt oder ist leer");
            return null;
        }

        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var bytes = new byte[compact.Length];
        var key = ECDsa.Create();
        try
        {
            if (!Convert.TryFromBase64String(compact, bytes, out var length))
            {
                throw new FormatException();
            }

            // The curve is checked by the caller: the public key must equal the trusted P-256 key (the SPKI names the curve).
            key.ImportPkcs8PrivateKey(bytes.AsSpan(0, length), out var read);
            if (read != length || key.KeySize != 256)
            {
                throw new FormatException();
            }

            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            key.Dispose();
            Console.Error.WriteLine($"update-sign: {variable} enthaelt keinen privaten P-256-Schluessel (PKCS#8, Base64, wie update-keygen ihn schreibt)");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

// update-verify [--manifest <file> --sig <file>] (--keys-from-source | --source <file>): signature and content as the
// app checks them; --keys-from-source takes the keys of src/Pagewing.Core/Updates/UpdateTrust.cs and fails while it
// holds only the placeholder. Without manifest and signature it checks only that (preflight job of release.yml).
static class UpdateVerify
{
    public static int Run(string[] args)
    {
        var fromSource = args.Contains("--keys-from-source", StringComparer.Ordinal);
        var parsed = Options.Parse([.. args.Where(a => a != "--keys-from-source")], "--manifest", "--sig", "--source");
        if (parsed is not { } p || p.Positional.Count != 0 || fromSource == p.Named.ContainsKey("--source") ||
            p.Named.ContainsKey("--manifest") != p.Named.ContainsKey("--sig"))
        {
            Console.Error.WriteLine("Aufruf: update-verify [--manifest <Datei> --sig <Datei>] (--keys-from-source | --source <C#-Datei mit new UpdateKey(...)>)   (ohne Manifest: prueft nur, dass ein echter Schluessel vertraut wird)");
            return 2;
        }

        var source = p.Named.GetValueOrDefault("--source", TrustSource.DefaultPath);
        if (TrustSource.Usable(source, "update-verify") is not { } keys)
        {
            return 1;
        }

        // Without a manifest: only the keys (the preflight job of release.yml, before anything is uploaded).
        if (!p.Named.TryGetValue("--manifest", out var manifestFile) || !p.Named.TryGetValue("--sig", out var sigFile))
        {
            Console.WriteLine($"update-verify: {source} vertraut {string.Join(", ", keys.Keys)} (kein Platzhalter allein)");
            return 0;
        }

        if (!File.Exists(manifestFile) || !File.Exists(sigFile))
        {
            Console.Error.WriteLine($"update-verify: Datei fehlt: {(File.Exists(manifestFile) ? sigFile : manifestFile)}");
            return 1;
        }

        var manifest = File.ReadAllBytes(manifestFile);
        var problem = UpdateSignature.Problem(manifest, File.ReadAllText(sigFile), keys) ?? UpdateManifestRules.Problem(manifest);
        if (problem is not null)
        {
            Console.Error.WriteLine($"update-verify: {manifestFile} abgelehnt: {problem}");
            return 1;
        }

        Console.WriteLine($"update-verify: {manifestFile} ist gueltig signiert ({source})");
        return 0;
    }
}

enum DebEntryKind
{
    Directory,
    File,
    Symlink,
}

// One entry of a package tree (.deb, AppImage); Path is relative to the package root ("usr/bin/pagewing"), Mode the Unix mode
// (0755 = 0x1ED, 0644 = 0x1A4). A file comes from Source or, when generated, from Content (UTF-8 without BOM).
sealed record DebEntry(string Path, DebEntryKind Kind, int Mode, string? Source = null, string? LinkTarget = null, string? Content = null);

static class Tree
{
    // Writes the entries below root with their modes and the fixed time (directories last, children first: they change the
    // parent's time). Symlinks keep the time of their creation; tools that need a fixed one clamp it (SOURCE_DATE_EPOCH).
    public static void Write(IReadOnlyList<DebEntry> entries, string root, DateTimeOffset time, IEnumerable<string>? extraDirectories = null)
    {
        Directory.CreateDirectory(root);
        var directories = new List<string> { root };
        foreach (var entry in entries)
        {
            var target = Path.Combine(root, entry.Path);
            switch (entry.Kind)
            {
                case DebEntryKind.Directory:
                    Directory.CreateDirectory(target);
                    File.SetUnixFileMode(target, (UnixFileMode)entry.Mode);
                    directories.Add(target);
                    break;
                case DebEntryKind.File:
                    if (entry.Content is { } content)
                    {
                        File.WriteAllText(target, content, new UTF8Encoding(false));
                    }
                    else
                    {
                        File.Copy(entry.Source!, target);
                    }

                    File.SetUnixFileMode(target, (UnixFileMode)entry.Mode);
                    File.SetLastWriteTimeUtc(target, time.UtcDateTime);
                    break;
                default:
                    File.CreateSymbolicLink(target, entry.LinkTarget!);
                    break;
            }
        }

        File.SetUnixFileMode(root, (UnixFileMode)0x1ED);
        foreach (var directory in directories.Concat(extraDirectories ?? []).OrderByDescending(d => d.Length))
        {
            Directory.SetLastWriteTimeUtc(directory, time.UtcDateTime);
        }
    }

    public static IEnumerable<string> Parents(string path)
    {
        for (var i = path.IndexOf('/', StringComparison.Ordinal); i > 0; i = path.IndexOf('/', i + 1))
        {
            yield return path[..i];
        }
    }

    // Adds a 0755 directory entry for every parent of every entry and sorts by path (ordinal).
    public static List<DebEntry> WithParents(IEnumerable<DebEntry> entries)
    {
        var all = entries.ToList();
        foreach (var directory in all.SelectMany(e => Parents(e.Path)).Distinct(StringComparer.Ordinal).ToList())
        {
            all.Add(new DebEntry(directory, DebEntryKind.Directory, 0x1ED));
        }

        return all.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }
}

static partial class Deb
{
    // PLACEHOLDER until the owner decides (open question of Milestone 8, task 9). A noreply address is
    // used on purpose; replace it here, nowhere else (control.template takes it from this constant).
    public const string Maintainer = "Pagewing contributors <noreply@users.noreply.github.com>";

    public static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int Dir = 0x1ED;   // 0755
    private const int Exec = 0x1ED;  // 0755
    private const int Plain = 0x1A4; // 0644
    private const string LibDir = "usr/lib/pagewing";

    [GeneratedRegex(@"^(?<core>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:-(?<pre>[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?$")]
    private static partial Regex VersionPattern();

    // x.y.z stays, x.y.z-rc1 becomes x.y.z~rc1 (a tilde sorts before the release); anything else is null.
    public static string? DebVersion(string version)
    {
        var m = VersionPattern().Match(version);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups["pre"].Success ? $"{m.Groups["core"].Value}~{m.Groups["pre"].Value}" : m.Groups["core"].Value;
    }

    // Reads one control field with dpkg-deb -f (selftest helper).
    public static string Field(string deb, string name) => Tool.Run("dpkg-deb", ["-f", deb, name], out var text) == 0 ? text.Trim() : "?";

    public static string FileName(string debVersion) => $"pagewing_{debVersion}_amd64.deb";

    // Installed-Size is in KiB; every file counts rounded up, like a block-based size would.
    public static long InstalledSizeKiB(IEnumerable<long> fileSizes) => Math.Max(1, fileSizes.Sum(s => (s + 1023) / 1024));

    public static string Control(string template, string debVersion, long installedSizeKiB, string maintainer) =>
        template.Replace("@VERSION@", debVersion, StringComparison.Ordinal)
            .Replace("@INSTALLED_SIZE@", installedSizeKiB.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@MAINTAINER@", maintainer, StringComparison.Ordinal);

    // The package tree: layout under /usr/lib/pagewing, launcher link, .desktop, icons, copyright. Throws IOException when an input is missing.
    public static List<DebEntry> Plan(string layout, string repo)
    {
        var files = new List<DebEntry>();
        foreach (var required in new[] { "Pagewing", "libpdfium.so", "THIRD-PARTY-NOTICES.txt", "cli/pagewing", "cli/libpdfium.so" })
        {
            if (!File.Exists(Path.Combine(layout, required)))
            {
                throw new IOException($"Layout enthaelt {required} nicht: {layout}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(layout, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(layout, file).Replace('\\', '/');
            if (relative == "LICENSE")
            {
                continue; // the licence is in /usr/share/doc/pagewing/copyright and THIRD-PARTY-NOTICES.txt
            }

            files.Add(new DebEntry($"{LibDir}/{relative}", DebEntryKind.File, relative is "Pagewing" or "cli/pagewing" ? Exec : Plain, file));
        }

        var icons = Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor");
        if (!Directory.Exists(icons))
        {
            throw new IOException($"Symbolordner fehlt: {icons}");
        }

        foreach (var icon in Directory.EnumerateDirectories(icons))
        {
            var png = Path.Combine(icon, "apps", "pagewing.png");
            if (File.Exists(png))
            {
                files.Add(new DebEntry($"usr/share/icons/hicolor/{Path.GetFileName(icon)}/apps/pagewing.png", DebEntryKind.File, Plain, png));
            }
        }

        if (files.All(f => !f.Path.StartsWith("usr/share/icons/", StringComparison.Ordinal)))
        {
            throw new IOException($"Keine Symbole in {icons}");
        }

        files.Add(Source(repo, "packaging/linux/pagewing.desktop", "usr/share/applications/pagewing.desktop"));
        files.Add(Source(repo, "packaging/linux/copyright.template", "usr/share/doc/pagewing/copyright"));
        files.Add(new DebEntry("usr/bin/pagewing", DebEntryKind.Symlink, 0x1FF, null, "../lib/pagewing/cli/pagewing"));
        return Tree.WithParents(files);
    }

    private static DebEntry Source(string repo, string relative, string target)
    {
        var file = Path.Combine(repo, relative);
        return File.Exists(file) ? new DebEntry(target, DebEntryKind.File, Plain, file) : throw new IOException($"Datei fehlt: {file}");
    }

    // Writes the tree with modes and the fixed time; dpkg-deb --root-owner-group makes everything root:root.
    public static void Stage(IReadOnlyList<DebEntry> entries, string stage, string control)
    {
        var debian = Path.Combine(stage, "DEBIAN");
        Directory.CreateDirectory(debian);
        File.SetUnixFileMode(debian, (UnixFileMode)Dir);
        var controlFile = Path.Combine(debian, "control");
        File.WriteAllText(controlFile, control, new UTF8Encoding(false));
        File.SetUnixFileMode(controlFile, (UnixFileMode)Plain);
        File.SetLastWriteTimeUtc(controlFile, FixedTime.UtcDateTime);
        Tree.Write(entries, stage, FixedTime, [debian]);
    }

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--layout", "--version", "--out", "--repo");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--layout", out var layout) || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: deb --layout <Ordner> --version <x.y.z> --out <Datei>");
            return 2;
        }

        if (DebVersion(version) is not { } debVersion)
        {
            Console.Error.WriteLine($"deb: ungueltige Version (erwartet x.y.z oder x.y.z-vorabversion): {version}");
            return 2;
        }

        if (Path.GetFileName(output) != FileName(debVersion))
        {
            Console.Error.WriteLine($"deb: die Ausgabedatei muss {FileName(debVersion)} heissen: {output}");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("deb: nur unter Linux (dpkg-deb)");
            return 1;
        }

        var repo = Path.GetFullPath(p.Named.GetValueOrDefault("--repo", "."));
        var work = Path.Combine(Path.GetTempPath(), "pagewing-deb-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!Directory.Exists(layout))
            {
                throw new IOException($"Layout-Ordner fehlt: {layout}");
            }

            var entries = Plan(layout, repo);
            var size = InstalledSizeKiB(entries.Where(e => e.Kind == DebEntryKind.File).Select(e => new FileInfo(e.Source!).Length));
            var template = File.ReadAllText(Path.Combine(repo, "packaging", "linux", "control.template"));
            Stage(entries, Path.Combine(work, "stage"), Control(template, debVersion, size, Maintainer));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Delete(output);
            var env = new Dictionary<string, string> { ["SOURCE_DATE_EPOCH"] = FixedTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
            var code = Tool.Run("dpkg-deb", ["--root-owner-group", "-Zxz", "--build", Path.Combine(work, "stage"), output], out var text, env: env);
            Console.Write(text);
            if (code != 0)
            {
                Console.Error.WriteLine($"deb: dpkg-deb endete mit Code {code}");
                return 1;
            }

            Console.WriteLine($"deb: {entries.Count(e => e.Kind == DebEntryKind.File)} Dateien in {output}");
            return 0;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"deb: {e.Message}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }
        }
    }
}

/// <summary>Resolves POSIX paths as text, independent of the host OS (Path.GetFullPath would add a drive letter on Windows).</summary>
static class PosixPath
{
    public static string Resolve(string directory, string relative)
    {
        var parts = new List<string>();
        foreach (var segment in (relative.StartsWith('/') ? relative : directory + "/" + relative).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return "/" + string.Join('/', parts);
    }
}

static class Tool
{
    // Runs a program without a shell and returns its exit code with stdout and stderr in one text; -1 when it is not installed.
    public static int Run(string file, IEnumerable<string> arguments, out string output, string? workingDirectory = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);
                output = "Zeitlimit";
                return -2;
            }

            process.WaitForExit();
            output = stdout.Result + stderr.Result;
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            output = $"{file} nicht gefunden";
            return -1;
        }
    }
}

static partial class DebSmoke
{
    [GeneratedRegex(@"^(?<type>[-dl])\S+\s+(?<owner>\S+)\s+\d+\s+\S+\s+\S+\s+(?<path>.+)$")]
    private static partial Regex ListingLine();

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--repo");
        if (parsed is not { } p || p.Positional.Count != 1)
        {
            Console.Error.WriteLine("Aufruf: deb-smoke <Datei.deb> [--repo <Ordner>]");
            return 2;
        }

        var deb = Path.GetFullPath(p.Positional[0]);
        var repo = Path.GetFullPath(p.Named.GetValueOrDefault("--repo", "."));
        if (!File.Exists(deb))
        {
            Console.Error.WriteLine($"deb-smoke: Datei fehlt: {deb}");
            return 1;
        }

        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok    " : "FEHLER")} {what}");
            if (!ok)
            {
                failures.Add(what);
            }
        }

        var work = Path.Combine(Path.GetTempPath(), "pagewing-deb-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            Check(Tool.Run("dpkg-deb", ["-I", deb], out var info) == 0, "dpkg-deb -I liest das Paket");
            Console.Write(info);
            foreach (var field in new[] { "Package", "Version", "Architecture", "Maintainer", "Depends", "Installed-Size" })
            {
                Check(Tool.Run("dpkg-deb", ["-f", deb, field], out var value) == 0 && value.Trim().Length > 0, $"Feld {field}: {value.Trim()}");
            }

            Check(Tool.Run("dpkg-deb", ["-c", deb], out var listing) == 0, "dpkg-deb -c listet den Inhalt");
            Console.Write(listing);
            var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => ListingLine().Match(l)).ToList();
            Check(lines.Count > 0 && lines.All(m => m.Success && m.Groups["owner"].Value == "root/root"), "alle Eintraege gehoeren root:root");
            Check(lines.Any(m => m.Success && m.Groups["path"].Value.StartsWith("./usr/bin/pagewing -> ", StringComparison.Ordinal)), "/usr/bin/pagewing ist ein Symlink");
            Check(lines.Any(m => m.Success && m.Groups["path"].Value == "./usr/share/doc/pagewing/copyright"), "copyright vorhanden");

            var root = Path.Combine(work, "root");
            Check(Tool.Run("dpkg-deb", ["-x", deb, root], out var extract) == 0, "dpkg-deb -x entpackt");
            if (failures.Count > 0)
            {
                Console.Write(extract);
                return Finish(failures);
            }

            var desktop = Path.Combine(root, "usr", "share", "applications", "pagewing.desktop");
            var exec = File.Exists(desktop) ? File.ReadLines(desktop).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal)) : null;
            var target = exec?["Exec=".Length..].Split(' ')[0] ?? string.Empty;
            var targetFile = Path.Combine(root, target.TrimStart('/'));
            Check(target.StartsWith('/') && File.Exists(targetFile) && (File.GetUnixFileMode(targetFile) & UnixFileMode.UserExecute) != 0, $"Exec der .desktop ({target}) ist im Paket vorhanden und ausfuehrbar");
            Check(Directory.GetFiles(Path.Combine(root, "usr", "share", "icons", "hicolor"), "pagewing.png", SearchOption.AllDirectories).Length > 0, "Symbole vorhanden");

            var link = Path.Combine(root, "usr", "bin", "pagewing");
            var linkTarget = new FileInfo(link).LinkTarget;
            var resolved = linkTarget is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, linkTarget));
            Check(resolved is not null && resolved.StartsWith(root + "/", StringComparison.Ordinal) && File.Exists(resolved), $"/usr/bin/pagewing loest innerhalb des Pakets auf ({linkTarget})");

            var pdf = Path.Combine(repo, "testdata", "pdfs", "text-basic.pdf");
            if (File.Exists(pdf))
            {
                var code = Tool.Run(link, ["info", pdf], out var cli);
                Check(code == 0 && cli.Trim().Length > 0, $"pagewing info text-basic.pdf (Code {code})");
                if (code != 0)
                {
                    Console.Write(cli);
                }
            }
            else
            {
                Console.WriteLine($"Hinweis: {pdf} fehlt, CLI-Aufruf uebersprungen");
            }

            Optional("desktop-file-validate", [desktop], Check);
            RunLintian(deb, Check);
            return Finish(failures);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }
        }
    }

    // Lintian errors that follow from shipping an upstream-built .deb with prebuilt native libraries (SkiaSharp, HarfBuzzSharp, PDFium
    // with its bundled FreeType, lcms2, libjpeg, libpng, OpenJPEG) and no Debian changelog. Reason: Docs/DECISIONS.md, entry ".deb ...".
    // Every other error, and every error without a tag we know, fails the smoke test.
    public static readonly string[] AcceptedLintianErrors = ["embedded-library", "unstripped-binary-or-object", "no-changelog"];

    // The error lines ("E: pagewing: <tag> ...") of a lintian report whose tag is not accepted.
    public static List<string> RejectedLintianErrors(string report) =>
        report.Split('\n')
            .Where(l => l.StartsWith("E: ", StringComparison.Ordinal))
            .Where(l => !AcceptedLintianErrors.Any(t => l.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) is [_, _, var rest] && (rest == t || rest.StartsWith(t + " ", StringComparison.Ordinal))))
            .ToList();

    private static void RunLintian(string deb, Action<bool, string> check)
    {
        var code = Tool.Run("lintian", ["--tag-display-limit", "0", deb], out var text);
        if (code == -1)
        {
            Console.WriteLine("Hinweis: lintian ist nicht installiert, Pruefung uebersprungen");
            return;
        }

        Console.Write(text);
        var rejected = RejectedLintianErrors(text);
        foreach (var line in rejected)
        {
            Console.Error.WriteLine($"lintian: nicht erlaubter Fehler: {line}");
        }

        // Exit code 2 is "errors found"; with only accepted errors that is fine, any other code is not.
        check(rejected.Count == 0 && code is 0 or 2, $"lintian (Code {code}, nur bekannte Fehler: {string.Join(", ", AcceptedLintianErrors)})");
    }

    // A tool that is not installed is a notice, not a failure; a tool that reports errors fails the smoke test.
    private static void Optional(string tool, string[] arguments, Action<bool, string> check)
    {
        var code = Tool.Run(tool, arguments, out var text);
        if (code == -1)
        {
            Console.WriteLine($"Hinweis: {tool} ist nicht installiert, Pruefung uebersprungen");
            return;
        }

        Console.Write(text);
        check(code == 0, $"{tool} (Code {code})");
    }

    private static int Finish(List<string> failures)
    {
        Console.WriteLine(failures.Count == 0 ? "deb-smoke: bestanden." : $"deb-smoke: {failures.Count} Pruefung(en) fehlgeschlagen.");
        return failures.Count == 0 ? 0 : 1;
    }
}

// The pinned static type2 runtime of the AppImage project (build input, see Docs/DECISIONS.md "AppImage: type2-Runtime 20251108 …").
static class AppImageRuntime
{
    public const string Version = "20251108";
    public const string Url = "https://github.com/AppImage/type2-runtime/releases/download/" + Version + "/runtime-x86_64";

    // From the release's own detached GnuPG signature runtime-x86_64.sig, checked against signing-pubkey.asc of
    // AppImage/type2-runtime (ed25519 key 570C77ACEA40C0F1B758902CBF96CCA56490F695, in the repository since 2024-05).
    public const string Sha256 = "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d";
    public const long Size = 944632;

    public static (long Size, string Sha256) Pin => (Size, Sha256);

    // $XDG_CACHE_HOME (or ~/.cache)/PagewingBuildTools/type2-runtime-<version>-x86_64
    public static string DefaultPath(string cacheHome) => Path.Combine(cacheHome, "PagewingBuildTools", $"type2-runtime-{Version}-x86_64");

    public static string DefaultPath()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return DefaultPath(string.IsNullOrEmpty(cache) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : cache);
    }
}

// One pinned source archive that belongs to the AppImage runtime.
record RuntimeSource(string FileName, string Url, long Size, string Sha256);

// libfuse is inside the statically linked AppImage runtime under LGPL-2.1, which obliges us to offer its source with the
// binary. The two archives are attached to every release (Docs/DECISIONS.md, entry "Quellangebot für libfuse ...").
static class RuntimeSources
{
    // Both pins were computed from the downloads on 2026-10-23. fuse-3.15.0.tar.xz equals the SHA-256 that the runtime's own
    // scripts/common/install-dependencies.sh checks. The type2-runtime archive is GitHub's generated tag archive; if
    // GitHub ever changes its bytes, runtime-sources fails loudly and the pin must be renewed after comparing the contents.
    public static readonly RuntimeSource[] All =
    [
        new("fuse-3.15.0.tar.xz",
            "https://github.com/libfuse/libfuse/releases/download/fuse-3.15.0/fuse-3.15.0.tar.xz",
            4589124,
            "70589cfd5e1cff7ccd6ac91c86c01be340b227285c5e200baa284e401eea2ca0"),
        new($"type2-runtime-{AppImageRuntime.Version}.tar.gz",
            $"https://github.com/AppImage/type2-runtime/archive/refs/tags/{AppImageRuntime.Version}.tar.gz",
            31045,
            "4c4f6df4647c9f01f871d7edd3716d8aeeffda9d22ffbebe3fccd95a6ab52c95"),
    ];

    // Puts every verified source into outDir (from fromDir instead of the network when given). 0 ok, 1 refused or failed.
    public static int Fetch(IReadOnlyList<RuntimeSource> sources, string outDir, string? fromDir)
    {
        foreach (var source in sources)
        {
            if (Pinned.PinProblem(source.Sha256, $"RuntimeSources ({source.FileName})") is { } pinProblem)
            {
                Console.Error.WriteLine($"runtime-sources: {pinProblem}");
                return 1;
            }
        }

        Directory.CreateDirectory(outDir);
        foreach (var source in sources)
        {
            var name = $"RuntimeSources ({source.FileName})";
            var target = Path.Combine(outDir, source.FileName);
            if (File.Exists(target))
            {
                if (Pinned.VerifyFile(target, source.Size, source.Sha256, name) is { } existing)
                {
                    Console.Error.WriteLine($"runtime-sources: vorhandene Datei {target} passt nicht ({existing}); nichts geladen, Datei bitte pruefen und loeschen");
                    return 1;
                }

                Console.WriteLine($"runtime-sources: schon vorhanden und geprueft: {target}");
                continue;
            }

            var download = target + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                if (fromDir is null)
                {
                    Console.WriteLine($"runtime-sources: lade {source.Url}");
                    Pinned.Fetch(source.Url, download);
                }
                else
                {
                    File.Copy(Path.Combine(fromDir, source.FileName), download);
                }

                if (Pinned.VerifyFile(download, source.Size, source.Sha256, name) is { } problem)
                {
                    Console.Error.WriteLine($"runtime-sources: {source.FileName} abgelehnt: {problem}");
                    return 1;
                }

                File.Move(download, target);
                Console.WriteLine($"runtime-sources: {source.FileName} (SHA-256 geprueft) in {target}");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"runtime-sources: {source.FileName}: {e.Message}");
                return 1;
            }
            finally
            {
                if (File.Exists(download))
                {
                    File.Delete(download);
                }
            }
        }

        return 0;
    }
}

static class RuntimeSourcesCommand
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--out", "--from");
        if (parsed is not { } p || p.Positional.Count != 0 || !p.Named.TryGetValue("--out", out var outDir))
        {
            Console.Error.WriteLine("Aufruf: runtime-sources --out <Ordner> [--from <Ordner>]");
            return 2;
        }

        return RuntimeSources.Fetch(RuntimeSources.All, Path.GetFullPath(outDir), p.Named.GetValueOrDefault("--from") is { } from ? Path.GetFullPath(from) : null);
    }
}

static class RuntimeFetch
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--out");
        if (parsed is not { } p || p.Positional.Count != 0)
        {
            Console.Error.WriteLine("Aufruf: runtime-fetch [--out <Datei>]");
            return 2;
        }

        if (Pinned.PinProblem(AppImageRuntime.Sha256, "AppImageRuntime.Sha256") is { } pinProblem)
        {
            Console.Error.WriteLine($"runtime-fetch: {pinProblem}");
            return 1;
        }

        var target = Path.GetFullPath(p.Named.GetValueOrDefault("--out") ?? AppImageRuntime.DefaultPath());
        if (File.Exists(target))
        {
            if (Pinned.VerifyFile(target, AppImageRuntime.Size, AppImageRuntime.Sha256, "AppImageRuntime.Sha256") is { } existing)
            {
                Console.Error.WriteLine($"runtime-fetch: vorhandene Datei {target} passt nicht ({existing}); nichts geladen, Datei bitte pruefen und loeschen");
                return 1;
            }

            Console.WriteLine($"runtime-fetch: schon vorhanden und geprueft: {target}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var download = target + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            Console.WriteLine($"runtime-fetch: lade {AppImageRuntime.Url}");
            Pinned.Fetch(AppImageRuntime.Url, download);
            if (Pinned.VerifyFile(download, AppImageRuntime.Size, AppImageRuntime.Sha256, "AppImageRuntime.Sha256") is { } problem)
            {
                Console.Error.WriteLine($"runtime-fetch: Download abgelehnt: {problem}");
                return 1;
            }

            File.Move(download, target);
            Console.WriteLine($"runtime-fetch: type2-Runtime {AppImageRuntime.Version} (SHA-256 geprueft) in {target}");
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"runtime-fetch: {e.Message}");
            return 1;
        }
        finally
        {
            File.Delete(download);
        }
    }
}

static partial class AppImage
{
    private const int Exec = 0x1ED;  // 0755
    private const int Plain = 0x1A4; // 0644
    private const string LibDir = "usr/lib/pagewing";
    private const string RootIconSize = "256x256";

    // Fixed time of everything in the image (SOURCE_DATE_EPOCH for mksquashfs), the same as ZIP and .deb.
    public static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // exec "$APPDIR/<path>" ... -> <path>
    [GeneratedRegex(@"^\s*exec\s+""\$\{?APPDIR\}?/(?<p>[^""$]+)""", RegexOptions.Multiline)]
    private static partial Regex ExecLine();

    public static string FileName(string version) => $"Pagewing-{version}-x86_64.AppImage";

    // The program that AppRun starts, relative to the AppDir; null when AppRun has no exec "$APPDIR/..." line.
    public static string? AppRunTarget(string appRun) => ExecLine().Match(appRun) is { Success: true } m ? m.Groups["p"].Value : null;

    // The .desktop template with the one Exec line replaced: inside the image the program is found through AppRun.
    public static string Desktop(string template)
    {
        var lines = template.Split('\n');
        if (lines.Count(l => l.StartsWith("Exec=", StringComparison.Ordinal)) != 1)
        {
            throw new IOException("die .desktop-Vorlage braucht genau eine Exec-Zeile");
        }

        return string.Join('\n', lines.Select(l => l.StartsWith("Exec=", StringComparison.Ordinal) ? "Exec=Pagewing %F" : l));
    }

    // The AppDir: AppRun, pagewing.desktop, pagewing.png and .DirIcon in the root, the layout in usr/lib/pagewing,
    // the hicolor icons in usr/share/icons (LinuxDefaultAppService copies them from $APPDIR). Throws IOException when an input is missing.
    public static List<DebEntry> Plan(string layout, string repo)
    {
        foreach (var required in new[] { "Pagewing", "libpdfium.so", "LICENSE", "THIRD-PARTY-NOTICES.txt", "cli/pagewing", "cli/libpdfium.so" })
        {
            if (!File.Exists(Path.Combine(layout, required)))
            {
                throw new IOException($"Layout enthaelt {required} nicht: {layout}");
            }
        }

        var entries = new List<DebEntry>();
        foreach (var file in Directory.EnumerateFiles(layout, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(layout, file).Replace('\\', '/');
            entries.Add(new DebEntry($"{LibDir}/{relative}", DebEntryKind.File, relative is "Pagewing" or "cli/pagewing" ? Exec : Plain, file));
        }

        var icons = Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor");
        var rootIcon = Path.Combine(icons, RootIconSize, "apps", "pagewing.png");
        if (!File.Exists(rootIcon))
        {
            throw new IOException($"Symbol fehlt: {rootIcon}");
        }

        foreach (var icon in Directory.EnumerateDirectories(icons))
        {
            var png = Path.Combine(icon, "apps", "pagewing.png");
            if (File.Exists(png))
            {
                entries.Add(new DebEntry($"usr/share/icons/hicolor/{Path.GetFileName(icon)}/apps/pagewing.png", DebEntryKind.File, Plain, png));
            }
        }

        var appRun = Path.Combine(repo, "packaging", "linux", "AppRun");
        var desktop = Path.Combine(repo, "packaging", "linux", "pagewing.desktop");
        foreach (var file in new[] { appRun, desktop })
        {
            if (!File.Exists(file))
            {
                throw new IOException($"Datei fehlt: {file}");
            }
        }

        entries.Add(new DebEntry("AppRun", DebEntryKind.File, Exec, appRun));
        entries.Add(new DebEntry("pagewing.desktop", DebEntryKind.File, Plain, null, null, Desktop(File.ReadAllText(desktop))));
        entries.Add(new DebEntry("pagewing.png", DebEntryKind.File, Plain, rootIcon));
        entries.Add(new DebEntry(".DirIcon", DebEntryKind.Symlink, 0x1FF, null, "pagewing.png"));
        return Tree.WithParents(entries);
    }

    // Stages the AppDir, builds the SquashFS image and writes runtime + image to output (0755). Returns 0 or 1 (message written).
    // The runtime must match pin before anything is built; on any failure no output file remains.
    public static int Build(string layout, string repo, string runtime, (long Size, string Sha256) pin, string output)
    {
        if (Pinned.VerifyFile(runtime, pin.Size, pin.Sha256, "AppImageRuntime.Sha256") is { } problem)
        {
            Console.Error.WriteLine($"appimage: Runtime abgelehnt ({runtime}): {problem}");
            return 1;
        }

        var work = Path.Combine(Path.GetTempPath(), "pagewing-appimage-" + Guid.NewGuid().ToString("N"));
        var fullOutput = Path.GetFullPath(output);
        var partial = fullOutput + ".partial";
        try
        {
            if (!Directory.Exists(layout))
            {
                throw new IOException($"Layout-Ordner fehlt: {layout}");
            }

            var entries = Plan(layout, repo);
            var appDir = Path.Combine(work, "AppDir");
            Tree.Write(entries, appDir, FixedTime);
            var image = Path.Combine(work, "image.squashfs");

            // SOURCE_DATE_EPOCH (squashfs-tools 4.4 and later) fixes the creation time and clamps every file time to it;
            // -all-time exists only from 4.6 on and must not be combined with it. The runtime reads zstd (it links libzstd).
            var env = new Dictionary<string, string> { ["SOURCE_DATE_EPOCH"] = FixedTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
            var code = Tool.Run("mksquashfs", [appDir, image, "-root-owned", "-noappend", "-comp", "zstd", "-no-xattrs", "-no-progress", "-quiet"], out var text, env: env);
            if (code != 0)
            {
                Console.Error.Write(text);
                Console.Error.WriteLine(code == -1
                    ? "appimage: mksquashfs fehlt (sudo apt-get install -y squashfs-tools)"
                    : $"appimage: mksquashfs endete mit Code {code}");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            File.Delete(fullOutput);
            using (var target = File.Create(partial))
            {
                using (var source = File.OpenRead(runtime))
                {
                    source.CopyTo(target);
                }

                using (var source = File.OpenRead(image))
                {
                    source.CopyTo(target);
                }
            }

            File.SetUnixFileMode(partial, (UnixFileMode)Exec);
            File.Move(partial, fullOutput);
            Console.WriteLine($"appimage: {entries.Count(e => e.Kind == DebEntryKind.File)} Dateien in {fullOutput}");
            return 0;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"appimage: {e.Message}");
            return 1;
        }
        finally
        {
            File.Delete(partial);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }
        }
    }

    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--layout", "--version", "--runtime", "--out", "--repo");
        if (parsed is not { } p || p.Positional.Count != 0 ||
            !p.Named.TryGetValue("--layout", out var layout) || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--out", out var output))
        {
            Console.Error.WriteLine("Aufruf: appimage --layout <Ordner> --version <x.y.z> --out <Pagewing-<Version>-x86_64.AppImage> [--runtime <Datei>] [--repo <Wurzel>]");
            return 2;
        }

        if (Deb.DebVersion(version) is null)
        {
            Console.Error.WriteLine($"appimage: ungueltige Version (erwartet x.y.z oder x.y.z-vorabversion): {version}");
            return 2;
        }

        if (Path.GetFileName(output) != FileName(version))
        {
            Console.Error.WriteLine($"appimage: die Ausgabedatei muss {FileName(version)} heissen: {output}");
            return 2;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("appimage: nur unter Linux (mksquashfs)");
            return 1;
        }

        var runtime = p.Named.GetValueOrDefault("--runtime") ?? AppImageRuntime.DefaultPath();
        return Build(layout, Path.GetFullPath(p.Named.GetValueOrDefault("--repo", ".")), runtime, AppImageRuntime.Pin, output);
    }
}

static partial class AppImageSmoke
{
    public static int Run(string[] args)
    {
        var parsed = Options.Parse(args, "--repo");
        if (parsed is not { } p || p.Positional.Count != 1)
        {
            Console.Error.WriteLine("Aufruf: appimage-smoke <Pagewing-<Version>-x86_64.AppImage> [--repo <Wurzel>]");
            return 2;
        }

        var source = Path.GetFullPath(p.Positional[0]);
        var repo = Path.GetFullPath(p.Named.GetValueOrDefault("--repo", "."));
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"appimage-smoke: Datei fehlt: {source}");
            return 1;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("appimage-smoke: nur unter Linux");
            return 1;
        }

        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok    " : "FEHLER")} {what}");
            if (!ok)
            {
                failures.Add(what);
            }
        }

        // (1) The image starts with exactly the pinned static runtime (no libfuse2 needed on the host).
        using (var stream = File.OpenRead(source))
        {
            var prefix = new byte[Math.Min(stream.Length, AppImageRuntime.Size)];
            stream.ReadExactly(prefix);
            var problem = Pinned.VerifyDownload(new MemoryStream(prefix), AppImageRuntime.Size, AppImageRuntime.Sha256, "AppImageRuntime.Sha256");
            Check(problem is null, $"beginnt mit der festgehaltenen type2-Runtime {AppImageRuntime.Version}{(problem is null ? string.Empty : $" ({problem})")}");
        }

        if (failures.Count > 0)
        {
            return Finish(failures); // never execute an unknown runtime
        }

        Check((File.GetUnixFileMode(source) & UnixFileMode.UserExecute) != 0, "ausfuehrbar");
        if (Tool.Run("objdump", ["-p", source], out var headers) != -1)
        {
            Check(ElfRules.Needed(headers).Count == 0, "Runtime ist statisch gelinkt (keine NEEDED-Bibliothek, kein libfuse2)");
        }

        // (2) In a folder with a space: --appimage-offset, --appimage-extract and the CLI from the extracted tree.
        var work = Path.Combine(Path.GetTempPath(), "pagewing-appimage-smoke-" + Guid.NewGuid().ToString("N"), "Pagewing Test");
        try
        {
            Directory.CreateDirectory(work);
            var appImage = Path.Combine(work, Path.GetFileName(source));
            File.Copy(source, appImage);
            File.SetUnixFileMode(appImage, (UnixFileMode)0x1ED);

            var offsetCode = Tool.Run(appImage, ["--appimage-offset"], out var offset);
            Check(offsetCode == 0 && offset.Trim() == AppImageRuntime.Size.ToString(CultureInfo.InvariantCulture),
                $"--appimage-offset ist die Groesse der Runtime ({offset.Trim()})");

            var extractCode = Tool.Run(appImage, ["--appimage-extract"], out var extractLog, workingDirectory: work);
            var root = Path.Combine(work, "squashfs-root");
            Check(extractCode == 0 && Directory.Exists(root), $"--appimage-extract (Code {extractCode})");
            if (!Directory.Exists(root))
            {
                Console.Write(extractLog);
                return Finish(failures);
            }

            var appRun = Path.Combine(root, "AppRun");
            var target = File.Exists(appRun) ? AppImage.AppRunTarget(File.ReadAllText(appRun)) : null;
            Check(target is not null && File.Exists(Path.Combine(root, target)) && (File.GetUnixFileMode(Path.Combine(root, target)) & UnixFileMode.UserExecute) != 0,
                $"AppRun startet ein vorhandenes Programm ({target})");
            var desktop = Path.Combine(root, "pagewing.desktop");
            Check(File.Exists(desktop) && File.ReadLines(desktop).Contains("Exec=Pagewing %F"), "pagewing.desktop mit Exec=Pagewing %F");
            Check(new FileInfo(Path.Combine(root, ".DirIcon")).LinkTarget == "pagewing.png" && File.Exists(Path.Combine(root, "pagewing.png")), ".DirIcon -> pagewing.png");
            Check(File.Exists(Path.Combine(root, "usr", "share", "icons", "hicolor", "256x256", "apps", "pagewing.png")), "Symbole unter usr/share/icons/hicolor");
            Check(File.Exists(Path.Combine(root, "usr", "lib", "pagewing", "THIRD-PARTY-NOTICES.txt")), "THIRD-PARTY-NOTICES.txt");

            var pdf = Path.Combine(repo, "testdata", "pdfs", "text-basic.pdf");
            var cli = Path.Combine(root, "usr", "lib", "pagewing", "cli", "pagewing");
            var cliCode = Tool.Run(cli, ["info", pdf], out var info);
            Check(cliCode == 0 && info.Contains("Pages: 2", StringComparison.Ordinal), $"usr/lib/pagewing/cli/pagewing info text-basic.pdf (Code {cliCode})");
            if (cliCode != 0)
            {
                Console.Write(info);
            }

            Directory.Delete(root, true);

            // (3) The app through AppRun: once without FUSE (APPIMAGE_EXTRACT_AND_RUN=1), once as a user starts it. In the second
            // run the runtime mounts with FUSE. Without /dev/fuse and fusermount (a container) it does NOT extract by itself: it
            // says so and exits (measured in the release dry run 39914298456), which is the expected outcome there.
            var display = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (!display && Tool.Run("xvfb-run", ["--help"], out _) == -1)
            {
                Console.WriteLine("Hinweis: weder Anzeige noch xvfb-run, Start der App uebersprungen (sudo apt-get install -y xvfb)");
                return Finish(failures);
            }

            foreach (var extract in new[] { true, false })
            {
                var env = extract ? new Dictionary<string, string> { ["APPIMAGE_EXTRACT_AND_RUN"] = "1" } : new Dictionary<string, string>();
                var command = display ? appImage : "xvfb-run";
                string[] arguments = display ? ["--measure-startup", pdf] : ["-a", appImage, "--measure-startup", pdf];
                var code = Tool.Run(command, arguments, out var output, workingDirectory: work, env: env);
                var fuse = File.Exists("/dev/fuse") && (Tool.Run("fusermount3", ["--version"], out _) != -1 || Tool.Run("fusermount", ["--version"], out _) != -1);
                var label = extract ? "ohne FUSE (APPIMAGE_EXTRACT_AND_RUN=1)"
                    : fuse ? "ohne Umgebungsvariable (ueber FUSE)"
                        : "ohne Umgebungsvariable (kein FUSE auf dem Rechner: die Runtime meldet es und startet nicht)";
                var ok = extract ? code == 0 && HasFirstSharp(output) : SecondRunIsAcceptable(fuse, code, output);
                Check(ok, $"App startet {label} und zeichnet text-basic.pdf (first-sharp, Code {code})");
                if (code != 0)
                {
                    Console.Write(output);
                }
            }

            return Finish(failures);
        }
        finally
        {
            var parent = Path.GetDirectoryName(work)!;
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    private static bool HasFirstSharp(string output) => output.Split('\n').Any(l => l.Trim() == "first-sharp");

    // The start as a user does it. With FUSE it must work. Without FUSE the static runtime refuses with a message about
    // fusermount (that is the expected result in a container); it counts as fine, any other failure does not.
    public static bool SecondRunIsAcceptable(bool fuseAvailable, int code, string output) =>
        code == 0 ? HasFirstSharp(output) : !fuseAvailable && output.Contains("fusermount", StringComparison.Ordinal);

    private static int Finish(List<string> failures)
    {
        Console.WriteLine(failures.Count == 0 ? "appimage-smoke: bestanden." : $"appimage-smoke: {failures.Count} Pruefung(en) fehlgeschlagen.");
        return failures.Count == 0 ? 0 : 1;
    }
}

// The tag check of .github/workflows/release.yml: the same rule once in PowerShell (-cnotmatch) and once in bash (=~).
static class TagRules
{
    // PowerShell: if ($env:TAG -cnotmatch '<pattern>') { throw ... }
    public static string? PowerShellPattern(string workflow)
    {
        var m = Regex.Match(workflow, @"-cnotmatch '(?<p>[^']+)'");
        return m.Success ? m.Groups["p"].Value : null;
    }

    // bash: [[ "$TAG" =~ <pattern> ]]
    public static string? BashPattern(string workflow)
    {
        var m = Regex.Match(workflow, @"\[\[ ""\$TAG"" =~ (?<p>\S+) \]\]");
        return m.Success ? m.Groups["p"].Value : null;
    }

    // Case-sensitive, culture-independent; the pattern is anchored itself.
    public static bool Accepts(string pattern, string tag) =>
        Regex.IsMatch(tag, pattern, RegexOptions.CultureInvariant);
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

    private static string CxxSymbols(params string[] versions) =>
        Header + string.Concat(versions.Select((v, i) =>
            $"0000000000000000      DF *UND*\t0000000000000000 (GLIBCXX_{v}) _ZSt{i}\n"));

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
        Expect("libfontconfig.so.1 (Skia) ist erlaubt", ElfRules.Violations(Symbols("2.2.5"), Needs("libc.so.6", "libfontconfig.so.1"), max, maxText).Count, 0);
        Expect("andere Systembibliothek (libX11) faellt weiter durch", ElfRules.Violations(Symbols("2.2.5"), Needs("libc.so.6", "libX11.so.6"), max, maxText).Count, 1);
        Expect("alle erlaubten NEEDED bestehen", ElfRules.Violations(Symbols("2.2.5"), Needs(ElfRules.AllowedNeeded), max, maxText).Count, 0);
        Expect("SONAME ist kein NEEDED", ElfRules.Needed(Needs("libc.so.6")).Count, 1);
        Expect("definiertes Symbol zaehlt nicht", ElfRules.HighestGlibc("0000 g DF .text 000b (GLIBC_2.99) x\n", out _) ?? "none", "none");
        Expect("Version des hoechsten Symbols", ElfRules.HighestGlibc(Symbols("2.2.5", "2.34", "2.17"), out _) ?? "none", "2.34");
        Expect("Vergleich 2.35 = 2.35.0", ElfRules.Compare([2, 35], [2, 35, 0]), 0);

        Expect("GLIBCXX 3.4.30 besteht (Grenze)", ElfRules.Violations(CxxSymbols("3.4.29", "3.4.30"), libc, max, maxText).Count, 0);
        Expect("GLIBCXX 3.4.31 faellt durch", ElfRules.Violations(CxxSymbols("3.4.19", "3.4.31"), libc, max, maxText).Count, 1);
        Expect("GLIBCXX 3.4.9 ist kleiner als 3.4.30 (Zahlentupel, nicht Text)", ElfRules.Violations(CxxSymbols("3.4.9"), libc, max, maxText).Count, 0);
        Expect("GLIBCXX 3.4.100 ist groesser als 3.4.30", ElfRules.Violations(CxxSymbols("3.4.100"), libc, max, maxText).Count, 1);
        Expect("GLIBCXX: --max-glibcxx hebt die Grenze", ElfRules.Violations(CxxSymbols("3.4.31"), libc, max, maxText, "3.4.31").Count, 0);
        Expect("GLIBCXX: Meldung nennt Symbol und Grenze", ElfRules.Violations(CxxSymbols("3.4.31"), libc, max, maxText).SingleOrDefault() ?? "none",
            "verlangt GLIBCXX_3.4.31 (erlaubt hoechstens GLIBCXX_3.4.30)");
        Expect("GLIBCXX und GLIBC melden getrennt", ElfRules.Violations(CxxSymbols("3.4.31") + Symbols("2.38"), libc, max, maxText).Count, 2);
        Expect("ohne GLIBCXX-Symbole besteht", ElfRules.Violations(Symbols("2.2.5"), libc, max, maxText).Count, 0);
        Expect("GLIBCXX zaehlt nicht als GLIBC", ElfRules.HighestGlibc(CxxSymbols("3.4.31"), out _) ?? "none", "none");
        Expect("hoechstes GLIBCXX-Symbol", ElfRules.HighestGlibcxx(CxxSymbols("3.4.9", "3.4.30", "3.4.19"), out _) ?? "none", "3.4.30");
        Expect("check-binary mit ungueltigem --max-glibcxx liefert 2", Cli.Run(["check-binary", "x", "--max-glibcxx", "abc"]), 2);

        PackagingCommands();
        InstallerParts();
        DebParts();
        AppImageParts();
        TagRuleParts();
        RuntimeSourceParts();
        BuildTelemetryParts();
        WorkflowYamlParts();
        LinuxContainerParts();
        UpdateReleaseParts();
        ManifestJobParts();
        PublicRepoWorkflowParts();
        UpdateTestBuildGuardParts();
        RealElf();
        Expect("unbekannter Befehl liefert 2", Cli.Run(["gibt-es-nicht"]), 2);
        Expect("check-binary ohne Datei liefert 2", Cli.Run(["check-binary"]), 2);

        Console.WriteLine(s_failures == 0
            ? $"selftest: {s_checks} Pruefungen bestanden."
            : $"selftest: {s_failures} von {s_checks} Pruefungen fehlgeschlagen.");
        return s_failures == 0 ? 0 : 1;
    }

    // The tag rule exists twice in release.yml (PowerShell and bash). Both must be the same and must accept
    // exactly SemVer tags, and a dry run must be able to build a branch without uploading it.
    private static void TagRuleParts()
    {
        const string workflow = ".github/workflows/release.yml";
        Expect($"tag: {workflow} vorhanden", File.Exists(workflow), true);
        if (!File.Exists(workflow))
        {
            return;
        }

        var yml = File.ReadAllText(workflow);
        var powerShell = TagRules.PowerShellPattern(yml);
        var bash = TagRules.BashPattern(yml);
        Expect("tag: PowerShell-Regel gefunden", powerShell is not null, true);
        Expect("tag: bash-Regel gefunden", bash is not null, true);
        if (powerShell is null || bash is null)
        {
            return;
        }

        Expect("tag: beide Regeln sind gleich", powerShell, bash);
        foreach (var tag in new[] { "v0.1.0", "v10.20.30", "v0.1.0-rc.1", "v1.0.0-beta" })
        {
            Expect($"tag: {tag} wird angenommen", TagRules.Accepts(bash, tag), true);
        }

        foreach (var tag in new[] { "V0.1.0", "v0.1", "v0.1.0.", "0.1.0", "v0.1.0-", "v0.1.0 ", " v0.1.0", "v0.1.0-rc 1", "" })
        {
            Expect($"tag: '{tag}' wird abgelehnt", TagRules.Accepts(bash, tag), false);
        }

        Expect("tag: alle bash-Regeln sind gleich (Linux- und Manifest-Job)",
            Regex.Matches(yml, @"\[\[ ""\$TAG"" =~ (?<p>\S+) \]\]").Select(m => m.Groups["p"].Value).Distinct(StringComparer.Ordinal).Count(), 1);
        Expect("tag: beide Checkouts der Bau-Jobs bauen auch einen Branch (inputs.ref)", Regex.Count(yml, @"ref: \$\{\{ inputs\.ref \|\| "), 2);
        Expect("tag: alle vier Jobs verweigern das Hochladen eines Branchs", Regex.Count(yml, "name: Refuse to upload a branch build"), 4);
    }

    // The AppImage contains libfuse (LGPL-2.1): its source and the runtime's build scripts are attached to every release.
    private static void RuntimeSourceParts()
    {
        foreach (var source in RuntimeSources.All)
        {
            Expect($"sources: {source.FileName} ist festgehalten", Pinned.PinProblem(source.Sha256, source.FileName), null);
            Expect($"sources: {source.FileName} hat eine Groesse", source.Size > 0, true);
            Expect($"sources: {source.FileName} kommt von einer festen Version", source.Url.StartsWith("https://github.com/", StringComparison.Ordinal), true);
        }

        Expect("sources: libfuse 3.15.0 (wie in third-party/appimage-runtime/README.md)", RuntimeSources.All.Any(x => x.FileName == "fuse-3.15.0.tar.xz"), true);
        Expect("sources: type2-runtime in der Version der Runtime", RuntimeSources.All.Any(x => x.FileName == $"type2-runtime-{AppImageRuntime.Version}.tar.gz"), true);

        const string container = "packaging/linux/container-build.sh";
        const string workflow = ".github/workflows/release.yml";
        Expect($"sources: {container} vorhanden", File.Exists(container), true);
        Expect($"sources: {workflow} vorhanden", File.Exists(workflow), true);
        if (File.Exists(container) && File.Exists(workflow))
        {
            var script = File.ReadAllText(container);
            var yml = File.ReadAllText(workflow);
            Expect("sources: container-build.sh ruft runtime-sources", script.Contains("runtime-sources --out out/release", StringComparison.Ordinal), true);
            foreach (var source in RuntimeSources.All)
            {
                Expect($"sources: {source.FileName} steht in SHA256SUMS-linux.txt", script.Contains($"out/release/{source.FileName}", StringComparison.Ordinal), true);
                Expect($"sources: {source.FileName} wird hochgeladen", yml.Contains($"out/release/{source.FileName}", StringComparison.Ordinal), true);
            }
        }

        var dir = Path.Combine(Path.GetTempPath(), "pagewing-sources-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var from = Path.Combine(dir, "from");
            Directory.CreateDirectory(from);
            var good = new RuntimeSource("a.tar.gz", "https://example.invalid/a.tar.gz", 5, Convert.ToHexStringLower(SHA256.HashData("hello"u8)));
            var other = new RuntimeSource("b.tar.xz", "https://example.invalid/b.tar.xz", 3, Convert.ToHexStringLower(SHA256.HashData("abc"u8)));
            File.WriteAllText(Path.Combine(from, "a.tar.gz"), "hello");
            File.WriteAllText(Path.Combine(from, "b.tar.xz"), "abc");

            var outDir = Path.Combine(dir, "out");
            Expect("sources: richtige Dateien werden uebernommen", RuntimeSources.Fetch([good, other], outDir, from), 0);
            Expect("sources: Datei liegt im Ausgabeordner", File.ReadAllText(Path.Combine(outDir, "a.tar.gz")), "hello");
            Expect("sources: kein Rest des Downloads", Directory.GetFiles(outDir).Length, 2);
            Expect("sources: zweiter Lauf erkennt vorhandene, geprueftes Dateien", RuntimeSources.Fetch([good, other], outDir, from), 0);

            File.WriteAllText(Path.Combine(from, "b.tar.xz"), "xyz");
            var refused = Path.Combine(dir, "refused");
            Expect("sources: falsche SHA-256 liefert 1", RuntimeSources.Fetch([good, other], refused, from), 1);
            Expect("sources: falsche Datei wird nicht abgelegt", File.Exists(Path.Combine(refused, "b.tar.xz")), false);
            Expect("sources: kein Download-Rest nach Ablehnung", Directory.GetFiles(refused).All(f => !f.Contains(".download-", StringComparison.Ordinal)), true);

            File.WriteAllText(Path.Combine(outDir, "a.tar.gz"), "tampered");
            Expect("sources: vorhandene falsche Datei wird abgelehnt", RuntimeSources.Fetch([good], outDir, from), 1);
            Expect("sources: falsche Datei bleibt unangetastet", File.ReadAllText(Path.Combine(outDir, "a.tar.gz")), "tampered");

            var unpinned = good with { Sha256 = "nicht festgehalten" };
            Expect("sources: nicht festgehaltene Pruefsumme liefert 1", RuntimeSources.Fetch([unpinned], Path.Combine(dir, "u"), from), 1);
            Expect("sources: fehlende Quelle liefert 1", RuntimeSources.Fetch([good with { FileName = "nope.tar.gz" }], Path.Combine(dir, "m"), from), 1);
            Expect("runtime-sources: ohne --out liefert 2", Cli.Run(["runtime-sources"]), 2);
            Expect("runtime-sources: unbekannte Option liefert 2", Cli.Run(["runtime-sources", "--out", dir, "--gibt-es-nicht", "x"]), 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Avalonia.BuildServices collects usage data while compiling unless AVALONIA_TELEMETRY_OPTOUT=1 is set
    // (Docs/DECISIONS.md, entry "Avalonia-Build-Telemetrie abschalten"). Every place that compiles the app must set it.
    private static void BuildTelemetryParts()
    {
        foreach (var file in new[]
        {
            ".github/workflows/ci.yml", ".github/workflows/release.yml", ".github/workflows/hardware-measure.yml",
            "packaging/linux/container-build.sh",
        })
        {
            Expect($"telemetrie: {file} vorhanden", File.Exists(file), true);
            if (File.Exists(file))
            {
                Expect($"telemetrie: {file} setzt AVALONIA_TELEMETRY_OPTOUT=1",
                    Regex.IsMatch(File.ReadAllText(file), @"AVALONIA_TELEMETRY_OPTOUT(: '1'|=1)"), true);
            }
        }
    }

    // There is no YAML parser in the base library. The one mistake that broke release.yml once: an unquoted one-line
    // "run: ..." value that contains ": " (YAML reads the second colon as a mapping and the whole file is invalid).
    private static void WorkflowYamlParts()
    {
        foreach (var file in Directory.GetFiles(".github/workflows", "*.yml"))
        {
            var bad = File.ReadLines(file)
                .Select((line, i) => (line, number: i + 1))
                .Where(x => Regex.IsMatch(x.line, @"^\s*(- )?(run|name|if): [^|>'""\s#{\[&*!%@`-][^#]*: "))
                .Select(x => $"{x.number}")
                .ToList();
            Expect($"yaml: {file} hat keinen einzeiligen Wert mit ': ' ohne Anfuehrungszeichen (Zeilen {string.Join(',', bad)})", bad.Count, 0);
        }
    }

    // The Linux job builds as root in ubuntu:22.04 (packaging/linux/container-build.sh). Two things must hold even when
    // the run is cancelled or times out: the workspace goes back to the runner user (else the next checkout of any
    // workflow fails with "Permission denied"), and the tool's own self-test runs on the host, not in the container
    // (jammy has squashfs-tools 4.5; the reproducibility checks of the self-test are verified with 4.6 only).
    private static void LinuxContainerParts()
    {
        const string workflow = ".github/workflows/release.yml";
        const string container = "packaging/linux/container-build.sh";
        if (!File.Exists(workflow) || !File.Exists(container))
        {
            Expect("container: release.yml und container-build.sh vorhanden", false, true);
            return;
        }

        var yml = File.ReadAllText(workflow);
        Expect("container: Arbeitsordner geht auch nach Abbruch zurueck (if: always() mit chown)",
            Regex.IsMatch(yml, @"name: Give the workspace back to the runner user \(Linux\)\s+if: always\(\)\s+run: \|[\s\S]*?chown -R"), true);
        Expect("container: Selbsttest des Pakettools laeuft in allen drei Jobs auf dem Host", Regex.Count(yml, "name: Self-test of the packaging tool"), 3);
        Expect("container: container-build.sh setzt NUGET_PACKAGES (dotnet und licenses.cs suchen dieselbe Ablage)",
            Regex.IsMatch(File.ReadAllText(container), @"(?m)^export .*\bNUGET_PACKAGES="), true);
        Expect("container: container-build.sh hat keine Pipe nach head (pipefail macht daraus Exit 141)",
            Regex.IsMatch(File.ReadAllText(container), @"\|\s*head\b"), false);
        Expect("container: container-build.sh startet den Selbsttest des Pakettools nicht",
            File.ReadAllText(container).Contains("package.cs -- selftest", StringComparison.Ordinal), false);
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

            var sumsHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sums)));
            Expect("sums-verify: passende Summe liefert 0", Cli.Run(["sums-verify", "--file", sums, "--sha256", sumsHash]), 0);
            Expect("sums-verify: Grossbuchstaben liefern 0", Cli.Run(["sums-verify", "--file", sums, "--sha256", sumsHash.ToUpperInvariant()]), 0);
            var manipulated = (sumsHash[0] == '0' ? "1" : "0") + sumsHash[1..];
            Expect("sums-verify: manipulierte Summe liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", manipulated]), 1);
            Expect("sums-verify: zu kurze Summe liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", sumsHash[..63]]), 1);
            Expect("sums-verify: zu lange Summe liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", sumsHash + "0"]), 1);
            Expect("sums-verify: keine Hexzahl liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", "g" + sumsHash[1..]]), 1);
            Expect("sums-verify: leere Summe liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", string.Empty]), 1);
            Expect("sums-verify: Leerzeichen allein liefert 1", Cli.Run(["sums-verify", "--file", sums, "--sha256", "  "]), 1);
            Expect("sums-verify: fehlende Datei liefert 1", Cli.Run(["sums-verify", "--file", Path.Combine(dir, "nope"), "--sha256", sumsHash]), 1);
            Expect("sums-verify: ohne --sha256 liefert 2", Cli.Run(["sums-verify", "--file", sums]), 2);
            Expect("sums-verify: ohne --file liefert 2", Cli.Run(["sums-verify", "--sha256", sumsHash]), 2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Everything of the .deb that needs no dpkg-deb: version mapping, control, file list with modes, copyright.
    private static void DebParts()
    {
        Expect("deb: Version 0.1.0", Deb.DebVersion("0.1.0"), "0.1.0");
        Expect("deb: Vorabversion x.y.z-rc1 wird x.y.z~rc1", Deb.DebVersion("1.2.3-rc1"), "1.2.3~rc1");
        Expect("deb: Vorabversion mit Punkt", Deb.DebVersion("1.0.0-beta.2"), "1.0.0~beta.2");
        Expect("deb: Version ohne Patch ist ungueltig", Deb.DebVersion("1.2"), null);
        Expect("deb: Version mit fuehrender Null ist ungueltig", Deb.DebVersion("01.2.3"), null);
        Expect("deb: Version mit Leerzeichen ist ungueltig", Deb.DebVersion("1.2.3 x"), null);
        Expect("deb: leere Vorabkennung ist ungueltig", Deb.DebVersion("1.2.3-"), null);
        Expect("deb: Dateiname", Deb.FileName("1.2.3~rc1"), "pagewing_1.2.3~rc1_amd64.deb");

        var template = File.ReadAllText(Path.Combine("packaging", "linux", "control.template"));
        var control = Deb.Control(template, "0.1.0", 1234, "Max Muster <max@example.org>");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in control.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.Length > 0 && line[0] != ' ' && colon > 0)
            {
                fields[line[..colon]] = line[(colon + 1)..].Trim();
            }
        }

        Expect("deb: Package", fields.GetValueOrDefault("Package"), "pagewing");
        Expect("deb: Version", fields.GetValueOrDefault("Version"), "0.1.0");
        Expect("deb: Architecture", fields.GetValueOrDefault("Architecture"), "amd64");
        Expect("deb: Maintainer", fields.GetValueOrDefault("Maintainer"), "Max Muster <max@example.org>");
        Expect("deb: Installed-Size", fields.GetValueOrDefault("Installed-Size"), "1234");
        Expect("deb: Section", fields.GetValueOrDefault("Section"), "graphics");
        Expect("deb: Priority", fields.GetValueOrDefault("Priority"), "optional");
        Expect("deb: Depends", fields.GetValueOrDefault("Depends"), "libc6 (>= 2.35), libfontconfig1, libx11-6, libice6, libsm6, libssl3 | libssl3t64");
        Expect("deb: Recommends", fields.GetValueOrDefault("Recommends"), "libcups2, xdg-utils");
        Expect("deb: Homepage", fields.GetValueOrDefault("Homepage"), "https://github.com/benjo-code/Pagewing");
        Expect("deb: Description (Kurztext)", fields.GetValueOrDefault("Description")?.Length > 0, true);
        Expect("deb: kein Platzhalter bleibt", new[] { "@VERSION@", "@MAINTAINER@", "@INSTALLED_SIZE@" }.Any(m => control.Contains(m, StringComparison.Ordinal)), false);
        Expect("deb: control endet mit LF", control.EndsWith('\n'), true);
        Expect("deb: Installed-Size 0 KiB wird 1", Deb.InstalledSizeKiB([0L]), 1L);
        Expect("deb: Installed-Size rundet je Datei auf", Deb.InstalledSizeKiB([1L, 1024L, 1025L]), 1L + 1L + 2L);
        var lintianReport = "E: pagewing: embedded-library freetype [usr/lib/x.so]\nE: pagewing: no-changelog usr/share/doc/pagewing/changelog.gz (native package)\nW: pagewing: no-manual-page [usr/bin/pagewing]\n";
        Expect("deb: Lintian, bekannte Fehler werden angenommen", DebSmoke.RejectedLintianErrors(lintianReport).Count, 0);
        Expect("deb: Lintian, anderer Fehler faellt durch", DebSmoke.RejectedLintianErrors(lintianReport + "E: pagewing: file-in-etc-not-marked-as-conffile etc/x\n").Count, 1);
        Expect("deb: Lintian, Tag mit gleichem Anfang faellt durch", DebSmoke.RejectedLintianErrors("E: pagewing: no-changelog-extra x\n").Count, 1);
        Expect("deb: Maintainer-Platzhalter ist festgehalten", Deb.Maintainer.Contains("noreply", StringComparison.Ordinal), true);

        var copyright = File.ReadAllText(Path.Combine("packaging", "linux", "copyright.template"));
        Expect("deb: copyright Format 1.0", copyright.StartsWith("Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/\n", StringComparison.Ordinal), true);
        Expect("deb: copyright nennt GPL-3+", copyright.Contains("License: GPL-3+", StringComparison.Ordinal), true);
        Expect("deb: copyright verweist auf THIRD-PARTY-NOTICES.txt", copyright.Contains("/usr/lib/pagewing/THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal), true);
        Expect("deb: copyright ohne Tabulatoren", copyright.Contains('\t', StringComparison.Ordinal), false);

        var dir = Path.Combine(Path.GetTempPath(), "pagewing-deb-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = Path.Combine(dir, "layout");
            var repo = Path.Combine(dir, "repo");
            Directory.CreateDirectory(Path.Combine(layout, "cli"));
            Directory.CreateDirectory(Path.Combine(layout, "sub"));
            File.WriteAllText(Path.Combine(layout, "Pagewing"), "app");
            File.WriteAllText(Path.Combine(layout, "libpdfium.so"), "pdfium");
            File.WriteAllText(Path.Combine(layout, "LICENSE"), "license");
            File.WriteAllText(Path.Combine(layout, "THIRD-PARTY-NOTICES.txt"), "notices");
            File.WriteAllText(Path.Combine(layout, "sub", "x.dat"), "x");
            File.WriteAllText(Path.Combine(layout, "cli", "pagewing"), "cli");
            File.WriteAllText(Path.Combine(layout, "cli", "libpdfium.so"), "pdfium");
            foreach (var size in new[] { "16x16", "512x512" })
            {
                var icons = Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor", size, "apps");
                Directory.CreateDirectory(icons);
                File.WriteAllText(Path.Combine(icons, "pagewing.png"), "png" + size);
            }

            Directory.CreateDirectory(Path.Combine(repo, "packaging", "linux"));
            File.WriteAllText(Path.Combine(repo, "packaging", "linux", "pagewing.desktop"), "[Desktop Entry]\n");
            File.WriteAllText(Path.Combine(repo, "packaging", "linux", "copyright.template"), "Format: x\n");

            var entries = Deb.Plan(layout, repo);
            DebEntry? Find(string path) => entries.FirstOrDefault(e => e.Path == path);
            Expect("deb: Plan hat keine doppelten Pfade", entries.Select(e => e.Path).Distinct().Count(), entries.Count);
            Expect("deb: Plan ist sortiert", entries.Select(e => e.Path).SequenceEqual(entries.Select(e => e.Path).Order(StringComparer.Ordinal)), true);
            Expect("deb: Programm 0755", Find("usr/lib/pagewing/Pagewing")?.Mode, 0x1ED);
            Expect("deb: CLI 0755", Find("usr/lib/pagewing/cli/pagewing")?.Mode, 0x1ED);
            Expect("deb: Bibliothek 0644", Find("usr/lib/pagewing/libpdfium.so")?.Mode, 0x1A4);
            Expect("deb: CLI-Bibliothek 0644", Find("usr/lib/pagewing/cli/libpdfium.so")?.Mode, 0x1A4);
            Expect("deb: Unterordner bleiben", Find("usr/lib/pagewing/sub/x.dat")?.Mode, 0x1A4);
            Expect("deb: THIRD-PARTY-NOTICES.txt im Programmordner", Find("usr/lib/pagewing/THIRD-PARTY-NOTICES.txt") is not null, true);
            Expect("deb: Verzeichnis 0755", Find("usr/lib/pagewing/cli")?.Mode, 0x1ED);
            Expect("deb: Verzeichnis usr/share/applications", Find("usr/share/applications")?.Kind, DebEntryKind.Directory);
            Expect("deb: Starter 0644", Find("usr/share/applications/pagewing.desktop")?.Mode, 0x1A4);
            Expect("deb: Symbol 16x16", Find("usr/share/icons/hicolor/16x16/apps/pagewing.png")?.Mode, 0x1A4);
            Expect("deb: Symbol 512x512", Find("usr/share/icons/hicolor/512x512/apps/pagewing.png") is not null, true);
            Expect("deb: copyright 0644", Find("usr/share/doc/pagewing/copyright")?.Mode, 0x1A4);
            var link = Find("usr/bin/pagewing");
            Expect("deb: /usr/bin/pagewing ist ein Symlink", link?.Kind, DebEntryKind.Symlink);
            Expect("deb: Symlink zeigt relativ auf die CLI", link?.LinkTarget, "../lib/pagewing/cli/pagewing");
            Expect("deb: Symlink loest auf die CLI auf", link is null ? null : PosixPath.Resolve("/usr/bin", link.LinkTarget!), "/usr/lib/pagewing/cli/pagewing");
            Expect("PosixPath: relativ mit ..", PosixPath.Resolve("/usr/bin", "../lib/a/./b"), "/usr/lib/a/b");
            Expect("PosixPath: absolutes Ziel", PosixPath.Resolve("/usr/bin", "/etc/x"), "/etc/x");
            Expect("PosixPath: .. ueber die Wurzel hinaus", PosixPath.Resolve("/a", "../../b"), "/b");
            Expect("deb: kein Eintrag ausserhalb usr/", entries.All(e => e.Path.StartsWith("usr", StringComparison.Ordinal)), true);
            Expect("deb: alle Dateien haben eine Quelle", entries.Where(e => e.Kind == DebEntryKind.File).All(e => e.Source is not null && File.Exists(e.Source)), true);
            Expect("deb: Elternverzeichnisse sind vorhanden", entries.Where(e => e.Path.Contains('/', StringComparison.Ordinal))
                .All(e => Find(e.Path[..e.Path.LastIndexOf('/')]) is { Kind: DebEntryKind.Directory }), true);

            if (!OperatingSystem.IsWindows())
            {
                var stage = Path.Combine(dir, "stage");
                Deb.Stage(entries, stage, control);
                Expect("deb: Staging control", File.ReadAllText(Path.Combine(stage, "DEBIAN", "control")), control);
                Expect("deb: Staging Programm 0755", File.GetUnixFileMode(Path.Combine(stage, "usr/lib/pagewing/Pagewing")), (UnixFileMode)0x1ED);
                Expect("deb: Staging Datei 0644", File.GetUnixFileMode(Path.Combine(stage, "usr/lib/pagewing/libpdfium.so")), (UnixFileMode)0x1A4);
                Expect("deb: Staging Verzeichnis 0755", File.GetUnixFileMode(Path.Combine(stage, "usr/lib/pagewing/cli")), (UnixFileMode)0x1ED);
                Expect("deb: Staging DEBIAN 0755", File.GetUnixFileMode(Path.Combine(stage, "DEBIAN")), (UnixFileMode)0x1ED);
                Expect("deb: Staging control 0644", File.GetUnixFileMode(Path.Combine(stage, "DEBIAN", "control")), (UnixFileMode)0x1A4);
                Expect("deb: Staging Symlink", new FileInfo(Path.Combine(stage, "usr/bin/pagewing")).LinkTarget, "../lib/pagewing/cli/pagewing");
                Expect("deb: Staging feste Zeit", File.GetLastWriteTimeUtc(Path.Combine(stage, "usr/lib/pagewing/Pagewing")), Deb.FixedTime.UtcDateTime);
                Expect("deb: Staging feste Zeit der Verzeichnisse", Directory.GetLastWriteTimeUtc(Path.Combine(stage, "usr/lib/pagewing")), Deb.FixedTime.UtcDateTime);
            }

            Expect("deb: falscher Dateiname liefert 2", Cli.Run(["deb", "--layout", layout, "--version", "1.2.3", "--out", Path.Combine(dir, "x.deb"), "--repo", repo]), 2);
            Expect("deb: ungueltige Version liefert 2", Cli.Run(["deb", "--layout", layout, "--version", "1.2", "--out", Path.Combine(dir, "pagewing_1.2_amd64.deb"), "--repo", repo]), 2);
            Expect("deb: fehlendes Layout liefert 1", Cli.Run(["deb", "--layout", Path.Combine(dir, "nope"), "--version", "1.2.3", "--out", Path.Combine(dir, "pagewing_1.2.3_amd64.deb"), "--repo", repo]), 1);
            Expect("deb: Layout ohne cli/pagewing liefert 1", Cli.Run(["deb", "--layout", Path.Combine(repo, "packaging"), "--version", "1.2.3", "--out", Path.Combine(dir, "pagewing_1.2.3_amd64.deb"), "--repo", repo]), 1);
            Expect("deb: ohne Option liefert 2", Cli.Run(["deb"]), 2);
            if (!OperatingSystem.IsWindows() && Exec("dpkg-deb", "--version") == 0)
            {
                File.WriteAllText(Path.Combine(repo, "packaging", "linux", "control.template"), template);
                var deb1 = Path.Combine(dir, "a", "pagewing_1.2.3~rc1_amd64.deb");
                var deb2 = Path.Combine(dir, "b", "pagewing_1.2.3~rc1_amd64.deb");
                Expect("deb: dpkg-deb baut", Cli.Run(["deb", "--layout", layout, "--version", "1.2.3-rc1", "--out", deb1, "--repo", repo]), 0);
                Thread.Sleep(1100);
                File.SetLastWriteTimeUtc(Path.Combine(layout, "Pagewing"), DateTime.UtcNow);
                Expect("deb: zweiter Lauf", Cli.Run(["deb", "--layout", layout, "--version", "1.2.3-rc1", "--out", deb2, "--repo", repo]), 0);
                Expect("deb: reproduzierbar (gleiche SHA-256)", Sums.Line(deb1)[..64], Sums.Line(deb2)[..64]);
                Expect("deb: dpkg-deb -f Version", Deb.Field(deb1, "Version"), "1.2.3~rc1");
                Expect("deb: dpkg-deb -f Maintainer", Deb.Field(deb1, "Maintainer"), Deb.Maintainer);
                Expect("deb: Smoke ohne echte CLI schlaegt fehl (Fake-Programme)", Cli.Run(["deb-smoke", deb1, "--repo", repo]), 1);
            }

            Expect("deb-smoke: ohne Datei liefert 2", Cli.Run(["deb-smoke"]), 2);
            Expect("deb-smoke: fehlende Datei liefert 1", Cli.Run(["deb-smoke", Path.Combine(dir, "nope.deb")]), 1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The AppImage: runtime pin, AppRun, .desktop, the AppDir plan and, with mksquashfs, a reproducible image from a fake runtime.
    private static void AppImageParts()
    {
        const string noFuse = "Error: No suitable fusermount binary found on the $PATH\nfuse: device not found";
        Expect("smoke: mit FUSE und Start ist ok", AppImageSmoke.SecondRunIsAcceptable(true, 0, "first-sharp\n"), true);
        Expect("smoke: mit FUSE und Fehlstart faellt durch", AppImageSmoke.SecondRunIsAcceptable(true, 127, noFuse), false);
        Expect("smoke: ohne FUSE meldet die Runtime es klar und startet nicht (ok)", AppImageSmoke.SecondRunIsAcceptable(false, 127, noFuse), true);
        Expect("smoke: ohne FUSE, aber anderer Absturz faellt durch", AppImageSmoke.SecondRunIsAcceptable(false, 139, "Segmentation fault"), false);
        Expect("smoke: ohne FUSE und trotzdem gestartet ist ok", AppImageSmoke.SecondRunIsAcceptable(false, 0, "first-sharp\n"), true);
        Expect("smoke: Start ohne first-sharp faellt durch", AppImageSmoke.SecondRunIsAcceptable(true, 0, "nichts\n"), false);

        Expect("appimage: Runtime-Pruefsumme ist festgehalten", Pinned.PinProblem(AppImageRuntime.Sha256, "AppImageRuntime.Sha256"), null);
        Expect("appimage: Runtime-Adresse nennt die Version", AppImageRuntime.Url,
            $"https://github.com/AppImage/type2-runtime/releases/download/{AppImageRuntime.Version}/runtime-x86_64");
        Expect("appimage: Runtime-Groesse festgehalten", AppImageRuntime.Size > 0, true);
        Expect("appimage: Runtime-Ablage im Cache", AppImageRuntime.DefaultPath("/home/x/.cache"),
            Path.Combine("/home/x/.cache", "PagewingBuildTools", $"type2-runtime-{AppImageRuntime.Version}-x86_64"));
        Expect("appimage: Dateiname", AppImage.FileName("1.2.3"), "Pagewing-1.2.3-x86_64.AppImage");
        Expect("appimage: Dateiname Vorabversion", AppImage.FileName("1.2.3-rc1"), "Pagewing-1.2.3-rc1-x86_64.AppImage");

        // AppRun: POSIX sh, starts the app inside the AppDir.
        var appRun = Path.Combine("packaging", "linux", "AppRun");
        Expect("appimage: packaging/linux/AppRun vorhanden", File.Exists(appRun), true);
        var appRunText = File.Exists(appRun) ? File.ReadAllText(appRun) : string.Empty;
        Expect("appimage: AppRun beginnt mit #!/bin/sh", appRunText.StartsWith("#!/bin/sh\n", StringComparison.Ordinal), true);
        Expect("appimage: AppRun ohne CR", appRunText.Contains('\r', StringComparison.Ordinal), false);
        Expect("appimage: AppRun startet usr/lib/pagewing/Pagewing", AppImage.AppRunTarget(appRunText), "usr/lib/pagewing/Pagewing");
        Expect("appimage: Ziel ohne exec-Zeile ist null", AppImage.AppRunTarget("#!/bin/sh\necho x\n"), null);

        // .desktop inside the image: only Exec differs from the template.
        var template = File.ReadAllText(Path.Combine("packaging", "linux", "pagewing.desktop"));
        var desktop = AppImage.Desktop(template);
        Expect("appimage: .desktop Exec=Pagewing %F", desktop.Split('\n').Count(l => l == "Exec=Pagewing %F"), 1);
        Expect("appimage: .desktop sonst gleich", string.Join('\n', desktop.Split('\n').Where(l => !l.StartsWith("Exec=", StringComparison.Ordinal))),
            string.Join('\n', template.Split('\n').Where(l => !l.StartsWith("Exec=", StringComparison.Ordinal))));
        Expect("appimage: .desktop ohne Exec wirft", ThrowsIo(() => AppImage.Desktop("[Desktop Entry]\nName=x\n")), true);
        Expect("appimage: .desktop mit zwei Exec wirft", ThrowsIo(() => AppImage.Desktop("Exec=a\nExec=b\n")), true);

        var dir = Path.Combine(Path.GetTempPath(), "pagewing-appimage-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = Path.Combine(dir, "layout");
            var repo = Path.Combine(dir, "repo");
            Directory.CreateDirectory(Path.Combine(layout, "cli"));
            File.WriteAllText(Path.Combine(layout, "Pagewing"), "app");
            File.WriteAllText(Path.Combine(layout, "libpdfium.so"), "pdfium");
            File.WriteAllText(Path.Combine(layout, "LICENSE"), "license");
            File.WriteAllText(Path.Combine(layout, "THIRD-PARTY-NOTICES.txt"), "notices");
            File.WriteAllText(Path.Combine(layout, "cli", "pagewing"), "cli");
            File.WriteAllText(Path.Combine(layout, "cli", "libpdfium.so"), "pdfium");
            foreach (var size in new[] { "16x16", "256x256" })
            {
                var icons = Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor", size, "apps");
                Directory.CreateDirectory(icons);
                File.WriteAllText(Path.Combine(icons, "pagewing.png"), "png" + size);
            }

            Directory.CreateDirectory(Path.Combine(repo, "packaging", "linux"));
            File.WriteAllText(Path.Combine(repo, "packaging", "linux", "pagewing.desktop"), template);
            File.WriteAllText(Path.Combine(repo, "packaging", "linux", "AppRun"), appRunText);

            var entries = AppImage.Plan(layout, repo);
            DebEntry? Find(string path) => entries.FirstOrDefault(e => e.Path == path);
            Expect("appimage: Plan ohne doppelte Pfade", entries.Select(e => e.Path).Distinct().Count(), entries.Count);
            Expect("appimage: Plan ist sortiert", entries.Select(e => e.Path).SequenceEqual(entries.Select(e => e.Path).Order(StringComparer.Ordinal)), true);
            Expect("appimage: AppRun 0755", Find("AppRun")?.Mode, 0x1ED);
            Expect("appimage: AppRun-Ziel ist ein ausfuehrbares Programm im Plan", Find(AppImage.AppRunTarget(appRunText) ?? "?") is { Kind: DebEntryKind.File, Mode: 0x1ED }, true);
            Expect("appimage: pagewing.desktop in der Wurzel", Find("pagewing.desktop")?.Content, desktop);
            Expect("appimage: pagewing.png ist das 256er-Symbol", Find("pagewing.png")?.Source, Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor", "256x256", "apps", "pagewing.png"));
            Expect("appimage: .DirIcon zeigt auf pagewing.png", Find(".DirIcon")?.LinkTarget, "pagewing.png");
            Expect("appimage: Programm 0755", Find("usr/lib/pagewing/Pagewing")?.Mode, 0x1ED);
            Expect("appimage: CLI 0755", Find("usr/lib/pagewing/cli/pagewing")?.Mode, 0x1ED);
            Expect("appimage: Bibliothek 0644", Find("usr/lib/pagewing/libpdfium.so")?.Mode, 0x1A4);
            Expect("appimage: LICENSE im Programmordner", Find("usr/lib/pagewing/LICENSE")?.Mode, 0x1A4);
            Expect("appimage: THIRD-PARTY-NOTICES.txt im Programmordner", Find("usr/lib/pagewing/THIRD-PARTY-NOTICES.txt") is not null, true);
            Expect("appimage: Symbol 16x16 unter usr/share/icons", Find("usr/share/icons/hicolor/16x16/apps/pagewing.png")?.Mode, 0x1A4);
            Expect("appimage: Elternverzeichnisse vorhanden", entries.Where(e => e.Path.Contains('/', StringComparison.Ordinal))
                .All(e => Find(e.Path[..e.Path.LastIndexOf('/')]) is { Kind: DebEntryKind.Directory, Mode: 0x1ED }), true);
            File.Delete(Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor", "256x256", "apps", "pagewing.png"));
            Expect("appimage: ohne 256er-Symbol wirft", ThrowsIo(() => AppImage.Plan(layout, repo)), true);
            File.WriteAllText(Path.Combine(repo, "Docs", "design", "icons", "linux", "hicolor", "256x256", "apps", "pagewing.png"), "png256x256");

            if (!OperatingSystem.IsWindows() && Exec("sh", "-c true") == 0)
            {
                // AppRun keeps every argument as it is, also with spaces in APPDIR and in the arguments.
                var appDir = Path.Combine(dir, "App Dir");
                var program = Path.Combine(appDir, "usr", "lib", "pagewing", "Pagewing");
                Directory.CreateDirectory(Path.GetDirectoryName(program)!);
                File.WriteAllText(program, "#!/bin/sh\nfor a in \"$@\"; do printf '<%s>' \"$a\"; done\n");
                File.SetUnixFileMode(program, (UnixFileMode)0x1ED);
                Expect("appimage: sh -n AppRun", Tool.Run("sh", ["-n", appRun], out _), 0);
                var code = Tool.Run("sh", [appRun, "a b", "", "c*"], out var echoed, env: new Dictionary<string, string> { ["APPDIR"] = appDir });
                Expect("appimage: AppRun reicht Argumente unveraendert weiter", (code, echoed), (0, "<a b><><c*>"));
            }

            Expect("appimage: falscher Dateiname liefert 2", Cli.Run(["appimage", "--layout", layout, "--version", "1.2.3", "--runtime", appRun, "--out", Path.Combine(dir, "x.AppImage"), "--repo", repo]), 2);
            Expect("appimage: ungueltige Version liefert 2", Cli.Run(["appimage", "--layout", layout, "--version", "1.2", "--runtime", appRun, "--out", Path.Combine(dir, "Pagewing-1.2-x86_64.AppImage"), "--repo", repo]), 2);
            Expect("appimage: ohne Option liefert 2", Cli.Run(["appimage"]), 2);
            var refused = Path.Combine(dir, "out", AppImage.FileName("1.2.3"));
            Expect("appimage: Runtime mit falscher SHA-256 liefert 1", Cli.Run(["appimage", "--layout", layout, "--version", "1.2.3", "--runtime", appRun, "--out", refused, "--repo", repo]), 1);
            Expect("appimage: bei falscher Runtime entsteht nichts", File.Exists(refused), false);
            Expect("appimage: fehlende Runtime liefert 1", Cli.Run(["appimage", "--layout", layout, "--version", "1.2.3", "--runtime", Path.Combine(dir, "nope"), "--out", refused, "--repo", repo]), 1);
            Expect("runtime-fetch: unbekannte Option liefert 2", Cli.Run(["runtime-fetch", "--gibt-es-nicht", "x"]), 2);
            var wrong = Path.Combine(dir, "wrong-runtime");
            File.WriteAllText(wrong, "not the runtime");
            Expect("runtime-fetch: vorhandene falsche Datei wird abgelehnt, nichts geladen", Cli.Run(["runtime-fetch", "--out", wrong]), 1);
            Expect("runtime-fetch: falsche Datei bleibt unangetastet", File.ReadAllText(wrong), "not the runtime");
            Expect("appimage-smoke: ohne Datei liefert 2", Cli.Run(["appimage-smoke"]), 2);
            Expect("appimage-smoke: fehlende Datei liefert 1", Cli.Run(["appimage-smoke", Path.Combine(dir, "nope.AppImage")]), 1);

            if (OperatingSystem.IsLinux() && Exec("mksquashfs", "-version") == 0)
            {
                // A fake runtime with its own pin: the image is built and reproducible; the real runtime is checked by appimage-smoke.
                var fake = Encoding.ASCII.GetBytes("FAKE-RUNTIME\n");
                var fakeRuntime = Path.Combine(dir, "fake-runtime");
                File.WriteAllBytes(fakeRuntime, fake);
                var pin = (Size: (long)fake.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(fake)));
                var image1 = Path.Combine(dir, "a", AppImage.FileName("1.2.3"));
                var image2 = Path.Combine(dir, "b", AppImage.FileName("1.2.3"));
                Expect("appimage: baut mit mksquashfs", AppImage.Build(layout, repo, fakeRuntime, pin, image1), 0);
                Thread.Sleep(1100);
                File.SetLastWriteTimeUtc(Path.Combine(layout, "Pagewing"), DateTime.UtcNow);
                Expect("appimage: zweiter Lauf", AppImage.Build(layout, repo, fakeRuntime, pin, image2), 0);
                Expect("appimage: reproduzierbar (gleiche SHA-256)", Sums.Line(image1)[..64], Sums.Line(image2)[..64]);
                Expect("appimage: beginnt mit der Runtime", File.ReadAllBytes(image1).AsSpan(0, fake.Length).SequenceEqual(fake), true);
                Expect("appimage: SquashFS direkt hinter der Runtime (hsqs)", Encoding.ASCII.GetString(File.ReadAllBytes(image1), fake.Length, 4), "hsqs");
                Expect("appimage: ausfuehrbar 0755", File.GetUnixFileMode(image1), (UnixFileMode)0x1ED);
                if (Tool.Run("unsquashfs", ["-o", fake.Length.ToString(CultureInfo.InvariantCulture), "-lln", image1], out var listing) == 0)
                {
                    var rows = listing.Split('\n').Where(l => l.Contains("squashfs-root", StringComparison.Ordinal)).ToList();
                    Expect("appimage: alle Eintraege gehoeren root (0/0)", rows.Count > 0 && rows.All(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1] == "0/0"), true);
                    Expect("appimage: feste Zeit 2000-01-01", rows.All(r => r.Contains(" 2000-01-01 ", StringComparison.Ordinal)), true);
                    Expect("appimage: AppRun im Image ausfuehrbar", rows.Any(r => r.StartsWith("-rwxr-xr-x", StringComparison.Ordinal) && r.EndsWith("squashfs-root/AppRun", StringComparison.Ordinal)), true);
                    Expect("appimage: .DirIcon ist ein Symlink", rows.Any(r => r.StartsWith('l') && r.EndsWith("squashfs-root/.DirIcon -> pagewing.png", StringComparison.Ordinal)), true);
                }
                else
                {
                    Console.WriteLine("selftest: unsquashfs fehlt oder kennt -o nicht, Inhaltspruefung des Images uebersprungen.");
                }

                Expect("appimage-smoke: Image mit falscher Runtime schlaegt fehl", Cli.Run(["appimage-smoke", image1]), 1);
            }
            else
            {
                Console.WriteLine("selftest: mksquashfs fehlt, Bau des AppImage uebersprungen (sudo apt-get install -y squashfs-tools).");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool ThrowsIo(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // Everything of the Windows installer that can be checked without Windows: pinning, quoting, names, the script.
    private static void InstallerParts()
    {
        // Pinned download of Inno Setup.
        Expect("inno: Pruefsumme ist festgehalten", InnoSetup.PinProblem(InnoSetup.Sha256), null);
        Expect("inno: leere Pruefsumme gilt als nicht festgehalten", InnoSetup.PinProblem("") is not null, true);
        Expect("inno: Pruefsumme in Grossbuchstaben gilt nicht", InnoSetup.PinProblem(InnoSetup.Sha256.ToUpperInvariant()) is not null, true);
        Expect("inno: Pruefsumme mit 63 Zeichen gilt nicht", InnoSetup.PinProblem(InnoSetup.Sha256[..63]) is not null, true);
        Expect("inno: Version ist 6.x", InnoSetup.Version.StartsWith("6.", StringComparison.Ordinal), true);
        Expect("inno: Adresse nennt die Version", InnoSetup.Url.EndsWith($"/innosetup-{InnoSetup.Version}.exe", StringComparison.Ordinal), true);
        Expect("inno: Adresse ist HTTPS", InnoSetup.Url.StartsWith("https://github.com/jrsoftware/issrc/releases/download/", StringComparison.Ordinal), true);
        var payload = Encoding.ASCII.GetBytes("not the installer");
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        Expect("inno: passende Datei besteht", InnoSetup.VerifyDownload(new MemoryStream(payload), payload.Length, payloadHash), null);
        Expect("inno: andere Pruefsumme faellt durch", InnoSetup.VerifyDownload(new MemoryStream(payload), payload.Length, InnoSetup.Sha256) is not null, true);
        Expect("inno: andere Groesse faellt durch", InnoSetup.VerifyDownload(new MemoryStream(payload), payload.Length + 1, payloadHash) is not null, true);
        Expect("inno: nicht festgehaltene Pruefsumme faellt durch", InnoSetup.VerifyDownload(new MemoryStream(payload), payload.Length, "") is not null, true);
        Expect("inno: Zielordner im Benutzerprofil", InnoSetup.DefaultDir(@"C:\Users\ci\AppData\Local"),
            Path.Combine(@"C:\Users\ci\AppData\Local", "PagewingBuildTools", $"InnoSetup-{InnoSetup.Version}"));
        var innoArgs = InnoSetup.InstallArguments(@"C:\Users\ci\AppData\Local\PagewingBuildTools\InnoSetup-6");
        Expect("inno: still, nur fuer den Benutzer, portabel",
            InnoArguments.Split(innoArgs).SequenceEqual(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/PORTABLE=1", "/NOICONS", @"/DIR=C:\Users\ci\AppData\Local\PagewingBuildTools\InnoSetup-6"]), true);

        // Command lines for Inno Setup programs (no backslash escapes: a quote toggles quoting, see GetParamStr in issrc).
        string[] tricky = [@"/DIR=C:\Users\a b\Pagewing Test ä&", @"C:\dir\", "", "/DAppVersion=1.2.3-beta.1", "plain", "tab\there"];
        Expect("inno-args: Rundreise", InnoArguments.Split(InnoArguments.Join(tricky)).SequenceEqual(tricky), true);
        Expect("inno-args: Leerzeichen werden gequotet", InnoArguments.Join([@"/DIR=C:\a b"]), "\"/DIR=C:\\a b\"");
        Expect("inno-args: Anfuehrungszeichen wird abgelehnt", Throws(() => InnoArguments.Join(["a\"b"])), true);
        Expect("inno-args: Parser wie Inno (Quote mitten im Wort)", InnoArguments.Split("/DIR=\"C:\\a b\" x").SequenceEqual([@"/DIR=C:\a b", "x"]), true);

        // Names and versions of the installer.
        Expect("installer: Dateiname", Installer.SetupFileName("1.2.3"), "Pagewing-1.2.3-win-x64-setup.exe");
        Expect("installer: Version 1.2.3 -> 1.2.3.0", Installer.NumericVersion("1.2.3"), "1.2.3.0");
        Expect("installer: Vorabversion -> Zahlenteil", Installer.NumericVersion("0.1.0-beta.2"), "0.1.0.0");
        Expect("installer: 1.2 ist keine Version", Installer.NumericVersion("1.2"), null);
        Expect("installer: v1.2.3 ist keine Version", Installer.NumericVersion("v1.2.3"), null);
        Expect("installer: 70000 passt nicht in die Dateiversion", Installer.NumericVersion("1.70000.0"), null);
        Expect("installer: Leerzeichen in der Version", Installer.NumericVersion("1.2.3 x"), null);
        var iscc = Installer.IsccArguments(@"C:\r\packaging\windows\pagewing.iss", @"C:\l a", @"C:\r", "1.2.3", @"C:\o u\t");
        Expect("installer: iscc-Argumente", string.Join("|", iscc),
            @"/DAppVersion=1.2.3|/DNumericVersion=1.2.3.0|/DLayoutDir=C:\l a|/DIconFile=" + Path.Combine(@"C:\r", "src", "Pagewing.App", "Assets", "pagewing.ico") +
            @"|/OC:\o u\t|/FPagewing-1.2.3-win-x64-setup|C:\r\packaging\windows\pagewing.iss");
        Expect("installer: Warnungen von iscc werden erkannt",
            Installer.Warnings("Compiling\r\nWarning: Constant \"x\" is deprecated.\r\n  Warning: second\r\nSuccessful compile").Count, 2);
        Expect("installer: ohne Warnung leer", Installer.Warnings("Successful compile (1.0 sec).\nNo warnings here: none").Count, 0);
        Expect("installer: Deinstallationsschluessel", Installer.UninstallKey,
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + Installer.AppId + "}_is1");
        Expect("installer: AppId ist eine GUID in Grossbuchstaben", Guid.TryParseExact(Installer.AppId, "D", out _) && !Installer.AppId.Any(char.IsLower), true);

        // The silent command of the smoke test is exactly the documented one.
        var smokeArgs = InnoArguments.Split(InstallerSmoke.SilentInstallArguments(@"C:\T\Pagewing Test ä&", @"C:\T\install-1.log"));
        Expect("smoke: stille Installation",
            smokeArgs.SequenceEqual(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", @"/DIR=C:\T\Pagewing Test ä&", @"/LOG=C:\T\install-1.log"]), true);
        Expect("smoke: stille Deinstallation", InnoArguments.Split(InstallerSmoke.SilentUninstallArguments(@"C:\T\u.log"))
            .SequenceEqual(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", @"/LOG=C:\T\u.log"]), true);
        Expect("smoke: Testordner mit Umlaut und &", Path.GetFileName(InstallerSmoke.TestDir(Path.GetTempPath())), "Pagewing Test ä&");
        Expect("smoke: Vergleich gleicher Schnappschuesse", InstallerSmoke.Differences(["a 1 2"], ["a 1 2"]).Count, 0);
        Expect("smoke: geaenderte Datei faellt auf", InstallerSmoke.Differences(["a 1 2"], ["a 1 3"]).Count, 1);
        Expect("smoke: geloeschte Datei faellt auf", InstallerSmoke.Differences(["a 1 2", "b 1 2"], ["a 1 2"]).Count, 1);

        // The script itself (packaging/windows/pagewing.iss, read from the repository root).
        const string script = "packaging/windows/pagewing.iss";
        Expect($"iss: {script} vorhanden", File.Exists(script), true);
        if (!File.Exists(script))
        {
            return;
        }

        var text = File.ReadAllText(script);
        var problems = IssRules.Problems(text);
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"selftest: iss: {problem}");
        }

        Expect("iss: alle Regeln erfuellt", problems.Count, 0);
        Expect("iss: nur ASCII (Texte kommen aus den .isl-Dateien)", text.All(c => c < 128), true);

        // Each rule must catch its own violation (mutations of the real script).
        string Mutate(string from, string to)
        {
            Expect($"iss-Mutation findet '{from}'", text.Contains(from, StringComparison.Ordinal), true);
            return text.Replace(from, to, StringComparison.Ordinal);
        }

        Expect("iss: PrivilegesRequired=admin faellt auf", IssRules.Problems(Mutate("PrivilegesRequired=lowest", "PrivilegesRequired=admin")).Count > 0, true);
        Expect("iss: fehlendes dialog faellt auf", IssRules.Problems(Mutate("PrivilegesRequiredOverridesAllowed=commandline dialog", "PrivilegesRequiredOverridesAllowed=commandline")).Count > 0, true);
        Expect("iss: andere AppId faellt auf", IssRules.Problems(Mutate(Installer.AppId, Guid.Empty.ToString("D"))).Count > 0, true);
        Expect("iss: CloseApplications=no faellt auf", IssRules.Problems(Mutate("CloseApplications=yes", "CloseApplications=no")).Count > 0, true);
        Expect("iss: ohne ChangesAssociations faellt auf", IssRules.Problems(Mutate("ChangesAssociations=yes", "")).Count > 0, true);
        Expect("iss: ohne Deutsch faellt auf", IssRules.Problems(Mutate(@"compiler:Languages\German.isl", @"compiler:Languages\French.isl")).Count > 0, true);
        Expect("iss: Start auch im stillen Modus faellt auf", IssRules.Problems(Mutate("postinstall nowait skipifsilent", "postinstall nowait")).Count > 0, true);
        Expect("iss: sichtbare Registrierung faellt auf", IssRules.Problems(Mutate("Parameters: \"--register-file-types={code:FileTypeScope}\"; Flags: runhidden", "Parameters: \"--register-file-types={code:FileTypeScope}\"; Flags: waituntilterminated")).Count > 0, true);
        Expect("iss: Deinstallation ohne RunOnceId faellt auf", IssRules.Problems(Mutate("; RunOnceId: \"UnregisterFileTypes\"", "")).Count > 0, true);
        Expect("iss: Benutzerdaten anfassen faellt auf", IssRules.Problems(text + "\n[UninstallDelete]\nType: filesandordirs; Name: \"{userappdata}\\Pagewing\"\n").Count > 0, true);
        Expect("iss: Bereich ohne IsAdminInstallMode faellt auf", IssRules.Problems(Mutate("if IsAdminInstallMode then", "if True then")).Count > 0, true);
        Expect("iss: UsePreviousPrivileges=no faellt auf", IssRules.Problems(Mutate("SolidCompression=yes", "SolidCompression=yes\nUsePreviousPrivileges=no")).Count > 0, true);
        Expect("iss: UsePreviousAppDir=no faellt auf", IssRules.Problems(Mutate("SolidCompression=yes", "SolidCompression=yes\nUsePreviousAppDir=no")).Count > 0, true);
        Expect("iss: UsePreviousPrivileges=yes ausdruecklich besteht", IssRules.Problems(Mutate("SolidCompression=yes", "SolidCompression=yes\nUsePreviousPrivileges=yes\nUsePreviousAppDir=yes")).Count, 0);

        // update-smoke: what it lays out is what the app writes and the helper reads (no installation here).
        // A path with the separators of the running OS: update-smoke gets real paths, and Path.GetFileName splits only on '/' outside Windows.
        Expect("update-smoke: Version aus dem Setup-Namen", UpdateSmoke.VersionOf(Path.Combine(Path.GetTempPath(), "o u", "Pagewing-0.0.2-win-x64-setup.exe")), "0.0.2");
        Expect("update-smoke: anderer Name hat keine Version", UpdateSmoke.VersionOf("setup.exe"), null);
        Expect("update-smoke: Vorabversion wird nicht installiert", UpdateSmoke.VersionOf("Pagewing-0.0.2-rc.1-win-x64-setup.exe"), null);
        var smokeManifest = UpdateSmoke.Manifest("0.0.2", "Pagewing-0.0.2-win-x64-setup.exe", 1234, new string('a', 64));
        using (var parsed = System.Text.Json.JsonDocument.Parse(smokeManifest))
        {
            var file = parsed.RootElement.GetProperty("files")[0];
            Expect("update-smoke: Manifest Format 1", parsed.RootElement.GetProperty("format").GetInt32(), 1);
            Expect("update-smoke: Manifest Version", parsed.RootElement.GetProperty("version").GetString(), "0.0.2");
            Expect("update-smoke: Manifest Art des Windows-Setups", file.GetProperty("kind").GetString(), "win-x64-setup");
            Expect("update-smoke: Manifest Groesse", file.GetProperty("size").GetInt64(), 1234L);
        }

        // A throwaway key in memory: the signature has the form the app verifies (key id, 64 bytes P1363, over the raw bytes).
        using (var throwaway = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var signature = UpdateSmoke.Signature(smokeManifest, throwaway, "test");
            var raw = Convert.FromBase64String(signature["test:".Length..]);
            Expect("update-smoke: Signatur nennt die Kennung", signature.StartsWith("test:", StringComparison.Ordinal), true);
            Expect("update-smoke: Signatur hat 64 Bytes", raw.Length, 64);
            Expect("update-smoke: Signatur prueft ueber die Rohbytes", throwaway.VerifyData(smokeManifest, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), true);

            // --key takes the private file of update-keygen (PKCS#8 as Base64, with a line break) and PEM.
            var spki = throwaway.ExportSubjectPublicKeyInfo();
            using (var fromKeygen = UpdateSmoke.ReadKey(Convert.ToBase64String(throwaway.ExportPkcs8PrivateKey()) + "\n"))
            {
                Expect("update-smoke: liest den Schluessel von update-keygen", fromKeygen?.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki), true);
            }

            using (var fromPem = UpdateSmoke.ReadKey(throwaway.ExportPkcs8PrivateKeyPem()))
            {
                Expect("update-smoke: liest den Schluessel als PEM", fromPem?.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki), true);
            }
        }

        Expect("update-smoke: kein Schluessel aus Unsinn", UpdateSmoke.ReadKey("kein Schluessel") is null, true);
        Expect("update-smoke: kein Schluessel aus einem oeffentlichen Schluessel", UpdateSmoke.ReadKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==") is null, true);
        const string testTrust = "src/Pagewing.Core/Updates/UpdateTrust.TestBuild.cs";
        Expect("update-smoke: die Kennung des Testaufbaus ist die Vorgabe von --key-id",
            File.Exists(testTrust) && File.ReadAllText(testTrust).Contains($"private const string TestKeyId = \"{UpdateSmoke.TestKeyId}\";", StringComparison.Ordinal), true);

        using (var request = System.Text.Json.JsonDocument.Parse(UpdateSmoke.Request("0.0.2", "Pagewing-0.0.2-win-x64-setup.exe", @"C:\P a\Pagewing.exe", @"C:\P a")))
        {
            Expect("update-smoke: request.json Paketform", request.RootElement.GetProperty("form").GetString(), "WindowsInstaller");
            Expect("update-smoke: request.json Programm", request.RootElement.GetProperty("exePath").GetString(), @"C:\P a\Pagewing.exe");
            Expect("update-smoke: request.json Zielordner", request.RootElement.GetProperty("targetDirectory").GetString(), @"C:\P a");
        }

        Expect("update-smoke: Helfer nur mit dem Staging-Ordner", string.Join("|", UpdateSmoke.HelperArguments(@"C:\a b\updates\0.0.2")), @"--finish-update|C:\a b\updates\0.0.2");
        Expect("update-smoke: Testordner mit Umlaut und &", Path.GetFileName(UpdateSmoke.TestDir(Path.GetTempPath())), "Pagewing Update Test ä&");

        // Command handling without Windows and without iscc.
        var dir = Path.Combine(Path.GetTempPath(), "pagewing-package-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var layout = Path.Combine(dir, "layout");
            Directory.CreateDirectory(Path.Combine(layout, "cli"));
            foreach (var f in Installer.RequiredLayoutFiles)
            {
                File.WriteAllText(Path.Combine(layout, f), "x");
            }

            Expect("installer: vollstaendiges Layout", Installer.MissingLayoutFiles(layout).Count, 0);
            File.Delete(Path.Combine(layout, "cli", "pdfium.dll"));
            Expect("installer: fehlende CLI-Bibliothek faellt auf", Installer.MissingLayoutFiles(layout).SequenceEqual(["cli/pdfium.dll"]), true);
            var output = Path.Combine(dir, Installer.SetupFileName("1.2.3"));
            Expect("installer: unvollstaendiges Layout liefert 1", Cli.Run(["installer", "--layout", layout, "--version", "1.2.3", "--out", output]), 1);
            Expect("installer: falscher Dateiname liefert 2", Cli.Run(["installer", "--layout", layout, "--version", "1.2.3", "--out", Path.Combine(dir, "setup.exe")]), 2);
            Expect("installer: falsche Version liefert 2", Cli.Run(["installer", "--layout", layout, "--version", "1.2", "--out", output]), 2);
            Expect("installer: ohne Optionen liefert 2", Cli.Run(["installer"]), 2);
            Expect("installer-smoke: ohne Setup liefert 2", Cli.Run(["installer-smoke"]), 2);
            Expect("installer-smoke: fehlendes Setup liefert 1", Cli.Run(["installer-smoke", Path.Combine(dir, "nope.exe")]), 1);
            Expect("update-smoke: ohne Schluessel liefert 2", Cli.Run(["update-smoke", "a.exe", "b.exe"]), 2);
            Expect("update-smoke: ohne zweites Setup liefert 2", Cli.Run(["update-smoke", "a.exe", "--key", "k.pem"]), 2);
            Expect("update-smoke: falsche Setup-Namen liefern 2", Cli.Run(["update-smoke", "a.exe", "b.exe", "--key", "k.pem"]), 2);
            Expect("update-smoke: fehlende Setups liefern 1", Cli.Run(["update-smoke", Path.Combine(dir, "Pagewing-0.0.1-win-x64-setup.exe"),
                Path.Combine(dir, "Pagewing-0.0.2-win-x64-setup.exe"), "--key", Path.Combine(dir, "k.pem")]), 1);
            Expect("inno-install: unbekannte Option liefert 2", Cli.Run(["inno-install", "--gibt-es-nicht", "x"]), 2);
            if (!OperatingSystem.IsWindows())
            {
                Expect("inno-install: ausserhalb von Windows liefert 1", Cli.Run(["inno-install", "--dir", Path.Combine(dir, "inno")]), 1);
                Expect("inno-install: ausserhalb von Windows wird nichts geladen", Directory.Exists(Path.Combine(dir, "inno")), false);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // Builds tiny ELF files with clang in a temp directory and runs the real command on them.
    // The fixed inputs of the test vector in testdata/updates (version 0.2.0): the four known kinds with made-up sizes;
    // the SHA-256 of a file is the SHA-256 of its name, so the inputs need no real files.
    private static readonly (string Kind, string Name, long Size)[] VectorFiles =
    [
        ("win-x64-setup", "Pagewing-0.2.0-win-x64-setup.exe", 41_234_567),
        ("win-x64-portable", "Pagewing-0.2.0-win-x64-portable.zip", 43_210_987),
        ("linux-appimage", "Pagewing-0.2.0-x86_64.AppImage", 52_345_678),
        ("linux-deb", "pagewing_0.2.0_amd64.deb", 38_765_432),
    ];

    private const string VectorDirectory = "testdata/updates";

    // The C# file that holds the public key of the test vector (as update-keygen prints it); update-verify reads it
    // like UpdateTrust.cs, and the Core test compiles it, so the printed literal is proven to be valid C#.
    private const string VectorKeySource = "tests/Pagewing.Core.Tests/Updates/ReleaseToolTests.cs";

    private static string NameHash(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));

    private static string SumsOf(params string[] names) => string.Concat(names.Select(n => $"{NameHash(n)}  {n}\n"));

    // As "gh release view <tag> --json assets" prints it (more fields than the tool reads).
    private static string AssetsJson(IEnumerable<(string Name, long Size)> assets) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["assets"] = new System.Text.Json.Nodes.JsonArray(assets.Select(a => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["apiUrl"] = "https://api.github.invalid/assets/1",
                ["contentType"] = "application/octet-stream",
                ["downloadCount"] = 0,
                ["name"] = a.Name,
                ["size"] = a.Size,
                ["state"] = "uploaded",
            }).ToArray()),
        }.ToJsonString();

    // Runs a command with stdout and stderr captured (both are checked for key material).
    private static (int Code, string Out, string Err) Capture(params string[] args)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (Cli.Run(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    // Whether text contains the secret or any 16-character piece of it (a partial print counts as a leak).
    private static bool Leaks(string text, string secret)
    {
        if (secret.Length == 0)
        {
            return false;
        }

        for (var i = 0; i + 16 <= secret.Length; i += 8)
        {
            if (text.Contains(secret.Substring(i, 16), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return text.Contains(secret, StringComparison.Ordinal);
    }

    // The literal update-keygen prints: new UpdateKey("<id>", Convert.FromBase64String("<SPKI>")),
    private static (string Id, byte[] Key)? KeyLiteral(string text)
    {
        var m = Regex.Match(text, @"new UpdateKey\(\s*""(?<id>[^""]+)"",\s*Convert\.FromBase64String\(""(?<key>[A-Za-z0-9+/=]+)""\)\),");
        return m.Success ? (m.Groups["id"].Value, Convert.FromBase64String(m.Groups["key"].Value)) : null;
    }

    // An independent check of "<keyId>:<base64 of the 64-byte IEEE P1363 ECDSA P-256 signature>" (as ManifestVerifier does).
    private static bool SignatureVerifies(byte[] data, string signatureText, string keyId, byte[] publicKey)
    {
        var parts = signatureText.Trim().Split(':');
        if (parts.Length != 2 || parts[0] != keyId)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        return signature.Length == 64 && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static List<(string Kind, string Name, long Size, string Sha256)> ManifestFiles(string file)
    {
        var result = new List<(string, string, long, string)>();
        if (!File.Exists(file))
        {
            return result;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            result.Add((f.GetProperty("kind").GetString()!, f.GetProperty("name").GetString()!, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString()!));
        }

        return result;
    }

    // The release tool for the update manifest (Milestone 8.1). Only throwaway keys, in a temp folder that is deleted at
    // the end and in an environment variable of this process that is removed again; the real signing key never appears.
    private static void UpdateReleaseParts()
    {
        const string keyEnv = "PAGEWING_SELFTEST_SIGNING_KEY";
        var dir = Path.Combine(Path.GetTempPath(), "pagewing-update-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // update-manifest
            var win = Path.Combine(dir, "SHA256SUMS-windows.txt");
            var linux = Path.Combine(dir, "SHA256SUMS-linux.txt");
            var sizes = Path.Combine(dir, "assets.json");
            var others = new[] { "fuse-3.15.0.tar.xz", "type2-runtime-20251108.tar.gz" };
            File.WriteAllText(win, SumsOf(VectorFiles[1].Name, VectorFiles[0].Name));
            File.WriteAllText(linux, SumsOf(VectorFiles[3].Name, VectorFiles[2].Name, others[0], others[1]));
            var assets = VectorFiles.Select(f => (f.Name, f.Size)).Concat(others.Select(o => (o, 1000L)))
                .Concat([("SHA256SUMS-windows.txt", 200L), ("SHA256SUMS-linux.txt", 400L)]).ToList();
            File.WriteAllText(sizes, AssetsJson(assets));
            string[] ManifestArgs(string version, string output, string sizesFile, params string[] sums) =>
                ["update-manifest", "--version", version, "--sums", .. sums, "--sizes", sizesFile, "--out", output];

            var manifest = Path.Combine(dir, "update-manifest.json");
            Expect("update-manifest: Erfolg", Cli.Run(ManifestArgs("0.2.0", manifest, sizes, win, linux)), 0);
            var files = ManifestFiles(manifest);
            Expect("update-manifest: nur bekannte Dateien (in fester Reihenfolge)", string.Join(",", files.Select(f => f.Kind)),
                "win-x64-setup,win-x64-portable,linux-appimage,linux-deb");
            foreach (var (kind, name, size) in VectorFiles)
            {
                Expect($"update-manifest: Groesse und SHA-256 stimmen ({kind})", files.Contains((kind, name, size, NameHash(name))), true);
            }

            var vectorManifest = Path.Combine(VectorDirectory, "update-manifest.json");
            Expect($"update-manifest: schreibt genau die Bytes von {vectorManifest} (Testvektor der App)",
                File.Exists(manifest) && File.Exists(vectorManifest) && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(File.ReadAllBytes(vectorManifest)), true);

            var refused = Path.Combine(dir, "refused.json");
            Expect("update-manifest: Vorabversion liefert 2 (die App liest nur X.Y.Z)", Cli.Run(ManifestArgs("0.2.0-rc.1", refused, sizes, win, linux)), 2);
            Expect("update-manifest: Dateinamen einer anderen Version liefern 1", Cli.Run(ManifestArgs("0.3.0", refused, sizes, win, linux)), 1);
            Expect("update-manifest: ohne Linux-Summen fehlen Arten, liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, sizes, win)), 1);
            var withoutSize = Path.Combine(dir, "without-size.json");
            File.WriteAllText(withoutSize, AssetsJson(assets.Where(a => a.Item1 != VectorFiles[2].Name)));
            Expect("update-manifest: fehlende Groesse liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, withoutSize, win, linux)), 1);
            var zeroSize = Path.Combine(dir, "zero-size.json");
            File.WriteAllText(zeroSize, AssetsJson(assets.Select(a => a.Item1 == VectorFiles[0].Name ? (a.Item1, 0L) : a)));
            Expect("update-manifest: Groesse 0 liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, zeroSize, win, linux)), 1);
            var broken = Path.Combine(dir, "SHA256SUMS-broken.txt");
            File.WriteAllText(broken, SumsOf(VectorFiles[1].Name) + "abc  Pagewing-0.2.0-win-x64-setup.exe\n");
            Expect("update-manifest: kaputte Zeile in SHA256SUMS liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, sizes, broken, linux)), 1);
            var twice = Path.Combine(dir, "SHA256SUMS-twice.txt");
            File.WriteAllText(twice, SumsOf(VectorFiles[1].Name, VectorFiles[0].Name, VectorFiles[0].Name));
            Expect("update-manifest: doppelte Datei liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, sizes, twice, linux)), 1);
            var notJson = Path.Combine(dir, "not-json.json");
            File.WriteAllText(notJson, "<html>");
            Expect("update-manifest: --sizes ohne JSON liefert 1", Cli.Run(ManifestArgs("0.2.0", refused, notJson, win, linux)), 1);
            Expect("update-manifest: bei Fehlern entsteht keine Datei", File.Exists(refused), false);
            Expect("update-manifest: ohne --sizes liefert 2", Cli.Run(["update-manifest", "--version", "0.2.0", "--sums", win, linux, "--out", refused]), 2);

            // update-keygen
            var keys = Path.Combine(dir, "keys");
            Directory.CreateDirectory(keys);
            var keyFile = Path.Combine(keys, "signing-key.txt");
            var (keygenCode, keygenOut, keygenErr) = Capture("update-keygen", "--id", "selftest", "--private-out", keyFile);
            Expect("update-keygen: Erfolg", keygenCode, 0);
            var privateText = File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : string.Empty;
            Expect("update-keygen: privater Schluessel steht in der Datei", privateText.Length > 0, true);
            Expect("update-keygen: privater Schluessel nie in der Ausgabe", privateText.Length > 0 && !Leaks(keygenOut + keygenErr, privateText), true);
            var literal = KeyLiteral(keygenOut);
            Expect("update-keygen: stdout ist ein Eintrag fuer UpdateTrust.Keys mit der Kennung", literal?.Id, "selftest");
            var matches = false;
            if (literal is { } lit && privateText.Length > 0)
            {
                using var fromFile = ECDsa.Create();
                fromFile.ImportPkcs8PrivateKey(Convert.FromBase64String(privateText), out _);
                using var fromLiteral = ECDsa.Create();
                fromLiteral.ImportSubjectPublicKeyInfo(lit.Key, out _);
                matches = fromFile.KeySize == 256 && fromLiteral.KeySize == 256 &&
                    fromFile.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(lit.Key);
            }

            Expect("update-keygen: P-256, PKCS#8 in der Datei passt zum ausgegebenen oeffentlichen Schluessel", matches, true);
            Expect("update-keygen: vorhandene Datei wird nie ueberschrieben", Cli.Run(["update-keygen", "--id", "selftest", "--private-out", keyFile]), 1);
            Expect("update-keygen: Datei bleibt unveraendert", File.Exists(keyFile) && File.ReadAllText(keyFile).Trim() == privateText, true);
            Expect("update-keygen: Kennung mit ':' liefert 2", Cli.Run(["update-keygen", "--id", "a:b", "--private-out", Path.Combine(keys, "x.txt")]), 2);
            Expect("update-keygen: ohne --private-out liefert 2", Cli.Run(["update-keygen", "--id", "selftest"]), 2);
            var repo = Path.Combine(dir, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Directory.CreateDirectory(Path.Combine(repo, "sub"));
            var inRepo = Path.Combine(repo, "sub", "key.txt");
            Expect("update-keygen: nie in ein Git-Arbeitsverzeichnis", Cli.Run(["update-keygen", "--id", "selftest", "--private-out", inRepo]), 1);
            Expect("update-keygen: im Git-Arbeitsverzeichnis entsteht nichts", File.Exists(inRepo), false);
            if (File.Exists(keyFile))
            {
                if (OperatingSystem.IsWindows())
                {
                    Expect("update-keygen: private Datei nur fuer den Benutzer lesbar (Windows-ACL)", UserOnlyAcl(keyFile), true);
                }
                else
                {
                    Expect("update-keygen: private Datei nur fuer den Benutzer lesbar (0600)", File.GetUnixFileMode(keyFile), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            // update-sign and update-verify with the throwaway key (trusted through a C# file like UpdateTrust.cs).
            var trust = Path.Combine(dir, "Trust.cs");
            File.WriteAllText(trust, "IReadOnlyList<UpdateKey> Keys = [\n" + keygenOut + "];\n");
            var sig = Path.Combine(dir, "update-manifest.json.sig");
            string[] SignArgs(string output, string id = "selftest", string source = "") =>
                ["update-sign", "--manifest", manifest, "--key-env", keyEnv, "--id", id, "--out", output, "--source", source.Length > 0 ? source : trust];

            Environment.SetEnvironmentVariable(keyEnv, privateText);
            var (signCode, signOut, signErr) = Capture(SignArgs(sig));
            Expect("update-sign: Erfolg", signCode, 0);
            var sigText = File.Exists(sig) ? File.ReadAllText(sig) : string.Empty;
            Expect("update-sign: Signatur prueft mit dem Schluessel", literal is { } l && File.Exists(manifest) &&
                SignatureVerifies(File.ReadAllBytes(manifest), sigText, "selftest", l.Key), true);
            Expect("update-sign: Schluessel nie in der Ausgabe", privateText.Length > 0 && !Leaks(signOut + signErr + sigText, privateText), true);
            Expect("update-verify: Erfolg", Cli.Run(["update-verify", "--manifest", manifest, "--sig", sig, "--source", trust]), 0);

            var tampered = Path.Combine(dir, "tampered.json");
            if (File.Exists(manifest))
            {
                File.WriteAllText(tampered, File.ReadAllText(manifest).Replace("41234567", "41234568", StringComparison.Ordinal));
            }

            Expect("update-verify: manipuliert -> Fehler", Cli.Run(["update-verify", "--manifest", tampered, "--sig", sig, "--source", trust]), 1);
            var badSig = Path.Combine(dir, "bad.sig");
            File.WriteAllText(badSig, sigText.Length > 20 ? sigText[..12] + (sigText[12] == 'A' ? 'B' : 'A') + sigText[13..] : "selftest:AAAA");
            Expect("update-verify: veraenderte Signatur -> Fehler", Cli.Run(["update-verify", "--manifest", manifest, "--sig", badSig, "--source", trust]), 1);
            var otherId = Path.Combine(dir, "other-id.sig");
            File.WriteAllText(otherId, sigText.Replace("selftest:", "other:", StringComparison.Ordinal));
            Expect("update-verify: unbekannte Kennung -> Fehler", Cli.Run(["update-verify", "--manifest", manifest, "--sig", otherId, "--source", trust]), 1);
            Expect("update-verify: ohne Schluesselquelle liefert 2", Cli.Run(["update-verify", "--manifest", manifest, "--sig", sig]), 2);
            Expect("update-verify: Manifest ohne Signatur liefert 2", Cli.Run(["update-verify", "--manifest", manifest, "--source", trust]), 2);
            Expect("update-verify: ohne Manifest prueft nur die Schluessel (echter Schluessel: 0)", Cli.Run(["update-verify", "--source", trust]), 0);

            var noSig = Path.Combine(dir, "none.sig");
            Expect("update-sign: --key als Argument liefert 2", Cli.Run(["update-sign", "--manifest", manifest, "--key", privateText, "--id", "selftest", "--out", noSig, "--source", trust]), 2);
            Expect("update-sign: unbekannte Kennung liefert 1", Cli.Run(SignArgs(noSig, id: "other")), 1);

            using (var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var otherText = Convert.ToBase64String(otherKey.ExportPkcs8PrivateKey());
                Environment.SetEnvironmentVariable(keyEnv, otherText);
                var (code, output, error) = Capture(SignArgs(noSig));
                Expect("update-sign: fremder Schluessel (passt nicht zur Kennung) liefert 1", code, 1);
                Expect("update-sign: fremder Schluessel nie in der Ausgabe", Leaks(output + error, otherText), false);
            }

            Environment.SetEnvironmentVariable(keyEnv, "kein-schluessel-ZQXJ");
            var (garbageCode, garbageOut, garbageErr) = Capture(SignArgs(noSig));
            Expect("update-sign: unlesbarer Schluessel liefert 1", garbageCode, 1);
            Expect("update-sign: unlesbarer Schluessel nie in der Ausgabe", (garbageOut + garbageErr).Contains("ZQXJ", StringComparison.Ordinal), false);

            Environment.SetEnvironmentVariable(keyEnv, null);
            Expect("update-sign: ohne Umgebungsvariable Fehler", Cli.Run(SignArgs(noSig)), 1);
            Expect("update-sign: bei Fehlern entsteht keine Signatur", File.Exists(noSig), false);

            // The placeholder of UpdateTrust.cs: no release is ever signed or verified with it.
            var placeholder = Path.Combine(dir, "PlaceholderTrust.cs");
            File.WriteAllText(placeholder,
                "public const string PlaceholderKeyId = \"placeholder\";\n" +
                "IReadOnlyList<UpdateKey> Keys { get; } =\n[\n    new UpdateKey(\n        PlaceholderKeyId,\n" +
                "        Convert.FromBase64String(\"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==\")),\n];\n");
            Environment.SetEnvironmentVariable(keyEnv, privateText);
            Expect("update-sign: mit dem Platzhalter wird nie signiert", Cli.Run(SignArgs(noSig, id: "placeholder", source: placeholder)), 1);
            Expect("update-verify: Platzhalter als einziger Schluessel liefert 1", Cli.Run(["update-verify", "--manifest", manifest, "--sig", sig, "--source", placeholder]), 1);
            Expect("update-verify: ohne Manifest scheitert die Schluesselpruefung am Platzhalter", Cli.Run(["update-verify", "--source", placeholder]), 1);
            Expect("update-sign: ohne --source nimmt UpdateTrust.cs (Kennung selftest steht nicht darin) und liefert 1",
                Cli.Run(["update-sign", "--manifest", manifest, "--key-env", keyEnv, "--id", "selftest", "--out", noSig]), 1);
            Environment.SetEnvironmentVariable(keyEnv, null);

            // The committed test vector (signed once by this tool with a throwaway key; ReleaseToolTests checks it with
            // the app's ManifestVerifier). update-verify must accept it with the key from the test source.
            var vectorSig = Path.Combine(VectorDirectory, "update-manifest.json.sig");
            Expect("update-verify: Testvektor prueft mit dem Schluessel aus ReleaseToolTests.cs",
                Cli.Run(["update-verify", "--manifest", vectorManifest, "--sig", vectorSig, "--source", VectorKeySource]), 0);
            var (realCode, _, realErr) = Capture("update-verify", "--manifest", vectorManifest, "--sig", vectorSig, "--keys-from-source");
            Expect("update-verify --keys-from-source: der Testschluessel ist im Programm nicht vertraut", realCode, 1);
            var trustText = File.Exists(TrustFile) ? File.ReadAllText(TrustFile) : string.Empty;
            if (Regex.Count(trustText, @"new UpdateKey\(") == 1 && Regex.IsMatch(trustText, @"new UpdateKey\(\s*PlaceholderKeyId\s*,"))
            {
                Expect("update-verify --keys-from-source: scheitert mit Hinweis, solange UpdateTrust.cs nur den Platzhalter hat",
                    realErr.Contains("Platzhalter", StringComparison.Ordinal), true);
                Expect("update-verify --keys-from-source ohne Manifest (preflight): scheitert, solange UpdateTrust.cs nur den Platzhalter hat",
                    Cli.Run(["update-verify", "--keys-from-source"]), 1);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyEnv, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string TrustFile = "src/Pagewing.Core/Updates/UpdateTrust.cs";

    // Windows: inheritance cut and exactly one rule, for the current user.
    private static bool UserOnlyAcl(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var security = new FileInfo(file).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        return security.AreAccessRulesProtected && rules.Count == 1 && rules[0].IdentityReference.Equals(user) &&
            rules[0].AccessControlType == System.Security.AccessControl.AccessControlType.Allow;
    }

    // Trust model once the repository is public: code from a fork PR never reaches a persistent self-hosted runner,
    // the checkout leaves no token behind, the signing secret lives in one hosted, environment-guarded job, and every
    // action is pinned to a commit. A missing workflow file (codeql.yml before it exists) is no violation.
    private const string PullRequestGuard = "github.event_name == 'pull_request'";

    private static string TriggersOf(string yml) =>
        Regex.Match(yml, @"(?m)^on:\n(?<b>(?:[ #].*\n|\n)*)").Groups["b"].Value;

    private static bool HasTrigger(string yml, string trigger) =>
        Regex.IsMatch(TriggersOf(yml), @"(?m)^  " + trigger + @":");

    private static List<string> PullRequestTargetViolations(string yml) =>
        HasTrigger(yml, "pull_request_target") ? ["pull_request_target"] : [];

    // The labels dotnet-linux, dotnet-windows and dotnet-windows-gpu exist only on the owner's runners.
    private static bool IsSelfHosted(string line) =>
        Regex.IsMatch(line, @"self-hosted|dotnet-linux|dotnet-windows");

    // A runs-on line that picks a hosted runner for pull requests and the own runner only otherwise.
    private static bool IsGuarded(string line) =>
        line.Contains(PullRequestGuard, StringComparison.Ordinal) && line.Contains("matrix.hosted", StringComparison.Ordinal);

    private static List<string> SelfHostedOnPullRequestViolations(string file, string yml)
    {
        var violations = new List<string>();
        var runsOn = Regex.Matches(yml, @"(?m)^    runs-on:.*$").Select(m => m.Value).ToList();
        if (file == "hardware-measure.yml" && HasTrigger(yml, "pull_request"))
        {
            violations.Add("Ausloeser pull_request");
        }

        if (!HasTrigger(yml, "pull_request"))
        {
            return violations;
        }

        var noSelfHostedAtAll = file is "roadmap.yml" or "codeql.yml";
        foreach (var line in runsOn.Where(IsSelfHosted))
        {
            if (noSelfHostedAtAll || !IsGuarded(line))
            {
                violations.Add(line.Trim());
            }
        }

        if (file == "ci.yml" && !runsOn.Any(IsGuarded))
        {
            violations.Add("kein gehostetes runs-on fuer Pull Requests");
        }

        return violations;
    }

    private static List<string> CheckoutViolations(string yml) =>
        [.. Regex.Split(yml, @"(?m)^(?=      - )").Where(s => s.Contains("actions/checkout@", StringComparison.Ordinal))
            .Where(s => !Regex.IsMatch(s, @"(?m)^ +persist-credentials: false\s*$")).Select(_ => "checkout ohne persist-credentials: false")];

    private static List<string> UnpinnedActionViolations(string yml) =>
        [.. Regex.Matches(yml, @"(?m)^\s*(?:- )?uses: (?<a>[^\s#]+)").Select(m => m.Groups["a"].Value)
            .Where(a => !a.StartsWith("./", StringComparison.Ordinal) && !Regex.IsMatch(a, @"@[0-9a-f]{40}$"))];

    private static List<string> PermissionsViolations(string yml)
    {
        var violations = new List<string>();
        if (!Regex.IsMatch(yml, @"(?m)^permissions:"))
        {
            violations.Add("permissions fehlt");
        }

        if (Regex.IsMatch(yml, @"permissions:\s*write-all"))
        {
            violations.Add("write-all");
        }

        return violations;
    }

    private static void PublicRepoWorkflowParts()
    {
        const string dir = ".github/workflows";
        string? Read(string file) => File.Exists(Path.Combine(dir, file))
            ? File.ReadAllText(Path.Combine(dir, file)).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.yml").Concat(Directory.GetFiles(dir, "*.yaml")).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];
        Expect("Workflows: Ordner .github/workflows hat Dateien", files.Count > 0, true);

        // Negative controls: the rules must bite on a deliberately bad workflow.
        const string bad = "on:\n  pull_request_target:\n  pull_request:\npermissions: write-all\njobs:\n  x:\n    runs-on: [self-hosted, dotnet-linux]\n" +
            "    steps:\n      - uses: actions/checkout@v4\n      - uses: actions/cache@0123456789abcdef0123456789abcdef01234567\n";
        Expect("Gegenprobe: pull_request_target wird erkannt", PullRequestTargetViolations(bad).Count, 1);
        Expect("Gegenprobe: self-hosted bei Pull Requests wird erkannt", SelfHostedOnPullRequestViolations("x.yml", bad).Count, 1);
        Expect("Gegenprobe: checkout ohne persist-credentials wird erkannt", CheckoutViolations(bad).Count, 1);
        Expect("Gegenprobe: nicht angeheftete Action wird erkannt", UnpinnedActionViolations(bad).Count, 1);
        Expect("Gegenprobe: write-all wird erkannt", PermissionsViolations(bad).Count, 1);
        const string good = "on:\n  pull_request:\njobs:\n  build:\n" +
            "    runs-on: ${{ fromJSON(github.event_name == 'pull_request' && format('[\"{0}\"]', matrix.hosted) || format('[\"self-hosted\",\"{0}\"]', matrix.label)) }}\n";
        Expect("Gegenprobe: richtig geschuetztes runs-on von ci.yml ist ohne Befund", SelfHostedOnPullRequestViolations("ci.yml", good).Count, 0);
        Expect("Gegenprobe: Label dotnet-windows allein zaehlt als self-hosted", SelfHostedOnPullRequestViolations("x.yml", "on:\n  pull_request:\njobs:\n  x:\n    runs-on: [dotnet-windows]\n").Count, 1);
        Expect("Gegenprobe: hardware-measure.yml mit pull_request wird erkannt",
            SelfHostedOnPullRequestViolations("hardware-measure.yml", "on:\n  pull_request:\njobs:\n  x:\n    runs-on: ubuntu-24.04\n").Count, 1);

        string Join(IEnumerable<string> v) => string.Join("; ", v);
        var noTarget = new List<string>();
        var selfHosted = new List<string>();
        var checkout = new List<string>();
        var unpinned = new List<string>();
        var permissions = new List<string>();
        var secretsOutside = new List<string>();
        foreach (var file in files)
        {
            var yml = Read(file)!;
            Expect($"Workflows: {file} hat einen lesbaren on:-Block", TriggersOf(yml).Trim().Length > 0, true);
            noTarget.AddRange(PullRequestTargetViolations(yml).Select(v => $"{file}: {v}"));
            selfHosted.AddRange(SelfHostedOnPullRequestViolations(file, yml).Select(v => $"{file}: {v}"));
            checkout.AddRange(CheckoutViolations(yml).Select(v => $"{file}: {v}"));
            unpinned.AddRange(UnpinnedActionViolations(yml).Select(v => $"{file}: {v}"));
            permissions.AddRange(PermissionsViolations(yml).Select(v => $"{file}: {v}"));
            if (file != "release.yml" && yml.Contains("secrets.", StringComparison.Ordinal))
            {
                secretsOutside.Add(file);
            }
        }

        // A workflow that is missing (codeql.yml before it exists) is simply not listed above.
        Expect("Workflows: kein pull_request_target", Join(noTarget), "");
        Expect("Workflows: Pull-Request-Laeufe nie auf self-hosted", Join(selfHosted), "");
        Expect("Workflows: jeder checkout mit persist-credentials: false", Join(checkout), "");
        Expect("Workflows: permissions oben und kein write-all", Join(permissions), "");
        Expect("Workflows: jede Action auf einer 40-stelligen SHA", Join(unpinned), "");
        Expect("Workflows: secrets. nur in release.yml", Join(secretsOutside), "");

        var release = Read("release.yml");
        if (release is not null)
        {
            Expect("release.yml: kein Ausloeser release", HasTrigger(release, "release"), false);
            var manifest = Regex.Match(release, @"(?m)^  manifest:\n(?<b>(?:    .*\n|\n)*)").Groups["b"].Value;
            Expect("release.yml: Job manifest gefunden", manifest.Length > 0, true);
            var otherJobs = manifest.Length > 0 ? release.Replace(manifest, string.Empty, StringComparison.Ordinal) : release;
            Expect("release.yml: secrets. nur im Job manifest", otherJobs.Contains("secrets.", StringComparison.Ordinal), false);
            Expect("release.yml: Job manifest mit environment: release-signing",
                Regex.IsMatch(manifest, @"(?m)^    environment: release-signing\s*$"), true);
            var manifestRunsOn = Regex.Match(manifest, @"(?m)^    runs-on:.*$").Value;
            Expect("release.yml: Job manifest auf gehostetem Runner", manifestRunsOn.Length > 0 && !IsSelfHosted(manifestRunsOn), true);
        }

        // Code scanning is paid in a private repository: the job must be skipped there, and the write right to the
        // security events belongs to the job only, never to the workflow.
        var codeql = Read("codeql.yml");
        if (codeql is not null)
        {
            var topLevel = Regex.Match(codeql, @"(?m)^permissions:\n(?<b>(?:  .*\n)*)").Groups["b"].Value;
            Expect("codeql.yml: Rechte oben nur contents: read", topLevel.Trim(), "contents: read");
            Expect("codeql.yml: security-events: write genau einmal, im Job", Regex.Count(codeql, @"security-events: write"), 1);
            Expect("codeql.yml: security-events: write steht unter dem Job (vier Leerzeichen eingerueckte permissions)",
                Regex.IsMatch(codeql, @"(?m)^    permissions:\n      contents: read\n      security-events: write\n"), true);
            Expect("codeql.yml: Job nur im oeffentlichen Repo", Regex.IsMatch(codeql, @"(?m)^    if: github\.event\.repository\.private == false\s*$"), true);
            Expect("codeql.yml: nur gehosteter Runner", Regex.Match(codeql, @"(?m)^    runs-on:.*$").Value.Trim(), "runs-on: ubuntu-24.04");
        }
    }

    // The third job of release.yml signs the update manifest. The signing key is a secret of the environment
    // release-signing: it may appear in exactly one place, the env of the step that runs the signing command, and
    // nowhere else (no job or workflow env, no other step, no action). The workflow is dispatch-only, so neither a
    // pull request nor a fork ever starts it; the tag whose code runs in the approved job must be on main or dev.
    private static void ManifestJobParts()
    {
        const string workflow = ".github/workflows/release.yml";
        const string container = "packaging/linux/container-build.sh";
        if (!File.Exists(workflow) || !File.Exists(container))
        {
            Expect("release.yml: release.yml und container-build.sh vorhanden", false, true);
            return;
        }

        var yml = File.ReadAllText(workflow).Replace("\r\n", "\n", StringComparison.Ordinal);
        var on = Regex.Match(yml, @"(?m)^on:\n(?<b>(?:[ #].*\n|\n)*)").Groups["b"].Value;
        // Dispatch only (M12.1): the workflow ref of a release event is a tag, and the runner-group restriction can name
        // branch refs only (refs/heads/main, refs/heads/dev).
        Expect("release.yml: Ausloeser nur workflow_dispatch",
            string.Join(",", Regex.Matches(on, @"(?m)^  (?<k>[a-z_]+):").Select(m => m.Groups["k"].Value)), "workflow_dispatch");
        Expect("release.yml: kein Ausloeser fuer Pull Requests", yml.Contains("pull_request", StringComparison.Ordinal), false);
        Expect("release.yml: nichts liest mehr ein Release-Ereignis (github.event.release, event_name == 'release')",
            Regex.IsMatch(yml, @"github\.event\.release|event_name == 'release'"), false);
        Expect("release.yml: TAG kommt in jedem Job nur aus inputs.tag", Regex.Count(yml, @"(?m)^      TAG: \$\{\{ inputs\.tag \}\}$"), Regex.Count(yml, @"(?m)^      TAG:"));
        Expect("release.yml: TAG wird in vier Jobs gesetzt", Regex.Count(yml, @"(?m)^      TAG:"), 4);
        Expect("release.yml: UPLOAD kommt in jedem Job nur aus inputs.upload", Regex.Count(yml, @"(?m)^      UPLOAD: \$\{\{ inputs\.upload \}\}$"), Regex.Count(yml, @"(?m)^      UPLOAD:"));
        Expect("release.yml: UPLOAD wird in drei Jobs gesetzt", Regex.Count(yml, @"(?m)^      UPLOAD:"), 3);
        Expect("release.yml: Nebenlaeufigkeit nur je inputs.tag", yml.Contains("  group: release-${{ inputs.tag }}\n", StringComparison.Ordinal), true);

        string Job(string name) => Regex.Match(yml, @"(?m)^  " + name + @":\n(?<b>(?:    .*\n|\n)*)").Groups["b"].Value;
        List<string> StepsOf(string jobText) => [.. Regex.Split(jobText, @"(?m)^(?=      - )").Skip(1)];
        string Name(string step) => Regex.Match(step, @"^      - name: (?<n>.+)$|^        name: (?<n>.+)$", RegexOptions.Multiline).Groups["n"].Value;

        // preflight: before anything is uploaded, the tag must trust a real key (a release with only the placeholder
        // could never update itself, and a rerun cannot fix a tag). Without upload (dry run) the check is skipped.
        var preflight = Job("preflight");
        Expect("release.yml: Job preflight vorhanden", preflight.Length > 0, true);
        Expect("release.yml: Job preflight auf dem Linux-Runner", preflight.Contains("    runs-on: [self-hosted, dotnet-linux]\n", StringComparison.Ordinal), true);
        Expect("release.yml: Job preflight liest nur (contents: read)", Regex.IsMatch(preflight, @"    permissions:\n      contents: read\n"), true);
        Expect("release.yml: Job preflight braucht keinen anderen Job", preflight.Contains("    needs:", StringComparison.Ordinal), false);
        var trustCheck = StepsOf(preflight).FirstOrDefault(s => Name(s) == "Check that the tag trusts a real update key") ?? string.Empty;
        Expect("release.yml: preflight prueft beim Hochladen die Schluessel des Tags (update-verify --keys-from-source)",
            trustCheck.Contains("        if: env.UPLOAD == 'true'\n", StringComparison.Ordinal) &&
            Regex.IsMatch(trustCheck, @"\n        run: dotnet run tools/package\.cs -- update-verify --keys-from-source\n"), true);
        // With upload=true the tag's commit must lie on main or dev (identical or an ancestor), checked before any step
        // checks out or runs code of the tag: the environment's branch rule constrains only the workflow ref, not the tag.
        const string onBranchName = "Check that the tag is on main or dev";
        void ExpectOnBranchStep(string step, string label)
        {
            Expect($"release.yml: {label} hat den Schritt '{onBranchName}'", step.Length > 0, true);
            Expect($"release.yml: {label}: Tag-Pruefung gegen main und dev (compare, identical/behind, sonst Abbruch)",
                step.Contains("for branch in main dev", StringComparison.Ordinal) &&
                step.Contains("compare/$branch...$sha", StringComparison.Ordinal) &&
                step.Contains("identical", StringComparison.Ordinal) && step.Contains("behind", StringComparison.Ordinal) &&
                step.Contains("exit 1", StringComparison.Ordinal) && step.Contains("set -euo pipefail", StringComparison.Ordinal), true);
            Expect($"release.yml: {label}: Tag-Pruefung loest den Tag ueber refs/tags auf (kein gleichnamiger Branch)",
                step.Contains("commits/refs/tags/$TAG", StringComparison.Ordinal), true);
            // The verified commit id leaves the step; the checkouts use it (never the tag name again: a tag can move).
            Expect($"release.yml: {label}: Tag-Pruefung gibt die gepruefte Commit-ID aus (id ontag, sha=$sha nach GITHUB_OUTPUT, erst nach bestandener Pruefung)",
                step.Contains("        id: ontag\n", StringComparison.Ordinal) &&
                Regex.IsMatch(step, @"behind \]\]; then\n(?:.*\n)*?\s+echo ""sha=\$sha"" >> ""\$GITHUB_OUTPUT""\n\s+exit 0"), true);
            var runAt = step.IndexOf("\n        run: |\n", StringComparison.Ordinal);
            Expect($"release.yml: {label}: Tag-Pruefung nimmt GH_TOKEN mit GH_REPO aus dem env des Schritts, TAG nur aus der Umgebung",
                Regex.IsMatch(step, @"\n        env:\n          GH_TOKEN: \$\{\{ github\.token \}\}\n(?:          #[^\n]*\n)?          GH_REPO: \$\{\{ github\.repository \}\}\n") &&
                runAt >= 0 && !step[runAt..].Contains("${{", StringComparison.Ordinal), true);
        }

        var preflightSteps = StepsOf(preflight);
        var preflightTagCheck = preflightSteps.FindIndex(x => Name(x) == "Check the tag");
        var preflightOnBranch = preflightSteps.FindIndex(x => Name(x) == onBranchName);
        ExpectOnBranchStep(preflightOnBranch >= 0 ? preflightSteps[preflightOnBranch] : string.Empty, "preflight");
        Expect("release.yml: preflight prueft den Tag direkt nach der Tag-Regel", preflightTagCheck >= 0 && preflightOnBranch == preflightTagCheck + 1, true);
        Expect("release.yml: preflight prueft den Tag nur beim Hochladen", preflightOnBranch >= 0 &&
            preflightSteps[preflightOnBranch].Contains("        if: env.UPLOAD == 'true'\n", StringComparison.Ordinal), true);
        Expect("release.yml: preflight checkt den Tag erst nach der Branch-Pruefung aus",
            preflightOnBranch >= 0 && preflightOnBranch < preflightSteps.FindIndex(x => x.Contains("actions/checkout@", StringComparison.Ordinal)), true);
        // No checkout by tag name on the upload path (the tag could move after the check): preflight and manifest check out
        // the commit id of their own verifying step, the build jobs the id from preflight (the tag name only as the
        // fallback of a dry run, where preflight does not resolve it and nothing is uploaded).
        Expect("release.yml: preflight und manifest checken die geprueften Commit-ID aus (steps.ontag.outputs.sha)",
            Regex.Count(yml, @"(?m)^          ref: \$\{\{ steps\.ontag\.outputs\.sha \}\}$"), 2);
        Expect("release.yml: Bau-Jobs checken inputs.ref, sonst die Commit-ID von preflight (Tag-Name nur als Rueckfall des Trockenlaufs)",
            Regex.Count(yml, @"(?m)^          ref: \$\{\{ inputs\.ref \|\| needs\.preflight\.outputs\.tag_sha \|\| format\('refs/tags/\{0\}', inputs\.tag\) \}\}$"), 2);
        Expect("release.yml: kein Checkout allein ueber den Tag-Namen", Regex.IsMatch(yml, @"(?m)^          ref: (refs/tags/)?\$\{\{ inputs\.tag \}\}$"), false);
        Expect("release.yml: preflight gibt die geprueften Commit-ID aus (outputs.tag_sha)",
            preflight.Contains("    outputs:\n      tag_sha: ${{ steps.ontag.outputs.sha }}\n", StringComparison.Ordinal), true);
        Expect("release.yml: jeder Checkout der Bau-Jobs steht nach 'Require the verified commit' (Hochladen ohne geprueften Commit bricht ab)",
            new[] { "windows", "linux" }.All(j =>
            {
                var st = StepsOf(Job(j));
                var guard = st.FindIndex(x => Name(x) == "Require the verified commit");
                return guard >= 0 && guard < st.FindIndex(x => x.Contains("actions/checkout@", StringComparison.Ordinal)) &&
                    st[guard].Contains("        if: env.UPLOAD == 'true'\n", StringComparison.Ordinal) &&
                    st[guard].Contains("TAG_SHA: ${{ needs.preflight.outputs.tag_sha }}", StringComparison.Ordinal) &&
                    st[guard].Contains("[0-9a-f]{40}", StringComparison.Ordinal);
            }), true);

        // The dispatch input ref accepts any ref; refs/pull/... would run foreign code on the persistent runners with a
        // write token. Every job that sees the input rejects refs/ prefixes other than refs/heads/ in its first step.
        foreach (var refJob in new[] { "preflight", "windows", "linux", "manifest" })
        {
            var refSteps = StepsOf(Job(refJob));
            var first = refSteps.Count > 0 ? refSteps[0] : string.Empty;
            Expect($"release.yml: Job {refJob} nimmt inputs.ref nur ueber die Umgebung (REF_INPUT)",
                Job(refJob).Contains("      REF_INPUT: ${{ inputs.ref }}\n", StringComparison.Ordinal), true);
            Expect($"release.yml: Job {refJob} weist im ersten Schritt Refs ausser refs/heads/ ab (kein refs/pull/)",
                first.Contains("REF_INPUT", StringComparison.Ordinal) && first.Contains("refs/", StringComparison.Ordinal) &&
                first.Contains("refs/heads/", StringComparison.Ordinal) && !first.Contains("inputs.ref", StringComparison.Ordinal), true);
        }

        foreach (var buildJob in new[] { "windows", "linux" })
        {
            Expect($"release.yml: Job {buildJob} wartet auf preflight", Job(buildJob).Contains("    needs: preflight\n", StringComparison.Ordinal), true);
        }

        var job = Job("manifest");
        Expect("release.yml: Job manifest vorhanden", job.Length > 0, true);
        Expect("release.yml: Job manifest wartet auf beide Jobs", job.Contains("    needs: [windows, linux]\n", StringComparison.Ordinal), true);
        Expect("release.yml: Job manifest laeuft nur nach Erfolg beider Jobs und nur beim Hochladen", job.Contains(
            "    if: ${{ !cancelled() && needs.windows.result == 'success' && needs.linux.result == 'success' && inputs.upload }}\n",
            StringComparison.Ordinal), true);
        // The only job that sees the signing key runs on a fresh GitHub-hosted runner, behind the environment.
        Expect("release.yml: Job manifest auf dem gehosteten Runner ubuntu-24.04", job.Contains("    runs-on: ubuntu-24.04\n", StringComparison.Ordinal), true);
        Expect("release.yml: Job manifest nennt kein self-hosted-Label", IsSelfHosted(Regex.Match(job, @"(?m)^    runs-on:.*$").Value), false);
        Expect("release.yml: Job manifest mit environment: release-signing", job.Contains("    environment: release-signing\n", StringComparison.Ordinal), true);
        foreach (var buildJob in new[] { "windows", "linux" })
        {
            var buildSteps = StepsOf(Job(buildJob));
            var uploadAt = buildSteps.FindIndex(x => Name(x) == "Upload to the release");
            var reportAt = buildSteps.FindIndex(x => Name(x) == "Report the SHA-256 of the checksum file");
            var report = reportAt >= 0 ? buildSteps[reportAt] : string.Empty;
            Expect($"release.yml: Job {buildJob}: Schritt 'Report the SHA-256' direkt nach dem Hochladen", uploadAt >= 0 && reportAt == uploadAt + 1, true);
            Expect($"release.yml: Job {buildJob}: Report-Schritt (id sums, nur beim Hochladen, hasht die hochgeladene Datei, schreibt sums_sha256 nach GITHUB_OUTPUT)",
                report.Contains("        id: sums\n", StringComparison.Ordinal) && report.Contains("        if: env.UPLOAD == 'true'\n", StringComparison.Ordinal) &&
                report.Contains($"out/release/SHA256SUMS-{buildJob}.txt", StringComparison.Ordinal) &&
                report.Contains("sums_sha256=$hash", StringComparison.Ordinal) && report.Contains("GITHUB_OUTPUT", StringComparison.Ordinal) &&
                report.Contains("[0-9a-f]{64}", StringComparison.Ordinal), true);
            Expect($"release.yml: Job {buildJob} gibt die SHA-256 seiner Pruefsummendatei aus (outputs.sums_sha256)",
                Job(buildJob).Contains("    outputs:\n      sums_sha256: ${{ steps.sums.outputs.sums_sha256 }}\n", StringComparison.Ordinal), true);
        }
        Expect("release.yml: Job manifest darf hochladen (contents: write)", Regex.IsMatch(job, @"    permissions:\n      contents: write\n"), true);

        var steps = StepsOf(job);
        int Index(string name) => steps.FindIndex(s => Name(s) == name);

        var manifestOnBranch = Index(onBranchName);
        ExpectOnBranchStep(manifestOnBranch >= 0 ? steps[manifestOnBranch] : string.Empty, "manifest");
        Expect("release.yml: manifest prueft den Tag direkt nach der Tag-Regel", manifestOnBranch >= 1 && Name(steps[manifestOnBranch - 1]) == "Check the tag and derive the version", true);
        Expect("release.yml: manifest prueft den Tag vor jedem Checkout und vor jedem Schritt, der Code des Tags laeuft",
            manifestOnBranch >= 0 && steps.Take(manifestOnBranch + 1).All(x => !x.Contains("actions/checkout@", StringComparison.Ordinal) && !Regex.IsMatch(x, @"dotnet (run|build|test|project)|package\.cs")), true);

        Expect("release.yml: genau eine Stelle nennt ein Secret", Regex.Count(yml, @"secrets\."), 1);
        var sign = Index("Sign the update manifest");
        Expect("release.yml: Schritt 'Sign the update manifest' vorhanden", sign >= 0, true);
        var signStep = sign >= 0 ? steps[sign] : string.Empty;
        Expect("release.yml: Secret nur im Signier-Schritt (env des Schritts)", Regex.IsMatch(signStep,
            @"\n        env:\n(?:          \S.*\n)*          PAGEWING_UPDATE_SIGNING_KEY: \$\{\{ secrets\.PAGEWING_UPDATE_SIGNING_KEY \}\}\n"), true);
        Expect("release.yml: Signier-Schritt startet keine Action", signStep.Contains("uses:", StringComparison.Ordinal), false);
        Expect("release.yml: Signier-Schritt fuehrt nur das vorher gebaute update-sign aus (eine Zeile, Schluessel aus der Umgebung)", Regex.IsMatch(signStep,
            @"\n        run: dotnet ""\$PACKAGE_TOOL"" update-sign --manifest \S+ --key-env PAGEWING_UPDATE_SIGNING_KEY --id ""\$UPDATE_KEY_ID"" --out \S+\n*$"), true);
        Expect("release.yml: Signier-Schritt baut nichts (kein dotnet run/build/publish, kein package.cs)",
            Regex.IsMatch(signStep, @"dotnet (run|build|publish)|package\.cs"), false);

        // The tool that sees the secret is built from the tag's checkout into a folder of this job, wiped first, as a
        // converted project (own obj and bin, no shared script cache), without build servers or directory imports.
        var buildTool = Index("Build the packaging tool for signing");
        var buildToolStep = buildTool >= 0 ? steps[buildTool] : string.Empty;
        Expect("release.yml: eigener Schritt baut das Werkzeug vor dem Signieren", buildTool >= 0 && buildTool < sign, true);
        Expect("release.yml: Werkzeug-Bau ohne Secret", buildToolStep.Contains("secrets.", StringComparison.Ordinal), false);
        Expect("release.yml: Werkzeug-Bau in einen frisch geleerten Ordner des Jobs", buildToolStep.Contains(@"rm -rf ""$RUNNER_TEMP/package-tool""", StringComparison.Ordinal) &&
            buildToolStep.Contains(@"dotnet project convert tools/package.cs -o ""$RUNNER_TEMP/package-tool/src""", StringComparison.Ordinal), true);
        Expect("release.yml: Werkzeug-Bau ohne Buildserver und ohne Directory-Importe", Regex.IsMatch(buildToolStep,
            @"dotnet build ""\$RUNNER_TEMP/package-tool/src"" -c Release -o ""\$RUNNER_TEMP/package-tool/bin"" --disable-build-servers -noAutoResponse -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false"), true);
        Expect("release.yml: PACKAGE_TOOL zeigt auf den frisch gebauten Ordner",
            buildToolStep.Contains(@"echo ""PACKAGE_TOOL=$RUNNER_TEMP/package-tool/bin/package.dll"" >> ""$GITHUB_ENV""", StringComparison.Ordinal), true);
        Expect("release.yml: GH_TOKEN nur im env von Schritten", Regex.Count(yml, @"(?m)^          GH_TOKEN: \$\{\{ github\.token \}\}$"), Regex.Count(yml, "GH_TOKEN:"));
        Expect("release.yml: jeder Schritt mit GH_TOKEN setzt GH_REPO", Regex.Count(yml, @"GH_REPO: \$\{\{ github\.repository \}\}"), Regex.Count(yml, "GH_TOKEN:"));
        Expect("release.yml: jeder Checkout ohne gespeicherte Zugangsdaten", Regex.Count(yml, "persist-credentials: false"), Regex.Count(yml, "uses: actions/checkout@"));

        var test = Index("Check that the release key is not the placeholder");
        var download = Index("Download the checksums and the file sizes");
        var sumsCheck = Index("Verify the checksum files against the build jobs");
        var build = Index("Build the update manifest");
        var verify = Index("Verify the signature with the keys in UpdateTrust.cs");
        var upload = Index("Upload the manifest to the release");
        Expect("release.yml: Reihenfolge Platzhalter-Test, Download, Summenvergleich, Bau, Signatur, Pruefung, Hochladen",
            test >= 0 && test < download && download < sumsCheck && sumsCheck < build && build < sign && sign < verify && verify < upload, true);
        var sumsStep = sumsCheck >= 0 ? steps[sumsCheck] : string.Empty;
        Expect("release.yml: Summenvergleich nach dem Werkzeug-Bau (nutzt PACKAGE_TOOL)", buildTool >= 0 && buildTool < sumsCheck, true);
        Expect("release.yml: Summenvergleich nimmt die Hashes der Bau-Jobs ueber die Umgebung (kein ${{ }} in der Befehlszeile)",
            Regex.IsMatch(sumsStep, @"\n        env:\n(?:          \S.*\n)*          WINDOWS_SUMS_SHA256: \$\{\{ needs\.windows\.outputs\.sums_sha256 \}\}\n") &&
            Regex.IsMatch(sumsStep, @"\n        env:\n(?:          \S.*\n)*          LINUX_SUMS_SHA256: \$\{\{ needs\.linux\.outputs\.sums_sha256 \}\}\n") &&
            sumsStep.Contains("\n        run: |\n", StringComparison.Ordinal) &&
            !sumsStep[sumsStep.IndexOf("\n        run: |\n", StringComparison.Ordinal)..].Contains("${{", StringComparison.Ordinal), true);
        Expect("release.yml: Summenvergleich prueft beide heruntergeladenen Dateien mit sums-verify und bricht bei Fehler ab",
            sumsStep.Contains("set -euo pipefail", StringComparison.Ordinal) &&
            sumsStep.Contains(@"sums-verify --file out/manifest/SHA256SUMS-windows.txt --sha256 ""$WINDOWS_SUMS_SHA256""", StringComparison.Ordinal) &&
            sumsStep.Contains(@"sums-verify --file out/manifest/SHA256SUMS-linux.txt --sha256 ""$LINUX_SUMS_SHA256""", StringComparison.Ordinal), true);
        Expect("release.yml: Summenvergleich ohne Secret und ohne Action", sumsStep.Contains("secrets.", StringComparison.Ordinal) || sumsStep.Contains("uses:", StringComparison.Ordinal), false);
        Expect("release.yml: Platzhalter-Test laeuft scharf (PAGEWING_REQUIRE_RELEASE_KEY)", test >= 0 &&
            steps[test].Contains("PAGEWING_REQUIRE_RELEASE_KEY: '1'", StringComparison.Ordinal) &&
            steps[test].Contains("ReleaseKeysAreNotThePlaceholderWhenReleasing", StringComparison.Ordinal), true);
        Expect("release.yml: Pruefung mit den Schluesseln des Programms", verify >= 0 &&
            steps[verify].Contains("update-verify --manifest out/manifest/update-manifest.json --sig out/manifest/update-manifest.json.sig --keys-from-source", StringComparison.Ordinal), true);
        Expect("release.yml: Manifest und Signatur werden mit --clobber hochgeladen", upload >= 0 &&
            steps[upload].Contains(@"gh release upload ""$TAG"" out/manifest/update-manifest.json out/manifest/update-manifest.json.sig --clobber", StringComparison.Ordinal), true);

        var keyId = Regex.Match(job, @"(?m)^      UPDATE_KEY_ID: (?<id>[A-Za-z0-9._-]+)$").Groups["id"].Value;
        Expect("release.yml: Job manifest nennt die Schluesselkennung (UPDATE_KEY_ID)", keyId.Length > 0, true);
        var trustText = File.Exists(TrustFile) ? File.ReadAllText(TrustFile) : string.Empty;
        var shippedIds = Regex.Matches(trustText, @"new UpdateKey\(\s*""(?<id>[^""]+)""").Select(m => m.Groups["id"].Value).ToList();
        if (shippedIds.Count > 0)
        {
            Expect($"release.yml: UPDATE_KEY_ID {keyId} steht in UpdateTrust.cs", shippedIds.Contains(keyId), true);
        }

        foreach (var (file, text) in new[] { (workflow, yml), (container, File.ReadAllText(container)) })
        {
            Expect($"release.yml: {file} setzt PagewingUpdateTestBuild nie",
                Regex.IsMatch(text, "PagewingUpdateTest|PAGEWING_UPDATE_TESTBUILD", RegexOptions.IgnoreCase), false);
        }
    }

    // The test build of the updater (packaging/README.md, "Update pruefen") may only come from an explicit -p: on the
    // command line. MSBuild turns environment variables into properties (names ignore case), so a variable inherited by
    // a release job would otherwise switch it on: Directory.Build.props neutralizes such values and its guard
    // (CheckPagewingUpdateTestBuildSource) fails the build. Runs dotnet msbuild on the Core project in child processes
    // with an environment cleaned of the three names; nothing is built (only the guard target or an evaluation).
    private static void UpdateTestBuildGuardParts()
    {
        const string project = "src/Pagewing.Core/Pagewing.Core.csproj";
        const string guard = "-t:CheckPagewingUpdateTestBuildSource";
        if (!File.Exists(project))
        {
            Expect("Testaufbau-Waechter: Core-Projekt vorhanden", false, true);
            return;
        }

        // A valid public P-256 key (the placeholder of UpdateTrust.cs; its private half was discarded).
        const string key = "-p:PagewingUpdateTestKey=" + TrustSource.PlaceholderKey;
        string[] names = ["PagewingUpdateTestBuild", "PagewingUpdateTestPort", "PagewingUpdateTestKey"];
        (int Code, string Output) Msbuild(Dictionary<string, string> environment, params string[] arguments)
        {
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in (string[])["msbuild", project, "-nologo", "-nodeReuse:false", "-v:m", .. arguments])
            {
                psi.ArgumentList.Add(argument);
            }

            foreach (var name in psi.Environment.Keys.Where(k => names.Any(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                psi.Environment.Remove(name);
            }

            foreach (var (name, value) in environment)
            {
                psi.Environment[name] = value;
            }

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        var none = new Dictionary<string, string>();
        Expect("Testaufbau-Waechter: ohne Eigenschaft und Variable laeuft er durch", Msbuild(none, guard).Code, 0);
        Expect("Testaufbau-Waechter: -p: auf der Kommandozeile ist erlaubt (auch mit -c Release)",
            Msbuild(none, guard, "-p:Configuration=Release", "-p:PagewingUpdateTestBuild=true", "-p:PagewingUpdateTestPort=8765", key).Code, 0);

        foreach (var (name, value) in new[]
        {
            ("PagewingUpdateTestBuild", "True"), ("PagewingUpdateTestPort", "8765"), ("PagewingUpdateTestKey", TrustSource.PlaceholderKey),
            ("PAGEWINGUPDATETESTBUILD", "true"),
        })
        {
            var (code, output) = Msbuild(new() { [name] = value }, guard, "-p:Configuration=Release");
            Expect($"Testaufbau-Waechter: Umgebungsvariable {name} bricht den Bau ab", code != 0 && output.Contains("Umgebungsvariable", StringComparison.Ordinal), true);
        }

        // Even with -p: on the command line, an environment variable of the same name is refused.
        Expect("Testaufbau-Waechter: Umgebungsvariable neben -p: bricht ebenfalls ab",
            Msbuild(new() { ["PagewingUpdateTestBuild"] = "true" }, guard, "-p:PagewingUpdateTestBuild=true", "-p:PagewingUpdateTestPort=8765", key).Code != 0, true);

        // The evaluation itself never switches: an environment variable leaves the release trust in place.
        var all = new Dictionary<string, string>
        {
            ["PagewingUpdateTestBuild"] = "True",
            ["PagewingUpdateTestPort"] = "8765",
            ["PagewingUpdateTestKey"] = TrustSource.PlaceholderKey,
        };
        var evaluated = Msbuild(all, "-p:Configuration=Release", "-getProperty:DefineConstants", "-getProperty:PagewingUpdateTestBuild", "-getItem:Compile", "-getItem:AssemblyMetadata");
        Expect("Testaufbau-Waechter: mit Umgebungsvariablen keine Konstante PAGEWING_UPDATE_TESTBUILD",
            evaluated.Code == 0 && !evaluated.Output.Contains("PAGEWING_UPDATE_TESTBUILD", StringComparison.Ordinal), true);
        Expect("Testaufbau-Waechter: mit Umgebungsvariablen bleibt UpdateTrust.cs, kein UpdateTrust.TestBuild.cs",
            evaluated.Output.Contains("UpdateTrust.cs", StringComparison.Ordinal) && !evaluated.Output.Contains("UpdateTrust.TestBuild.cs", StringComparison.Ordinal), true);
        Expect("Testaufbau-Waechter: mit Umgebungsvariablen keine Metadaten des Testaufbaus",
            evaluated.Output.Contains("PagewingUpdateTestKey", StringComparison.Ordinal), false);
        var explicitBuild = Msbuild(none, "-p:PagewingUpdateTestBuild=true", "-p:PagewingUpdateTestPort=8765", key, "-getProperty:DefineConstants", "-getItem:Compile");
        Expect("Testaufbau-Waechter: mit -p: gilt der Testaufbau (Gegenprobe)",
            explicitBuild.Output.Contains("PAGEWING_UPDATE_TESTBUILD", StringComparison.Ordinal) && explicitBuild.Output.Contains("UpdateTrust.TestBuild.cs", StringComparison.Ordinal), true);
    }

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
