using System.Globalization;

namespace Pagewing.Core.Updates;

/// <summary>
/// A plain release version <c>MAJOR.MINOR.PATCH</c>. Pre-release and build metadata are not versions of a stable
/// release and do not parse.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    /// <summary>
    /// Parses <c>X.Y.Z</c>, optionally with a lower-case <c>v</c> in front (the form of a release tag). Anything else
    /// (a missing part, a sign, white space, <c>-rc.1</c>, <c>+build</c>, a number beyond <see cref="int.MaxValue"/>)
    /// gives <c>false</c> and the default value.
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var span = text.AsSpan();
        if (span[0] == 'v')
        {
            span = span[1..];
        }

        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3)
        {
            return false;
        }

        if (!TryParsePart(span[parts[0]], out var major)
            || !TryParsePart(span[parts[1]], out var minor)
            || !TryParsePart(span[parts[2]], out var patch))
        {
            return false;
        }

        version = new UpdateVersion(major, minor, patch);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(UpdateVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0)
        {
            return byMajor;
        }

        var byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Whether <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> is older than or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is newer than or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    /// <summary>The version as <c>X.Y.Z</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool TryParsePart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        if (part.IsEmpty)
        {
            return false;
        }

        foreach (var c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
