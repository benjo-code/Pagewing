using Pagewing.Ocr;

namespace Pagewing.Core.Ocr;

/// <summary>The selection of recognition languages: one or more names (<c>[a-z_]+</c>) joined by <c>+</c>, in the order of their priority.</summary>
public static class OcrLanguages
{
    /// <summary>The most languages a selection can have; more is nonsense and is dropped.</summary>
    public const int MaxLanguages = 8;

    /// <summary>More than this many languages make a run slower and less accurate; the card says so.</summary>
    public const int SlowFrom = 3;

    // The two-letter codes of the interface language and the names of their models, for the languages people commonly have installed.
    private static readonly Dictionary<string, string> ModelOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "deu",
        ["en"] = "eng",
        ["fr"] = "fra",
        ["es"] = "spa",
        ["it"] = "ita",
        ["nl"] = "nld",
        ["pt"] = "por",
        ["pl"] = "pol",
        ["cs"] = "ces",
        ["sk"] = "slk",
        ["sv"] = "swe",
        ["da"] = "dan",
        ["fi"] = "fin",
        ["hu"] = "hun",
        ["ro"] = "ron",
        ["tr"] = "tur",
        ["ru"] = "rus",
        ["uk"] = "ukr",
        ["el"] = "ell",
        ["ja"] = "jpn",
        ["ko"] = "kor",
        ["ar"] = "ara",
    };

    /// <summary>
    /// Normalizes a selection: parts trimmed, repeated names dropped (the first stays), joined with <c>+</c>. A selection with an empty or
    /// invalid name, or with more than <see cref="MaxLanguages"/> names, gives <see langword="null"/>.
    /// </summary>
    public static string? Normalize(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return null;
        }

        var names = new List<string>();
        foreach (var part in selection.Split('+'))
        {
            var name = part.Trim();
            if (!OcrEngine.IsLanguageName(name))
            {
                return null;
            }

            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names.Count > MaxLanguages ? null : string.Join('+', names);
    }

    /// <summary>The names of a normalized selection.</summary>
    public static IReadOnlyList<string> Split(string normalized) => normalized.Split('+');

    /// <summary>
    /// The selection a run starts with: the model of the interface language and English, at most two, as far as they are installed (the
    /// first installed language when neither is). Empty when nothing is installed.
    /// </summary>
    /// <param name="uiLanguage">The two-letter code of the interface language (<c>de</c>).</param>
    /// <param name="installed">The names of the installed models.</param>
    public static string DefaultFor(string uiLanguage, IReadOnlyList<string> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var chosen = new List<string>();
        var code = (uiLanguage ?? string.Empty).Split('-', '_')[0];
        if (ModelOf.TryGetValue(code, out var model) && model != "eng" && installed.Contains(model, StringComparer.Ordinal))
        {
            chosen.Add(model);
        }

        if (installed.Contains("eng", StringComparer.Ordinal))
        {
            chosen.Add("eng");
        }

        if (chosen.Count == 0 && installed.Count > 0)
        {
            chosen.Add(installed[0]);
        }

        return string.Join('+', chosen);
    }

    /// <summary>Tells whether a selection has more languages than <see cref="SlowFrom"/>.</summary>
    public static bool IsSlow(string normalized) => Split(normalized).Count > SlowFrom;
}
