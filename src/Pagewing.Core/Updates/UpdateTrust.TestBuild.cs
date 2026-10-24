using System.Globalization;
using System.Reflection;

namespace Pagewing.Core.Updates;

/// <summary>
/// The TEST BUILD of <see cref="UpdateTrust"/>: compiled instead of <c>UpdateTrust.cs</c> only with the build property
/// <c>PagewingUpdateTestBuild=true</c> (Pagewing.Core.csproj), never in a release. It trusts exactly one test key and
/// fetches only from the test server on 127.0.0.1 (<c>tools/update-testserver.cs</c>), both given at build time as
/// <c>PagewingUpdateTestKey</c> and <c>PagewingUpdateTestPort</c> (packaging/README.md, section "Update prüfen"). The
/// values are read from the assembly metadata of this build, which the property alone adds; nothing at run time (no
/// environment variable, no switch) can change them, as in the release build.
/// </summary>
public static class UpdateTrust
{
    /// <summary>The identifier of the placeholder key of the release build (not trusted here).</summary>
    public const string PlaceholderKeyId = "placeholder";

    /// <summary>The identifier of the test key (<c>package.cs update-keygen --id test-1</c>).</summary>
    private const string TestKeyId = "test-1";

    private static readonly string Origin = "http://127.0.0.1:" + Port().ToString(CultureInfo.InvariantCulture);

    /// <summary>The one accepted key: the public test key of the build.</summary>
    public static IReadOnlyList<UpdateKey> Keys { get; } =
        [new UpdateKey(TestKeyId, Convert.FromBase64String(Metadata("PagewingUpdateTestKey")))];

    /// <summary>The answer naming the newest release, from the test server.</summary>
    public static string ReleasesLatestUrl => Origin + "/releases/latest";

    /// <summary>The "download page" of the test server.</summary>
    public static string ReleasesPageUrl => Origin + "/";

    /// <summary>The address the files are below on the test server; a file is <c>&lt;base&gt;/&lt;tag&gt;/&lt;name&gt;</c>.</summary>
    public static string DownloadBaseUrl => Origin + "/download";

    /// <summary>What the about card shows after the version: this is a test build.</summary>
    /// <param name="culture">The language of the user interface.</param>
    public static string? TestBuildNote(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName == "de" ? "(Testaufbau)" : "(test build)";
    }

    private static int Port()
    {
        var text = Metadata("PagewingUpdateTestPort");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException("The test build has no valid PagewingUpdateTestPort: " + text);
    }

    private static string Metadata(string key) =>
        typeof(UpdateTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value
            ?? throw new InvalidOperationException("The test build was built without " + key + ".");
}
