namespace Pagewing.Core.Redaction;

public static partial class RedactionPatterns
{
    // Grouped forms, in the order they are tried: 4-4-4-4-3 (19 digits), Amex 4-6-5, Diners 4-6-4, 4-4-4-4, and 4-4-4-3 (15 digits).
    // A shape that is "Luhn only" is accepted only with a right checksum (it never makes a ChecksumFailed candidate).
    private static readonly (int[] Groups, bool LuhnOnly)[] CardShapes =
    [
        ([4, 4, 4, 4, 3], false),
        ([4, 6, 5], false),
        ([4, 6, 4], false),
        ([4, 4, 4, 4], false),
        ([4, 4, 4, 3], true),
    ];

    private const int MaxCardGroups = 5;

    private const int MaxCardGroupRead = 7;

    // A number in front of a card ("PIN 1234 4111 1111 1111 1111", a year, a date) can make the first shape start one or two groups too early.
    private const int ShiftsTried = 2;

    /// <summary>
    /// Payment card numbers: 13 to 19 digits, in one piece or grouped 4-4-4-4, 4-4-4-4-3, 4-6-5 (Amex) or 4-6-4 (Diners) with a space
    /// or a hyphen between the groups; the Luhn check gives <see cref="PatternCertainty.ChecksumOk"/>. A wrong checksum is
    /// <see cref="PatternCertainty.ChecksumFailed"/> only for the grouped forms or a number that starts like a card of a large network,
    /// otherwise every long number would be a candidate. A card number does not start with 0 and does not follow a plus sign.
    /// </summary>
    private static void ScanCards(string t, List<PatternMatch> ok, List<PatternMatch> other)
    {
        Span<byte> digits = stackalloc byte[MaxCardDigits];
        Span<int> groupStart = stackalloc int[MaxCardGroups];
        Span<int> groupEnd = stackalloc int[MaxCardGroups];
        Span<int> savedStart = stackalloc int[ShiftsTried + 1];
        Span<int> savedEnd = stackalloc int[ShiftsTried + 1];
        var n = t.Length;
        var i = 0;
        while (i < n)
        {
            if (!IsDigit(t[i]))
            {
                i++;
                continue;
            }

            // i is the start of a run of digits (the digits before it were skipped as a whole).
            var runEnd = i;
            while (runEnd < n && IsDigit(t[runEnd]))
            {
                runEnd++;
            }

            if (t[i] != '0' && (i == 0 || (!LetterOrDigitBefore(t, i) && t[i - 1] != '+'))
                && TryCard(t, i, runEnd, digits, groupStart, groupEnd, out var end, out var certainty, out var groups))
            {
                var matchStart = i;
                if (certainty != PatternCertainty.ChecksumOk)
                {
                    // Prefer a card with a right checksum that starts one or two groups later.
                    for (var k = 0; k <= ShiftsTried; k++)
                    {
                        savedStart[k] = k < groups ? groupStart[k] : -1;
                        savedEnd[k] = k < groups ? groupEnd[k] : -1;
                    }

                    for (var k = 1; k <= ShiftsTried && savedStart[k] >= 0; k++)
                    {
                        if (t[savedStart[k]] != '0'
                            && TryCard(t, savedStart[k], savedEnd[k], digits, groupStart, groupEnd, out var shiftedEnd, out var shiftedCertainty, out _)
                            && shiftedCertainty == PatternCertainty.ChecksumOk)
                        {
                            matchStart = savedStart[k];
                            end = shiftedEnd;
                            certainty = shiftedCertainty;
                            break;
                        }
                    }
                }

                (certainty == PatternCertainty.ChecksumOk ? ok : other).Add(new PatternMatch(RedactionPatternKind.Card, matchStart, end, certainty));
                i = end;
                continue;
            }

            i = runEnd;
        }
    }

