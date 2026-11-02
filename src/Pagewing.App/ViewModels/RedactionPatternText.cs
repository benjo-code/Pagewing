using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>The words of the pattern search in the language of the interface (milestone 14.1): the kinds and the levels of certainty.</summary>
internal static class RedactionPatternText
{
    /// <summary>Gets the name of a kind of pattern: "E-mail", "IBAN", "Phone" or "Credit card".</summary>
    /// <param name="kind">The kind.</param>
    public static string NameOf(RedactionPatternKind kind) => kind switch
    {
        RedactionPatternKind.Email => Strings.RedactPatternEmail,
        RedactionPatternKind.Iban => Strings.RedactPatternIban,
        RedactionPatternKind.Phone => Strings.RedactPatternPhone,
        RedactionPatternKind.Card => Strings.RedactPatternCard,
        _ => kind.ToString(),
    };

    /// <summary>Gets the words of a level of certainty, the heading of its group: "Checksum matches", "Format matches" or "Checksum does not match".</summary>
    /// <param name="certainty">The level.</param>
    public static string NameOf(PatternCertainty certainty) => certainty switch
    {
        PatternCertainty.ChecksumOk => Strings.RedactGroupChecksumOk,
        PatternCertainty.ShapeOnly => Strings.RedactGroupShapeOnly,
        _ => Strings.RedactGroupChecksumFailed,
    };
}
