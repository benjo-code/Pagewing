namespace Pagewing.Core.Forms;

/// <summary>Tab order through the widgets of a form.</summary>
public static class FormNavigation
{
    /// <summary>
    /// The next or previous widget that Pagewing can change (<see cref="FormLock.None"/>). <paramref name="inOrder"/> is
    /// the order pages first, then <c>/Annots</c> within a page. Without a <paramref name="current"/> widget (or one that is
    /// not in the list) the walk starts outside the list: at the first widget going forward, at the last going back.
    /// </summary>
    /// <param name="inOrder">The widgets of the document in tab order.</param>
    /// <param name="current">The widget that has the focus, or <c>null</c>.</param>
    /// <param name="forward">Whether to go forward (Tab) or back (Shift+Tab).</param>
    /// <returns>The key of the widget, or <c>null</c> past the end or before the start (the caller lets Tab through).</returns>
    public static FormWidgetKey? Next(IReadOnlyList<FormField> inOrder, FormWidgetKey? current, bool forward)
    {
        ArgumentNullException.ThrowIfNull(inOrder);
        var position = -1;
        if (current is { } key)
        {
            for (var i = 0; i < inOrder.Count; i++)
            {
                if (inOrder[i].Key == key)
                {
                    position = i;
                    break;
                }
            }
        }

        var step = forward ? 1 : -1;
        var start = position >= 0 ? position : (forward ? -1 : inOrder.Count);
        for (var i = start + step; i >= 0 && i < inOrder.Count; i += step)
        {
            if (inOrder[i].Lock == FormLock.None)
            {
                return inOrder[i].Key;
            }
        }

        return null;
    }
}
