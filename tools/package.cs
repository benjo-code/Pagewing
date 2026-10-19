#:property PublishAot=false

// Packaging tool of Pagewing (grows with Milestone 8; further subcommands are added to Commands).
//   dotnet run tools/package.cs -- check-binary <file>... [--max-glibc 2.35]
//   dotnet run tools/package.cs -- layout --rid <win-x64|linux-x64> --app <dir> --cli <dir> --out <dir> [--repo <root>]
//   dotnet run tools/package.cs -- zip --layout <dir> --version <x.y.z> --out <file>
//   dotnet run tools/package.cs -- sums <file>... --out <SHA256SUMS.txt>
//   dotnet run tools/package.cs -- inno-install [--dir <dir>]                         (Windows)
//   dotnet run tools/package.cs -- installer --layout <dir> --version <x.y.z> --out <file> [--iscc <ISCC.exe>] [--repo <root>]   (Windows)
//   dotnet run tools/package.cs -- installer-smoke <setup.exe> [--repo <root>]          (Windows)
//   dotnet run tools/package.cs -- selftest
// Run from the repository root. Only inno-install uses the network (one download with a pinned SHA-256).

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
        "  inno-install [--dir <Ordner>]                                     laedt Inno Setup (feste Version, SHA-256 geprueft) ins Benutzerprofil (Windows)\n" +
        "  installer --layout <Ordner> --version <x.y.z> --out <Datei> [--iscc <ISCC.exe>]   baut Pagewing-<Version>-win-x64-setup.exe (Windows)\n" +
        "  installer-smoke <Setup.exe>                                       installiert still, prueft, installiert erneut, deinstalliert (Windows)\n" +
        "  selftest                                     prueft die Werkzeugteile";

    // Command name -> handler (receives the arguments after the command name). Add new subcommands here.
    private static readonly Dictionary<string, Func<string[], int>> Commands = new(StringComparer.Ordinal)
    {
        ["check-binary"] = CheckBinary.Run,
        ["layout"] = Layout.Run,
        ["zip"] = ZipCommand.Run,
        ["sums"] = Sums.Run,
        ["inno-install"] = InnoInstall.Run,
        ["installer"] = Installer.Run,
        ["installer-smoke"] = InstallerSmoke.Run,
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

    // null when the value is a SHA-256 in lower-case hex; otherwise why it is not pinned.
    public static string? PinProblem(string pinned) =>
        pinned.Length == 64 && pinned.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? null
            : "Pruefsumme nicht festgehalten (hash not pinned): InnoSetup.Sha256 in tools/package.cs muss eine SHA-256 in Kleinbuchstaben sein";

    // null when the stream has the expected size and the pinned SHA-256; otherwise the reason.
    public static string? VerifyDownload(Stream stream, long expectedSize, string pinned)
    {
        if (PinProblem(pinned) is { } problem)
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
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = client.GetAsync(InnoSetup.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var source = response.Content.ReadAsStream();
                using var target = File.Create(download);
                source.CopyTo(target);
            }

            using (var stream = File.OpenRead(download))
            {
                if (InnoSetup.VerifyDownload(stream, InnoSetup.Size, InnoSetup.Sha256) is { } problem)
                {
                    Console.Error.WriteLine($"inno-install: Download abgelehnt, nichts ausgefuehrt: {problem}");
                    return 1;
                }
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
        InstallerParts();
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
