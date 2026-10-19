#:property PublishAot=false

// Licence inventory of Pagewing (third-party components, their licence texts and the generated notices file).
//   dotnet run tools/licenses.cs -- generate [--root <dir>]
//   dotnet run tools/licenses.cs -- check [--publish-dir <dir>]... [--root <dir>]
//   dotnet run tools/licenses.cs -- selftest
// Run from the repository root. The tool never uses the network: it reads the manifest third-party/licenses.json,
// the licence texts under third-party/, Directory.Packages.props, the NuGet cache (nuspec files) and the publish folders.
// check also asks `dotnet list <project> package --include-transitive --format json` (App and CLI) for the package set;
// the hidden option --packages-json <file> replaces that call with a saved output (used by selftest).

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

return Cli.Run(args);

static class Cli
{
    private const string Usage =
        "Aufruf: dotnet run tools/licenses.cs -- <Befehl>\n" +
        "  generate [--root <Ordner>]                         schreibt src/Pagewing.App/Resources/ThirdPartyNotices.txt\n" +
        "  check [--publish-dir <Ordner>]... [--root <Ordner>] prueft Manifest, Lizenzdateien, Publish-Ordner, PDFium, Hinweisdatei\n" +
        "  selftest                                           prueft die Werkzeugteile";

    // Command name -> handler (receives the arguments after the command name). Add new subcommands here.
    private static readonly Dictionary<string, Func<string[], int>> Commands = new(StringComparer.Ordinal)
    {
        ["generate"] = Generate,
        ["check"] = Check,
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

    private sealed record Options(string Root, List<string> PublishDirs, string? PackagesJson);

    private static Options? ParseOptions(string[] args)
    {
        var root = ".";
        var dirs = new List<string>();
        string? packagesJson = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length)
            {
                root = args[++i];
            }
            else if (args[i] == "--publish-dir" && i + 1 < args.Length)
            {
                dirs.Add(args[++i]);
            }
            else if (args[i] == "--packages-json" && i + 1 < args.Length)
            {
                packagesJson = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"unbekannte Option: {args[i]}");
                return null;
            }
        }

        return new Options(root, dirs, packagesJson);
    }

    private static int Generate(string[] args)
    {
        var o = ParseOptions(args);
        if (o is null)
        {
            return 2;
        }

        var paths = new Paths(o.Root);
        var errors = new List<string>();
        var manifest = Inventory.LoadManifest(paths, errors);
        string? text = null;
        if (manifest is not null)
        {
            text = Inventory.Render(paths, manifest, errors);
        }

        if (text is null || errors.Count > 0)
        {
            return Report(errors);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(paths.Notices)!);
        File.WriteAllText(paths.Notices, text, new UTF8Encoding(false));
        Console.WriteLine($"{paths.Display(paths.Notices)} geschrieben.");
        return 0;
    }

    private static int Check(string[] args)
    {
        var o = ParseOptions(args);
        if (o is null)
        {
            return 2;
        }

        var paths = new Paths(o.Root);
        var errors = new List<string>();
        var packages = o.PackagesJson is null
            ? PackageList.FromDotnet(paths, errors)
            : PackageList.Parse(File.ReadAllText(o.PackagesJson), o.PackagesJson, errors);
        if (errors.Count == 0)
        {
            errors.AddRange(Inventory.Validate(new Inputs(paths, packages, NuGetCache(), o.PublishDirs)));
        }

        var code = Report(errors);
        if (code == 0)
        {
            Console.WriteLine("licenses check: ok");
        }

        return code;
    }

    private static int Report(List<string> errors)
    {
        foreach (var e in errors)
        {
            Console.Error.WriteLine(e);
        }

        return errors.Count == 0 ? 0 : 1;
    }

    private static string NuGetCache()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        return !string.IsNullOrEmpty(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
}

