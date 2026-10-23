namespace Pagewing.Core.Pdf;

/// <summary>
/// Entries of the document information dictionary; <see langword="null"/> when missing or empty. Dates are the
/// raw PDF date text (for example <c>D:20261016120000+02'00'</c>).
/// </summary>
public sealed record PdfMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    string? CreationDate,
    string? ModDate);
