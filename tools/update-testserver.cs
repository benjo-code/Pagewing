#:property PublishAot=false

// Test server for the test build of the updater (Milestone 8.1; packaging/README.md, section "Update prüfen").
//   dotnet run tools/update-testserver.cs -- prepare --version <x.y.z> --key <test-key.txt> --out <folder> <file>... [--id test-1] [--tamper signature|checksum]
//   dotnet run tools/update-testserver.cs -- serve --root <folder> --port <port>
//   dotnet run tools/update-testserver.cs -- selftest
// prepare copies the release files of one version into a new folder, writes update-manifest.json for them (as the
// release job does), signs it with the throwaway test key (the private file of `package.cs update-keygen`) and writes
// latest.json (what GitHub answers at releases/latest). --tamper writes a manifest whose signature does not match, or a
// correctly signed manifest with a wrong SHA-256 (the error paths of the manual check).
// serve answers ONLY on 127.0.0.1 (never another interface) with exactly the routes the test build asks for:
//   GET /releases/latest        -> latest.json
//   GET /download/v<x.y.z>/<n>  -> 302 to http://127.0.0.1:<port>/assets/<n> (as GitHub redirects to its storage)
//   GET /assets/<n>             -> the file <n> directly in the folder
//   GET /                       -> a short page (the "download page" of the test build)
// Everything else is 404: no subfolders, no "..", no percent-encoding, no backslash, no absolute form. Ctrl+C stops it.
// It never uses the network beyond the loopback interface and never writes outside --out.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

return Cli.Run(args);

static class Cli
{
    private const string Usage =
        "Aufruf: dotnet run tools/update-testserver.cs -- <Befehl>\n" +
        "  prepare --version <x.y.z> --key <Testschluessel.txt> --out <Ordner> <Datei>... [--id test-1] [--tamper signature|checksum]\n" +
        "          legt den Ordner des Testservers an: Dateien, update-manifest.json (signiert), latest.json\n" +
        "  serve --root <Ordner> --port <Port>     beantwortet Anfragen nur auf 127.0.0.1, bis Strg+C\n" +
        "  selftest                                prueft Routen, Pfadtraversal und prepare";

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return args[0] switch
        {
            "prepare" => Prepare.Run(args[1..]),
            "serve" => Serve.Run(args[1..]),
            "selftest" => SelfTest.Run(),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unbekannter Befehl: {command}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    // "--name value" pairs and positional arguments; null when a named option has no value or appears twice.
    public static (Dictionary<string, string> Named, List<string> Positional)? Parse(string[] args, params string[] names)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (names.Contains(args[i]))
            {
                if (i + 1 >= args.Length || !named.TryAdd(args[i], args[i + 1]))
                {
                    return null;
                }

                i++;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        return (named, positional);
    }
}

// The manifest of the release job (tools/package.cs, UpdateManifestRules; Pagewing.Core.Updates.UpdateManifest) for the
// files given; only the kinds the app knows, each named exactly as the release names it.
static partial class Prepare
{
    public const string ManifestName = "update-manifest.json";
    public const string SignatureName = "update-manifest.json.sig";
    public const string LatestName = "latest.json";
    public const string DefaultKeyId = "test-1";

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z")]
    public static partial Regex StableVersion();

    // The kinds in the order of the release manifest, with the release file name of a version.
    public static readonly (string Kind, Func<string, string> Name)[] Kinds =
    [
        ("win-x64-setup", v => $"Pagewing-{v}-win-x64-setup.exe"),
        ("win-x64-portable", v => $"Pagewing-{v}-win-x64-portable.zip"),
        ("linux-appimage", v => $"Pagewing-{v}-x86_64.AppImage"),
        ("linux-deb", v => $"pagewing_{v}_amd64.deb"),
    ];

