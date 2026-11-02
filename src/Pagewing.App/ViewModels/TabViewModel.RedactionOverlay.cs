using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The text on the areas (milestone 14.1, spec 4.2): the text of the selected marks and the style that applies to all of them. Both are
/// steps of the history like a mark, so undo takes them back, and a plan with such a change counts as an unsaved change like any other.
/// Nothing of it is made before the mode is on; the controls of the panel exist only while a mark is selected (<see cref="RedactionViewModel.OverlayEditor"/>).
/// </summary>
public sealed partial class TabViewModel
{
    private static readonly RedactionOverlayStyle DefaultOverlayStyle = new();

    /// <summary>Gets how the text on the areas is drawn for the whole plan: repeated or once, sized to fit or not, in which colour.</summary>
    public RedactionOverlayStyle RedactionOverlayStyle => _redactionHistory?.Current.Overlay ?? DefaultOverlayStyle;

    /// <summary>
    /// Sets the text on the areas of the marks with <paramref name="ids"/> in one step of the history. A text the plan refuses (too long,
    /// a character the font does not have) changes nothing and sets <see cref="RedactionNotice"/>; white space alone clears the text. Only in
    /// the mode, and only while the plan takes changes.
    /// </summary>
    /// <param name="ids">The ids of the marks.</param>
    /// <param name="text">The text, or <see langword="null"/> for none.</param>
    /// <returns>Whether the plan changed.</returns>
    internal bool SetOverlayText(IReadOnlySet<Guid> ids, string? text)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return StageRedaction(plan => plan.WithOverlayText(ids, text), null);
    }

    /// <summary>Changes the style of the text on the areas in one step of the history (every mark of the plan follows it).</summary>
    /// <param name="change">Makes the new style from the current one.</param>
    /// <returns>Whether the plan changed.</returns>
    internal bool SetOverlayStyle(Func<RedactionOverlayStyle, RedactionOverlayStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return StageRedaction(plan => plan.WithOverlayStyle(change(plan.Overlay)), null);
    }

    /// <summary>Gets whether the plan takes changes now (the editor of the text greys itself out otherwise).</summary>
    internal bool CanEditOverlay => CanEditRedactions;
}
