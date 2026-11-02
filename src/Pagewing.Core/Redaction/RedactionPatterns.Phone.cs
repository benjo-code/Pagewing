namespace Pagewing.Core.Redaction;

public static partial class RedactionPatterns
{
    private static List<PatternMatch> ScanPhones(string t) => new PhoneScanner(t).Run();

    private static bool IsDashOrSlash(char c) => IsHyphen(c) || c == '/';

    private static bool IsPhoneSeparator(char c) => IsSpaceSeparator(c) || IsHyphen(c) || c is '/' or '.';

    /// <summary>
    /// Telephone numbers: 7 to 15 digits that start with <c>+</c>, <c>0</c> or an opening bracket and are joined by space, hyphen, slash,
    /// dot or brackets (<c>+49 711 1234567</c>, <c>0711/1234567</c>, <c>(0711) 123 45 67</c>). Works on a chain: digit groups joined
    /// by one separator (and brackets). The chain as a whole is the candidate, so a number in the middle of a longer run of digits is no
    /// match; if the chain as a whole is none, numbers that start with 0 inside it are tried (two numbers written one after the other).
    /// Dates (dd.mm.yyyy, dd.mm.yy, dd/mm/yyyy, yyyy-mm-dd, dd-mm-yyyy) at the start or the end of a chain, decimals that start with "0.",
    /// and ISBN-10 are not telephone numbers.
    /// </summary>
    private sealed class PhoneScanner(string t)
    {
        private const int MaxGroups = 64;
        private const byte CloseFlag = 1;
        private const byte OpenFlag = 2;

        private readonly int[] _groupStart = new int[MaxGroups];
        private readonly int[] _groupEnd = new int[MaxGroups];
        private readonly char[] _joinSeparator = new char[MaxGroups];
        private readonly byte[] _joinFlags = new byte[MaxGroups];
        private readonly List<PatternMatch> _result = [];
        private int _count;
        private bool _plus;
        private bool _paren;
        private bool _startOk;
        private int _chainStart;

        public List<PatternMatch> Run()
        {
            var i = 0;
            while (i < t.Length)
            {
                i = IsDigit(t[i]) ? ScanChain(i) : i + 1;
            }

            return _result;
        }

        private int ScanChain(int first)
        {
            _plus = false;
            _paren = false;
            _chainStart = first;
            if (first > 0 && t[first - 1] == '(')
            {
                _paren = true;
                _chainStart = first - 1;
                if (_chainStart > 0 && t[_chainStart - 1] == '+')
                {
                    _plus = true;
                    _chainStart--;
                }
            }
            else if (first > 0 && t[first - 1] == '+')
            {
                _plus = true;
                _chainStart = first - 1;
            }

            _startOk = !LetterOrDigitBefore(t, _chainStart);
            _count = 0;
            var overlong = false;
            var pendingSeparator = '\0';
            byte pendingFlags = 0;
            var spacedUsed = false;
            var pos = first;
            int end;
            while (true)
            {
                var s = pos;
                var e = s;
                while (e < t.Length && IsDigit(t[e]))
                {
                    e++;
                }

                if (_count < MaxGroups)
                {
                    _groupStart[_count] = s;
                    _groupEnd[_count] = e;
                    _joinSeparator[_count] = pendingSeparator;
                    _joinFlags[_count] = pendingFlags;
                    _count++;
                }
                else
                {
                    overlong = true;
                }

                var j = e;
                byte flags = 0;
                var separator = '\0';
                var spaced = false;
                if (j < t.Length && t[j] == ')')
                {
                    flags |= CloseFlag;
                    j++;
                }

                if (j + 1 < t.Length && IsSpaceSeparator(t[j]) && IsDashOrSlash(t[j + 1]))
                {
                    j++;
                    spaced = true;
                }

                if (j < t.Length && IsPhoneSeparator(t[j]))
                {
                    separator = t[j];
                    j++;
                    if (IsDashOrSlash(separator) && j < t.Length && IsSpaceSeparator(t[j]))
                    {
                        j++;
                        spaced = true;
                    }
                }

                if (j < t.Length && t[j] == '(')
                {
                    flags |= OpenFlag;
                    j++;
                }

                // A slash or hyphen with a space beside it ("0711 / 1234567") joins groups only once per number and only behind
                // the first or the second group, so that ranges and lists ("12 - 18", "1.2.3 / 4.5.6") stay apart.
                var spacedAllowed = !spaced || (!spacedUsed && _count <= 2);
                if (j > e && j < t.Length && IsDigit(t[j]) && spacedAllowed)
                {
                    spacedUsed |= spaced;
                    pendingSeparator = separator;
                    pendingFlags = flags;
                    pos = j;
                    continue;
                }

                end = e;
                break;
            }

            // A chain of more than MaxGroups groups has more than 15 digits even without a date at either end.
            if (!overlong)
            {
                Evaluate();
            }

            return end;
        }

        private void Evaluate()
        {
            var a = 0;
            var b = _count;
            if (b >= 3 && IsDateAt(0))
            {
                a = 3;
            }

            if (b - a >= 3 && IsDateAt(b - 3))
            {
                b -= 3;
            }

            if (a >= b || TryRange(a, b))
            {
                return;
            }

            // Not one number as a whole: try the numbers that start with 0 (or a bracket) inside, e.g. "0711 1234567 0711 7654321".
            var g = a;
            while (g < b)
            {
                if (!StartsNumber(g))
                {
                    g++;
                    continue;
                }

                var digits = 0;
                var best = -1;
                for (var h = g; h < b; h++)
                {
                    digits += _groupEnd[h] - _groupStart[h];
                    if (digits > MaxPhoneDigits)
                    {
                        break;
                    }

                    if (digits >= MinPhoneDigits && (h + 1 == b || StartsNumber(h + 1)))
                    {
                        best = h;
                    }
                }

                if (best >= 0 && TryRange(g, best + 1))
                {
                    g = best + 1;
                }
                else
                {
                    g++;
                }
            }
        }

