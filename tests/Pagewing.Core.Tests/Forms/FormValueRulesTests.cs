using Pagewing.Core.Forms;
using Xunit;
using static Pagewing.Core.Tests.Forms.FormTestFields;

namespace Pagewing.Core.Tests.Forms;

public sealed class FormValueRulesTests
{
    [Fact]
    public void GermanUmlautsAndEuroAreAccepted()
    {
        var check = FormValueRules.CheckText(Make(), "Grüße aus Köln, 5 €");

        Assert.True(check.Accepted);
        Assert.Null(check.Reason);
    }

    [Fact]
    public void GermanQuotesEnDashAndEllipsisAreRefusedAsAWhole()
    {
        var check = FormValueRules.CheckText(Make(), "Hallo – „Welt“…");

        Assert.False(check.Accepted);
        Assert.Contains("–", check.Reason);
        Assert.Contains("„", check.Reason);
        Assert.Contains("…", check.Reason);
    }

    [Fact]
    public void SoftHyphenIsRefused()
    {
        var check = FormValueRules.CheckText(Make(), "ab­c");

        Assert.False(check.Accepted);
        Assert.NotNull(check.Reason);
    }

    [Fact]
    public void TabAndCarriageReturnAreRefusedInASingleLineField()
    {
        Assert.False(FormValueRules.CheckText(Make(), "a\tb").Accepted);
        Assert.False(FormValueRules.CheckText(Make(), "a\rb").Accepted);
        Assert.False(FormValueRules.CheckText(Make(traits: FormFieldTraits.Multiline), "a\tb").Accepted);
    }

    [Fact]
    public void EmojiIsRefusedAsAWhole()
    {
        const string Text = "Hi \U0001F600!";

        var check = FormValueRules.CheckText(Make(), Text);

        Assert.False(check.Accepted);
        Assert.Contains("\U0001F600", check.Reason);
        Assert.Equal("Hi \U0001F600!", Text);
    }

    [Fact]
    public void TextAboveMaxLengthIsRefused()
    {
        var field = Make(maxLength: 5);

        Assert.True(FormValueRules.CheckText(field, "12345").Accepted);
        Assert.False(FormValueRules.CheckText(field, "123456").Accepted);
    }

    [Fact]
    public void ALineBreakCountsAsOneCharacterForMaxLength()
    {
        var field = Make(traits: FormFieldTraits.Multiline, maxLength: 3);

        Assert.True(FormValueRules.CheckText(field, "a\r\nb").Accepted);
        Assert.True(FormValueRules.CheckText(field, "a\nb").Accepted);
        Assert.True(FormValueRules.CheckText(field, "a\rb").Accepted);
        Assert.False(FormValueRules.CheckText(field, "a\r\nbc").Accepted);
    }

    [Fact]
    public void LineBreakIsRefusedInASingleLineField()
    {
        Assert.False(FormValueRules.CheckText(Make(), "a\nb").Accepted);
        Assert.False(FormValueRules.CheckText(Make(), "a\r\nb").Accepted);
    }

    [Fact]
    public void LineBreakIsAcceptedInAMultilineField()
    {
        var field = Make(traits: FormFieldTraits.Multiline);

        Assert.True(FormValueRules.CheckText(field, "a\nb").Accepted);
        Assert.True(FormValueRules.CheckText(field, "a\r\nb").Accepted);
        Assert.True(FormValueRules.CheckText(field, "a\rb").Accepted);
    }

    [Fact]
    public void ControlCharacterIsRefused()
    {
        Assert.False(FormValueRules.CheckText(Make(traits: FormFieldTraits.Multiline), "a\u0001b").Accepted);
        Assert.False(FormValueRules.CheckText(Make(), "a\u007Fb").Accepted);
        Assert.False(FormValueRules.CheckText(Make(), "a\0b").Accepted);
    }

    [Fact]
    public void TextAboveTheAbsoluteLimitIsRefused()
    {
        var field = Make(traits: FormFieldTraits.Multiline);

        Assert.True(FormValueRules.CheckText(field, new string('a', FormValueRules.MaxValueLength)).Accepted);
        Assert.False(FormValueRules.CheckText(field, new string('a', FormValueRules.MaxValueLength + 1)).Accepted);
    }

    [Fact]
    public void AnEmptyTextIsAccepted()
    {
        Assert.True(FormValueRules.CheckText(Make(maxLength: 1), string.Empty).Accepted);
    }

    [Fact]
    public void NonBreakingSpaceEuroAndTheWinAnsiExtrasAreAccepted()
    {
        Assert.True(FormValueRules.CheckText(Make(), "a b € ˆ ˜ ™ ÿ").Accepted);
    }
}
