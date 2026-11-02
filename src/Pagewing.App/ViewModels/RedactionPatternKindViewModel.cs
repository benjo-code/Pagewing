using System.Globalization;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One of the four check boxes of the pattern search in the panel "Redact" (E-mail, IBAN, Phone, Credit card): whether the kind is
/// looked for, and, once a search ran, how many places of it were found ("IBAN (2)"). UI thread only.
/// </summary>
public sealed class RedactionPatternKindViewModel : ViewModelBase
{
    private bool _isChecked = true;
    private int? _count;

    internal RedactionPatternKindViewModel(RedactionPatternKind kind) => Kind = kind;

    /// <summary>Raised when <see cref="IsChecked"/> changed (a search of the old choice ends).</summary>
    internal event Action<RedactionPatternKindViewModel>? CheckedChanged;

    /// <summary>Gets the kind of pattern.</summary>
    public RedactionPatternKind Kind { get; }

    /// <summary>Gets or sets whether the kind is looked for (all four are at first, because a number that is missed is worse than one too many).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                CheckedChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Gets how many places of this kind the search found, or <see langword="null"/> before a search.</summary>
    public int? Count
    {
        get => _count;
        internal set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    /// <summary>Gets the label of the check box: the name of the kind, and after a search the number of its places in brackets.</summary>
    public string Label => _count is { } count
        ? $"{RedactionPatternText.NameOf(Kind)} ({count.ToString(CultureInfo.InvariantCulture)})"
        : RedactionPatternText.NameOf(Kind);
}
