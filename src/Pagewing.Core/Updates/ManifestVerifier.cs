using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pagewing.Core.Updates;

/// <summary>Why a manifest was refused.</summary>
public enum ManifestRejection
{
    /// <summary>Not valid JSON of the expected shape (checked only after the signature).</summary>
    Malformed,

    /// <summary>The signature names a key the program does not know.</summary>
    UnknownKey,

    /// <summary>The signature is not well formed or does not match the manifest bytes.</summary>
    BadSignature,

    /// <summary>A manifest format this program does not read.</summary>
    UnsupportedFormat,

    /// <summary>The version is not newer than the running one.</summary>
    NotNewer,

    /// <summary>A file name that is not a plain name (path parts, control characters, odd length).</summary>
    UnsafeFileName,

    /// <summary>A file size of zero or less, or beyond <see cref="ManifestVerifier.MaxFileSize"/>.</summary>
    UnsafeSize,
}

/// <summary>The outcome of <see cref="ManifestVerifier.Verify"/>: exactly one of the two is set.</summary>
public readonly record struct ManifestResult(UpdateManifest? Manifest, ManifestRejection? Rejection);

/// <summary>
/// Checks a downloaded manifest. The signature (<c>&lt;keyId&gt;:&lt;base64 of the 64 byte IEEE P1363 ECDSA P-256
/// signature&gt;</c>) is over the raw bytes of the manifest file with SHA-256; the bytes are parsed only after it
/// verified. Every deviation refuses the manifest.
/// </summary>
public static partial class ManifestVerifier
{
    /// <summary>The largest file size a manifest may announce (1 GiB).</summary>
    public const long MaxFileSize = 1L << 30;

    private const int SignatureLength = 64;
    private const int MaxFiles = 16;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();

    [GeneratedRegex("^[0-9A-Fa-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    /// <summary>Verifies and parses a manifest; <paramref name="current"/> is the running version.</summary>
    public static ManifestResult Verify(
        ReadOnlySpan<byte> manifestJson,
        string signatureText,
        IReadOnlyList<UpdateKey> keys,
        UpdateVersion current)
    {
        if (CheckSignature(manifestJson, signatureText, keys) is { } signatureProblem)
        {
            return Reject(signatureProblem);
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestJson, UpdateManifestJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            return Reject(ManifestRejection.Malformed);
        }

        if (manifest is null)
        {
            return Reject(ManifestRejection.Malformed);
        }

        if (manifest.Format != UpdateManifest.CurrentFormat)
        {
            return Reject(ManifestRejection.UnsupportedFormat);
        }

        if (!UpdateVersion.TryParse(manifest.Version, out var version) || manifest.Files is not { Count: > 0 and <= MaxFiles })
        {
            return Reject(ManifestRejection.Malformed);
        }

        if (version <= current)
        {
            return Reject(ManifestRejection.NotNewer);
        }

        return CheckFiles(manifest.Files) is { } fileProblem ? Reject(fileProblem) : new ManifestResult(manifest, null);
    }

    private static ManifestResult Reject(ManifestRejection rejection) => new(null, rejection);

    private static ManifestRejection? CheckSignature(ReadOnlySpan<byte> data, string signatureText, IReadOnlyList<UpdateKey> keys)
    {
        var text = signatureText.AsSpan().Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return colon == 0 ? ManifestRejection.UnknownKey : ManifestRejection.BadSignature;
        }

        var keyId = text[..colon].ToString();
        var candidates = keys.Where(key => string.Equals(key.Id, keyId, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0)
        {
            return ManifestRejection.UnknownKey;
        }

        var signature = new byte[SignatureLength + 3];
        if (!Convert.TryFromBase64Chars(text[(colon + 1)..], signature, out var written) || written != SignatureLength)
        {
            return ManifestRejection.BadSignature;
        }

        foreach (var key in candidates)
        {
            if (Verifies(data, signature.AsSpan(0, SignatureLength), key))
            {
                return null;
            }
        }

        return ManifestRejection.BadSignature;
    }

    private static bool Verifies(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, UpdateKey key)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ManifestRejection? CheckFiles(IReadOnlyList<UpdateFile> files)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file is null
                || file.Kind is null
                || file.Name is null
                || file.Sha256 is null
                || !UpdateFileKinds.All.Contains(file.Kind)
                || !kinds.Add(file.Kind)
                || !Sha256Hex().IsMatch(file.Sha256))
            {
                return ManifestRejection.Malformed;
            }

            if (!SafeFileName().IsMatch(file.Name))
            {
                return ManifestRejection.UnsafeFileName;
            }

            if (file.Size is <= 0 or > MaxFileSize)
            {
                return ManifestRejection.UnsafeSize;
            }
        }

        return null;
    }
}
