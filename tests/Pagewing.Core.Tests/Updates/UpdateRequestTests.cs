using System.Text.Json;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

/// <summary>The request the app leaves in the staging folder for the installer (<c>request.json</c>).</summary>
public sealed class UpdateRequestTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-request-" + Guid.NewGuid().ToString("N"));

    public UpdateRequestTests() => Directory.CreateDirectory(_directory);

    private string RequestPath => Path.Combine(_directory, UpdateStaging.RequestFileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WritesAndReadsEveryField()
    {
        var request = new UpdateRequest
        {
            Version = "0.2.0",
            FileName = "Pagewing-0.2.0-win-x64-setup.exe",
            Form = PackageForm.WindowsInstaller,
            ExePath = @"C:\Program Files\Pagewing\Pagewing.exe",
            TargetDirectory = @"C:\Program Files\Pagewing",
            Left = -1200,
            Top = 80,
            Width = 460,
            Height = 352.5,
        };

        Assert.True(request.TryWrite(RequestPath));
        var read = UpdateRequest.Load(RequestPath);

        Assert.NotNull(read);
        Assert.Equal(1, read.Format);
        Assert.Equal(request.Version, read.Version);
        Assert.Equal(request.FileName, read.FileName);
        Assert.Equal(request.Form, read.Form);
        Assert.Equal(request.ExePath, read.ExePath);
        Assert.Equal(request.TargetDirectory, read.TargetDirectory);
        Assert.Equal<(int?, int?, double?, double?)>((-1200, 80, 460, 352.5), (read.Left, read.Top, read.Width, read.Height));
    }

    [Fact]
    public void TheFileHasPlainNamesAndNoEmptyFields()
    {
        Assert.True(new UpdateRequest { Version = "0.2.0", FileName = "a.exe", Form = PackageForm.AppImage }.TryWrite(RequestPath));

        using var document = JsonDocument.Parse(File.ReadAllText(RequestPath));
        var root = document.RootElement;
        Assert.Equal("0.2.0", root.GetProperty("version").GetString());
        Assert.Equal("a.exe", root.GetProperty("fileName").GetString());
        Assert.Equal("AppImage", root.GetProperty("form").GetString());
        Assert.False(root.TryGetProperty("left", out _));
        Assert.False(root.TryGetProperty("exePath", out _));
    }

    [Fact]
    public void AMissingOrBrokenFileGivesNothing()
    {
        Assert.Null(UpdateRequest.Load(RequestPath));

        File.WriteAllText(RequestPath, "{ not json");
        Assert.Null(UpdateRequest.Load(RequestPath));

        File.WriteAllText(RequestPath, "{\"format\":1}");
        Assert.Null(UpdateRequest.Load(RequestPath));
    }

    [Fact]
    public void WritingNeverMakesTheFolder()
    {
        // The staging folder may have been removed meanwhile (cancelled, the app ending): the request goes into the folder
        // that is there, or nowhere, and never brings a removed folder back.
        var gone = Path.Combine(_directory, UpdateStaging.DirectoryName, "0.2.0");

        Assert.False(new UpdateRequest { Version = "0.2.0", FileName = "a.exe" }.TryWrite(Path.Combine(gone, UpdateStaging.RequestFileName)));

        Assert.False(Directory.Exists(Path.Combine(_directory, UpdateStaging.DirectoryName)));
    }

    [Fact]
    public void TheStagingFolderIsNamedAfterTheVersion()
    {
        Assert.Equal(
            Path.Combine(_directory, "updates", "0.2.0"),
            UpdateStaging.DirectoryFor(_directory, new UpdateVersion(0, 2, 0)));
    }
}
