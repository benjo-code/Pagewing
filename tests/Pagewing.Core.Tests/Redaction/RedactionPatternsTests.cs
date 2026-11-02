using System.Diagnostics;
using System.Numerics;
using System.Text;
using Pagewing.Core.Redaction;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

public sealed class RedactionPatternsTests
{
    private const string Nbsp = "\u00A0";
    private const string Nnbsp = "\u202F";

    private static readonly IReadOnlySet<RedactionPatternKind> All = new HashSet<RedactionPatternKind>
    {
        RedactionPatternKind.Email,
        RedactionPatternKind.Iban,
        RedactionPatternKind.Phone,
        RedactionPatternKind.Card,
    };

    private static IReadOnlyList<PatternMatch> Scan(string text, params RedactionPatternKind[] kinds) =>
        RedactionPatterns.Scan(text, kinds.Length == 0 ? All : new HashSet<RedactionPatternKind>(kinds));

    private static string Slice(string text, PatternMatch match) => text[match.Start..match.End];

    private static PatternMatch Single(string text, params RedactionPatternKind[] kinds)
    {
        var matches = Scan(text, kinds);
        return Assert.Single(matches);
    }

    private static bool Mod97WithBigInteger(string iban)
    {
        var rearranged = iban[4..] + iban[..4];
        var digits = new StringBuilder();
        foreach (var c in rearranged)
        {
            digits.Append(char.IsDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return BigInteger.Parse(digits.ToString(), System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
    }

    private static string GroupByFour(string compact, string separator)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < compact.Length; i += 4)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(compact, i, Math.Min(4, compact.Length - i));
        }

        return builder.ToString();
    }

    // ---- IBAN ----

    [Fact]
    public void EveryRegistryExamplePassesLengthAndMod97()
    {
        var seen = new HashSet<string>();
        foreach (var (country, iban) in IbanRegistry.Examples)
        {
            Assert.True(seen.Add(country), $"{country} is listed twice");
            Assert.StartsWith(country, iban, StringComparison.Ordinal);
            Assert.True(IbanRegistry.TryGetLength(country, out var length), $"{country} has no length");
            Assert.True(iban.Length == length, $"{country}: example has {iban.Length} characters, the table says {length}");
            Assert.True(Mod97WithBigInteger(iban), $"{country}: {iban} fails mod 97");
            Assert.True(RedactionPatterns.Mod97IsOne(iban), $"{country}: {iban} fails the chunked mod 97");
        }

        Assert.Equal(89, IbanRegistry.Examples.Count);
    }

    [Fact]
    public void EveryRegistryExampleIsFoundAsChecksumOkInEveryWritingForm()
    {
        foreach (var (country, iban) in IbanRegistry.Examples)
        {
            foreach (var written in new[] { iban, GroupByFour(iban, " "), GroupByFour(iban, Nbsp), GroupByFour(iban, Nnbsp) })
            {
                var text = "IBAN: " + written + ".";
                var match = Single(text, RedactionPatternKind.Iban);
                Assert.True(match.Certainty == PatternCertainty.ChecksumOk, $"{country}: {written}");
                Assert.Equal(written, Slice(text, match));
            }
        }
    }

    [Fact]
    public void IbanRegistryLookupIsCaseInsensitiveAndRejectsNonCodes()
    {
        Assert.True(IbanRegistry.TryGetLength("de", out var length));
        Assert.Equal(22, length);
        Assert.False(IbanRegistry.TryGetLength("XX", out _));
        Assert.False(IbanRegistry.TryGetLength("D", out _));
        Assert.False(IbanRegistry.TryGetLength("DEU", out _));
        Assert.False(IbanRegistry.TryGetLength("1E", out _));
        Assert.False(IbanRegistry.TryGetLength(null!, out _));
    }

