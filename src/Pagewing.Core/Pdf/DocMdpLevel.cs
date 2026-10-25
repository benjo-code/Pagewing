namespace Pagewing.Core.Pdf;

/// <summary>The change level (renamed from DocMdpPermission: CA1711 forbids the suffix "Permission") a certification signature (DocMDP, PDF 32000-1 section 12.8.2.2) allows after signing.</summary>
public enum DocMdpLevel
{
    /// <summary>The document has no DocMDP entry (or none that PDFium can read).</summary>
    NotSet = 0,

    /// <summary>No changes are permitted.</summary>
    NoChanges = 1,

    /// <summary>Filling in forms and signing are permitted.</summary>
    FormsOnly = 2,

    /// <summary>Filling in forms, signing and annotating are permitted.</summary>
    FormsAndAnnotations = 3,
}