        private bool StartsNumber(int g) =>
            g == 0 ? _plus || _paren || t[_groupStart[0]] == '0'
                   : (_joinFlags[g] & OpenFlag) != 0 || (t[_groupStart[g]] == '0' && _groupEnd[g] - _groupStart[g] >= 2);

        private bool TryRange(int a, int b)
        {
            var digits = 0;
            for (var k = a; k < b; k++)
            {
                digits += _groupEnd[k] - _groupStart[k];
            }

            if (digits is < MinPhoneDigits or > MaxPhoneDigits || (a == 0 && !_startOk))
            {
                return false;
            }

            var openHere = a == 0 ? _paren : (_joinFlags[a] & OpenFlag) != 0;
            var closeInside = false;
            for (var k = a + 1; k < b; k++)
            {
                closeInside |= (_joinFlags[k] & CloseFlag) != 0;
            }

            var plusHere = a == 0 && _plus;
            var bracketed = openHere && closeInside;
            if (!(plusHere || bracketed || t[_groupStart[a]] == '0'))
            {
                return false;
            }

            // "0.123456789": a decimal number, not a telephone number.
            if (b - a >= 2 && _groupEnd[a] - _groupStart[a] == 1 && t[_groupStart[a]] == '0' && _joinSeparator[a + 1] == '.')
            {
                return false;
            }

            var end = _groupEnd[b - 1];
            if (IsIsbn10(a, b, digits, end))
            {
                return false;
            }

            if (LetterOrDigitAt(t, end))
            {
                return false;
            }

            int start;
            if (a == 0)
            {
                start = plusHere || bracketed ? _chainStart : _groupStart[0];
            }
            else
            {
                start = bracketed ? _groupStart[a] - 1 : _groupStart[a];
            }

            _result.Add(new PatternMatch(RedactionPatternKind.Phone, start, end, PatternCertainty.ShapeOnly));
            return true;
        }

        private int Length(int g) => _groupEnd[g] - _groupStart[g];

        private int Value(int g)
        {
            var value = 0;
            for (var p = _groupStart[g]; p < _groupEnd[g]; p++)
            {
                value = (value * 10) + (t[p] - '0');
            }

            return value;
        }

        private static bool IsDayAndMonth(int day, int month) => day is >= 1 and <= 31 && month is >= 1 and <= 12;

        /// <summary>Whether the groups <paramref name="k"/> to <c>k + 2</c> are a date: dd.mm.yyyy, dd.mm.yy, dd/mm/yyyy, dd/mm/yy (day and month in either order), yyyy-mm-dd or dd-mm-yyyy.</summary>
        private bool IsDateAt(int k)
        {
            if (k + 2 >= _count || _joinFlags[k + 1] != 0 || _joinFlags[k + 2] != 0 || _joinSeparator[k + 1] != _joinSeparator[k + 2])
            {
                return false;
            }

            var separator = _joinSeparator[k + 1];
            int l0 = Length(k), l1 = Length(k + 1), l2 = Length(k + 2);
            if (l0 > 4 || l1 > 2 || l2 > 4)
            {
                return false;
            }

            int v0 = Value(k), v1 = Value(k + 1), v2 = Value(k + 2);
            if (separator == '.' || separator == '/')
            {
                if (l0 is not (1 or 2) || l1 is not (1 or 2) || l2 is not (2 or 4))
                {
                    return false;
                }

                return IsDayAndMonth(v0, v1) || (separator == '/' && IsDayAndMonth(v1, v0));
            }

            if (IsHyphen(separator))
            {
                if (l0 == 4 && l1 is 1 or 2 && l2 is 1 or 2)
                {
                    return IsDayAndMonth(v2, v1);
                }

                return l0 is 1 or 2 && l1 is 1 or 2 && l2 == 4 && IsDayAndMonth(v0, v1);
            }

            return false;
        }

        /// <summary>An ISBN-10 as books print it: four groups joined by hyphens or spaces with a valid check digit (also "-X" after nine digits).</summary>
        private bool IsIsbn10(int a, int b, int digits, int end)
        {
            var groups = b - a;
            for (var k = a + 1; k < b; k++)
            {
                if (_joinFlags[k] != 0 || !(IsHyphen(_joinSeparator[k]) || IsSpaceSeparator(_joinSeparator[k])))
                {
                    return false;
                }
            }

            var sum = 0;
            var position = 0;
            if (groups == 4 && digits == 10 && Length(b - 1) == 1)
            {
                for (var k = a; k < b; k++)
                {
                    for (var p = _groupStart[k]; p < _groupEnd[k]; p++)
                    {
                        sum += (10 - position) * (t[p] - '0');
                        position++;
                    }
                }

                return sum % 11 == 0;
            }

            if (groups == 3 && digits == 9 && end + 1 < t.Length && IsHyphen(t[end]) && t[end + 1] is 'X' or 'x'
                && !LetterOrDigitAt(t, end + 2))
            {
                for (var k = a; k < b; k++)
                {
                    for (var p = _groupStart[k]; p < _groupEnd[k]; p++)
                    {
                        sum += (10 - position) * (t[p] - '0');
                        position++;
                    }
                }

                return (sum + 10) % 11 == 0;
            }

            return false;
        }
    }
}