// Repository-relative locations; Display() gives the forward-slash path used in error messages.
sealed class Paths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Manifest => Path.Combine(Root, "third-party", "licenses.json");

    public string Notices => Path.Combine(Root, "src", "Pagewing.App", "Resources", "ThirdPartyNotices.txt");

    public string PackageProps => Path.Combine(Root, "Directory.Packages.props");

    public string OwnLicense => Path.Combine(Root, "LICENSE");

    public string Resolve(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Display(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');
}

sealed record Entry(
    string Id,
    string Kind,
    string Version,
    string License,
    string LicenseFile,
    string[] LicenseFiles,
    string[] Files,
    bool Shipped,
    bool SdkVersion = false);

sealed record Package(string Id, string Version);

sealed record Inputs(Paths Paths, List<Package> Packages, string NuGetCache, List<string> PublishDirs);

static class PackageList
{
    private static readonly string[] Projects = ["src/Pagewing.App/Pagewing.App.csproj", "src/Pagewing.Cli/Pagewing.Cli.csproj"];

    public static List<Package> FromDotnet(Paths paths, List<string> errors)
    {
        var all = new List<Package>();
        foreach (var project in Projects)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "list", paths.Resolve(project), "package", "--include-transitive", "--format", "json" })
            {
                psi.ArgumentList.Add(a);
            }

            string stdout;
            int exit;
            string stderr;
            try
            {
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                stderr = errTask.Result;
                exit = p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                errors.Add($"{project}: dotnet nicht gefunden ({ex.Message})");
                return all;
            }

            if (exit != 0)
            {
                errors.Add($"{project}: dotnet list package fehlgeschlagen (zuerst dotnet restore): {stderr.Trim()} {stdout.Trim()}");
                continue;
            }

            all.AddRange(Parse(stdout, project, errors));
        }

        return all.DistinctBy(p => (p.Id.ToLowerInvariant(), p.Version)).ToList();
    }

    // Reads the JSON of `dotnet list package --format json` (top-level and transitive packages of all frameworks).
    public static List<Package> Parse(string json, string source, List<string> errors)
    {
        var result = new List<Package>();
        var start = json.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            errors.Add($"{source}: keine JSON-Ausgabe von dotnet list package");
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            if (!doc.RootElement.TryGetProperty("projects", out var projects))
            {
                errors.Add($"{source}: JSON ohne \"projects\"");
                return result;
            }

            foreach (var project in projects.EnumerateArray())
            {
                if (!project.TryGetProperty("frameworks", out var frameworks))
                {
                    continue;
                }

                foreach (var framework in frameworks.EnumerateArray())
                {
                    foreach (var group in new[] { "topLevelPackages", "transitivePackages" })
                    {
                        if (!framework.TryGetProperty(group, out var list))
                        {
                            continue;
                        }

                        foreach (var p in list.EnumerateArray())
                        {
                            result.Add(new Package(p.GetProperty("id").GetString()!, p.GetProperty("resolvedVersion").GetString()!));
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"{source}: JSON nicht lesbar: {ex.Message}");
        }

        return result;
    }
}

static partial class Inventory
{
    private static readonly string[] Kinds = ["nuget", "native", "asset", "runtime"];

    [GeneratedRegex("""<license\s+type="expression"[^>]*>\s*([^<]+?)\s*</license>""")]
    private static partial Regex NuspecExpression();

    [GeneratedRegex("""<license\s+type="file"[^>]*>""")]
    private static partial Regex NuspecFile();

    [GeneratedRegex("""<PackageVersion\s+Include="(?<id>bblanchon\.PDFium\.[^"]+)"\s+Version="(?<v>[^"]+)""")]
    private static partial Regex PdfiumVersion();

    // Own executables and debug symbols are not third-party files.
    private static bool IsIgnoredPublishFile(string name) =>
        name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pagewing", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pagewing.exe", StringComparison.OrdinalIgnoreCase);

    public static List<Entry>? LoadManifest(Paths paths, List<string> errors)
    {
        var where = paths.Display(paths.Manifest);
        if (!File.Exists(paths.Manifest))
        {
            errors.Add($"{where}: Datei fehlt");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.Manifest));
            var list = new List<Entry>();
            var index = 0;
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = Str(e, "id");
                var label = $"{where}: entries[{index}] ({(id.Length == 0 ? "ohne id" : id)})";
                index++;
                var kind = Str(e, "kind");
                if (id.Length == 0 || Str(e, "version").Length == 0 || Str(e, "license").Length == 0 || Str(e, "licenseFile").Length == 0)
                {
                    errors.Add($"{label}: id, version, license und licenseFile sind Pflicht");
                    continue;
                }

                if (!Kinds.Contains(kind, StringComparer.Ordinal))
                {
                    errors.Add($"{label}: unbekannte kind \"{kind}\" (erlaubt: {string.Join(", ", Kinds)})");
                    continue;
                }

                list.Add(new Entry(
                    id, kind, Str(e, "version"), Str(e, "license"), Str(e, "licenseFile"),
                    Strings(e, "licenseFiles"), Strings(e, "files"),
                    !e.TryGetProperty("shipped", out var shipped) || shipped.ValueKind != JsonValueKind.False,
                    e.TryGetProperty("sdkVersion", out var sdkVersion) && sdkVersion.ValueKind == JsonValueKind.True));
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            errors.Add($"{where}: nicht lesbar: {ex.Message}");
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];

    // The licence files of an entry in output order: licenseFile first, then licenseFiles (a trailing "/" means every file of that folder).
    private static List<string> Expand(Paths paths, Entry entry, string where, List<string> errors)
    {
        var result = new List<string>();
        foreach (var spec in entry.LicenseFiles.Prepend(entry.LicenseFile))
        {
            if (spec.EndsWith('/'))
            {
                var dir = paths.Resolve(spec.TrimEnd('/'));
                var files = Directory.Exists(dir)
                    ? Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal).Select(paths.Display).ToList()
                    : [];
                if (files.Count == 0)
                {
                    errors.Add($"{where}: Eintrag {entry.Id}: Lizenzordner {spec} fehlt oder ist leer");
                }

                result.AddRange(files);
                continue;
            }

            var full = paths.Resolve(spec);
            if (!File.Exists(full) || new FileInfo(full).Length == 0)
            {
                errors.Add($"{where}: Eintrag {entry.Id}: Lizenzdatei {spec} fehlt oder ist leer");
                continue;
            }

            result.Add(spec);
        }

        return result;
    }

    // Packages the SDK adds on its own (ILCompiler, ILLink tasks) follow the patch version of the installed SDK, which differs
    // between machines. Entries marked "sdkVersion" therefore only have to agree in major.minor; all others agree exactly.
    private static bool VersionMatches(Entry entry, string actual)
    {
        if (entry.Version == actual)
        {
            return true;
        }

        if (!entry.SdkVersion)
        {
            return false;
        }

        static string[] MajorMinor(string v) => v.Split('.').Take(2).ToArray();
        return MajorMinor(entry.Version).SequenceEqual(MajorMinor(actual), StringComparer.Ordinal);
    }

    public static List<string> Validate(Inputs input)
    {
        var paths = input.Paths;
        var errors = new List<string>();
        var manifest = LoadManifest(paths, errors);
        if (manifest is null)
        {
            return errors;
        }

        var where = paths.Display(paths.Manifest);

        // 2. Licence files exist and are not empty (also done by Render, which has to read them).
        var text = Render(paths, manifest, errors);

        // 1. Every package of App and CLI is in the manifest with the same version and the SPDX of its nuspec.
        var nuget = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest.Where(e => e.Kind == "nuget"))
        {
            if (!nuget.TryAdd(e.Id, e))
            {
                errors.Add($"{where}: Eintrag {e.Id} kommt mehrfach vor");
            }
        }

        foreach (var package in input.Packages)
        {
            if (!nuget.TryGetValue(package.Id, out var entry))
            {
                errors.Add($"{where}: Paket {package.Id} {package.Version} fehlt im Manifest");
                continue;
            }

            if (!VersionMatches(entry, package.Version))
            {
                errors.Add($"{where}: Paket {package.Id}: Manifest hat Version {entry.Version}, das Projekt {package.Version}");
            }

            CheckNuspec(input.NuGetCache, package, entry, where, errors);
        }

        foreach (var e in nuget.Values)
        {
            if (!input.Packages.Any(p => string.Equals(p.Id, e.Id, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{where}: Eintrag {e.Id} gehoert zu keinem Paket von App oder CLI");
            }
        }

        // 3. Every file of the publish folders (except the own executable and debug symbols) belongs to exactly one entry.
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest)
        {
            foreach (var f in e.Files)
            {
                if (!owners.TryAdd(f, [e.Id]))
                {
                    owners[f].Add(e.Id);
                }
            }
        }

        foreach (var (file, ids) in owners.Where(o => o.Value.Count > 1))
        {
            errors.Add($"{where}: Datei {file} gehoert zu mehreren Eintraegen: {string.Join(", ", ids)}");
        }

        foreach (var dir in input.PublishDirs)
        {
            if (!Directory.Exists(dir))
            {
                errors.Add($"{dir}: Publish-Ordner nicht gefunden");
                continue;
            }

            foreach (var full in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(dir, full).Replace('\\', '/');
                if (!IsIgnoredPublishFile(Path.GetFileName(full)) && !owners.ContainsKey(relative))
                {
                    errors.Add($"{dir.TrimEnd('/', '\\')}/{relative}: Datei gehoert zu keinem Eintrag in {where} (files)");
                }
            }
        }

        // 4. The PDFium version of the manifest equals Directory.Packages.props; both packages have one version.
        CheckPdfium(paths, manifest, where, errors);

        // 5. The notices file is current.
        var notices = paths.Display(paths.Notices);
        if (text is not null)
        {
            if (!File.Exists(paths.Notices))
            {
                errors.Add($"{notices}: fehlt (dotnet run tools/licenses.cs -- generate)");
            }
            else if (File.ReadAllText(paths.Notices).Replace("\r\n", "\n", StringComparison.Ordinal) != text)
            {
                errors.Add($"{notices}: veraltet (dotnet run tools/licenses.cs -- generate)");
            }
        }

        return errors;
    }

    private static void CheckNuspec(string cache, Package package, Entry entry, string where, List<string> errors)
    {
        var id = package.Id.ToLowerInvariant();
        var nuspec = Path.Combine(cache, id, package.Version.ToLowerInvariant(), id + ".nuspec");
        if (!File.Exists(nuspec))
        {
            errors.Add($"{nuspec}: nuspec nicht gefunden (dotnet restore?), SPDX von {package.Id} nicht pruefbar");
            return;
        }

        var xml = File.ReadAllText(nuspec);
        var expression = NuspecExpression().Match(xml);
        if (expression.Success)
        {
            if (expression.Groups[1].Value != entry.License)
            {
                errors.Add($"{where}: Paket {package.Id}: Manifest hat Lizenz {entry.License}, die nuspec {expression.Groups[1].Value}");
            }
        }
        else if (!NuspecFile().IsMatch(xml))
        {
            errors.Add($"{nuspec}: weder SPDX-Ausdruck noch Lizenzdatei in der nuspec, Lizenz von {package.Id} bitte pruefen");
        }

        // A nuspec with type="file" has no SPDX expression to compare; the manifest value stands (see third-party/*/README.md).
    }

    private static void CheckPdfium(Paths paths, List<Entry> manifest, string where, List<string> errors)
    {
        var props = paths.Display(paths.PackageProps);
        if (!File.Exists(paths.PackageProps))
        {
            errors.Add($"{props}: Datei fehlt");
            return;
        }

        var versions = PdfiumVersion().Matches(File.ReadAllText(paths.PackageProps))
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
        if (versions.Count < 2 || versions.Values.Distinct().Count() != 1)
        {
            errors.Add($"{props}: die bblanchon.PDFium.*-Pakete brauchen dieselbe Version ({string.Join(", ", versions.Select(v => $"{v.Key} {v.Value}"))})");
            return;
        }

        var expected = versions.Values.First();
        foreach (var id in versions.Keys)
        {
            var entry = manifest.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                errors.Add($"{where}: Eintrag {id} fehlt");
            }
            else if (entry.Version != expected)
            {
                errors.Add($"{where}: Eintrag {id} hat Version {entry.Version}, {props} hat {expected}");
            }
        }
    }

    // Reads a licence text: strict UTF-8 (BOM dropped) or, if that fails, ISO-8859-1; line endings LF; trailing blank lines trimmed.
    private static string ReadText(string full)
    {
        var bytes = File.ReadAllBytes(full);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    // Builds the notices text; null if a licence file is missing (the errors say which).
    public static string? Render(Paths paths, List<Entry> manifest, List<string> errors)
    {
        var where = paths.Display(paths.Manifest);
        var before = errors.Count;
        var sb = new StringBuilder();
        sb.Append("Hinweise zu Drittsoftware / Third-party notices\n\n");
        sb.Append("Pagewing steht unter der GPL-3.0-or-later. Es enthaelt oder nutzt die folgenden Komponenten unter ihren eigenen Lizenzen.\n");
        sb.Append("Pagewing is licensed under the GPL-3.0-or-later. It includes or uses the following components under their own licences.\n\n");

        sb.Append("====\n");
        sb.Append("Pagewing\nGPL-3.0-or-later\n\n");
        if (File.Exists(paths.OwnLicense) && new FileInfo(paths.OwnLicense).Length > 0)
        {
            sb.Append(ReadText(paths.OwnLicense));
        }
        else
        {
            errors.Add($"{paths.Display(paths.OwnLicense)}: Datei fehlt oder ist leer");
        }

        // Entries sharing SPDX and licence files form one section: the text appears once, with all members above it.
        var groups = manifest
            .Where(e => e.Shipped)
            .GroupBy(e => $"{e.License}|{e.LicenseFile}|{string.Join('|', e.LicenseFiles)}")
            .Select(g => g.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderBy(g => g[0].Id, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var files = Expand(paths, group[0], where, errors);
            sb.Append("====\n");
            foreach (var e in group)
            {
                sb.Append($"{e.Id} {e.Version}\n");
            }

            sb.Append($"{group[0].License}\n");
            foreach (var f in files)
            {
                sb.Append($"\n--- {f} ---\n\n");
                sb.Append(ReadText(paths.Resolve(f)));
            }
        }

        sb.Append("====\n");

        // Hidden (not shipped) entries still need their licence files to exist.
        foreach (var e in manifest.Where(e => !e.Shipped))
        {
            Expand(paths, e, where, errors);
        }

        return errors.Count == before ? sb.ToString() : null;
    }
}

static class SelfTest
{
    private static int s_checks;
    private static int s_failures;

    private const string NuspecMit = "<package><metadata><license type=\"expression\">MIT</license></metadata></package>";
    private const string NuspecApache = "<package><metadata><license type=\"expression\">Apache-2.0</license></metadata></package>";
    private const string NuspecFile = "<package><metadata><license type=\"file\">LICENSE</license></metadata></package>";

    // A complete, consistent repository: manifest, licence files, props, NuGet cache, publish folder, notices.
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Dir = Path.Combine(Path.GetTempPath(), "pagewing-licenses-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Write("LICENSE", "GPL TEXT\nline two\n");
            Write("third-party/lib/LICENSE", "MIT TEXT\n");
            Write("third-party/pdfium/LICENSE", "PDFIUM BUILD LICENSE\n");
            Write("third-party/pdfium/licenses/a.txt", "ALPHA\n");
            Write("third-party/pdfium/licenses/b.txt", "BETA\n");
            Write("Directory.Packages.props", Props("1.2.3", "1.2.3"));
            Write("cache/foo/1.0.0/foo.nuspec", NuspecMit);
            Write("cache/bar.native/2.0.0/bar.native.nuspec", NuspecFile);
            Write("cache/bblanchon.pdfium.linux/1.2.3/bblanchon.pdfium.linux.nuspec", NuspecApache);
            Write("cache/bblanchon.pdfium.win32/1.2.3/bblanchon.pdfium.win32.nuspec", NuspecApache);
            Write("publish/Pagewing", "exe");
            Write("publish/Pagewing.pdb", "pdb");
            Write("publish/libbar.so", "native");
            Write("publish/libpdfium.so", "native");
            WriteManifest(Manifest());
            Paths = new Paths(Dir);
            Generate();
        }

        public string Dir { get; }

        public Paths Paths { get; }

        public string Publish => Path.Combine(Dir, "publish");

        public string Cache => Path.Combine(Dir, "cache");

        public List<Package> Packages { get; set; } =
        [
            new("Foo", "1.0.0"), new("Bar.Native", "2.0.0"),
            new("bblanchon.PDFium.Linux", "1.2.3"), new("bblanchon.PDFium.Win32", "1.2.3"),
        ];

        public static string Props(string linux, string win32) =>
            $"<Project><ItemGroup>\n<PackageVersion Include=\"bblanchon.PDFium.Linux\" Version=\"{linux}\" />\n" +
            $"<PackageVersion Include=\"bblanchon.PDFium.Win32\" Version=\"{win32}\" />\n</ItemGroup></Project>";

        public static string Manifest(string fooVersion = "1.0.0", string fooLicense = "MIT", bool withBar = true, string pdfiumWin = "1.2.3") =>
            "{ \"entries\": [\n" +
            $"  {{ \"id\": \"Foo\", \"kind\": \"nuget\", \"version\": \"{fooVersion}\", \"license\": \"{fooLicense}\", \"licenseFile\": \"third-party/lib/LICENSE\" }},\n" +
            (withBar
                ? "  { \"id\": \"Bar.Native\", \"kind\": \"nuget\", \"version\": \"2.0.0\", \"license\": \"BSD-3-Clause\", \"licenseFile\": \"third-party/lib/LICENSE\", \"files\": [\"libbar.so\"] },\n"
                : string.Empty) +
            "  { \"id\": \"bblanchon.PDFium.Linux\", \"kind\": \"nuget\", \"version\": \"1.2.3\", \"license\": \"Apache-2.0\", \"licenseFile\": \"third-party/pdfium/LICENSE\", \"licenseFiles\": [\"third-party/pdfium/licenses/\"], \"files\": [\"libpdfium.so\"] },\n" +
            $"  {{ \"id\": \"bblanchon.PDFium.Win32\", \"kind\": \"nuget\", \"version\": \"{pdfiumWin}\", \"license\": \"Apache-2.0\", \"licenseFile\": \"third-party/pdfium/LICENSE\", \"licenseFiles\": [\"third-party/pdfium/licenses/\"], \"files\": [\"pdfium.dll\"] }},\n" +
            "  { \"id\": \"Runtime\", \"kind\": \"runtime\", \"version\": \"10\", \"license\": \"MIT\", \"licenseFile\": \"third-party/lib/LICENSE\" },\n" +
            "  { \"id\": \"BuildOnly\", \"kind\": \"asset\", \"version\": \"1\", \"license\": \"MIT\", \"licenseFile\": \"third-party/lib/LICENSE\", \"shipped\": false }\n" +
            "] }";

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void WriteManifest(string json) => Write("third-party/licenses.json", json);

        public void Generate()
        {
            var errors = new List<string>();
            var manifest = Inventory.LoadManifest(Paths, errors);
            var text = Inventory.Render(Paths, manifest!, errors);
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.Notices)!);
            File.WriteAllText(Paths.Notices, text ?? string.Empty);
        }

        public List<string> Check(bool withPublish = true) =>
            Inventory.Validate(new Inputs(Paths, Packages, Cache, withPublish ? [Publish] : []));

        public void Dispose() => Directory.Delete(Dir, true);
    }

    public static int Run(string[] args)
    {
        _ = args;
        using (var f = new Fixture())
        {
            Expect("vollstaendiger Satz: keine Fehler", string.Join(" | ", f.Check()), string.Empty);
            Expect("ohne Publish-Ordner (anderes OS) keine Fehler", f.Check(false).Count, 0);
            Expect("Paketnamen ohne Beachtung der Schreibung", f.Check().Count, 0);
        }

        Case("fehlender Manifest-Eintrag nennt Manifest und Paket", f =>
        {
            f.WriteManifest(Fixture.Manifest(withBar: false));
            f.Generate();
        }, "third-party/licenses.json: Paket Bar.Native 2.0.0 fehlt im Manifest");

        Case("falsche Version nennt beide Versionen", f =>
        {
            f.WriteManifest(Fixture.Manifest(fooVersion: "0.9.0"));
            f.Generate();
        }, "Paket Foo: Manifest hat Version 0.9.0, das Projekt 1.0.0");

        using (var f = new Fixture())
        {
            f.WriteManifest(Fixture.Manifest(fooVersion: "1.0.5").Replace("\"id\": \"Foo\", ", "\"id\": \"Foo\", \"sdkVersion\": true, ", StringComparison.Ordinal));
            f.Generate();
            Expect("sdkVersion: anderer Patchstand ist erlaubt", string.Join(" | ", f.Check()), string.Empty);
        }

        Case("sdkVersion: andere Nebenversion faellt durch", f =>
        {
            f.WriteManifest(Fixture.Manifest(fooVersion: "1.1.0").Replace("\"id\": \"Foo\", ", "\"id\": \"Foo\", \"sdkVersion\": true, ", StringComparison.Ordinal));
            f.Generate();
        }, "Paket Foo: Manifest hat Version 1.1.0, das Projekt 1.0.0");

        Case("SPDX-Abweichung zur nuspec", f =>
        {
            f.WriteManifest(Fixture.Manifest(fooLicense: "Apache-2.0"));
            f.Generate();
        }, "Paket Foo: Manifest hat Lizenz Apache-2.0, die nuspec MIT");

        Case("native Datei ohne Besitzer nennt Publish-Pfad", f => f.Write("publish/libnew.so", "x"),
            "publish/libnew.so: Datei gehoert zu keinem Eintrag");

        Case("native Datei in Unterordner ohne Besitzer", f => f.Write("publish/sub/libnew.so", "x"),
            "publish/sub/libnew.so: Datei gehoert zu keinem Eintrag");

        Case("veraltete Hinweisdatei", f => File.AppendAllText(f.Paths.Notices, "x"),
            "src/Pagewing.App/Resources/ThirdPartyNotices.txt: veraltet");

        Case("fehlende Hinweisdatei", f => File.Delete(f.Paths.Notices),
            "src/Pagewing.App/Resources/ThirdPartyNotices.txt: fehlt");

        Case("Hinweisdatei mit CRLF gilt als aktuell", f => File.WriteAllText(f.Paths.Notices, File.ReadAllText(f.Paths.Notices).Replace("\n", "\r\n", StringComparison.Ordinal)),
            null);

        Case("leere Lizenzdatei", f => f.Write("third-party/lib/LICENSE", string.Empty),
            "Lizenzdatei third-party/lib/LICENSE fehlt oder ist leer");

        Case("fehlende Lizenzdatei", f => File.Delete(Path.Combine(f.Dir, "third-party", "lib", "LICENSE")),
            "Lizenzdatei third-party/lib/LICENSE fehlt oder ist leer");

        Case("PDFium-Version im Manifest weicht ab", f =>
        {
            f.WriteManifest(Fixture.Manifest(pdfiumWin: "1.2.2"));
            f.Generate();
        }, "Eintrag bblanchon.PDFium.Win32 hat Version 1.2.2, Directory.Packages.props hat 1.2.3");

        Case("PDFium-Pakete mit verschiedenen Versionen", f => f.Write("Directory.Packages.props", Fixture.Props("1.2.3", "1.2.4")),
            "Directory.Packages.props: die bblanchon.PDFium.*-Pakete brauchen dieselbe Version");

        Case("Datei gehoert zwei Eintraegen", f =>
        {
            f.WriteManifest(Fixture.Manifest().Replace("\"files\": [\"pdfium.dll\"]", "\"files\": [\"pdfium.dll\", \"libbar.so\"]", StringComparison.Ordinal));
            f.Generate();
        }, "Datei libbar.so gehoert zu mehreren Eintraegen: Bar.Native, bblanchon.PDFium.Win32");

        Case("nuspec fehlt im Cache", f => Directory.Delete(Path.Combine(f.Cache, "foo"), true),
            "foo.nuspec: nuspec nicht gefunden");

        Case("Eintrag ohne Paket", f => f.Packages.RemoveAt(0),
            "Eintrag Foo gehoert zu keinem Paket von App oder CLI");

        Case("Manifest fehlt", f => File.Delete(Path.Combine(f.Dir, "third-party", "licenses.json")),
            "third-party/licenses.json: Datei fehlt");

        Case("unbekannte kind", f => f.WriteManifest(Fixture.Manifest().Replace("\"runtime\"", "\"magic\"", StringComparison.Ordinal)),
            "unbekannte kind \"magic\"");

        Rendering();
        PackageJson();
        EndToEnd();

        Console.WriteLine(s_failures == 0
            ? $"selftest: {s_checks} Pruefungen bestanden."
            : $"selftest: {s_failures} von {s_checks} Pruefungen fehlgeschlagen.");
        return s_failures == 0 ? 0 : 1;
    }

    // Applies a change to a fresh fixture; expects exactly the given error fragment (null: no error at all).
    private static void Case(string name, Action<Fixture> change, string? expected)
    {
        using var f = new Fixture();
        change(f);
        var errors = f.Check();
        if (expected is null)
        {
            Expect(name, string.Join(" | ", errors), string.Empty);
        }
        else
        {
            Expect(name, errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), true);
            if (!errors.Any(e => e.Contains(expected, StringComparison.Ordinal)))
            {
                Console.Error.WriteLine($"  Fehler waren: {string.Join(" | ", errors)}");
            }
        }
    }

    private static void Rendering()
    {
        using var f = new Fixture();
        var text = File.ReadAllText(f.Paths.Notices);
        var firstSection = text.IndexOf("====\n", StringComparison.Ordinal);
        Expect("Pagewing steht als erster Abschnitt", text[firstSection..].StartsWith("====\nPagewing\nGPL-3.0-or-later\n", StringComparison.Ordinal), true);
        Expect("Lizenztext von Pagewing enthalten", text.Contains("GPL TEXT", StringComparison.Ordinal), true);
        Expect("Ordner-Lizenzen (a.txt, b.txt) enthalten", text.Contains("ALPHA", StringComparison.Ordinal) && text.Contains("BETA", StringComparison.Ordinal), true);
        Expect("gleiche Lizenz erscheint einmal (PDFium Linux und Win32)", text.Split("PDFIUM BUILD LICENSE").Length - 1, 1);
        Expect("beide PDFium-Pakete stehen im Abschnitt", text.Contains("bblanchon.PDFium.Linux 1.2.3\nbblanchon.PDFium.Win32 1.2.3\nApache-2.0", StringComparison.Ordinal), true);
        Expect("shipped=false steht nicht in den Hinweisen", text.Contains("BuildOnly", StringComparison.Ordinal), false);
        Expect("Hinweisdatei endet mit Trenner", text.EndsWith("====\n", StringComparison.Ordinal), true);

        // Latin-1 licence text (FreeType in the PDFium archive) is decoded, not turned into U+FFFD.
        File.WriteAllBytes(Path.Combine(f.Dir, "third-party", "pdfium", "licenses", "a.txt"), [0x4D, 0xFC, 0x6C, 0x6C, 0x65, 0x72, 0x0A]);
        var errors = new List<string>();
        var rendered = Inventory.Render(f.Paths, Inventory.LoadManifest(f.Paths, errors)!, errors);
        Expect("ISO-8859-1-Lizenz wird gelesen", rendered?.Contains("Müller", StringComparison.Ordinal) ?? false, true);
        Expect("Ausgabe ohne Ersatzzeichen", rendered?.Contains('\uFFFD') ?? true, false);
    }

    private static void PackageJson()
    {
        var errors = new List<string>();
        var json = "Warnung\n{ \"projects\": [ { \"frameworks\": [ { \"topLevelPackages\": [ { \"id\": \"A\", \"resolvedVersion\": \"1\" } ], " +
                   "\"transitivePackages\": [ { \"id\": \"B\", \"resolvedVersion\": \"2\" } ] } ] } ] }";
        var packages = PackageList.Parse(json, "x", errors);
        Expect("dotnet-list-JSON: oberste und transitive Pakete", string.Join(",", packages.Select(p => $"{p.Id} {p.Version}")), "A 1,B 2");
        PackageList.Parse("kein json", "quelle.json", errors);
        Expect("dotnet-list-JSON: Fehler nennt die Quelle", errors.Count == 1 && errors[0].StartsWith("quelle.json:", StringComparison.Ordinal), true);
    }

    // The real commands on the fixture: exit codes and the written file.
    private static void EndToEnd()
    {
        using var f = new Fixture();
        var json = Path.Combine(f.Dir, "packages.json");
        File.WriteAllText(json,
            "{ \"projects\": [ { \"frameworks\": [ { \"topLevelPackages\": [ { \"id\": \"Foo\", \"resolvedVersion\": \"1.0.0\" } ], \"transitivePackages\": [ " +
            "{ \"id\": \"Bar.Native\", \"resolvedVersion\": \"2.0.0\" }, { \"id\": \"bblanchon.PDFium.Linux\", \"resolvedVersion\": \"1.2.3\" }, " +
            "{ \"id\": \"bblanchon.PDFium.Win32\", \"resolvedVersion\": \"1.2.3\" } ] } ] } ] }");

        // The real command reads the NuGet cache of the user; point it at the fixture cache.
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", f.Cache);
        try
        {
            string[] common = ["--root", f.Dir, "--packages-json", json];
            Expect("check Exit 0 auf dem vollstaendigen Satz", Cli.Run(["check", .. common, "--publish-dir", f.Publish]), 0);
            File.Delete(f.Paths.Notices);
            Expect("check Exit 1 ohne Hinweisdatei", Cli.Run(["check", .. common]), 1);
            Expect("generate Exit 0", Cli.Run(["generate", "--root", f.Dir]), 0);
            Expect("check Exit 0 nach generate", Cli.Run(["check", .. common, "--publish-dir", f.Publish]), 0);
            f.Write("publish/libnew.so", "x");
            Expect("check Exit 1 mit unbekannter nativer Datei", Cli.Run(["check", .. common, "--publish-dir", f.Publish]), 1);
            Expect("check Exit 1 bei fehlendem Publish-Ordner", Cli.Run(["check", .. common, "--publish-dir", Path.Combine(f.Dir, "gibt-es-nicht")]), 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }

        Expect("unbekannter Befehl liefert 2", Cli.Run(["gibt-es-nicht"]), 2);
        Expect("unbekannte Option liefert 2", Cli.Run(["check", "--nope"]), 2);
        Expect("ohne Argumente liefert 2", Cli.Run([]), 2);
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
