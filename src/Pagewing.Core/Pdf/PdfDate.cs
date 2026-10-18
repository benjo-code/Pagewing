namespace Pagewing.Core.Pdf;

/// <summary>Parses PDF date strings (<c>D:YYYYMMDDHHmmSSOHH'mm'</c>).</summary>
public static class PdfDate
{
    /// <summary>
    /// Parses <paramref name="value"/> in the form <c>D:YYYY[MM[DD[HH[mm[SS]]]]]</c> followed by an optional zone
    /// (<c>Z</c>, <c>+HH'mm'</c> or <c>-HH'mm'</c>; the apostrophes and the minutes are optional). A date without
    /// a zone is local time. The <c>D:</c> prefix is optional and surrounding white space is ignored. Never throws.
    /// </summary>
    /// <returns>The date, or <see langword="null"/> when <paramref name="value"/> is missing or not a valid date.</returns>
    public static DateTimeOffset? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.AsSpan().Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var position = 0;
        if (!TryReadNumber(text, ref position, 4, out var year))
        {
            return null;
        }

        var month = ReadOptional(text, ref position, 1, out var fieldsOk);
        var day = fieldsOk ? ReadOptional(text, ref position, 1, out fieldsOk) : 1;
        var hour = fieldsOk ? ReadOptional(text, ref position, 0, out fieldsOk) : 0;
        var minute = fieldsOk ? ReadOptional(text, ref position, 0, out fieldsOk) : 0;
        var second = fieldsOk ? ReadOptional(text, ref position, 0, out fieldsOk) : 0;
        if (!fieldsOk)
        {
            return null;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        var zone = text[position..];
        if (zone.IsEmpty)
        {
            return ToLocal(local);
        }

        return TryParseZone(zone, out var offset) ? ToOffset(local, offset) : null;
    }

    // Reads a two-digit field; a missing field (end of text) gives the default. A partial or non-numeric field
    // is only an error when the text goes on with digits, so that the zone ("Z", "+", "-") can start here.
    private static int ReadOptional(ReadOnlySpan<char> text, ref int position, int defaultValue, out bool ok)
    {
        ok = true;
        if (position >= text.Length || !char.IsAsciiDigit(text[position]))
        {
            if (position < text.Length && text[position] is not ('Z' or 'z' or '+' or '-'))
            {
                ok = false;
            }

            return defaultValue;
        }

        if (!TryReadNumber(text, ref position, 2, out var number))
        {
            ok = false;
            return defaultValue;
        }

        return number;
    }

    private static bool TryReadNumber(ReadOnlySpan<char> text, ref int position, int digits, out int number)
    {
        number = 0;
        if (position + digits > text.Length)
        {
            return false;
        }

        for (var i = 0; i < digits; i++)
        {
            var c = text[position + i];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            number = (number * 10) + (c - '0');
        }

        position += digits;
        return true;
    }

    private static bool TryParseZone(ReadOnlySpan<char> zone, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var sign = zone[0];
        var rest = zone[1..];
        if (sign is 'Z' or 'z')
        {
            // "Z" alone, or "Z00'00'" as written by some producers.
            return rest.IsEmpty || TryParseOffsetDigits(rest, out var zero) && zero == TimeSpan.Zero;
        }

        if (sign is not ('+' or '-') || !TryParseOffsetDigits(rest, out var magnitude))
        {
            return false;
        }

        offset = sign == '-' ? -magnitude : magnitude;
        return true;
    }

    // HH, HH'mm', HH'mm, HHmm.
    private static bool TryParseOffsetDigits(ReadOnlySpan<char> text, out TimeSpan magnitude)
    {
        magnitude = TimeSpan.Zero;
        var position = 0;
        if (!TryReadNumber(text, ref position, 2, out var hours))
        {
            return false;
        }

        var minutes = 0;
        if (position < text.Length)
        {
            if (text[position] == '\'')
            {
                position++;
            }

            if (!TryReadNumber(text, ref position, 2, out minutes))
            {
                return false;
            }

            if (position < text.Length && text[position] == '\'')
            {
                position++;
            }

            if (position != text.Length)
            {
                return false;
            }
        }

        if (hours > 23 || minutes > 59)
        {
            return false;
        }

        magnitude = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private static DateTimeOffset? ToLocal(DateTime local)
    {
        try
        {
            return new DateTimeOffset(local);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ToOffset(DateTime local, TimeSpan offset)
    {
        try
        {
            return new DateTimeOffset(local, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
