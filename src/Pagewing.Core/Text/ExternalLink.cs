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
    /// Accepts only absolute <c>http</c>, <c>https</c> and <c>mailto</c> addresses, not longer than
    /// <see cref="MaxLength"/> after trimming white space. Rejected: an address with a control, invisible or
    /// direction-changing character (category Cc or Cf, line and paragraph separators, any white space), with a
    /// character that is not valid unescaped in an address (<c>"</c>, <c>&lt;</c>, <c>&gt;</c>, <c>`</c>, <c>{</c>, <c>|</c>,
    /// <c>}</c>, <c>^</c>) or a backslash (UNC forms), so nothing can break out of the quoting of a launcher; a web
    /// address that does not start with <c>scheme://host</c>, one the .NET parser rejects, or a mail address with
    /// nothing after <c>mailto:</c>. A mail address may only have the fields <c>to</c>, <c>cc</c>, <c>bcc</c>,
    /// <c>subject</c> and <c>body</c> (so no <c>attach</c>), and no line break or NUL in a field other than <c>body</c>.
    /// The scheme is compared case-insensitively.
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
    /// Checks the fields of a mail address and reads the domains of its recipients (the part before "?" and the
    /// values of <c>to</c>, <c>cc</c> and <c>bcc</c>, percent-decoded, separated by "," or ";"). The first domain is the
    /// host; a Punycode label in any of them counts. Fails for a field other than <c>to</c>, <c>cc</c>, <c>bcc</c>,
    /// <c>subject</c> and <c>body</c> (<c>attach</c> would send a local file), for a line break or NUL in any field but
    /// <c>body</c> (header injection, for example "%0D%0ABcc:"), and for a recipient with a forbidden character.
    /// </summary>
    private static bool TryReadMailDomains(string text, out string host, out string idnHost, out bool punycode)
    {
        host = string.Empty;
        idnHost = string.Empty;
        punycode = false;
        var address = text[Mailto.Length..];
        var fragment = address.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            address = address[..fragment];
        }

        var lists = new List<string>();
        var query = address.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            foreach (var field in address[(query + 1)..].Split('&'))
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
                        break;
                    case "body":
                        continue;
                    default:
                        return false;
                }

                if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                {
                    return false;
                }
            }

            address = address[..query];
        }

        lists.Insert(0, Uri.UnescapeDataString(address));
        foreach (var list in lists)
        {
            if (HasForbiddenCharacter(list))
            {
                return false;
            }

            foreach (var recipient in list.Split(',', ';'))
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

    private static bool HasForbiddenCharacter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\\' or ' ' or '"' or '<' or '>' or '`' or '{' or '|' or '}' or '^')
            {
                return true;
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return true;
            }

            if (Rune.IsWhiteSpace(rune))
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
