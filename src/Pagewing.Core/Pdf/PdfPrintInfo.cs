namespace Pagewing.Core.Pdf;

/// <summary>What a document says about printing.</summary>
/// <param name="IsEncrypted">Whether the document is encrypted.</param>
/// <param name="PrintAllowed">Whether the permissions of whoever opened it allow printing (an unencrypted document, or one opened with its owner password, allows it).</param>
public readonly record struct PdfPrintInfo(bool IsEncrypted, bool PrintAllowed);
