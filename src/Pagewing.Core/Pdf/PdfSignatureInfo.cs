namespace Pagewing.Core.Pdf;

/// <summary>What a document declares about digital signatures. Pagewing only reads the declaration; it never validates a signature.</summary>
/// <param name="SignatureCount">The number of signatures in the document.</param>
/// <param name="Permission">The most restrictive DocMDP level any signature declares, or <see cref="DocMdpLevel.NotSet"/>.</param>
public sealed record PdfSignatureInfo(int SignatureCount, DocMdpLevel Permission);
