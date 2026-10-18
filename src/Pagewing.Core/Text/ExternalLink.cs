using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Pagewing.Core.Text;

/// <summary>What is suspicious about an external address, shown with the confirmation.</summary>
public enum ExternalLinkWarning
{
    /// <summary>Nothing noticed.</summary>
    None,

    /// <summary>The address has user information before the host ("user@"), the usual disguise of a phishing address.</summary>
    UserInfo,

    /// <summary>The host has an internationalised label (Punycode, "xn--"), which can imitate a well-known name.</summary>
    Punycode,
}

/// <summary>
/// An address outside the document that the viewer may offer to open: absolute, only <c>http</c>, <c>https</c> or
/// <c>mailto</c>. This is the only gate between what a PDF says and the system browser or mail program, so
/// <see cref="TryParse"/> is strict: whatever it does not accept is not clickable. Opening still needs a click and a
/// confirmation that shows <see cref="Url"/> in full.
/// </summary>
public sealed class ExternalLink
{
    /// <summary>The longest address accepted, in UTF-16 code units, after trimming.</summary>
    public const int MaxLength = 2048;

    private const string Http = "http://";
    private const string Https = "https://";
    private const string Mailto = "mailto:";
    private const string PunycodePrefix = "xn--";

    private ExternalLink(string url, string scheme, string host, string unicodeHost, ExternalLinkWarning warning)
    {
        Url = url;
        Scheme = scheme;
        Host = host;
        UnicodeHost = unicodeHost;
        Warning = warning;
    }

    /// <summary>
    /// Gets the address as given, without surrounding white space. This exact string is displayed in the confirmation
    /// and is the only string that may be passed to the system launcher.
    /// </summary>
    public string Url { get; }

    /// <summary>Gets the scheme in lower case: <c>http</c>, <c>https</c> or <c>mailto</c>.</summary>
    public string Scheme { get; }

    /// <summary>Gets the host in lower case as the address names it (the domain of a mail address); empty for a mail address without a domain.</summary>
    public string Host { get; }

    /// <summary>Gets <see cref="Host"/> with internationalised labels decoded, for display; empty if there is no host.</summary>
    public string UnicodeHost { get; }

    /// <summary>Gets what is suspicious about the address; user information wins over Punycode if both apply.</summary>
    public ExternalLinkWarning Warning { get; }

