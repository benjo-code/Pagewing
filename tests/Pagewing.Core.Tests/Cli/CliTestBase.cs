using System.Text;
using Pagewing.Cli;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

/// <summary>What a run of the command line printed and returned.</summary>
public sealed record CliRun(int ExitCode, byte[] OutputBytes, string Error)
{
    public string Output { get; } = new UTF8Encoding(false, true).GetString(OutputBytes);

    public string[] OutputLines { get; } = new UTF8Encoding(false, true).GetString(OutputBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A progress that calls back at once on the thread that reports (<see cref="Progress{T}"/> would post).</summary>
public sealed class CallbackProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

/// <summary>A writer that tells when the first line is written (the first warning), to prove what happened before.</summary>
public sealed class ProbeWriter(Action onFirstWrite) : StringWriter
{
    private bool first = true;

    public override void WriteLine(string? value)
    {
        Probe();
        base.WriteLine(value);
    }

    public override void Write(string? value)
    {
        Probe();
        base.Write(value);
    }

    private void Probe()
    {
        if (first)
        {
            first = false;
            onFirstWrite();
        }
    }
}

/// <summary>Shared by the tests of the page commands: a temp folder, copies of the fixtures and a way to run the command line.</summary>
public abstract class CliTestBase : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    protected const string UserPassword = "hôtel";

    private readonly List<string> folderLinks = [];

    protected DirectoryInfo WorkDirectory { get; } = Directory.CreateTempSubdirectory("pagewing-cli-tests-");

    protected PdfEngine Engine { get; } = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        // A folder link goes first and alone: a recursive delete must never walk through it into the folder it points to.
        foreach (var link in folderLinks)
        {
            try
            {
                Directory.Delete(link, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do; the folder is in the temp folder.
            }
        }

        WorkDirectory.Delete(recursive: true);
    }

    /// <summary>Copies a fixture into the work folder (tests never write beside <c>testdata</c>).</summary>
    protected string Copy(string fixture, string? asName = null)
    {
        var path = Path.Combine(WorkDirectory.FullName, asName ?? fixture);
        File.Copy(TestData.Pdf(fixture), path, overwrite: true);
        return path;
    }

    protected string Folder(string name) => Directory.CreateDirectory(Path.Combine(WorkDirectory.FullName, name)).FullName;

    /// <summary>The temporary files the saver and the exporter leave behind on a failure (there must be none).</summary>
    protected string[] TemporaryFiles() =>
        Directory.GetFiles(WorkDirectory.FullName, ".pw-*", SearchOption.AllDirectories);

    protected static Task<CliRun> RunAsync(params string[] args) => RunAsync(new StringWriter(), environment: null, CancellationToken.None, args);

    protected static Task<CliRun> RunAsync(Dictionary<string, string> environment, params string[] args) =>
        RunAsync(new StringWriter(), environment, CancellationToken.None, args);

    protected static async Task<CliRun> RunAsync(StringWriter stderr, Dictionary<string, string>? environment, CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new MemoryStream();
        var exitCode = await CliApp.RunAsync(
            args,
            stdout,
            stderr,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            cancellationToken);
        return new CliRun(exitCode, stdout.ToArray(), stderr.ToString());
    }

    /// <summary>Runs the command line with a progress that the test can use to cancel or change things at a known point of the write.</summary>
    protected static async Task<CliRun> RunWithProgressAsync(IProgress<double> progress, CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        var exitCode = await CliApp.RunAsync(args, stdout, stderr, _ => null, openDocument: null, runCommand: null, cancellationToken, progress);
        return new CliRun(exitCode, stdout.ToArray(), stderr.ToString());
    }

    /// <summary>A directory junction on Windows (no privilege needed), a symbolic link elsewhere.</summary>
    protected bool CreateFolderLink(string link, string target)
    {
        folderLinks.Add(link);
        if (!OperatingSystem.IsWindows())
        {
            return TryCreate(() => Directory.CreateSymbolicLink(link, target));
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    protected static bool TryCreate(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    protected static void CreateHardLink(string link, string target)
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            : new System.Diagnostics.ProcessStartInfo("ln", ["--", target, link]);
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("The link tool could not be started.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(link))
        {
            throw new IOException("The hard link could not be created.");
        }
    }

    protected static string[] Titles(IReadOnlyList<PdfOutlineItem> items) =>
        [.. items.SelectMany(static item => new[] { item.Title }.Concat(Titles(item.Children)))];

    protected async Task<string[]> PageTextsAsync(string path, string? password = null)
    {
        await using var document = await Engine.OpenAsync(path, password, Ct);
        var texts = new string[document.PageCount];
        for (var page = 0; page < texts.Length; page++)
        {
            texts[page] = (await document.GetTextAsync(page, Ct)).Trim();
        }

        return texts;
    }
}
