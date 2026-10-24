using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pagewing.Core.Updates;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

/// <summary>
/// What the program trusts for updates. The normal run checks the release build (the build of every release); the test
/// build (<c>-p:PagewingUpdateTestBuild=true</c>, packaging/README.md, section "Update prüfen") is checked by running
/// these tests once with the property:
/// <c>dotnet test --project tests/Pagewing.Core.Tests -p:PagewingUpdateTestBuild=true -p:PagewingUpdateTestPort=&lt;port&gt; -p:PagewingUpdateTestKey=&lt;Base64&gt; -- --filter-class "*UpdateTrustTests"</c>.
/// </summary>
public sealed class UpdateTrustTests
{
    private static string CoreSource(params string[] parts) => Path.Combine([TestData.Root, "src", "Pagewing.Core", .. parts]);

    [Fact]
    public void EveryKeyIsAValidP256PublicKeyWithAUniqueId()
    {
        Assert.NotEmpty(UpdateTrust.Keys);
        Assert.Equal(UpdateTrust.Keys.Count, UpdateTrust.Keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var key in UpdateTrust.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key.Id));
            Assert.DoesNotContain(':', key.Id);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }

    /// <summary>
    /// Armed only in the signing job of the release workflow (<c>PAGEWING_REQUIRE_RELEASE_KEY=1</c>, job
    /// <c>manifest</c> in <c>.github/workflows/release.yml</c>): a release must never ship the placeholder as its only
    /// trust. Everywhere else it passes, because the placeholder stays until the owner enters the real key
    /// (<c>packaging/README.md</c>, section "Signaturschlüssel für Updates").
    /// </summary>
    [Fact]
    public void ReleaseKeysAreNotThePlaceholderWhenReleasing()
    {
        if (Environment.GetEnvironmentVariable("PAGEWING_REQUIRE_RELEASE_KEY") != "1")
        {
            return;
        }

        Assert.NotEmpty(UpdateTrust.Keys);
        Assert.DoesNotContain(UpdateTrust.Keys, key => key.Id == UpdateTrust.PlaceholderKeyId);
        Assert.DoesNotContain(UpdateTrust.Keys, key => Convert.ToBase64String(key.PublicKey) == PlaceholderPublicKey);
    }

    /// <summary>The public half of the design-time placeholder (its private half was discarded).</summary>
    private const string PlaceholderPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==";

    /// <summary>
    /// The shipped trust has no way to be overridden (spec: no environment variable, no switch): the text of
    /// <c>UpdateTrust.cs</c> asks neither the environment nor the command line, has no conditional compilation and reads
    /// no value of the build. The test build lives in a file of its own that only the build property brings in.
    /// </summary>
    [Fact]
    public void ReleaseBuildHasNoOverride()
    {
        var text = File.ReadAllText(CoreSource("Updates", "UpdateTrust.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCommandLineArgs", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bargs\b", text);
        Assert.DoesNotContain("Environment.", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*#\s*if\b", text);
        Assert.DoesNotContain("AssemblyMetadata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomAttribute", text, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);

        // The project: the test file never comes in by default, and only the property swaps the files.
        var project = File.ReadAllText(CoreSource("Pagewing.Core.csproj")).ReplaceLineEndings("\n");
        Assert.Matches(@"<ItemGroup>\s*<Compile Remove=""Updates\\UpdateTrust\.TestBuild\.cs"" />\s*</ItemGroup>", project);
        Assert.Matches(
            @"<ItemGroup Condition=""'\$\(PagewingUpdateTestBuild\)' == 'true'"">\s*<Compile Remove=""Updates\\UpdateTrust\.cs"" />\s*<Compile Include=""Updates\\UpdateTrust\.TestBuild\.cs"" />",
            project);

        // The values of the test build appear only inside the parts that the property switches on (and the comment).
        var gated = Regex.Replace(
            project,
            @"<!--.*?-->|<ItemGroup Condition=""'\$\(PagewingUpdateTestBuild\)' == 'true'"">.*?</ItemGroup>|<Target [^>]*Condition=""'\$\(PagewingUpdateTestBuild\)' == 'true'"">.*?</Target>",
            string.Empty,
            RegexOptions.Singleline);
        Assert.DoesNotContain("PagewingUpdateTestPort", gated, StringComparison.Ordinal);
        Assert.DoesNotContain("PagewingUpdateTestKey", gated, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyMetadata", gated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The test build swaps the whole file, so both must declare the same public members; otherwise the test build stops
    /// compiling unnoticed (the normal run never compiles <c>UpdateTrust.TestBuild.cs</c>).
    /// </summary>
    [Fact]
    public void TheTestBuildFileDeclaresTheSameMembers()
    {
        static List<string> Members(string file) =>
        [
            .. Regex.Matches(File.ReadAllText(file), @"(?m)^[ \t]*public[ \t]+(?:static|const)\b(?![ \t]+class\b)[^\n;{=(]*?\b(?<name>[A-Z]\w*)[ \t]*(?:\(|\{|=>|=)")
                .Select(match => match.Groups["name"].Value)
                .Order(StringComparer.Ordinal),
        ];

        var shipped = Members(CoreSource("Updates", "UpdateTrust.cs"));
        var test = Members(CoreSource("Updates", "UpdateTrust.TestBuild.cs"));

        Assert.Equal(["DownloadBaseUrl", "Keys", "PlaceholderKeyId", "ReleasesLatestUrl", "ReleasesPageUrl", "TestBuildNote"], shipped);
        Assert.Equal(shipped, test);
    }

#if PAGEWING_UPDATE_TESTBUILD
    /// <summary>
    /// The test build trusts exactly one test key (never the placeholder) and fetches only from the test server on
    /// 127.0.0.1, all three addresses on the same port; the about card names it a test build.
    /// </summary>
    [Fact]
    public void TestBuildPointsAtLoopbackOnly()
    {
        var key = Assert.Single(UpdateTrust.Keys);
        Assert.Equal("test-1", key.Id);
        Assert.NotEqual(PlaceholderPublicKey, Convert.ToBase64String(key.PublicKey));

        var addresses = new[] { UpdateTrust.ReleasesLatestUrl, UpdateTrust.ReleasesPageUrl, UpdateTrust.DownloadBaseUrl }.Select(url => new Uri(url)).ToList();
        foreach (var address in addresses)
        {
            Assert.Equal(Uri.UriSchemeHttp, address.Scheme);
            Assert.Equal("127.0.0.1", address.Host);
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)));
            Assert.Empty(address.UserInfo);
            Assert.InRange(address.Port, 1, 65535);
            Assert.Equal(addresses[0].Port, address.Port);
            Assert.DoesNotContain("github", address.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("/releases/latest", addresses[0].AbsolutePath);
        Assert.Equal("/download", addresses[2].AbsolutePath);
        Assert.Equal("(test build)", UpdateTrust.TestBuildNote(new CultureInfo("en")));
        Assert.Equal("(Testaufbau)", UpdateTrust.TestBuildNote(new CultureInfo("de-AT")));
    }
#else
    [Fact]
    public void TheAddressesAreHttps()
    {
        Assert.StartsWith("https://", UpdateTrust.ReleasesLatestUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", UpdateTrust.DownloadBaseUrl, StringComparison.Ordinal);
    }

    /// <summary>The release build is GitHub only, names no test build and carries no value of a test build.</summary>
    [Fact]
    public void TheReleaseBuildIsGitHubOnlyAndCarriesNoTestValues()
    {
        foreach (var url in new[] { UpdateTrust.ReleasesLatestUrl, UpdateTrust.ReleasesPageUrl, UpdateTrust.DownloadBaseUrl })
        {
            var address = new Uri(url);
            Assert.Equal(Uri.UriSchemeHttps, address.Scheme);
            Assert.True(address.IsDefaultPort);
            Assert.True(address.Host is "github.com" or "api.github.com", address.Host);
        }

        Assert.Null(UpdateTrust.TestBuildNote(new CultureInfo("en")));
        Assert.Null(UpdateTrust.TestBuildNote(new CultureInfo("de")));
        Assert.DoesNotContain(
            typeof(UpdateTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key.StartsWith("PagewingUpdateTest", StringComparison.Ordinal));
    }
#endif
}
