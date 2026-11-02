using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Redaction;
using Pagewing.Core.Settings;

namespace Pagewing.App.ViewModels;

/// <summary>One saved text module ("snippet") of the editor of the text on the areas: the text and the two commands of its buttons.</summary>
public sealed class RedactionSnippetViewModel
{
    internal RedactionSnippetViewModel(string text, Action<string> use, Action<string> remove)
    {
        Text = text;
        UseName = Strings.RedactOverlayUseSnippet(text);
        RemoveName = Strings.RedactOverlayRemoveSnippet(text);
        UseCommand = new RelayCommand(() => use(text));
        RemoveCommand = new RelayCommand(() => remove(text));
    }

    /// <summary>Gets the text of the snippet (the label of its button).</summary>
    public string Text { get; }

    /// <summary>Gets the name of the button that puts the snippet on the selected areas, for screen readers.</summary>
    public string UseName { get; }

    /// <summary>Gets the name of the button that removes the snippet from the list, for screen readers.</summary>
    public string RemoveName { get; }

    /// <summary>Puts the snippet on the selected areas (one step of the plan).</summary>
    public ICommand UseCommand { get; }

    /// <summary>Takes the snippet out of the saved list.</summary>
    public ICommand RemoveCommand { get; }
}

/// <summary>
/// The field "Text on the area" of the panel "Redact" for the selected marks (milestone 14.1, spec 4.2), with the saved snippets and the
/// options that apply to the whole plan (repeat, fit the size, colour of the text). It exists only while the mode is on and at least one
/// mark is selected (<see cref="RedactionViewModel.OverlayEditor"/> makes and drops it), so nothing of it is in the startup path.
/// </summary>
/// <remarks>
/// The text in the field is a draft: typing is never a step of the history. Enter and leaving the field apply it, and everything the draft
/// is checked against while it is typed (a character the font does not have, a redacted term in the text) is told at once, in words next to
/// the field. A text with a character the font does not have is refused as a whole and never changed; the plan stays as it was, and the
/// analysis refuses a text that repeats a term whatever the field says. UI thread only.
/// </remarks>
public sealed class RedactionOverlayEditorViewModel : ViewModelBase, IDisposable
{
    private readonly TabViewModel _tab;
    private readonly ObservableCollection<RedactionSnippetViewModel> _snippets = [];
    private readonly RelayCommand _commitCommand;
    private readonly RelayCommand _commitColorCommand;
    private readonly RelayCommand _addCodeCommand;

    private IReadOnlySet<Guid> _targetIds = new HashSet<Guid>();
    private string _text = string.Empty;
    private string _loaded = string.Empty;
    private bool _mixed;
    private bool _dirty;
    private int _invalidAt = -1;
    private string _colorText = string.Empty;
    private string? _colorError;
    private bool _disposed;

    internal RedactionOverlayEditorViewModel(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
        Snippets = _snippets;
        _commitCommand = new RelayCommand(() => Commit());
        _commitColorCommand = new RelayCommand(() => CommitColor());
        _addCodeCommand = new RelayCommand(AddCode, () => CanAddCode);
        _tab.PropertyChanged += OnTabPropertyChanged;
        _tab.AnnotationSettings.Changed += OnSettingsChanged;
        Refresh();
        RefreshSnippets();
    }

    /// <summary>Gets the longest text the field takes, in characters.</summary>
    public int MaxLength { get; } = RedactionLimits.MaxOverlayTextLength;

    /// <summary>Gets the longest colour text the colour field takes (<c>#RRGGBB</c>).</summary>
    public int ColorMaxLength { get; } = 7;

    /// <summary>
    /// Gets or sets the text in the field (a draft). It is applied by <see cref="Commit"/>; typing alone changes nothing in the plan. With
    /// several marks selected that have different texts the field starts empty (<see cref="Placeholder"/> says so), and any edit then applies
    /// to all of them.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_text, next, StringComparison.Ordinal))
            {
                return;
            }