    public static int Run(string[] args)
    {
        var parsed = Cli.Parse(args, "--version", "--key", "--out", "--id", "--tamper");
        if (parsed is not { } p || p.Positional.Count == 0 || !p.Named.TryGetValue("--version", out var version) ||
            !p.Named.TryGetValue("--key", out var keyFile) || !p.Named.TryGetValue("--out", out var output) ||
            p.Named.GetValueOrDefault("--tamper", "none") is not ("none" or "signature" or "checksum"))
        {
            Console.Error.WriteLine("Aufruf: prepare --version <x.y.z> --key <Testschluessel.txt> --out <Ordner> <Datei>... [--id test-1] [--tamper signature|checksum]");
            return 2;
        }

        if (!StableVersion().IsMatch(version))
        {
            Console.Error.WriteLine($"prepare: '{version}' ist keine Version X.Y.Z (die App liest nur stabile Versionen)");
            return 2;
        }

        var files = p.Positional.Select(Path.GetFullPath).ToList();
        var problems = new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                problems.Add($"Datei fehlt: {file}");
            }
            else if (!Kinds.Any(k => k.Name(version) == Path.GetFileName(file)))
            {
                problems.Add($"{Path.GetFileName(file)} ist keine Release-Datei der Version {version} ({string.Join(", ", Kinds.Select(k => k.Name(version)))})");
            }
        }

        if (files.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count() != files.Count)
        {
            problems.Add("eine Datei steht doppelt");
        }

        var root = Path.GetFullPath(output);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
        {
            problems.Add($"{root} gibt es schon und ist nicht leer; prepare legt immer einen neuen Ordner an");
        }

        if (problems.Count > 0)
        {
            problems.ForEach(x => Console.Error.WriteLine($"prepare: {x}"));
            return 1;
        }

        using var key = ReadKey(File.Exists(keyFile) ? File.ReadAllText(keyFile) : string.Empty);
        if (key is null)
        {
            Console.Error.WriteLine($"prepare: {keyFile} enthaelt keinen privaten P-256-Schluessel (Base64 wie von package.cs update-keygen, oder PEM)");
            return 1;
        }

        Directory.CreateDirectory(root);
        var entries = new List<(string Kind, string Name, long Size, string Sha256)>();
        foreach (var (kind, nameOf) in Kinds)
        {
            if (files.FirstOrDefault(f => Path.GetFileName(f) == nameOf(version)) is { } file)
            {
                var target = Path.Combine(root, Path.GetFileName(file));
                File.Copy(file, target);
                using var stream = File.OpenRead(target);
                entries.Add((kind, Path.GetFileName(file), stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
            }
        }

        var tamper = p.Named.GetValueOrDefault("--tamper", "none");
        var id = p.Named.GetValueOrDefault("--id", DefaultKeyId);
        var (manifest, signature) = Write(version, entries, key, id, tamper);
        File.WriteAllBytes(Path.Combine(root, ManifestName), manifest);
        File.WriteAllText(Path.Combine(root, SignatureName), signature, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, LatestName), Latest(version), new UTF8Encoding(false));

        Console.WriteLine($"prepare: {version} mit {entries.Count} Datei(en) in {root}, signiert mit {id}{(tamper == "none" ? string.Empty : $", absichtlich falsch ({tamper})")}");
        Console.WriteLine($"prepare: oeffentlicher Testschluessel (muss PagewingUpdateTestKey des Testaufbaus sein): {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        return 0;
    }

    // The manifest bytes and the signature text as the app verifies them; tamper "signature" changes the manifest after
    // signing, "checksum" signs a manifest whose first SHA-256 is wrong.
    public static (byte[] Manifest, string Signature) Write(string version, List<(string Kind, string Name, long Size, string Sha256)> entries, ECDsa key, string id, string tamper)
    {
        var list = entries.Select((e, i) => tamper == "checksum" && i == 0 ? e with { Sha256 = Flip(e.Sha256) } : e);
        var json = "{\"format\":1,\"version\":\"" + version + "\",\"files\":[" +
            string.Join(",", list.Select(e => "{\"kind\":\"" + e.Kind + "\",\"name\":\"" + e.Name + "\",\"size\":" +
                e.Size.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":\"" + e.Sha256 + "\"}")) + "]}";
        var manifest = Encoding.UTF8.GetBytes(json);
        var signature = id + ":" + Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        if (tamper == "signature")
        {
            manifest = Encoding.UTF8.GetBytes(json.Replace("\"format\":1", "\"format\": 1", StringComparison.Ordinal));
        }

        return (manifest, signature);
    }

    // What api.github.com answers at releases/latest, as far as the app reads it (tag_name).
    public static string Latest(string version) =>
        "{\"tag_name\":\"v" + version + "\",\"name\":\"Pagewing " + version + " (Testserver)\",\"draft\":false,\"prerelease\":false}";

    private static string Flip(string hex) => (hex[0] == '0' ? '1' : '0') + hex[1..];

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
}

// What the server answers: a status, the file or bytes to send, a redirect target.
record Reply(int Status, string? File = null, byte[]? Body = null, string? Location = null, string ContentType = "application/octet-stream");

static partial class Serve
{
    // A file name the release uses (ManifestVerifier.SafeFileName) plus latest.json: no separator, no "..", no colon.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\z")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z")]
    private static partial Regex Tag();