    [Fact]
    public void IbanGroupedByNonBreakingSpacesIsOneMatch()
    {
        var iban = "DE89" + Nbsp + "3704" + Nbsp + "0044" + Nbsp + "0532" + Nbsp + "0130" + Nbsp + "00";
        var text = "Konto " + iban + " Ende";

        var match = Single(text, RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
        Assert.Equal(iban, Slice(text, match));
        Assert.Equal(6, match.Start);
    }

    [Fact]
    public void IbanGroupedByNarrowNonBreakingSpacesAndPlainSpacesIsOneMatch()
    {
        var text = "DE89" + Nnbsp + "3704 0044" + Nbsp + "0532 0130" + Nnbsp + "00";

        var match = Single(text, RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
        Assert.Equal(text, Slice(text, match));
    }

    [Fact]
    public void IbanWithWrongCheckDigitsIsChecksumFailedNotDropped()
    {
        var match = Single("DE88 3704 0044 0532 0130 00", RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumFailed, match.Certainty);
        Assert.Equal(0, match.Start);
        Assert.Equal("DE88 3704 0044 0532 0130 00".Length, match.End);
    }

    [Fact]
    public void IbanWithWrongLengthForItsCountryIsChecksumFailed()
    {
        // 21 and 23 characters for a German IBAN (22).
        var shorter = Single("DE8937040044053201300", RedactionPatternKind.Iban);
        var longer = Single("DE893704004405320130001", RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumFailed, shorter.Certainty);
        Assert.Equal(PatternCertainty.ChecksumFailed, longer.Certainty);
        Assert.Equal(23, longer.End);
    }

    [Fact]
    public void IbanShorterThanFifteenCharactersIsNoCandidate()
    {
        Assert.Empty(Scan("DE89 3704 0044 05", RedactionPatternKind.Iban));
    }

    [Fact]
    public void IbanLongerThanThirtyFourCharactersIsNoCandidate()
    {
        Assert.Empty(Scan("DE89" + new string('1', 31), RedactionPatternKind.Iban));
        Assert.Single(Scan("DE89" + new string('1', 30), RedactionPatternKind.Iban));
    }

    [Fact]
    public void UnknownCountryCodeIsNoCandidate()
    {
        Assert.Empty(Scan("XX89 3704 0044 0532 0130 00", RedactionPatternKind.Iban));
        Assert.Empty(Scan("XX89370400440532013000", RedactionPatternKind.Iban));
    }

    [Fact]
    public void IbanInsideALongerRunIsNoMatch()
    {
        Assert.Empty(Scan("X" + "DE89370400440532013000", RedactionPatternKind.Iban));
        Assert.Empty(Scan("1DE89370400440532013000", RedactionPatternKind.Iban));
        Assert.Empty(Scan("DE89370400440532013000" + "é", RedactionPatternKind.Iban));
    }

    [Fact]
    public void IbanAtTheStartAndTheEndOfTheText()
    {
        var iban = "DE89370400440532013000";

        Assert.Equal((0, iban.Length), (Single(iban, RedactionPatternKind.Iban).Start, Single(iban, RedactionPatternKind.Iban).End));
        var both = Scan(iban + " " + iban, RedactionPatternKind.Iban);
        Assert.Equal(2, both.Count);
        Assert.Equal(iban.Length + 1, both[1].Start);
    }

    [Fact]
    public void IbanWordsAfterALastBlockOfFourAreNotPartOfIt()
    {
        // AD has 24 characters, so the last block has four of them and a word could be taken for another block.
        var text = "AD12 0001 2030 2003 5910 0100 Max Mustermann";

        var match = Single(text, RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
        Assert.Equal("AD12 0001 2030 2003 5910 0100", Slice(text, match));
    }

    [Fact]
    public void IbanInLowerCaseIsFound()
    {
        var match = Single("de89 3704 0044 0532 0130 00", RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
    }

    [Fact]
    public void IbanBlocksOfOtherSizesAreNotJoined()
    {
        // "DE89 37040044 0532013000": not blocks of four, so only the first block is read: too short.
        Assert.Empty(Scan("DE89 37040044 0532013000", RedactionPatternKind.Iban));
    }

    // ---- Card ----

    [Fact]
    public void CardLuhnOkAndGrouped()
    {
        var visa = Single("Karte 4111 1111 1111 1111 gültig", RedactionPatternKind.Card);
        var visaHyphen = Single("4111-1111-1111-1111", RedactionPatternKind.Card);
        var visaNbsp = Single("4111" + Nbsp + "1111" + Nbsp + "1111" + Nbsp + "1111", RedactionPatternKind.Card);
        var amex = Single("3782 822463 10005", RedactionPatternKind.Card);
        var diners = Single("3056 930902 5904", RedactionPatternKind.Card);
        var nineteen = Single("4111 1111 1111 1111 110", RedactionPatternKind.Card);
        var plain = Single("Nr.4111111111111111.", RedactionPatternKind.Card);
        var mastercard = Single("5555555555554444", RedactionPatternKind.Card);

        foreach (var match in new[] { visa, visaHyphen, visaNbsp, amex, diners, nineteen, plain, mastercard })
        {
            Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
        }

        Assert.Equal(6, visa.Start);
        Assert.Equal(25, visa.End);
        Assert.Equal(3, plain.Start);
        Assert.Equal(19, plain.End);
        Assert.Equal(23, nineteen.End);
    }

    [Fact]
    public void CardWithBadLuhnNeedsGroupingOrKnownPrefix()
    {
        // Grouped: always a candidate.
        Assert.Equal(PatternCertainty.ChecksumFailed, Single("1234 5678 9012 3456", RedactionPatternKind.Card).Certainty);
        Assert.Equal(PatternCertainty.ChecksumFailed, Single("1234 567890 12345", RedactionPatternKind.Card).Certainty);

        // Ungrouped: only with the start of a large network.
        foreach (var prefix in new[] { "4", "51", "55", "2221", "2720", "34", "37", "6011", "65", "644", "649", "36", "38", "39", "300", "305", "3528", "3589", "62" })
        {
            var number = prefix + new string('1', 15 - prefix.Length) + (prefix is "2221" or "65" ? "3" : "2");
            var match = Single(number, RedactionPatternKind.Card);
            Assert.True(match.Certainty == PatternCertainty.ChecksumFailed, number);
        }

        Assert.Empty(Scan("1234567890123456", RedactionPatternKind.Card));
        Assert.Empty(Scan("9111111111111112", RedactionPatternKind.Card));
        Assert.Empty(Scan("2220111111111112", RedactionPatternKind.Card));
        Assert.Empty(Scan("3527111111111112", RedactionPatternKind.Card));
    }

    [Fact]
    public void LuhnOkWithoutAnyPrefixRuleIsStillACard()
    {
        // 15 digits, Luhn right, no network prefix: the checksum alone makes it ChecksumOk.
        Assert.Equal(PatternCertainty.ChecksumOk, Single("123456789012347", RedactionPatternKind.Card).Certainty);
    }

    [Fact]
    public void TwelveRandomDigitsAreNoCard()
    {
        Assert.Empty(Scan("123456789018", RedactionPatternKind.Card));
        Assert.Empty(Scan("4111 1111 1111", RedactionPatternKind.Card));
    }

    [Fact]
    public void CardInsideALongerRunIsNoMatch()
    {
        Assert.Empty(Scan("41111111111111111111", RedactionPatternKind.Card));
        Assert.Empty(Scan("x4111111111111111", RedactionPatternKind.Card));
        Assert.Empty(Scan("4111111111111111x", RedactionPatternKind.Card));
        Assert.Empty(Scan("4111 1111 1111 1111x", RedactionPatternKind.Card));
        Assert.Empty(Scan("+4111111111111111", RedactionPatternKind.Card));
    }

    [Fact]
    public void CardFollowedByAnExpiryDateIsStillFound()
    {
        var text = "4111 1111 1111 1111 12/25";

        var match = Single(text, RedactionPatternKind.Card);

        Assert.Equal("4111 1111 1111 1111", Slice(text, match));
    }

    [Fact]
    public void CardAtTheStartAndEndOfTheText()
    {
        Assert.Equal(0, Single("4111111111111111", RedactionPatternKind.Card).Start);
        var text = "end 4111 1111 1111 1111";
        Assert.Equal(text.Length, Single(text, RedactionPatternKind.Card).End);
    }

    [Fact]
    public void IsbnThirteenIsNoCard()
    {
        Assert.Empty(Scan("9783161484100", RedactionPatternKind.Card));
        Assert.Empty(Scan("ISBN 978-3-16-148410-0", RedactionPatternKind.Card));
    }

    [Fact]
    public void CardDoesNotStartWithZero()
    {
        Assert.Empty(Scan("0111111111111117", RedactionPatternKind.Card));
    }

    // ---- Phone ----

    [Fact]
    public void PhoneForms()
    {
        var international = "+49 711 1234567";
        var slash = "0711/1234567";
        var bracket = "(0711) 123 45 67";

        foreach (var number in new[] { international, slash, bracket })
        {
            var text = "Tel. " + number + ", Fax";
            var match = Single(text, RedactionPatternKind.Phone);
            Assert.Equal(PatternCertainty.ShapeOnly, match.Certainty);
            Assert.Equal(number, Slice(text, match));
        }
    }

    [Fact]
    public void MorePhoneForms()
    {
        foreach (var number in new[]
        {
            "+4917112345678", "01711234567", "0171-1234567", "0171.1234567", "+49 (0)711 1234567", "0049 711 1234567",
            "+1 (555) 123-4567", "(711) 123 4567", "030 12345678", "+49" + Nbsp + "711" + Nbsp + "1234567",
        })
        {
            var text = "x " + number;
            var match = Single(text, RedactionPatternKind.Phone);
            Assert.Equal(number, Slice(text, match));
        }
    }

    [Fact]
    public void PhoneAtTheStartAndTheEndOfTheText()
    {
        Assert.Equal(0, Single("0711 1234567", RedactionPatternKind.Phone).Start);
        Assert.Equal(12, Single("0711 1234567", RedactionPatternKind.Phone).End);
    }

    [Fact]
    public void PhoneBracketsOutsideTheNumberAreNotPartOfIt()
    {
        var text = "(Tel 0711 1234567)";

        var match = Single(text, RedactionPatternKind.Phone);

        Assert.Equal("0711 1234567", Slice(text, match));
    }

    [Fact]
    public void DatesAreNoPhones()
    {
        foreach (var text in new[]
        {
            "01.02.2024", "01.02.24", "2024-02-01", "01/02/2024", "01/02/24", "31-12-2024", "1.2.2024",
            "01.02.2024 14:30", "01.02.2024 14.30", "2024-02-01 08:15:00", "Am 01.02.2024, 14:30 Uhr", "01/02/2024 14:30",
        })
        {
            Assert.True(Scan(text, RedactionPatternKind.Phone).Count == 0, text);
        }
    }

    [Fact]
    public void ADateInFrontOfAPhoneNumberDoesNotHideIt()
    {
        var text = "01.02.2024 0711 1234567";

        var match = Single(text, RedactionPatternKind.Phone);

        Assert.Equal("0711 1234567", Slice(text, match));
    }

    [Fact]
    public void ADateBehindAPhoneNumberDoesNotHideIt()
    {
        var text = "0711 1234567 01.02.2024";

        var match = Single(text, RedactionPatternKind.Phone);

        Assert.Equal("0711 1234567", Slice(text, match));
    }

    [Fact]
    public void AnInvalidDateIsStillAPhoneShape()
    {
        // Month 13 and day 32 are not dates.
        Assert.Single(Scan("01.13.2024", RedactionPatternKind.Phone));
        Assert.Single(Scan("00.32.2024", RedactionPatternKind.Phone));
    }

    [Fact]
    public void PureDigitRunWithoutPrefixIsNoPhone()
    {
        Assert.Empty(Scan("1234567890", RedactionPatternKind.Phone));
        Assert.Empty(Scan("7111234567", RedactionPatternKind.Phone));
        Assert.Empty(Scan("711 1234567", RedactionPatternKind.Phone));
    }

    [Fact]
    public void IsbnLikeIsNoPhone()
    {
        Assert.Empty(Scan("ISBN 0-306-40615-2", RedactionPatternKind.Phone));
        Assert.Empty(Scan("0 306 40615 2", RedactionPatternKind.Phone));
        Assert.Empty(Scan("ISBN 0-8044-2957-X", RedactionPatternKind.Phone));
        Assert.Empty(Scan("ISBN 978-3-16-148410-0", RedactionPatternKind.Phone));
    }

    [Fact]
    public void DecimalNumbersAreNoPhones()
    {
        Assert.Empty(Scan("Wert 0.123456789", RedactionPatternKind.Phone));
    }

    [Fact]
    public void PhoneInsideALongerDigitRunIsNoMatch()
    {
        Assert.Empty(Scan("07111234567890123456", RedactionPatternKind.Phone));
        Assert.Empty(Scan("0711 12345678901234", RedactionPatternKind.Phone));
        Assert.Empty(Scan("a0711 1234567", RedactionPatternKind.Phone));
        Assert.Empty(Scan("0711 1234567x", RedactionPatternKind.Phone));
        Assert.Empty(Scan("x+49 711 1234567", RedactionPatternKind.Phone));
    }

    [Fact]
    public void PhoneNumbersWrittenOneAfterTheOtherAreSeparated()
    {
        var text = "0711 1234567 0711 7654321";

        var matches = Scan(text, RedactionPatternKind.Phone);

        Assert.Equal(["0711 1234567", "0711 7654321"], matches.Select(m => Slice(text, m)));
    }

    [Fact]
    public void APhoneNumberAfterAnotherNumberIsFound()
    {
        var text = "Kunde 12 0711 1234567";

        var match = Single(text, RedactionPatternKind.Phone);

        Assert.Equal("0711 1234567", Slice(text, match));
    }

    [Fact]
    public void ShortAndLongDigitCountsAreRefused()
    {
        Assert.Empty(Scan("0711 12", RedactionPatternKind.Phone));
        Assert.Single(Scan("0711 123", RedactionPatternKind.Phone));
        Assert.Single(Scan("0711 123 4", RedactionPatternKind.Phone));
        Assert.Single(Scan("+49 123 456 789 912 3", RedactionPatternKind.Phone));
        Assert.Empty(Scan("+49 123 456 789 912 34", RedactionPatternKind.Phone));
    }

    // ---- E-mail ----

    [Fact]
    public void EmailForms()
    {
        foreach (var address in new[]
        {
            "john@example.com", "john.doe@example.com", "john+tag@example.co.uk", "j_o-h%n@sub.example.org", "o'brien@example.ie",
            "müller@bücher.de", "josé+tag@exämple.com", "用户@例子.广告", "a@b.cd", "x@1.example.com", "john@example-shop.de",
        })
        {
            var text = "Mail: " + address + ".";
            var match = Single(text, RedactionPatternKind.Email);
            Assert.Equal(PatternCertainty.ShapeOnly, match.Certainty);
            Assert.Equal(address, Slice(text, match));
        }
    }

    [Fact]
    public void EmailWithoutADotInTheDomainIsNoMatch()
    {
        Assert.Empty(Scan("john@localhost", RedactionPatternKind.Email));
        Assert.Empty(Scan("john@example", RedactionPatternKind.Email));
        Assert.Empty(Scan("john@example.", RedactionPatternKind.Email));
        Assert.Empty(Scan("@example.com", RedactionPatternKind.Email));
        Assert.Empty(Scan("john@.com", RedactionPatternKind.Email));
        Assert.Empty(Scan("john@example.c", RedactionPatternKind.Email));
        Assert.Empty(Scan("john@192.168.1.1", RedactionPatternKind.Email));
    }

    [Fact]
    public void EmailEdgesAreDelimitedByCharactersOfNoAddress()
    {
        var text = "(<john@example.com>), john@example.com; \"a.b@c.de\"";

        var matches = Scan(text, RedactionPatternKind.Email);

        Assert.Equal(["john@example.com", "john@example.com", "a.b@c.de"], matches.Select(m => Slice(text, m)));
    }

    [Fact]
    public void EmailAtTheStartAndTheEndOfTheText()
    {
        var match = Single("john@example.com", RedactionPatternKind.Email);

        Assert.Equal(0, match.Start);
        Assert.Equal(16, match.End);
    }

    [Fact]
    public void EmailTrailingDotAndLeadingDotAreNotPartOfIt()
    {
        var text = ".john@example.com.";

        var match = Single(text, RedactionPatternKind.Email);

        Assert.Equal("john@example.com", Slice(text, match));
    }

    [Fact]
    public void EmailIsAtMost254CharactersLong()
    {
        // user@ + domain: 254 characters in total.
        static string Domain(int length)
        {
            var labels = new List<string>();
            var remaining = length;
            while (remaining > 63 + 1 + 2)
            {
                labels.Add(new string('a', 63));
                remaining -= 64;
            }

            labels.Add(new string('b', remaining));
            return string.Join('.', labels);
        }

        var ok = "user@" + Domain(249);
        var tooLong = "user@" + Domain(250);

        Assert.Equal(254, ok.Length);
        Assert.Equal(ok, Slice(ok, Single(ok, RedactionPatternKind.Email)));
        Assert.Empty(Scan(tooLong, RedactionPatternKind.Email));
    }

    [Fact]
    public void EmailLocalPartIsAtMost64Characters()
    {
        Assert.Single(Scan(new string('a', 64) + "@example.com", RedactionPatternKind.Email));
        Assert.Empty(Scan(new string('a', 65) + "@example.com", RedactionPatternKind.Email));
    }

    [Fact]
    public void EmailWithAnEmojiNextToTheLocalPartTakesOnlyTheAddress()
    {
        var text = "\U0001F600john@example.com";

        var match = Single(text, RedactionPatternKind.Email);

        Assert.Equal("john@example.com", Slice(text, match));
    }

    [Fact]
    public void EmailWithADecomposedLetterIsOneMatch()
    {
        var text = "mu\u0308ller@example.com";

        var match = Single(text, RedactionPatternKind.Email);

        Assert.Equal(text, Slice(text, match));
    }

    // ---- Overlaps and mixes ----

    [Fact]
    public void IbanOrCardWinsOverPhone()
    {
        // The tail "0417 1643 00" of this Dutch IBAN is a telephone shape on its own.
        var iban = "NL91 ABNA 0417 1643 00";
        var both = Scan("Konto " + iban, RedactionPatternKind.Iban, RedactionPatternKind.Phone);

        var single = Assert.Single(both);
        Assert.Equal(RedactionPatternKind.Iban, single.Kind);
        Assert.Equal(6, single.Start);

        // Without the IBAN scanner the phone shape is there.
        Assert.Single(Scan("Konto " + iban, RedactionPatternKind.Phone));
    }

    [Fact]
    public void ACardWithAGoodChecksumWinsOverAPhoneShapeInsideIt()
    {
        // "(1) 4222222222222" is a bracketed telephone shape (14 digits); the 13 digits behind the bracket are a Visa test number.
        var text = "(1) 4222222222222";

        var phoneOnly = Scan(text, RedactionPatternKind.Phone);
        var both = Scan(text, RedactionPatternKind.Phone, RedactionPatternKind.Card);

        Assert.Single(phoneOnly);
        var match = Assert.Single(both);
        Assert.Equal(RedactionPatternKind.Card, match.Kind);
        Assert.Equal("4222222222222", Slice(text, match));
    }

    [Fact]
    public void ACardWithAWrongChecksumDoesNotHideAPhoneNumber()
    {
        // 15 digits in the 4-6-5 form, Luhn wrong: a failed card shape that is also a telephone number.
        var text = "0711 123456 78901";

        var matches = Scan(text, RedactionPatternKind.Phone, RedactionPatternKind.Card);

        var match = Assert.Single(matches);
        Assert.Equal(RedactionPatternKind.Phone, match.Kind);
    }

    [Fact]
    public void EmailWinsOverTheDigitsInsideIt()
    {
        var text = "0711123456@example.com";

        var match = Single(text);

        Assert.Equal(RedactionPatternKind.Email, match.Kind);
        Assert.Equal(text, Slice(text, match));
    }

    [Fact]
    public void MixedTextGivesSortedMatchesOfEveryKind()
    {
        var text = "Max <max@example.com>, Tel +49 711 1234567, IBAN DE89 3704 0044 0532 0130 00, Karte 4111 1111 1111 1111, am 01.02.2024.";

        var matches = Scan(text);

        Assert.Equal(
            [RedactionPatternKind.Email, RedactionPatternKind.Phone, RedactionPatternKind.Iban, RedactionPatternKind.Card],
            matches.Select(m => m.Kind));
        Assert.Equal(["max@example.com", "+49 711 1234567", "DE89 3704 0044 0532 0130 00", "4111 1111 1111 1111"], matches.Select(m => Slice(text, m)));
    }

    [Fact]
    public void OnlyTheAskedKindsAreScanned()
    {
        var text = "max@example.com 4111111111111111";

        Assert.Single(Scan(text, RedactionPatternKind.Email));
        Assert.Single(Scan(text, RedactionPatternKind.Card));
        Assert.Empty(Scan(text, RedactionPatternKind.Iban, RedactionPatternKind.Phone));
        Assert.Empty(RedactionPatterns.Scan(text, new HashSet<RedactionPatternKind>()));
        Assert.Empty(Scan(string.Empty));
    }

    [Fact]
    public void PatternsAreNotFoundAcrossALineBreak()
    {
        Assert.Empty(Scan("DE89 3704 0044\n0532 0130 00", RedactionPatternKind.Iban));
        Assert.Empty(Scan("4111 1111\r\n1111 1111", RedactionPatternKind.Card));
        Assert.Empty(Scan("0711\n1234567", RedactionPatternKind.Phone));
    }

    // ---- Fix round 1 ----

    [Theory]
    [InlineData("PL61 1090 1014 0000 0712 1981 2874", " Zahlung")]
    [InlineData("PL61 1090 1014 0000 0712 1981 2874", " Zahlungseingang vom Kunden")]
    [InlineData("ES91 2100 0418 4502 0005 1332", " Kontoinhaber Max")]
    [InlineData("AT61 1904 3002 3457 3201", " Verwendungszweck Miete")]
    [InlineData("AD12 0001 2030 2003 5910 0100", " Müller")]
    [InlineData("AT61 1904 3002 3457 3201", " Überweisung")]
    [InlineData("AD12 0001 2030 2003 5910 0100", " 12 Euro")]
    [InlineData("AD12 0001 2030 2003 5910 0100", " 5")]
    [InlineData("PL61109010140000071219812874", " Zahlung")]
    [InlineData("ES9121000418450200051332", " Kontoinhaber")]
    [InlineData("AT611904300234573201", " Verwendungszweck Miete")]
    public void ValidIbanFollowedByAWordIsFoundWithoutTheWord(string iban, string tail)
    {
        foreach (var separator in new[] { " ", Nbsp })
        {
            var written = iban.Replace(" ", separator, StringComparison.Ordinal);
            var text = "Konto: " + written + tail + ".";

            var match = Single(text);

            Assert.Equal(RedactionPatternKind.Iban, match.Kind);
            Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
            Assert.Equal(written, Slice(text, match));
        }
    }

    [Fact]
    public void IbanFollowedByANonAsciiLetterWithoutASeparatorIsNoMatch()
    {
        Assert.Empty(Scan("AD12 0001 2030 2003 5910 0100é", RedactionPatternKind.Iban));
        Assert.Empty(Scan("AT611904300234573201ü", RedactionPatternKind.Iban));
    }

    [Fact]
    public void IbanWithAnAsciiLetterAttachedIsTheWrongLengthNotACut()
    {
        var text = "AD12 0001 2030 2003 5910 0100X";

        var match = Single(text, RedactionPatternKind.Iban);

        Assert.Equal(PatternCertainty.ChecksumFailed, match.Certainty);
        Assert.Equal(text, Slice(text, match));
    }

    [Fact]
    public void ABareAlphanumericRunLongerThanThirtyFourStaysNoCandidateEvenWithAWordBehindIt()
    {
        Assert.Empty(Scan("DE89" + new string('1', 40) + " Zahlung", RedactionPatternKind.Iban));
        Assert.Empty(Scan("PL61109010140000071219812874" + new string('1', 10), RedactionPatternKind.Iban));
    }

    [Fact]
    public void RegistryHasExactlyTheEightyNineCountriesAndNotThePartialOnes()
    {
        var expected = "AD AE AL AT AZ BA BE BG BH BI BR BY CH CR CY CZ DE DJ DK DO EE EG ES FI FK FO FR GB GE GI GL GR GT HN HR HU IE IL IQ IS IT JO KW KZ LB LC LI LT LU LV LY MC MD ME MK MN MR MT MU NI NL NO OM PK PL PS PT QA RO RS RU SA SC SD SE SI SK SM SO ST SV TL TN TR UA VA VG XK YE".Split(' ');

        Assert.Equal(89, expected.Length);
        Assert.Equal(expected, IbanRegistry.Examples.Select(e => e.Country).OrderBy(c => c, StringComparer.Ordinal));
        foreach (var partial in new[] { "AO", "BF", "BJ", "CF", "CG", "CI", "CM", "CV", "DZ", "GA", "GQ", "GW", "IR", "MA", "MG", "ML", "MZ", "NE", "SN", "TD", "TG", "KM" })
        {
            Assert.False(IbanRegistry.TryGetLength(partial, out _), partial);
        }
    }

    [Fact]
    public void CardAfterAFourDigitNumberIsFoundNotTheShiftedShape()
    {
        foreach (var prefix in new[] { "Zahlung 2024 ", "12.03.2024 ", "PIN 1234 ", "Nr 5678 " })
        {
            var text = prefix + "4111 1111 1111 1111";

            var match = Single(text, RedactionPatternKind.Card);

            Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
            Assert.Equal("4111 1111 1111 1111", Slice(text, match));
        }
    }

    [Fact]
    public void ANineteenDigitCardBehindAFourDigitNumberIsFoundWhole()
    {
        var text = "PIN 1234 4111 1111 1111 1111 110";

        var match = Single(text, RedactionPatternKind.Card);

        Assert.Equal(PatternCertainty.ChecksumOk, match.Certainty);
        Assert.Equal("4111 1111 1111 1111 110", Slice(text, match));
    }

    [Fact]
    public void ANineteenDigitCardStaysWhole()
    {
        var text = "4111 1111 1111 1111 110";

        Assert.Equal(text, Slice(text, Single(text, RedactionPatternKind.Card)));
    }

    [Fact]
    public void TwoCardsBackToBackAreTwoMatches()
    {
        var text = "4111 1111 1111 1111 5555 5555 5555 4444";

        var matches = Scan(text, RedactionPatternKind.Card);

        Assert.Equal(["4111 1111 1111 1111", "5555 5555 5555 4444"], matches.Select(m => Slice(text, m)));
        Assert.All(matches, m => Assert.Equal(PatternCertainty.ChecksumOk, m.Certainty));
    }

    [Fact]
    public void AFailedCardShapeIsKeptWhenNoShiftedShapeHasAGoodChecksum()
    {
        var text = "1234 5678 9012 3456 7890";

        var match = Assert.Single(Scan(text, RedactionPatternKind.Card));

        Assert.Equal(PatternCertainty.ChecksumFailed, match.Certainty);
        Assert.Equal("1234 5678 9012 3456", Slice(text, match));
    }

    [Fact]
    public void FifteenDigitCardInFourFourFourThreeIsFoundOnlyWithAGoodChecksum()
    {
        var good = Single("Karte 4929 1234 5678 909.", RedactionPatternKind.Card);

        Assert.Equal(PatternCertainty.ChecksumOk, good.Certainty);
        Assert.Empty(Scan("4929 1234 5678 900", RedactionPatternKind.Card));
    }

    [Theory]
    [InlineData("Tel. 0711 / 123 45 67", "0711 / 123 45 67")]
    [InlineData("0711 / 1234567", "0711 / 1234567")]
    [InlineData("Tel. 0711 - 1234567", "0711 - 1234567")]
    [InlineData("Tel. 0711 /1234567", "0711 /1234567")]
    [InlineData("Tel. 0711/ 1234567", "0711/ 1234567")]
    [InlineData("Tel: +49 711 / 1234567", "+49 711 / 1234567")]
    [InlineData("Tel: +49 / 711 1234567", "+49 / 711 1234567")]
    public void PhoneWithSpacesAroundASlashOrHyphenIsOneMatch(string text, string expected)
    {
        var match = Single(text, RedactionPatternKind.Phone);

        Assert.Equal(expected, Slice(text, match));
        Assert.Equal(PatternCertainty.ShapeOnly, match.Certainty);
    }

    [Theory]
    [InlineData("Seiten 12 - 18")]
    [InlineData("Kapitel 3 / 4")]
    [InlineData("Seiten 0 - 18")]
    [InlineData("von 01.02.2024 - 31.12.2024")]
    [InlineData("2024 - 2025")]
    [InlineData("Version 1.2.3 / 4.5.6")]
    [InlineData("ISBN 0 - 306 - 40615 - 2")]
    public void RangesAndNumbersWithSpacedDashesAreNoPhones(string text)
    {
        Assert.Empty(Scan(text, RedactionPatternKind.Phone));
    }

    [Fact]
    public void ASpacedDashIsOnlyAcceptedOnceAndEarlyInTheNumber()
    {
        // Rule: a slash or hyphen with a space on one or both sides joins groups only once per number and only behind the first or the
        // second group. A spaced dash behind the third group ends the number.
        var behindThree = "0711 123 45 - 67 89 01";
        Assert.Equal("0711 123 45", Slice(behindThree, Single(behindThree, RedactionPatternKind.Phone)));
        Assert.Empty(Scan("0711 - 12 - 3456789", RedactionPatternKind.Phone));
    }

    [Fact]
    public void NeighboursThatArePairsOfSurrogatesAreNoLettersOrDigits()
    {
        var emoji = "\U0001F600";
        var phone = Single(emoji + "+49 711 1234567" + emoji, RedactionPatternKind.Phone);
        var iban = Single(emoji + "DE89 3704 0044 0532 0130 00" + emoji, RedactionPatternKind.Iban);
        var card = Single(emoji + "4111 1111 1111 1111" + emoji, RedactionPatternKind.Card);

        Assert.Equal(PatternCertainty.ChecksumOk, iban.Certainty);
        Assert.Equal(PatternCertainty.ChecksumOk, card.Certainty);
        Assert.Equal((2, 17), (phone.Start, phone.End));
        Assert.Equal((2, 29), (iban.Start, iban.End));
        Assert.Equal((2, 21), (card.Start, card.End));
    }

    [Fact]
    public void ALetterThatIsAPairOfSurrogatesIsPartOfALongerRun()
    {
        // U+1D400 MATHEMATICAL BOLD CAPITAL A is a letter outside the BMP.
        var letter = "\U0001D400";
        Assert.Empty(Scan(letter + "+49 711 1234567", RedactionPatternKind.Phone));
        Assert.Empty(Scan("0711 1234567" + letter, RedactionPatternKind.Phone));
        Assert.Empty(Scan(letter + "DE89370400440532013000", RedactionPatternKind.Iban));
        Assert.Empty(Scan("DE89370400440532013000" + letter, RedactionPatternKind.Iban));
        Assert.Empty(Scan(letter + "4111111111111111", RedactionPatternKind.Card));
        Assert.Empty(Scan("4111111111111111" + letter, RedactionPatternKind.Card));
    }

    // ---- Hostile input ----

    [Fact]
    public void ScannerIsLinearOnAHostileInput()
    {
        const int Size = 1_000_000;
        // The second value is the exact number of matches (-1: only the general rules are checked: sorted, in bounds, no overlap).
        var inputs = new List<(string Text, int Expected)>
        {
            (new string('1', Size), 0),
            (string.Concat(Enumerable.Repeat("1 ", Size / 2)), 0),
            (string.Concat(Enumerable.Repeat("1234 ", Size / 5)), -1),
            (string.Concat(Enumerable.Repeat("1-", Size / 2)), 0),
            (string.Concat(Enumerable.Repeat("(0)", Size / 3)), 0),
            (string.Concat(Enumerable.Repeat("+4", Size / 2)), 0),
            (string.Concat(Enumerable.Repeat("DE11 ", Size / 5)), 1), // only the last at most eight blocks fit the size of an IBAN: one ChecksumFailed match
            (string.Concat(Enumerable.Repeat("AA11", Size / 4)), 0),
            (string.Concat(Enumerable.Repeat("@a", Size / 2)), 0),
            (string.Concat(Enumerable.Repeat("a@", Size / 2)), 0),
            (new string('a', Size) + "@" + new string('b', Size / 2), 0),
            (new string('a', Size / 2) + "@" + new string('b', Size / 2) + "." + new string('c', 10), 0),
            (string.Concat(Enumerable.Repeat("a.b@c.", Size / 6)), 0),
            (string.Concat(Enumerable.Repeat("0 ", Size / 2)), 0),
            (string.Concat(Enumerable.Repeat("01.02.", Size / 6)), 0),
            (string.Concat(Enumerable.Repeat("4111 1111 1111 1111 ", Size / 20)), Size / 20),
            (string.Concat(Enumerable.Repeat("x@y.de ", Size / 7)), Size / 7),
            (string.Concat(Enumerable.Repeat("DE89 3704 0044 0532 0130 00 ", Size / 28)), Size / 28),
            (string.Concat(Enumerable.Repeat("0711 / 1234567, ", Size / 16)), Size / 16),
            (new string('\u00A0', Size), 0),
        };

        var watch = Stopwatch.StartNew();
        foreach (var (input, expected) in inputs)
        {
            var matches = Scan(input);
            if (expected >= 0)
            {
                Assert.True(expected == matches.Count, $"{input[..Math.Min(24, input.Length)]}...: expected {expected}, found {matches.Count}");
            }

            var previousEnd = 0;
            foreach (var match in matches)
            {
                Assert.True(match.Start >= previousEnd && match.End > match.Start && match.End <= input.Length);
                previousEnd = match.End;
            }
        }

        watch.Stop();
        // About one second alone in Debug; the budget leaves room for a loaded machine. A quadratic scanner would need minutes here.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ScannerNeverThrowsOnOddText()
    {
        foreach (var text in new[] { "\uD83D", "\uDE00", "a@\uD83D", "\uD83D@b.com", "((((", "))))", "+", "+(", "(", "0", "@", "DE", "DE8", "DE89", "+(0)", "\0\0@\0.\0\0" })
        {
            Assert.NotNull(Scan(text));
        }
    }
}