    private static bool TryCard(string t, int start, int runEnd, Span<byte> digits, Span<int> groupStart, Span<int> groupEnd, out int end, out PatternCertainty certainty, out int groups)
    {
        end = start;
        certainty = PatternCertainty.ChecksumFailed;

        // Read up to five groups: the run, then runs that follow after exactly one separator.
        groups = 0;
        var s = start;
        var e = runEnd;
        while (true)
        {
            groupStart[groups] = s;
            groupEnd[groups] = e;
            groups++;
            if (groups == MaxCardGroups || e + 1 >= t.Length || !IsCardSeparator(t[e]) || !IsDigit(t[e + 1]))
            {
                break;
            }

            s = e + 1;
            e = s;
            var limit = s + MaxCardGroupRead;
            while (e < t.Length && e < limit && IsDigit(t[e]))
            {
                e++;
            }
        }

        var found = false;
        var foundEnd = start;
        var foundOk = false;
        foreach (var (shape, luhnOnly) in CardShapes)
        {
            if (groups < shape.Length || !GroupsMatch(shape, groupStart, groupEnd))
            {
                continue;
            }

            var shapeEnd = groupEnd[shape.Length - 1];
            if (LetterOrDigitAt(t, shapeEnd))
            {
                continue;
            }

            var length = CopyDigits(t, groupStart, groupEnd, shape.Length, digits);
            if (length >= MinCardDigits && Luhn(digits[..length]))
            {
                found = true;
                foundEnd = shapeEnd;
                foundOk = true;
                break;
            }

            if (!found && !luhnOnly)
            {
                found = true;
                foundEnd = shapeEnd;
            }
        }

        if (!found || !foundOk)
        {
            // The ungrouped form: the run alone.
            var runLength = runEnd - start;
            if (runLength is >= MinCardDigits and <= MaxCardDigits && !LetterOrDigitAt(t, runEnd))
            {
                var length = CopyDigits(t, groupStart, groupEnd, 1, digits);
                if (!IsIsbn13(digits[..length]))
                {
                    if (Luhn(digits[..length]))
                    {
                        found = true;
                        foundEnd = runEnd;
                        foundOk = true;
                    }
                    else if (!found && HasKnownCardPrefix(digits[..length]))
                    {
                        found = true;
                        foundEnd = runEnd;
                    }
                }
            }
        }

        if (!found)
        {
            return false;
        }

        end = foundEnd;
        certainty = foundOk ? PatternCertainty.ChecksumOk : PatternCertainty.ChecksumFailed;
        return true;
    }

    private static bool GroupsMatch(int[] shape, Span<int> groupStart, Span<int> groupEnd)
    {
        for (var k = 0; k < shape.Length; k++)
        {
            if (groupEnd[k] - groupStart[k] != shape[k])
            {
                return false;
            }
        }

        return true;
    }

    private static int CopyDigits(string t, Span<int> groupStart, Span<int> groupEnd, int groups, Span<byte> digits)
    {
        var length = 0;
        for (var k = 0; k < groups; k++)
        {
            for (var p = groupStart[k]; p < groupEnd[k]; p++)
            {
                digits[length++] = (byte)(t[p] - '0');
            }
        }

        return length;
    }

    private static bool IsCardSeparator(char c) => IsSpaceSeparator(c) || IsHyphen(c);

    private static bool Luhn(ReadOnlySpan<byte> digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var k = digits.Length - 1; k >= 0; k--)
        {
            var d = digits[k];
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>A 13-digit number that is a valid ISBN-13 (978 or 979 and the EAN-13 check): a book, not a card.</summary>
    private static bool IsIsbn13(ReadOnlySpan<byte> digits)
    {
        if (digits.Length != 13 || digits[0] != 9 || digits[1] != 7 || digits[2] is not (8 or 9))
        {
            return false;
        }

        var sum = 0;
        for (var k = 0; k < 13; k++)
        {
            sum += digits[k] * (k % 2 == 0 ? 1 : 3);
        }

        return sum % 10 == 0;
    }

    /// <summary>Visa 4; Mastercard 51-55 and 2221-2720; Amex 34, 37; Discover 6011, 65, 644-649; Diners 36, 38, 39, 300-305; JCB 3528-3589; UnionPay 62.</summary>
    private static bool HasKnownCardPrefix(ReadOnlySpan<byte> d)
    {
        var p2 = (d[0] * 10) + d[1];
        var p3 = (p2 * 10) + d[2];
        var p4 = (p3 * 10) + d[3];
        return d[0] == 4
            || p2 is >= 51 and <= 55
            || p4 is >= 2221 and <= 2720
            || p2 is 34 or 37
            || p4 == 6011 || p2 == 65 || p3 is >= 644 and <= 649
            || p2 is 36 or 38 or 39 || p3 is >= 300 and <= 305
            || p4 is >= 3528 and <= 3589
            || p2 == 62;
    }
}
