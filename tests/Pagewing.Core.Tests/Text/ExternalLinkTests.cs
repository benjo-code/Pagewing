using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class ExternalLinkTests
{
    [Theory]
    [InlineData("https://example.org/a", true)]
    [InlineData("http://example.org", true)]
    [InlineData("mailto:me@example.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("ftp://example.org", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyWebAndMailAddressesAreAccepted(string? url, bool expected)
    {
        Assert.Equal(expected, ExternalLink.TryParse(url, out var link));
        Assert.Equal(expected, link is not null);
    }

    [Fact]
    public void ControlCharactersAndOverlongAddressesAreRejected()
    {
        Assert.False(ExternalLink.TryParse("https://a.org/\u0000", out _));
        Assert.False(ExternalLink.TryParse("https://a.org/" + new string('a', 3000), out _));
        Assert.False(ExternalLink.TryParse(new string('a', 3000), out _));

        var longest = "https://a.org/" + new string('a', ExternalLink.MaxLength - "https://a.org/".Length);
        Assert.Equal(ExternalLink.MaxLength, longest.Length);
        Assert.True(ExternalLink.TryParse(longest, out _));
        Assert.False(ExternalLink.TryParse(longest + "a", out _));
    }

    [Theory]
    [InlineData("https://exa\tmple.org/")]
    [InlineData("https://example.org/a\nb")]
    [InlineData("https://example.org/a\rb")]
    [InlineData("https://example.org/\u007f")]
    [InlineData("https://example.org/a\u0085b")]
    [InlineData("https://example.org/a\u2028b")]
    [InlineData("https://example.org/a\u2029b")]
    [InlineData("https://exam\u200Bple.org/")]
    [InlineData("https://example\u200E.org/")]
    [InlineData("https://example.org/\u202Egpj.exe")]
    [InlineData("https://example.org/\u2066a")]
    [InlineData("https://example.org/\u2069a")]
    [InlineData("https://exam\u2060ple.org/")]
    [InlineData("https://exam\uFEFFple.org/")]
    [InlineData("mailto:me@example.org\u0000")]
    [InlineData("mailto:me@example.org%0D%0ABcc:evil@example.org")]
    [InlineData("mailto:me%00@example.org")]
    public void HiddenAndControlLikeCharactersAreRejectedAnywhere(string url)
    {
        Assert.False(ExternalLink.TryParse(url, out _));
    }

    [Theory]
    [InlineData("https:\\\\evil.example")]
    [InlineData("https:/\\evil.example")]
    [InlineData("https://example.org\\@evil.example/")]
    [InlineData("https://example.org/a\\b")]
    [InlineData("http:\\\\example.org")]
    [InlineData("https:/evil.example")]
    [InlineData("https:evil.example")]
    [InlineData("http:///evil.example")]
    [InlineData("//evil.example/")]
    [InlineData("https://")]
    [InlineData("mailto:")]
    [InlineData("https://example.org:99999/")]
    [InlineData("https ://example.org/")]
    [InlineData(":https://example.org/")]
    [InlineData("\uFF48ttps://example.org/")]
    public void OddFormsAreRejected(string url)
    {
        Assert.False(ExternalLink.TryParse(url, out _));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedAndTheSchemeIsCaseInsensitive()
    {
        Assert.True(ExternalLink.TryParse("  \u00A0HTTPS://Example.ORG/Path \n", out var link));

        Assert.Equal("HTTPS://Example.ORG/Path", link.Url);
        Assert.Equal("https", link.Scheme);
        Assert.Equal("example.org", link.Host);
        Assert.Equal("example.org", link.UnicodeHost);

        Assert.True(ExternalLink.TryParse("MailTo:me@example.org", out var mail));
        Assert.Equal("mailto", mail.Scheme);
    }

    [Theory]
    [InlineData("https://example.org/\" --new-window \"x")]
    [InlineData("https://example.org/a b")]
    [InlineData("https://example.org/?q=\"x")]
    [InlineData("https://example.org/?q=a b")]
    [InlineData("https://example.org/#\"x")]
    [InlineData("https://exa mple.org/")]
    [InlineData("https://example.org/a\"b")]
    [InlineData("https://example.org/a<b")]
    [InlineData("https://example.org/a>b")]
    [InlineData("https://example.org/a`b")]
    [InlineData("https://example.org/a{b")]
    [InlineData("https://example.org/a|b")]
    [InlineData("https://example.org/a}b")]
    [InlineData("https://example.org/a^b")]
    [InlineData("https://example.org/a\u00A0b")]
    [InlineData("https://example.org/a\u3000b")]
    [InlineData("mailto:a@b.c?subject=\" /a \"C:/Windows/win.ini")]
    [InlineData("mailto:a@b.c?subject=a b")]
    [InlineData("mailto:a b@c.d")]
    [InlineData("mailto:a@b.c?body=\"x")]
    [InlineData("mailto:a@b.c?body=a<b")]
    public void CharactersThatBreakOutOfALauncherAreRejectedEverywhere(string url)
    {
        Assert.False(ExternalLink.TryParse(url, out _));
    }

    [Theory]
    [InlineData("mailto:a@b.c?attach=/etc/passwd")]
    [InlineData("mailto:?attach=/etc/passwd")]
    [InlineData("mailto:a@b.c?attachment=file:///etc/passwd")]
    [InlineData("mailto:a@b.c?cc=x%0D%0ABcc:evil@e.f")]
    [InlineData("mailto:a@b.c?subject=Hi&attach=/etc/passwd")]
    [InlineData("mailto:a@b.c?ATTACH=/etc/passwd")]
    [InlineData("mailto:a@b.c?%61ttach=/etc/passwd")]
    [InlineData("mailto:a@b.c?x-header=1")]
    [InlineData("mailto:a@b.c?in-reply-to=x")]
    [InlineData("mailto:a@b.c?subject=Hi%0D%0ABcc:evil@e.f")]
    [InlineData("mailto:a@b.c?subject=Hi%0AX")]
    [InlineData("mailto:a@b.c?subject=Hi%00")]
    [InlineData("mailto:a@b.c?to=x%0Ay@z.d")]
    [InlineData("mailto:a@b.c?bcc=x%0Dy@z.d")]
    [InlineData("mailto:a@b.c?cc=%22x%22@z.d")]
    [InlineData("mailto:a@b.c?cc=x@z.d%20,y@z.d")]
    [InlineData("mailto:a@b.c?body=ok&attach=1")]
    [InlineData("mailto:a@b.c?attach")]
    public void MailFieldsOtherThanTheKnownOnesAreRejected(string url)
    {
        Assert.False(ExternalLink.TryParse(url, out _));
    }

    [Theory]
    [InlineData("mailto:a@b.c?subject=Hi%20there&body=Line1%0D%0ALine2")]
    [InlineData("mailto:a@b.c?SUBJECT=Hi&Body=x")]
    [InlineData("mailto:a@b.c?cc=d@e.f&bcc=g@h.i&subject=S")]
    [InlineData("mailto:?to=a@b.c&subject=Hi")]
    [InlineData("mailto:a@b.c?body=a%0Ab%00c")]
    [InlineData("mailto:a@b.c?subject=a&&body=b&")]
    [InlineData("mailto:a@b.c?subject=Hi#frag")]
    public void MailAddressesWithKnownFieldsAreAccepted(string url)
    {
        Assert.True(ExternalLink.TryParse(url, out var link));
        Assert.Equal(url, link.Url);
    }

    [Fact]
    public void RecipientsInMailFieldsAreCheckedLikeTheAddressPart()
    {
        Assert.True(ExternalLink.TryParse("mailto:?to=me@xn--pple-43d.com", out var puny));
        Assert.Equal(ExternalLinkWarning.Punycode, puny.Warning);
        Assert.Equal("xn--pple-43d.com", puny.Host);

        Assert.True(ExternalLink.TryParse("mailto:a@example.org?cc=you@xn--pple-43d.com", out var cc));
        Assert.Equal(ExternalLinkWarning.Punycode, cc.Warning);
        Assert.Equal("example.org", cc.Host);

        Assert.True(ExternalLink.TryParse("mailto:a@example.org?bcc=x@y.z,u@xn--pple-43d.com", out var several));
        Assert.Equal(ExternalLinkWarning.Punycode, several.Warning);
    }

    [Fact]
    public void UserInfoAndPunycodeAreFlagged()
    {
        Assert.True(ExternalLink.TryParse("https://paypal.com@evil.example/", out var userInfo));
        Assert.Equal(ExternalLinkWarning.UserInfo, userInfo.Warning);
        Assert.Equal("evil.example", userInfo.Host);

        Assert.True(ExternalLink.TryParse("https://xn--pple-43d.com", out var punycode));
        Assert.Equal(ExternalLinkWarning.Punycode, punycode.Warning);
        Assert.Equal("xn--pple-43d.com", punycode.Host);
        Assert.Equal("\u0430pple.com", punycode.UnicodeHost);
    }

    [Theory]
    [InlineData("https://@evil.example/")]
    [InlineData("https://:@evil.example/")]
    [InlineData("https://user:secret@example.org/")]
    [InlineData("http://example.org:80@evil.example/")]
    public void EveryFormOfUserInfoIsFlagged(string url)
    {
        Assert.True(ExternalLink.TryParse(url, out var link));
        Assert.Equal(ExternalLinkWarning.UserInfo, link.Warning);
    }

    [Theory]
    [InlineData("https://\u0430pple.com/")]
    [InlineData("https://sub.XN--pple-43d.com/")]
    public void UnicodeAndPunycodeHostsAreBothFlaggedAsPunycode(string url)
    {
        Assert.True(ExternalLink.TryParse(url, out var link));
        Assert.Equal(ExternalLinkWarning.Punycode, link.Warning);
        Assert.Contains("\u0430pple.com", link.UnicodeHost, StringComparison.Ordinal);
    }

    [Fact]
    public void AtSignsAfterTheHostAreNoUserInfo()
    {
        Assert.True(ExternalLink.TryParse("https://example.org/path@evil.example", out var path));
        Assert.Equal(ExternalLinkWarning.None, path.Warning);
        Assert.True(ExternalLink.TryParse("https://example.org/?mail=a@b.c#x@y", out var query));
        Assert.Equal(ExternalLinkWarning.None, query.Warning);
        Assert.Equal("example.org", query.Host);
    }

    [Fact]
    public void MailAddressesHaveTheirDomainAsHostAndNoUserInfoWarning()
    {
        Assert.True(ExternalLink.TryParse("mailto:me@example.org?subject=Hi", out var mail));
        Assert.Equal(ExternalLinkWarning.None, mail.Warning);
        Assert.Equal("example.org", mail.Host);

        Assert.True(ExternalLink.TryParse("mailto:me", out var local));
        Assert.Equal(string.Empty, local.Host);
        Assert.Equal(string.Empty, local.UnicodeHost);
        Assert.Equal(ExternalLinkWarning.None, local.Warning);

        Assert.True(ExternalLink.TryParse("mailto:me@xn--pple-43d.com", out var puny));
        Assert.Equal(ExternalLinkWarning.Punycode, puny.Warning);

        // The .NET parser does not take several recipients or a percent-encoded "@", so such addresses are not clickable.
        Assert.False(ExternalLink.TryParse("mailto:me@example.org,you@xn--pple-43d.com", out _));
        Assert.False(ExternalLink.TryParse("mailto:me%40xn--pple-43d.com", out _));
    }

    [Theory]
    [InlineData("https://example.org/")]
    [InlineData("http://www.example.org:8080/a/b?c=d#e")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("mailto:me@example.org")]
    public void CleanAddressesHaveNoWarning(string url)
    {
        Assert.True(ExternalLink.TryParse(url, out var link));
        Assert.Equal(ExternalLinkWarning.None, link.Warning);
        Assert.Equal(url, link.Url);
    }
}