    public static int Run(string[] args)
    {
        var parsed = Cli.Parse(args, "--root", "--port");
        if (parsed is not { } p || p.Positional.Count != 0 || !p.Named.TryGetValue("--root", out var rootArg) ||
            !p.Named.TryGetValue("--port", out var portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("Aufruf: serve --root <Ordner> --port <1..65535>");
            return 2;
        }

        var root = Path.GetFullPath(rootArg);
        if (!File.Exists(Path.Combine(root, Prepare.LatestName)))
        {
            Console.Error.WriteLine($"serve: {Path.Combine(root, Prepare.LatestName)} fehlt; den Ordner mit prepare anlegen");
            return 1;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Server server;
        try
        {
            server = Server.Start(root, port);
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"serve: 127.0.0.1:{port} ist nicht frei ({e.SocketErrorCode})");
            return 1;
        }

        using (server)
        {
            server.Log = Console.WriteLine;
            Console.WriteLine($"serve: {root} auf http://127.0.0.1:{server.Port}/ (nur 127.0.0.1), Ende mit Strg+C");
            try
            {
                Task.Delay(Timeout.Infinite, stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("serve: beendet");
            }
        }

        return 0;
    }

    // The answer to "<method> <target>" for the folder root (a full path) and the port the server listens on.
    public static Reply Route(string root, int port, string method, string target)
    {
        if (method is not ("GET" or "HEAD"))
        {
            return new Reply(405);
        }

        // Only the origin form "/..." without query, percent-encoding, backslash, control characters or empty segments.
        if (!target.StartsWith('/') || target.IndexOfAny(['%', '\\', '?', '#', ':']) >= 0 || target.Any(char.IsControl) ||
            target.Contains("//", StringComparison.Ordinal))
        {
            return new Reply(404);
        }

        var segments = target[1..].Split('/');
        if (target == "/")
        {
            var names = Directory.EnumerateFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            var page = "Pagewing update test server (test build only)\n\n" + string.Join("\n", names) + "\n";
            return new Reply(200, Body: Encoding.UTF8.GetBytes(page), ContentType: "text/plain; charset=utf-8");
        }

        if (segments is ["releases", "latest"])
        {
            return FileReply(root, Prepare.LatestName, "application/json");
        }

        if (segments is ["download", var tag, var name] && Tag().IsMatch(tag) && FileReply(root, name, null).Status == 200)
        {
            return new Reply(302, Location: $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/assets/{name}");
        }

        if (segments is ["assets", var asset])
        {
            return FileReply(root, asset, null);
        }

        return new Reply(404);
    }

    // A file directly in root, by a plain name only.
    private static Reply FileReply(string root, string name, string? contentType)
    {
        if (!SafeName().IsMatch(name))
        {
            return new Reply(404);
        }

        var full = Path.GetFullPath(Path.Combine(root, name));
        var inRoot = string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        if (!inRoot || !File.Exists(full) || new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new Reply(404);
        }

        return new Reply(200, File: full, ContentType: contentType ?? "application/octet-stream");
    }
}

// A minimal HTTP/1.1 server on 127.0.0.1 (one request per connection, "Connection: close").
sealed class Server : IDisposable
{
    private const int MaxHead = 8192;
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private Server(string root, TcpListener listener)
    {
        _root = root;
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    public Action<string>? Log { get; set; }

    // Listens on 127.0.0.1 only; port 0 picks a free one (selftest).
    public static Server Start(string root, int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = OperatingSystem.IsWindows();
        listener.Start();
        return new Server(Path.GetFullPath(root), listener);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with the listener.
        }

        _listener.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token);
                var line = head?.Split("\r\n")[0].Split(' ');
                var reply = line is [var method, var target, var version] && version.StartsWith("HTTP/1.", StringComparison.Ordinal)
                    ? Serve.Route(_root, Port, method, target)
                    : new Reply(400);
                Log?.Invoke($"{DateTime.Now:HH:mm:ss} {(line is { Length: > 1 } ? line[0] + " " + line[1] : "?")} -> {reply.Status}");
                await WriteAsync(stream, reply, line is ["HEAD", ..], timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away or took too long.
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var text = Encoding.Latin1.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, Reply reply, bool headOnly, CancellationToken ct)
    {
        var reason = reply.Status switch { 200 => "OK", 302 => "Found", 400 => "Bad Request", 405 => "Method Not Allowed", _ => "Not Found" };
        var body = reply.Body ?? (reply.File is null ? Encoding.UTF8.GetBytes(reason + "\n") : null);
        using var file = reply.File is null ? null : new FileStream(reply.File, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = file?.Length ?? body!.Length;
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {reply.Status} {reason}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {(reply.File is null && reply.Body is null ? "text/plain; charset=utf-8" : reply.ContentType)}\r\n")
            .Append(reply.Location is null ? string.Empty : $"Location: {reply.Location}\r\n")
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (!headOnly)
        {
            if (file is not null)
            {
                await file.CopyToAsync(stream, ct);
            }
            else
            {
                await stream.WriteAsync(body, ct);
            }
        }

        await stream.FlushAsync(ct);
    }
}

static class SelfTest
{
    private static int _checks;
    private static readonly List<string> Failures = [];

    private static void Expect<T>(string name, T actual, T expected)
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            Failures.Add($"{name}: erwartet {expected}, erhalten {actual}");
        }
    }

    public static int Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pagewing-update-testserver-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            Routes(temp);
            TestserverServesOnlyTheGivenFolder(temp);
            PrepareWritesWhatTheAppVerifies(temp);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }

        if (Failures.Count > 0)
        {
            Failures.ForEach(f => Console.Error.WriteLine($"selftest: {f}"));
            Console.Error.WriteLine($"selftest: {Failures.Count} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($"selftest: {_checks} Pruefungen bestanden");
        return 0;
    }

    // Route alone, without a socket.
    private static void Routes(string temp)
    {
        var root = Path.Combine(temp, "routes");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Prepare.LatestName), Prepare.Latest("0.0.2"));
        File.WriteAllText(Path.Combine(root, "Pagewing-0.0.2-win-x64-setup.exe"), "setup");
        File.WriteAllText(Path.Combine(temp, "secret.txt"), "secret");

        Expect("Route: releases/latest", Serve.Route(root, 8765, "GET", "/releases/latest").File, Path.Combine(root, Prepare.LatestName));
        var redirect = Serve.Route(root, 8765, "GET", "/download/v0.0.2/Pagewing-0.0.2-win-x64-setup.exe");
        Expect("Route: download leitet um (302)", redirect.Status, 302);
        Expect("Route: Umleitung auf 127.0.0.1 und den eigenen Port", redirect.Location, "http://127.0.0.1:8765/assets/Pagewing-0.0.2-win-x64-setup.exe");
        Expect("Route: assets liefert die Datei", Serve.Route(root, 8765, "GET", "/assets/Pagewing-0.0.2-win-x64-setup.exe").File, Path.Combine(root, "Pagewing-0.0.2-win-x64-setup.exe"));
        Expect("Route: HEAD wie GET", Serve.Route(root, 8765, "HEAD", "/assets/Pagewing-0.0.2-win-x64-setup.exe").Status, 200);
        Expect("Route: Startseite", Serve.Route(root, 8765, "GET", "/").Status, 200);
        Expect("Route: POST nicht erlaubt", Serve.Route(root, 8765, "POST", "/releases/latest").Status, 405);
        foreach (var target in new[]
        {
            "/download/v0.0.2/missing.exe", "/download/0.0.2/Pagewing-0.0.2-win-x64-setup.exe", "/download/v0.0.2/../secret.txt",
            "/assets/../secret.txt", "/assets/..", "/assets/.", "/assets/%2e%2e/secret.txt", "/assets/%2e%2e%2fsecret.txt",
            "/assets/..%2Fsecret.txt", "/assets/..\\secret.txt", "/assets/sub/secret.txt", "/../secret.txt", "/secret.txt",
            "/assets/" + Path.Combine(temp, "secret.txt"), "/" + Path.Combine(temp, "secret.txt").Replace('\\', '/'),
            "http://127.0.0.1/assets/Pagewing-0.0.2-win-x64-setup.exe", "*", "/assets//Pagewing-0.0.2-win-x64-setup.exe",
            "/assets/Pagewing-0.0.2-win-x64-setup.exe?x=1", "/assets/C:secret.txt", "/releases/latest/", "/releases",
        })
        {
            Expect($"Route: {target} -> 404", Serve.Route(root, 8765, "GET", target).Status, 404);
        }
    }

