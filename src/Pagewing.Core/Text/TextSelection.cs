namespace Pagewing.Core.Text;

/// <summary>An insertion position in the text of a document: a caret between two characters of a page.</summary>
/// <param name="Page">The 0-based page index.</param>
/// <param name="Caret">The insertion position in the page's characters, 0 to <see cref="PageText.Chars"/>.Count.</param>
public readonly record struct TextPosition(int Page, int Caret) : IComparable<TextPosition>
{
    /// <summary>Gets whether <paramref name="left"/> comes before <paramref name="right"/>.</summary>
    public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;

    /// <summary>Gets whether <paramref name="left"/> comes after <paramref name="right"/>.</summary>
    public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;

    /// <summary>Gets whether <paramref name="left"/> does not come after <paramref name="right"/>.</summary>
    public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Gets whether <paramref name="left"/> does not come before <paramref name="right"/>.</summary>
    public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;

    /// <summary>Compares by page, then by caret.</summary>
    /// <param name="other">The position to compare with.</param>
    /// <returns>A negative number if this position comes first, a positive number if it comes last, else 0.</returns>
    public int CompareTo(TextPosition other)
    {
        var page = Page.CompareTo(other.Page);
        return page != 0 ? page : Caret.CompareTo(other.Caret);
    }
}

/// <summary>A selection of text from where the pointer went down (the anchor) to where it is now (the focus).</summary>
/// <param name="Anchor">Where the selection started.</param>
/// <param name="Focus">Where the selection ends now; it can be before the anchor.</param>
public readonly record struct TextSelection(TextPosition Anchor, TextPosition Focus)
{
    /// <summary>Gets the smaller of <see cref="Anchor"/> and <see cref="Focus"/>.</summary>
    public TextPosition Start => Focus < Anchor ? Focus : Anchor;

    /// <summary>Gets the larger of <see cref="Anchor"/> and <see cref="Focus"/>.</summary>
    public TextPosition End => Focus < Anchor ? Anchor : Focus;

    /// <summary>Gets whether the selection covers no text (<see cref="Start"/> equals <see cref="End"/>).</summary>
    public bool IsEmpty => Anchor == Focus;
}
