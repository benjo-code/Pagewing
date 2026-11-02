namespace Pagewing.Core.Redaction;

/// <summary>An example IBAN of a country in the registry.</summary>
/// <param name="Country">The two-letter country code.</param>
/// <param name="Iban">An example IBAN of that country (electronic form, no group separators) that passes the length check and ISO 7064 mod 97-10.</param>
public readonly record struct IbanExample(string Country, string Iban);

/// <summary>
/// The IBAN length of each country (ISO 13616). Facts only: the country code, the total length of an IBAN and one example.
/// </summary>
/// <remarks>
/// Source and state, 2026-11-02: the table is the SWIFT IBAN Registry (ISO 13616) as the author knows it and as the public list at
/// https://www.iban.com/structure shows it; the reviewer of this file also compared the 89 countries and lengths with the
/// Wikipedia article "International Bank Account Number" (no length differs, no country of the registry is missing). The primary
/// register at swift.com was not available; it still has to be compared by a person before each release.
/// The countries with a "partial" or "experimental" IBAN are not in the registry and are left out on purpose: AO, BF, BJ, CF, CG, CI,
/// CM, CV, DZ, GA, GQ, GW, IR, MA, MG, ML, MZ, NE, SN, TD, TG and KM. An IBAN of an unknown country is no candidate for the scanner.
/// The lengths are typed in the table, not taken from the examples; <c>EveryRegistryExamplePassesLengthAndMod97</c> compares the two and
/// checks mod 97 for every example, which catches typos in the transcription.
/// </remarks>
public static class IbanRegistry
{
    private static readonly Entry[] Table =
    [
        new("AD", 24, "AD1200012030200359100100"),
        new("AE", 23, "AE070331234567890123456"),
        new("AL", 28, "AL47212110090000000235698741"),
        new("AT", 20, "AT611904300234573201"),
        new("AZ", 28, "AZ21NABZ00000000137010001944"),
        new("BA", 20, "BA391290079401028494"),
        new("BE", 16, "BE68539007547034"),
        new("BG", 22, "BG80BNBG96611020345678"),
        new("BH", 22, "BH67BMAG00001299123456"),
        new("BI", 27, "BI4210000100010000332045181"),
        new("BR", 29, "BR1800360305000010009795493C1"),
        new("BY", 28, "BY13NBRB3600900000002Z00AB00"),
        new("CH", 21, "CH9300762011623852957"),
        new("CR", 22, "CR05015202001026284066"),
        new("CY", 28, "CY17002001280000001200527600"),
        new("CZ", 24, "CZ6508000000192000145399"),
        new("DE", 22, "DE89370400440532013000"),
        new("DJ", 27, "DJ2100010000000154000100186"),
        new("DK", 18, "DK5000400440116243"),
        new("DO", 28, "DO28BAGR00000001212453611324"),
        new("EE", 20, "EE382200221020145685"),
        new("EG", 29, "EG380019000500000000263180002"),
        new("ES", 24, "ES9121000418450200051332"),
        new("FI", 18, "FI2112345600000785"),
        new("FK", 18, "FK88SC123456789012"),
        new("FO", 18, "FO6264600001631634"),
        new("FR", 27, "FR1420041010050500013M02606"),
        new("GB", 22, "GB29NWBK60161331926819"),
        new("GE", 22, "GE29NB0000000101904917"),
        new("GI", 23, "GI75NWBK000000007099453"),
        new("GL", 18, "GL8964710001000206"),
        new("GR", 27, "GR1601101250000000012300695"),
        new("GT", 28, "GT82TRAJ01020000001210029690"),
        new("HN", 28, "HN88CABF00000000000250005469"),
        new("HR", 21, "HR1210010051863000160"),
        new("HU", 28, "HU42117730161111101800000000"),
        new("IE", 22, "IE29AIBK93115212345678"),
        new("IL", 23, "IL620108000000099999999"),
        new("IQ", 23, "IQ98NBIQ850123456789012"),
        new("IS", 26, "IS140159260076545510730339"),
        new("IT", 27, "IT60X0542811101000000123456"),
        new("JO", 30, "JO94CBJO0010000000000131000302"),
        new("KW", 30, "KW81CBKU0000000000001234560101"),
        new("KZ", 20, "KZ86125KZT5004100100"),
        new("LB", 28, "LB62099900000001001901229114"),
        new("LC", 32, "LC55HEMM000100010012001200023015"),
        new("LI", 21, "LI21088100002324013AA"),
        new("LT", 20, "LT121000011101001000"),
        new("LU", 20, "LU280019400644750000"),
        new("LV", 21, "LV80BANK0000435195001"),
        new("LY", 25, "LY83002048000020100120361"),
        new("MC", 27, "MC5811222000010123456789030"),
        new("MD", 24, "MD24AG000225100013104168"),
        new("ME", 22, "ME25505000012345678951"),
        new("MK", 19, "MK07250120000058984"),
        new("MN", 20, "MN121234123456789123"),
        new("MR", 27, "MR1300020001010000123456753"),
        new("MT", 31, "MT84MALT011000012345MTLCAST001S"),
        new("MU", 30, "MU17BOMM0101101030300200000MUR"),
        new("NI", 28, "NI45BAPR00000013000003558124"),
        new("NL", 18, "NL91ABNA0417164300"),
        new("NO", 15, "NO9386011117947"),
        new("OM", 23, "OM810180000001299123456"),
        new("PK", 24, "PK36SCBL0000001123456702"),
        new("PL", 28, "PL61109010140000071219812874"),
        new("PS", 29, "PS92PALS000000000400123456702"),
        new("PT", 25, "PT50000201231234567890154"),
        new("QA", 29, "QA58DOHB00001234567890ABCDEFG"),
        new("RO", 24, "RO49AAAA1B31007593840000"),
        new("RS", 22, "RS35260005601001611379"),
        new("RU", 33, "RU0304452522540817810538091310419"),
        new("SA", 24, "SA0380000000608010167519"),
        new("SC", 31, "SC18SSCB11010000000000001497USD"),
        new("SD", 18, "SD2129010501234001"),
        new("SE", 24, "SE4550000000058398257466"),
        new("SI", 19, "SI56263300012039086"),
        new("SK", 24, "SK3112000000198742637541"),
        new("SM", 27, "SM86U0322509800000000270100"),
        new("SO", 23, "SO211000001001000100141"),
        new("ST", 25, "ST68000100010051845310112"),
        new("SV", 28, "SV62CENR00000000000000700025"),
        new("TL", 23, "TL380080012345678910157"),
        new("TN", 24, "TN5910006035183598478831"),
        new("TR", 26, "TR330006100519786457841326"),
        new("UA", 29, "UA213223130000026007233566001"),
        new("VA", 22, "VA59001123000012345678"),
        new("VG", 24, "VG96VPVG0000012345678901"),
        new("XK", 20, "XK051212012345678906"),
        new("YE", 30, "YE15CBYE0001018861234567891234"),
    ];