    /// <summary>
    /// Accepts an address only if all of these hold (white space around it is trimmed first):
    /// <list type="bullet">
    /// <item><description>Scheme: absolute <c>http://</c> or <c>https://</c> with a host, or <c>mailto:</c> with something after it; compared case-insensitively. Nothing else (no <c>javascript:</c>, <c>file:</c>, <c>ftp:</c>, relative or UNC forms), and the .NET parser must accept it.</description></item>
    /// <item><description>Length: at most <see cref="MaxLength"/> UTF-16 code units.</description></item>
    /// <item><description>Characters, in the whole string: no control, format, line or paragraph separator character (categories Cc, Cf, Zl, Zp), no white space (a space must be written <c>%20</c>), no invisible filler (U+115F, U+1160, U+180E, U+2800, U+3164, U+FFA0), no lone surrogate, none of <c>"</c> <c>&lt;</c> <c>&gt;</c> <c>`</c> <c>{</c> <c>|</c> <c>}</c> <c>^</c> and backslash (so nothing can break out of the quoting of a launcher), and no <c>%</c> that is not followed by two hex digits.</description></item>
    /// <item><description>Mail grammar: no raw <c>#</c>, no raw <c>;</c>, at most one raw <c>?</c>; after the <c>?</c> only the fields <c>to</c>, <c>cc</c>, <c>bcc</c>, <c>subject</c> and <c>body</c> (case-insensitive, <c>&amp;</c> separated; so no <c>attach</c>). Decoded recipients (address part and <c>to</c>, <c>cc</c>, <c>bcc</c>, separated by <c>,</c>) have none of the characters above and none of <c>&amp;</c> <c>;</c> <c>?</c> <c>#</c> <c>=</c>; decoded <c>subject</c> has no control, format or separator character and no white space other than a space; decoded <c>body</c> has no control character other than CR, LF and tab.</description></item>
    /// </list>
    /// Not rejected but flagged by <see cref="Warning"/>: user information before the host, and Punycode labels.
    /// </summary>
    /// <param name="url">The address; may be <see langword="null"/>.</param>
    /// <param name="link">The parsed address, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the address is acceptable.</returns>
    public static bool TryParse(string? url, [NotNullWhen(true)] out ExternalLink? link)
    {
        link = null;
        if (url is null)
        {
            return false;
        }

        var text = url.Trim();
        if (text.Length == 0 || text.Length > MaxLength || HasForbiddenCharacter(text))
        {
            return false;
        }

        string scheme;
        int prefixLength;
        if (text.StartsWith(Https, StringComparison.OrdinalIgnoreCase))
        {
            (scheme, prefixLength) = ("https", Https.Length);
        }
        else if (text.StartsWith(Http, StringComparison.OrdinalIgnoreCase))
        {
            (scheme, prefixLength) = ("http", Http.Length);
        }
        else if (text.StartsWith(Mailto, StringComparison.OrdinalIgnoreCase))
        {
            (scheme, prefixLength) = ("mailto", Mailto.Length);
        }
        else
        {
            return false;
        }

        if (prefixLength == text.Length)
        {
            return false;
        }

        var web = scheme != "mailto";
        var authority = string.Empty;
        if (web)
        {
            var rest = text.AsSpan(prefixLength);
            var end = rest.IndexOfAny('/', '?', '#');
            authority = (end < 0 ? rest : rest[..end]).ToString();
            if (authority.Length == 0)
            {
                return false;
            }
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != scheme || (web && uri.Host.Length == 0))
        {
            return false;
        }

        string host;
        string idnHost;
        var punycode = false;
        if (web)
        {
            host = uri.Host;
            try
            {
                idnHost = uri.IdnHost;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            punycode = HasPunycodeLabel(idnHost);
        }
        else if (!TryReadMailDomains(text, out host, out idnHost, out punycode))
        {
            return false;
        }

        var warning = ExternalLinkWarning.None;
        if (web && (authority.Contains('@', StringComparison.Ordinal) || uri.UserInfo.Length > 0))
        {
            warning = ExternalLinkWarning.UserInfo;
        }
        else if (punycode)
        {
            warning = ExternalLinkWarning.Punycode;
        }

        link = new ExternalLink(text, scheme, host, UnicodeOf(host, idnHost), warning);
        return true;
    }

    /// <summary>
    /// Checks the grammar of a mail address (see <see cref="TryParse"/>) and reads the domains of its recipients (the
    /// address part and the values of <c>to</c>, <c>cc</c> and <c>bcc</c>, percent-decoded, separated by ","). The
    /// first domain is the host; a Punycode label in any of them counts.
    /// </summary>
    private static bool TryReadMailDomains(string text, out string host, out string idnHost, out bool punycode)
    {
        host = string.Empty;
        idnHost = string.Empty;
        punycode = false;
        var address = text[Mailto.Length..];

        // RFC 6068 has no fragment, and clients split on these characters; each must be percent-encoded inside a field.
        if (address.Contains('#', StringComparison.Ordinal) || address.Contains(';', StringComparison.Ordinal))
        {
            return false;
        }

        var lists = new List<string>();
        var query = address.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            var fields = address[(query + 1)..];
            if (fields.Contains('?', StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var field in fields.Split('&'))
            {
                if (field.Length == 0)
                {
                    continue;
                }

                var equals = field.IndexOf('=', StringComparison.Ordinal);
                var name = Uri.UnescapeDataString(equals < 0 ? field : field[..equals]).ToLowerInvariant();
                var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(field[(equals + 1)..]);
                switch (name)
                {
                    case "to":
                    case "cc":
                    case "bcc":
                        lists.Add(value);
                        break;
                    case "subject":
                        if (HasHiddenCharacter(value))
                        {
                            return false;
                        }

                        break;
                    case "body":
                        if (HasControlOtherThanLineBreakAndTab(value))
                        {
                            return false;
                        }

                        break;
                    default:
                        return false;
                }
            }

            address = address[..query];
        }

        lists.Insert(0, Uri.UnescapeDataString(address));
        foreach (var list in lists)
        {
            if (HasHiddenCharacter(list) || HasBreakoutCharacter(list) || list.AsSpan().IndexOfAny("&;?#=") >= 0)
            {
                return false;
            }

            foreach (var recipient in list.Split(','))
            {
                var at = recipient.LastIndexOf('@');
                if (at < 0)
                {
                    continue;
                }

                var domain = recipient[(at + 1)..].ToLowerInvariant();
                if (domain.Length == 0)
                {
                    continue;
                }

                var ascii = AsciiOf(domain);
                punycode |= HasPunycodeLabel(ascii);
                if (host.Length == 0)
                {
                    host = domain;
                    idnHost = ascii;
                }
            }
        }

        return true;
    }

    private static string AsciiOf(string host)
    {
        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }

    private static bool HasForbiddenCharacter(string text) =>
        HasHiddenCharacter(text) || HasBreakoutCharacter(text) || HasMalformedEscape(text);

    // Characters that are invisible, change the direction or break lines: control, format, separators, white space
    // other than a plain space, invisible fillers and lone surrogates.
    private static bool HasHiddenCharacter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return true;
            }

            if ((Rune.IsWhiteSpace(rune) && rune.Value != ' ') || rune.Value is 0x115F or 0x1160 or 0x180E or 0x2800 or 0x3164 or 0xFFA0)
            {
                return true;
            }
        }

        return false;
    }

    // Characters that are not valid unescaped in an address and could break out of the quoting of a launcher.
    private static bool HasBreakoutCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c is '\\' or ' ' or '"' or '<' or '>' or '`' or '{' or '|' or '}' or '^')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMalformedEscape(string text)
    {
        for (var i = text.IndexOf('%', StringComparison.Ordinal); i >= 0; i = text.IndexOf('%', i + 1))
        {
            if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasControlOtherThanLineBreakAndTab(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPunycodeLabel(string idnHost)
    {
        foreach (var label in idnHost.Split('.'))
        {
            if (label.StartsWith(PunycodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string UnicodeOf(string host, string idnHost)
    {
        if (host.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return new IdnMapping().GetUnicode(idnHost);
        }
        catch (ArgumentException)
        {
            // An IP literal or a label the mapping cannot decode: show the host as the parser gave it.
            return host;
        }
    }
}