            _text = next;
            _dirty = _mixed || !string.Equals(_text, _loaded, StringComparison.Ordinal);
            OnPropertyChanged();
            RaiseDraftState();
        }
    }

    /// <summary>Gets the hint shown in the empty field: no text, or that the selected areas have different texts.</summary>
    public string Placeholder => _mixed ? Strings.RedactOverlayMixed : Strings.RedactOverlayPlaceholder;

    /// <summary>Gets whether the draft is not applied yet.</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Gets the line that says the draft has a character that cannot be drawn and where (position from 1), or <see langword="null"/>. The
    /// text is refused as a whole and never changed; Enter does nothing until the character is gone.
    /// </summary>
    public string? InvalidText => _invalidAt < 0 ? null : Strings.RedactOverlayInvalidAt(_invalidAt + 1);

    /// <summary>Gets whether <see cref="InvalidText"/> is on show.</summary>
    public bool HasInvalid => _invalidAt >= 0;

    /// <summary>
    /// Gets the warning that the text (as typed, or as it stands on the marks) contains a text that is redacted and would put it back on the
    /// page, or <see langword="null"/>. Applying stays refused by the analysis whatever this says; this is only the early word.
    /// </summary>
    public string? Warning => Refusal() is null ? null : Strings.RedactOverlayRepeatsTerm;

    /// <summary>Gets whether <see cref="Warning"/> is on show.</summary>
    public bool HasWarning => Warning is not null;

    /// <summary>Gets the saved snippets: those of the settings, or the one of the language while none were saved.</summary>
    public ObservableCollection<RedactionSnippetViewModel> Snippets { get; }

    /// <summary>Gets the line that says why the text in the field cannot be saved as a snippet, or <see langword="null"/>.</summary>
    public string? CodeNote
    {
        get
        {
            var candidate = _text.Trim();
            if (candidate.Length == 0 || CanAddCode || _invalidAt >= 0)
            {
                return null;
            }

            if (candidate.Length > AppSettings.MaxRedactionCodeLength)
            {
                return Strings.RedactOverlayCodeTooLong(AppSettings.MaxRedactionCodeLength);
            }

            return EffectiveCodes().Contains(candidate, StringComparer.Ordinal)
                ? Strings.RedactOverlayCodeKnown
                : Strings.RedactOverlayCodesFull(AppSettings.MaxRedactionCodes);
        }
    }

    /// <summary>Gets whether <see cref="CodeNote"/> is on show.</summary>
    public bool HasCodeNote => CodeNote is not null;

    /// <summary>Gets whether the text in the field can be saved as a snippet: it is not empty, short enough, drawable, not saved yet, and the list is not full.</summary>
    public bool CanAddCode
    {
        get
        {
            var candidate = _text.Trim();
            var codes = EffectiveCodes();
            return candidate.Length is > 0 and <= AppSettings.MaxRedactionCodeLength
                && candidate.All(TextBoxCharacters.IsSupported)
                && !codes.Contains(candidate, StringComparer.Ordinal)
                && codes.Count < AppSettings.MaxRedactionCodes;
        }
    }

    /// <summary>Applies the draft (Enter in the field); the same as leaving it.</summary>
    public ICommand CommitCommand => _commitCommand;

    /// <summary>Applies the colour in the colour field (Enter in it); the same as leaving it.</summary>
    public ICommand CommitColorCommand => _commitColorCommand;

    /// <summary>Saves the text in the field as a snippet.</summary>
    public ICommand AddCodeCommand => _addCodeCommand;

    /// <summary>Gets or sets whether the text is repeated over the area (a step of the plan; applies to every mark).</summary>
    public bool Repeat
    {
        get => _tab.RedactionOverlayStyle.Repeat;
        set => _tab.SetOverlayStyle(style => style with { Repeat = value });
    }

    /// <summary>Gets or sets whether the size of the text is chosen to fit the area (otherwise 12 pt, and a text that does not fit is left out).</summary>
    public bool AutoSize
    {
        get => _tab.RedactionOverlayStyle.AutoSize;
        set => _tab.SetOverlayStyle(style => style with { AutoSize = value });
    }

    /// <summary>
    /// Gets or sets whether the colour of the text is chosen by contrast against the fill (black or white). Turning it off starts from the
    /// colour that would have been chosen.
    /// </summary>
    public bool IsColorAuto
    {
        get => _tab.RedactionOverlayStyle.TextColor is null;
        set
        {
            if (value == IsColorAuto)
            {
                return;
            }

            _tab.SetOverlayStyle(style => style with { TextColor = value ? null : RedactionOverlayColor.Choose(_tab.RedactionFill, null) });
        }
    }

    /// <summary>Gets or sets the colour of the text as typed (<c>#RRGGBB</c>); applied by <see cref="CommitColor"/>.</summary>
    public string ColorText
    {
        get => _colorText;
        set
        {
            if (SetProperty(ref _colorText, value ?? string.Empty))
            {
                ColorError = null;
            }
        }
    }

    /// <summary>Gets the line that says the colour is not written as <c>#RRGGBB</c>, or <see langword="null"/>.</summary>
    public string? ColorError
    {
        get => _colorError;
        private set
        {
            if (SetProperty(ref _colorError, value))
            {
                OnPropertyChanged(nameof(HasColorError));
            }
        }
    }

    /// <summary>Gets whether <see cref="ColorError"/> is on show.</summary>
    public bool HasColorError => _colorError is not null;

    /// <summary>
    /// Gets the warning that the chosen colour has less than 4.5:1 contrast against the fill, or <see langword="null"/>. Told in words, never
    /// forbidden: the user may want it.
    /// </summary>
    public string? ContrastWarning
    {
        get
        {
            if (_tab.RedactionOverlayStyle.TextColor is not { } color)
            {
                return null;
            }

            var ratio = RedactionOverlayColor.Contrast(color, _tab.RedactionFill);
            return ratio < RedactionOverlayColor.MinContrast ? Strings.RedactOverlayContrastLow(ratio) : null;
        }
    }

    /// <summary>Gets whether <see cref="ContrastWarning"/> is on show.</summary>
    public bool HasContrastWarning => ContrastWarning is not null;

    /// <summary>
    /// Applies the draft to the selected marks as one step of the history. Nothing happens when there is no draft, when it has a character
    /// that cannot be drawn, or while the plan takes no changes; a text the plan refuses stays in the field and the tab says why.
    /// </summary>
    /// <returns>Whether nothing is left to apply (no draft, or it is in the plan now).</returns>
    public bool Commit()
    {
        if (_disposed)
        {
            return false;
        }

        if (!_dirty)
        {
            return true;
        }

        if (_invalidAt >= 0 || !_tab.CanEditOverlay)
        {
            return false;
        }

        _tab.SetOverlayText(_targetIds, _text);
        if (_tab.RedactionNotice is not null)
        {
            // The plan refused it (the tab says why): the draft stays.
            return false;
        }

        // The plan keeps the text as it normalizes it (white space alone is no text), and the field shows that.
        _dirty = false;
        Refresh();
        return true;
    }

    /// <summary>
    /// Applies the colour typed in the colour field when it is written as <c>#RRGGBB</c>; otherwise the field keeps what was typed and
    /// <see cref="ColorError"/> says so.
    /// </summary>
    /// <returns>Whether the colour of the plan is the typed one now.</returns>
    public bool CommitColor()
    {
        if (_disposed || IsColorAuto)
        {
            return false;
        }

        var text = _colorText.Trim();
        if (!RedactionFill.TryParse(text, out var fill))
        {
            ColorError = Strings.RedactFillHexError;
            return false;
        }

        ColorError = null;
        _tab.SetOverlayStyle(style => style with { TextColor = fill });
        return _tab.RedactionOverlayStyle.TextColor == fill;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab.AnnotationSettings.Changed -= OnSettingsChanged;
    }

    // The snippets -----------------------------------------------------------------------------------------------------------------

    // What the settings keep, or the one text of the language while the user changed nothing; an empty list means they took them all away.
    private List<string> EffectiveCodes() => _tab.AnnotationSettings.Current.RedactionCodes ?? [Strings.RedactOverlayCodeDefault];

    private void AddCode()
    {
        var candidate = _text.Trim();
        if (!CanAddCode)
        {
            return;
        }

        _tab.AnnotationSettings.Update(settings => settings.WithRedactionCodes([.. EffectiveCodes(), candidate]));
    }

    private void RemoveCode(string code) =>
        _tab.AnnotationSettings.Update(settings => settings.WithRedactionCodes(EffectiveCodes().Where(existing => !string.Equals(existing, code, StringComparison.Ordinal))));

    private void UseCode(string code)
    {
        Text = code;
        Commit();
    }

    private void RefreshSnippets()
    {
        _snippets.Clear();
        foreach (var code in EffectiveCodes())
        {
            _snippets.Add(new RedactionSnippetViewModel(code, UseCode, RemoveCode));
        }

        RaiseCodeState();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (!_disposed)
        {
            RefreshSnippets();
        }
    }

    private void RaiseCodeState()
    {
        OnPropertyChanged(nameof(CanAddCode));
        OnPropertyChanged(nameof(CodeNote));
        OnPropertyChanged(nameof(HasCodeNote));
        _addCodeCommand.RaiseCanExecuteChanged();
    }

    // The draft and the plan ------------------------------------------------------------------------------------------------------

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName is nameof(TabViewModel.RedactionPlan) or nameof(TabViewModel.SelectedMarks) or nameof(TabViewModel.RedactionOverlayStyle))
        {
            Refresh();
        }
    }

    // Brings the field in line with the marks it targets. A draft survives a change of the plan (it is the user's), but not a change of
    // the selection: it belonged to other marks.
    private void Refresh()
    {
        var ids = _tab.SelectedMarks;
        var selectionChanged = !_targetIds.SetEquals(ids);
        _targetIds = ids;
        var texts = new HashSet<string>(StringComparer.Ordinal);
        if (_tab.RedactionPlan is { } plan)
        {
            foreach (var mark in plan.Marks)
            {
                if (ids.Contains(mark.Id))
                {
                    texts.Add(mark.OverlayText ?? string.Empty);
                }
            }
        }

        _mixed = texts.Count > 1;
        _loaded = texts.Count == 1 ? texts.First() : string.Empty;
        if (!_dirty || selectionChanged)
        {
            _dirty = false;
            if (!string.Equals(_text, _loaded, StringComparison.Ordinal))
            {
                _text = _loaded;
                OnPropertyChanged(nameof(Text));
            }
        }

        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(AutoSize));
        OnPropertyChanged(nameof(IsColorAuto));
        OnPropertyChanged(nameof(ContrastWarning));
        OnPropertyChanged(nameof(HasContrastWarning));
        if (IsColorAuto)
        {
            ColorText = string.Empty;
        }
        else if (_tab.RedactionOverlayStyle.TextColor is { } color && !string.Equals(_colorText.Trim(), color.ToHex(), StringComparison.OrdinalIgnoreCase))
        {
            ColorText = color.ToHex();
        }

        RaiseDraftState();
    }

    private void RaiseDraftState()
    {
        _invalidAt = FirstUnsupported(_text);
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(InvalidText));
        OnPropertyChanged(nameof(HasInvalid));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
        RaiseCodeState();
    }

    private static int FirstUnsupported(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!TextBoxCharacters.IsSupported(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    // The refusal the rule would give the plan with the draft on the selected marks (or the plan as it is, with no draft).
    private RedactionRefusal? Refusal()
    {
        if (_tab.RedactionPlan is not { } plan)
        {
            return null;
        }

        try
        {
            var candidate = _dirty && _invalidAt < 0 ? plan.WithOverlayText(_targetIds, _text) : plan;
            return RedactionOverlayRule.Refusal(candidate);
        }
        catch (RedactionException)
        {
            // Too long for the plan: the field does not allow it, and there is nothing to warn about yet.
            return null;
        }
    }
}