    // Country code (two letters, A-Z) -> total IBAN length; 0 = not in the registry. The lengths are typed in the table, not derived
    // from the examples, so that the self-test can compare the two.
    private static readonly byte[] Lengths = BuildLengths();

    private static readonly IbanExample[] ExampleList = BuildExamples();

    /// <summary>One example IBAN per country of the registry; the length of an example is the length the registry names for that country.</summary>
    public static IReadOnlyList<IbanExample> Examples => ExampleList;

    /// <summary>Looks up the IBAN length of a country.</summary>
    /// <param name="country">The two-letter country code (any case).</param>
    /// <param name="length">The total length of an IBAN of that country.</param>
    /// <returns><see langword="true"/> when the country is in the registry.</returns>
    public static bool TryGetLength(string country, out int length)
    {
        length = 0;
        if (country is not { Length: 2 })
        {
            return false;
        }

        return TryGetLength(country[0], country[1], out length);
    }

    internal static bool TryGetLength(char first, char second, out int length)
    {
        length = 0;
        var a = (first | 0x20) - 'a';
        var b = (second | 0x20) - 'a';
        if ((uint)a > 'z' - 'a' || (uint)b > 'z' - 'a' || !IsAsciiLetter(first) || !IsAsciiLetter(second))
        {
            return false;
        }

        length = Lengths[(a * 26) + b];
        return length != 0;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static byte[] BuildLengths()
    {
        var lengths = new byte[26 * 26];
        foreach (var example in Table)
        {
            lengths[((example.Country[0] - 'A') * 26) + (example.Country[1] - 'A')] = (byte)example.Length;
        }

        return lengths;
    }

    private static IbanExample[] BuildExamples()
    {
        var examples = new IbanExample[Table.Length];
        for (var i = 0; i < Table.Length; i++)
        {
            examples[i] = new IbanExample(Table[i].Country, Table[i].Iban);
        }

        return examples;
    }

    private readonly record struct Entry(string Country, int Length, string Iban);
}
