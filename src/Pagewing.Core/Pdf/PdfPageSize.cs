namespace Pagewing.Core.Pdf;

/// <summary>Size of a page in points (1/72 inch), after rotation and CropBox.</summary>
public readonly record struct PdfPageSize(double Width, double Height);
