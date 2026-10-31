using System.Text.Json;
using System.Text.Json.Serialization;
using Pagewing.Core.Annotations;

namespace Pagewing.Core.Signatures;

/// <summary>How a saved signature was made.</summary>
public enum SavedSignatureKind
{
    /// <summary>Drawn by hand: strokes in the library, written to a PDF as ink.</summary>
    Drawn,

    /// <summary>Imported from an image: a PNG in the library, written to a PDF as a stamp with an image.</summary>
    Image,
}

/// <summary>A signature of the library.</summary>
/// <param name="Id">The identity and file name (32 lower-case hex digits, a GUID, never derived from the name).</param>
/// <param name="Name">The name the user gave it (at most <see cref="SignatureStore.MaxNameLength"/> characters).</param>
/// <param name="Kind">How it was made.</param>
/// <param name="Aspect">Width divided by height, between 0.05 and 20.</param>
/// <param name="Color">The ink of a drawn signature when it is placed (a placed one can be recoloured); unused for an image.</param>
/// <param name="Strokes">The strokes of a drawn signature in the unit square (x to the right, y down, both 0 to 1), otherwise <see langword="null"/>.</param>
public sealed record SavedSignature(
    string Id,
    string Name,
    SavedSignatureKind Kind,
    double Aspect,
    DrawColor Color,
    IReadOnlyList<IReadOnlyList<DisplayPoint>>? Strokes);

/// <summary>The shape of the index file of the library.</summary>
internal sealed class SignatureLibraryDocument
{
    /// <summary>Version of the file format; a newer version than this app knows is tolerated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The signatures in the order they were added.</summary>
    public List<SavedSignature?> Items { get; set; } = [];
}

/// <summary>Source-generated JSON metadata for the library index (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SignatureLibraryDocument))]
internal sealed partial class SignatureJsonContext : JsonSerializerContext
{
}

/// <summary>The library already holds <see cref="SignatureLimits.MaxSignatures"/> signatures.</summary>
public sealed class SignatureLimitException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public SignatureLimitException()
        : base($"The library holds at most {SignatureLimits.MaxSignatures} signatures.")
    {
    }
}
