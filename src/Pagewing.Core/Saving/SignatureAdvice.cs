namespace Pagewing.Core.Saving;

/// <summary>What saving changes does to the digital signatures of a document.</summary>
public enum SignatureAdvice
{
    /// <summary>The document has no signature; nothing to warn about.</summary>
    None,

    /// <summary>The document is signed, but appended changes (incremental save) are allowed or at least do not touch the signed bytes.</summary>
    AppendedOnly,

    /// <summary>The signature declares that annotations are not allowed; saving annotations would invalidate it.</summary>
    WouldInvalidate,
}