    // The real server on a free port of 127.0.0.1, with raw requests (an HTTP client would clean ".." itself).
    private static void TestserverServesOnlyTheGivenFolder(string temp)
    {
        var root = Path.Combine(temp, "served");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Prepare.LatestName), Prepare.Latest("0.0.2"));
        File.WriteAllBytes(Path.Combine(root, "Pagewing-0.0.2-win-x64-setup.exe"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(temp, "outside.txt"), "outside");

        int port;
        using (var server = Server.Start(root, 0))
        {
            port = server.Port;
            Expect("TestserverServesOnlyTheGivenFolder: lauscht nur auf 127.0.0.1", server.Endpoint.Address.Equals(IPAddress.Loopback), true);

            var (status, headers, body) = Request(port, "GET /assets/Pagewing-0.0.2-win-x64-setup.exe HTTP/1.1");
            Expect("TestserverServesOnlyTheGivenFolder: Datei des Ordners (200)", status, 200);
            Expect("TestserverServesOnlyTheGivenFolder: Inhalt der Datei", Convert.ToHexString(body), "01020304");
            Expect("TestserverServesOnlyTheGivenFolder: Laenge der Datei", headers.Contains("Content-Length: 4\r\n", StringComparison.Ordinal), true);

            (status, headers, _) = Request(port, "GET /download/v0.0.2/Pagewing-0.0.2-win-x64-setup.exe HTTP/1.1");
            Expect("TestserverServesOnlyTheGivenFolder: Download leitet um (302)", status, 302);
            Expect("TestserverServesOnlyTheGivenFolder: Ziel auf 127.0.0.1", headers.Contains($"Location: http://127.0.0.1:{port}/assets/Pagewing-0.0.2-win-x64-setup.exe\r\n", StringComparison.Ordinal), true);

            (status, _, body) = Request(port, "GET /releases/latest HTTP/1.1");
            Expect("TestserverServesOnlyTheGivenFolder: releases/latest", (status, Encoding.UTF8.GetString(body)), (200, Prepare.Latest("0.0.2")));

            foreach (var target in new[]
            {
                "/assets/../outside.txt", "/../outside.txt", "/assets/%2e%2e/outside.txt", "/assets/%2E%2E%2Foutside.txt",
                "/assets/..\\outside.txt", "/assets/" + Path.Combine(temp, "outside.txt"), "/" + Path.Combine(temp, "outside.txt").Replace('\\', '/'),
                "http://127.0.0.1/../outside.txt", "/download/v0.0.2/..%2foutside.txt",
            })
            {
                (status, _, body) = Request(port, $"GET {target} HTTP/1.1");
                Expect($"TestserverServesOnlyTheGivenFolder: {target} -> 404", status, 404);
                Expect($"TestserverServesOnlyTheGivenFolder: {target} liefert nichts von aussen", Encoding.UTF8.GetString(body).Contains("outside", StringComparison.Ordinal), false);
            }

            (status, _, _) = Request(port, "garbage");
            Expect("TestserverServesOnlyTheGivenFolder: kaputte Anfrage (400)", status, 400);
        }

        // Stopped: the port no longer answers.
        using var probe = new TcpClient();
        var refused = false;
        try
        {
            probe.Connect(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            refused = true;
        }

        Expect("TestserverServesOnlyTheGivenFolder: nach dem Ende lauscht nichts mehr", refused, true);
    }

    private static (int Status, string Headers, byte[] Body) Request(int port, string requestLine)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 10000;
        var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(requestLine + "\r\nHost: 127.0.0.1\r\n\r\n"));
        using var all = new MemoryStream();
        stream.CopyTo(all);
        var bytes = all.ToArray();
        var split = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        if (split < 0)
        {
            return (0, string.Empty, []);
        }

