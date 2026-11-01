using System.Globalization;
using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One hit of "find and redact" in the list of the panel "Redact": the page, where the hit was found (page text, comment, form field),
/// whether the text is invisible, the text around it, and the check mark that says whether "Mark checked" takes it. A hit that cannot
/// become a mark says why and cannot be checked. UI thread only.
/// </summary>
public sealed class RedactionHitViewModel : ViewModelBase
{
    private bool _isChecked;

    internal RedactionHitViewModel(RedactionHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        Hit = hit;
        CanCheck = hit.Markable;
        _isChecked = hit.Markable;

        // Line breaks around the hit would make a row taller than the others.
        Before = OneLine(hit.Snippet.Before);
        Match = OneLine(hit.Snippet.Match.Length > 0 ? hit.Snippet.Match : hit.Text);
        After = OneLine(hit.Snippet.After);
    }

    /// <summary>Raised when <see cref="IsChecked"/> changed, with the new value (the panel counts the checked hits).</summary>
    internal event Action<RedactionHitViewModel>? CheckedChanged;

    /// <summary>Gets the hit.</summary>
    public RedactionHit Hit { get; }

    /// <summary>Gets whether the hit can be checked, that is, can become a mark.</summary>
    public bool CanCheck { get; }

    /// <summary>Gets or sets whether "Mark checked" takes the hit. A hit that cannot become a mark is never checked.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value && CanCheck))
            {
                OnPropertyChanged(nameof(AutomationName));
                CheckedChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Gets the 1-based page as the page mark of the row shows it ("3").</summary>
    public string PageText => (Hit.Page + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the text before the match, in one line.</summary>
    public string Before { get; }

    /// <summary>Gets the matched text as the page has it, in one line.</summary>
    public string Match { get; }

    /// <summary>Gets the text after the match, in one line.</summary>
    public string After { get; }

    /// <summary>Gets where the hit was found: "Page text", "Comment" or "Form field" (never by colour alone: it is a label).</summary>
    public string SourceLabel => Hit.Source switch
    {
        RedactionHitSource.Annotation => Strings.RedactHitSourceComment,
        RedactionHitSource.FormField => Strings.RedactHitSourceField,
        _ => Strings.RedactHitSourceText,
    };

    /// <summary>Gets what holds the hit besides the page (the kind of the annotation, the name of the form field), or <see langword="null"/>.</summary>
    public string? DetailLabel => Hit.Source == RedactionHitSource.PageText ? null : Hit.Detail;

    /// <summary>Gets whether the matched page text shows nothing (the text layer of a scan, render mode "invisible").</summary>
    public bool IsInvisible => Hit.Invisible;

    /// <summary>
    /// Gets whether the text of the hit is too short for the check of the raw bytes of the result and is not page text: the hit is in an
    /// annotation or a form value, and nothing but the extracted text covers it, which for these two sources does not read the raw file.
    /// </summary>
    public bool HasShortNote => Hit.Source != RedactionHitSource.PageText && Hit.Text.Length < RedactionLimits.MinRawCheckLength;

    /// <summary>Gets why the hit cannot become a mark (in the language of the interface), or <see langword="null"/> for a hit that can.</summary>
    public string? NotMarkableReason
    {
        get
        {
            if (Hit.Markable)
            {
                return null;
            }

            // The same order as the decision of the search (RedactionSearch.Make).
            if (Hit.Areas.Count == 0)
            {
                return Strings.RedactReasonNoArea;
            }

            if (Hit.Areas.Count > RedactionLimits.MaxAreasPerMark)
            {
                return Strings.RedactReasonTooManyLines;
            }

            if (string.IsNullOrWhiteSpace(Hit.Text))
            {
                return Strings.RedactReasonBlank;
            }

            return Hit.Text.Length > RedactionLimits.MaxTermLength ? Strings.RedactReasonTooLong : Strings.RedactReasonLossy;
        }
    }

    /// <summary>Gets the line that tells why the hit cannot be marked, or <see langword="null"/>.</summary>
    public string? NotMarkableText => NotMarkableReason is { } reason ? Strings.RedactHitNotMarkableFor(reason) : null;

    /// <summary>Gets the name of the row for assistive technology: the page, where it is (and invisible), the text, and whether it is checked.</summary>
    public string AutomationName
    {
        get
        {
            var source = IsInvisible ? $"{SourceLabel}, {Strings.RedactHitInvisible}" : SourceLabel;
            var state = !CanCheck ? NotMarkableText! : _isChecked ? Strings.RedactHitChecked : Strings.RedactHitUnchecked;
            return Strings.RedactHitNameFor(PageText, source, Before + Match + After, state);
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
