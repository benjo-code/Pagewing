namespace Pagewing.Core.Viewing;

/// <summary>The render cache budgets of a tab: the full budget while it is shown, a small one while it is not.</summary>
public static class TabBudget
{
    /// <summary>The budget of the tab that is shown: 256 MiB.</summary>
    public const long ActiveBytes = RenderCache.DefaultBudgetBytes;

    /// <summary>The budget of a tab in the background: 16 MiB.</summary>
    public const long InactiveBytes = 16L * 1024 * 1024;

    /// <summary>Returns the budget for an active or an inactive tab.</summary>
    internal static long For(bool active) => active ? ActiveBytes : InactiveBytes;
}
