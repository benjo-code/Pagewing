namespace Pagewing.Core.Redaction;

public static partial class RedactionPatterns
{
    /// <summary>
    /// IBANs: a country code of the registry, two digits, then letters and digits; <see cref="MinIbanLength"/> to
    /// <see cref="MaxIbanLength"/> characters. Either written in one piece or in blocks of four separated by one space (also no-break
    /// space); the last block may be shorter. Right length for the country and mod 97 gives <see cref="PatternCertainty.ChecksumOk"/>,
    /// a fitting shape with a wrong checksum or length <see cref="PatternCertainty.ChecksumFailed"/>. A country code that is not in the
    /// registry is no candidate.
    /// </summary>
    private static List<PatternMatch> ScanIbans(string t)
    {
        var result = new List<PatternMatch>();
        Span<char> buffer = stackalloc char[MaxIbanLength];
        Span<int> boundaryCount = stackalloc int[MaxIbanLength / 4 + 1];
        Span<int> boundaryEnd = stackalloc int[MaxIbanLength / 4 + 1];
        var i = 0;
        while (i + 4 <= t.Length)
        {
            if (IsAsciiLetter(t[i]) && IsAsciiLetter(t[i + 1]) && IsDigit(t[i + 2]) && IsDigit(t[i + 3])
                && (i == 0 || !ContinuesRun(t[i - 1]))
                && TryIban(t, i, buffer, boundaryCount, boundaryEnd, out var end, out var certainty))
            {
                result.Add(new PatternMatch(RedactionPatternKind.Iban, i, end, certainty));
                i = end;
                continue;
            }

            i++;
        }

        return result;
    }

    private static bool TryIban(string t, int start, Span<char> buffer, Span<int> boundaryCount, Span<int> boundaryEnd, out int end, out PatternCertainty certainty)
    {
        end = start;
        certainty = PatternCertainty.ChecksumFailed;
        if (!IbanRegistry.TryGetLength(t[start], t[start + 1], out var countryLength))
        {
            return false;
        }

        var count = 0;
        var groupLength = 0;
        var boundaries = 0;
        var i = start;
        while (i < t.Length)
        {
            var c = t[i];
            if (IsAsciiLetterOrDigit(c))
            {
                if (count == MaxIbanLength)
                {
                    return false;
                }

                buffer[count++] = UpperAscii(c);
                groupLength++;
                i++;
            }
            else if (groupLength == 4 && IsSpaceSeparator(c) && i + 1 < t.Length && IsAsciiLetterOrDigit(t[i + 1]))
            {
                boundaryCount[boundaries] = count;
                boundaryEnd[boundaries] = i;
                boundaries++;
                groupLength = 0;
                i++;
            }
            else
            {
                break;
            }
        }

        if (i < t.Length && ContinuesRun(t[i]))
        {
            // A non-ASCII letter or digit right behind the code: part of a longer word.
            return false;
        }

        if (count < MinIbanLength)
        {
            return false;
        }

        end = i;
        if (count > countryLength)
        {
            // Words after a last block of four ("... 0100 Max") are not part of the IBAN: cut at the block that makes the right length.
            for (var k = 0; k < boundaries; k++)
            {
                if (boundaryCount[k] == countryLength)
                {
                    end = boundaryEnd[k];
                    count = countryLength;
                    break;
                }
            }
        }

        certainty = count == countryLength && Mod97IsOne(buffer[..count]) ? PatternCertainty.ChecksumOk : PatternCertainty.ChecksumFailed;
        return true;
    }

    /// <summary>The check of ISO 7064 mod 97-10 on an IBAN in upper case, without a big integer: the first four characters move to the end.</summary>
    internal static bool Mod97IsOne(ReadOnlySpan<char> iban)
    {
        if (iban.Length < 5)
        {
            return false;
        }

        var remainder = 0;
        for (var k = 4; k < iban.Length; k++)
        {
            remainder = Mod97Step(remainder, iban[k]);
        }

        for (var k = 0; k < 4; k++)
        {
            remainder = Mod97Step(remainder, iban[k]);
        }

        return remainder == 1;
    }

    private static int Mod97Step(int remainder, char c) => IsDigit(c)
        ? ((remainder * 10) + (c - '0')) % 97
        : ((remainder * 100) + (c - 'A' + 10)) % 97;
}
