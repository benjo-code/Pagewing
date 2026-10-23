using System.Globalization;
using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One entry of the list of matches in the "Search" panel: the page mark and the text around the match. UI thread only.
/// </summary>
public sealed class SearchHitViewModel : ViewModelBase
{
    // The format of the automation name, parsed once per language.
    private static (CultureInfo Culture, CompositeFormat Format)? _nameFormat;

    private bool _isCurrent;

    /// <summary>Creates the entry of <paramref name="hit"/>.</summary>
    public SearchHitViewModel(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        Hit = hit;

        // Line breaks around the match would make a row taller than the others.
        Before = OneLine(hit.Snippet.Before);
        Match = OneLine(hit.Snippet.Match);
        After = OneLine(hit.Snippet.After);
    }

    /// <summary>Gets the match.</summary>
    public SearchHit Hit { get; }

    /// <summary>Gets the 1-based page number as the page mark shows it ("3").</summary>
    public string PageText => (Hit.PageIndex + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the text before the match, in one line.</summary>
    public string Before { get; }

    /// <summary>Gets the matched text, in one line; the list shows it on <c>search.hit</c>.</summary>
    public string Match { get; }

    /// <summary>Gets the text after the match, in one line.</summary>
    public string After { get; }

    /// <summary>Gets the name for assistive technology: "Page 3: text around the match".</summary>
    public string AutomationName
    {
        get
        {
            var culture = Strings.Culture;
            if (_nameFormat is not { } cached || !ReferenceEquals(cached.Culture, culture))
            {
                cached = (culture, CompositeFormat.Parse(Strings.SearchHitName));
                _nameFormat = cached;
            }

            return string.Format(culture, cached.Format, PageText, Before + Match + After);
        }
    }

    /// <summary>
    /// Gets whether this is the current match: the list gives it an accent.subtle surface and a 2 px accent.default
    /// edge on the left (design system 7.4, never by colour alone).
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