        var headers = Encoding.ASCII.GetString(bytes, 0, split + 2);
        var status = int.TryParse(headers.Split(' ')[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return (status, headers, bytes[(split + 4)..]);
    }

    // prepare with a key file as update-keygen writes it: the manifest verifies with the public key (as ManifestVerifier
    // does: id, 64 bytes P1363 over the raw bytes), names size and SHA-256 of the copy, and the tampered forms fail.
    private static void PrepareWritesWhatTheAppVerifies(string temp)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(temp, "test-key.txt");
        File.WriteAllText(keyFile, Convert.ToBase64String(key.ExportPkcs8PrivateKey()) + "\n");
        var input = Path.Combine(temp, "in");
        Directory.CreateDirectory(input);
        var setup = Path.Combine(input, "Pagewing-0.0.2-win-x64-setup.exe");
        File.WriteAllBytes(setup, [9, 8, 7]);
        var appImage = Path.Combine(input, "Pagewing-0.0.2-x86_64.AppImage");
        File.WriteAllBytes(appImage, [1]);

        bool Verifies(string folder) =>
            File.ReadAllText(Path.Combine(folder, Prepare.SignatureName)) is var sig && sig.StartsWith("test-1:", StringComparison.Ordinal) &&
            key.VerifyData(File.ReadAllBytes(Path.Combine(folder, Prepare.ManifestName)), Convert.FromBase64String(sig["test-1:".Length..]),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var good = Path.Combine(temp, "good");
        Expect("prepare: Exit 0", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", keyFile, "--out", good, setup, appImage])), 0);
        Expect("prepare: Signatur passt (Kennung test-1)", Verifies(good), true);
        var sha = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 9, 8, 7 }));
        Expect("prepare: Manifest wie das des Release-Jobs", File.ReadAllText(Path.Combine(good, Prepare.ManifestName)),
            "{\"format\":1,\"version\":\"0.0.2\",\"files\":[{\"kind\":\"win-x64-setup\",\"name\":\"Pagewing-0.0.2-win-x64-setup.exe\",\"size\":3,\"sha256\":\"" + sha +
            "\"},{\"kind\":\"linux-appimage\",\"name\":\"Pagewing-0.0.2-x86_64.AppImage\",\"size\":1,\"sha256\":\"" + Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1 })) + "\"}]}");
        Expect("prepare: latest.json nennt den Tag", File.ReadAllText(Path.Combine(good, Prepare.LatestName)).Contains("\"tag_name\":\"v0.0.2\"", StringComparison.Ordinal), true);
        Expect("prepare: Dateien kopiert", File.Exists(Path.Combine(good, "Pagewing-0.0.2-win-x64-setup.exe")) && File.Exists(Path.Combine(good, "Pagewing-0.0.2-x86_64.AppImage")), true);

        var badSignature = Path.Combine(temp, "bad-signature");
        Expect("prepare --tamper signature: Exit 0", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", keyFile, "--out", badSignature, "--tamper", "signature", setup])), 0);
        Expect("prepare --tamper signature: Signatur passt nicht", Verifies(badSignature), false);

        var badChecksum = Path.Combine(temp, "bad-checksum");
        Expect("prepare --tamper checksum: Exit 0", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", keyFile, "--out", badChecksum, "--tamper", "checksum", setup])), 0);
        Expect("prepare --tamper checksum: Signatur passt", Verifies(badChecksum), true);
        Expect("prepare --tamper checksum: SHA-256 falsch", File.ReadAllText(Path.Combine(badChecksum, Prepare.ManifestName)).Contains(sha, StringComparison.Ordinal), false);

        Expect("prepare: vorhandener Ordner wird nie ueberschrieben", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", keyFile, "--out", good, setup])), 1);
        Expect("prepare: Datei einer anderen Version", Quiet(() => Prepare.Run(["--version", "0.0.3", "--key", keyFile, "--out", Path.Combine(temp, "x"), setup])), 1);
        Expect("prepare: Vorabversion", Quiet(() => Prepare.Run(["--version", "0.0.2-rc.1", "--key", keyFile, "--out", Path.Combine(temp, "y"), setup])), 2);
        Expect("prepare: oeffentlicher statt privater Schluessel", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", setup, "--out", Path.Combine(temp, "z"), setup])), 1);
        Expect("prepare: ohne Datei", Quiet(() => Prepare.Run(["--version", "0.0.2", "--key", keyFile, "--out", Path.Combine(temp, "w")])), 2);
        Expect("prepare: nichts angelegt nach Fehlern", Directory.Exists(Path.Combine(temp, "x")) || Directory.Exists(Path.Combine(temp, "z")), false);
        Expect("serve: falscher Port", Quiet(() => Serve.Run(["--root", good, "--port", "0"])), 2);
        Expect("serve: Ordner ohne latest.json", Quiet(() => Serve.Run(["--root", input, "--port", "8765"])), 1);
    }

    private static int Quiet(Func<int> action)
    {
        var (output, error) = (Console.Out, Console.Error);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            return action();
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
