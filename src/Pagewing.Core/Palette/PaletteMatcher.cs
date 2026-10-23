namespace Pagewing.Core.Palette;

/// <summary>
/// How well an entry of the command palette matches what was typed: every word of the input (separated by white space)
/// must occur in the text, without regard to case (umlauts and other letters stay as they are: "uber" does not find
/// "Über"). Where a word occurs counts: at the start of the text beats the start of a word, which beats the middle of
/// a word; between texts where the words lie equally well, the shorter text wins.
/// </summary>
public static class PaletteMatcher
{
    // Weight of the best place of one word; the sum over the words is scaled above any text length.
    private const int TextStart = 3;
    private const int WordStart = 2;
    private const int Inner = 1;
    private const int PlaceScale = 1024;
    private const int MaxCountedLength = PlaceScale - 1;

    /// <summary>Scores <paramref name="text"/> for <paramref name="query"/>.</summary>
    /// <returns>
    /// <c>-1</c> when a word of the query does not occur in the text; <c>0</c> for an empty query (everything matches
    /// equally); otherwise a positive number, higher for a better match.
    /// </returns>
    public static int Score(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);
        var words = query.AsSpan();
        var places = 0;
        var any = false;
        while (true)
        {
            words = words.TrimStart();
            if (words.IsEmpty)
            {
                break;
            }

            var end = IndexOfWhiteSpace(words);
            var word = end < 0 ? words : words[..end];
            words = end < 0 ? [] : words[end..];
            var place = BestPlace(text, word);
            if (place == 0)
            {
                return -1;
            }

            places += place;
            any = true;
        }

        return any ? (places * PlaceScale) - Math.Min(text.Length, MaxCountedLength) : 0;
    }

    /// <summary>The best place of <paramref name="word"/> in <paramref name="text"/>: one of the weights, or 0 when it does not occur.</summary>
    private static int BestPlace(string text, ReadOnlySpan<char> word)
    {
        var best = 0;
        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var found = text.AsSpan(from).IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            var index = from + found;
            var place = index == 0 ? TextStart : char.IsLetterOrDigit(text[index - 1]) ? Inner : WordStart;
            if (place == TextStart)
            {
                return place;
            }

            best = Math.Max(best, place);
            from = index + 1;
        }

        return best;
    }

    private static int IndexOfWhiteSpace(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
