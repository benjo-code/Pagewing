using System.Globalization;
using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// An address a link of the document asks to open, waiting for the user's confirmation (<see cref="MainViewModel.PendingLink"/>).
/// Says how the confirmation shows it: always <see cref="ExternalLink.Url"/> in full, the host emphasised where it
/// really stands (<see cref="HostRange"/>) and a warning for a deceptive address (<see cref="WarningText"/>).
/// </summary>
/// <param name="Link">The address, as <see cref="ExternalLink.TryParse"/> accepted it.</param>
public sealed record PendingLink(ExternalLink Link)
{
    /// <summary>
    /// Gets where the host stands in <see cref="ExternalLink.Url"/> (start and length), or <see langword="null"/> when it
    /// is not written there exactly as <see cref="ExternalLink.Host"/> (another case, percent-escapes): then nothing is
    /// emphasised rather than the wrong part. For a web address the host must follow the last "@" of the authority (or
    /// the "//"), for a mail address an "@" and end at a delimiter.
    /// </summary>
    public (int Start, int Length)? HostRange => FindHost(Link);

    /// <summary>
    /// Gets the warning line, or <see langword="null"/> without a warning: user information before the host names the
    /// real host in ASCII (a look-alike cannot hide in it); Punycode names the host as it reads (<see cref="DisplayHost"/>).
    /// </summary>
    public string? WarningText => Link.Warning switch
    {
        ExternalLinkWarning.UserInfo => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.LinkWarningUserInfo), AsciiHost(Link.Host)),
        ExternalLinkWarning.Punycode => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.LinkWarningPunycode), DisplayHost(Link)),
        _ => null,
    };

    /// <summary>
    /// Gets the host for display: <see cref="ExternalLink.UnicodeHost"/> if it consists only of letters, digits, "-" and
    /// "." (no symbol, mark or other look-alike material), else <see cref="ExternalLink.Host"/> as the address writes it.
    /// </summary>
    internal static string DisplayHost(ExternalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var unicode = link.UnicodeHost;
        if (unicode.Length == 0)
        {
            return link.Host;
        }

        foreach (var rune in unicode.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune) && rune.Value is not ('-' or '.'))
            {
                return link.Host;
            }
        }

        return unicode;
    }

    private static string AsciiHost(string host)
    {
        if (host.All(char.IsAscii))
        {
            return host;
        }

        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }

    private static (int Start, int Length)? FindHost(ExternalLink link)
    {
        var url = link.Url;
        var host = link.Host;
        if (host.Length == 0)
        {
            return null;
        }

        if (link.Scheme == "mailto")
        {
            // The first recipient domain as written: after an "@", up to the end of that recipient.
            for (var at = url.IndexOf('@', StringComparison.Ordinal); at >= 0; at = url.IndexOf('@', at + 1))
            {
                var start = at + 1;
                var end = start + host.Length;
                if (string.CompareOrdinal(url, start, host, 0, host.Length) == 0
                    && (end == url.Length || url[end] is ',' or '?' or '&'))
                {
                    return (start, host.Length);
                }
            }

            return null;
        }

        var authorityStart = url.IndexOf("//", StringComparison.Ordinal) + 2;
        if (authorityStart < 2)
        {
            return null;
        }

        var authorityEnd = url.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        authorityEnd = authorityEnd < 0 ? url.Length : authorityStart + authorityEnd;
        if (authorityEnd <= authorityStart)
        {
            return null;
        }

        // The host ends the authority or comes before the port.
        var lastAt = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        var hostStart = lastAt >= 0 ? lastAt + 1 : authorityStart;
        var hostEnd = hostStart + host.Length;
        return hostEnd <= authorityEnd && (hostEnd == authorityEnd || url[hostEnd] == ':')
            && string.CompareOrdinal(url, hostStart, host, 0, host.Length) == 0
            ? (hostStart, host.Length)
            : null;
    }
}
