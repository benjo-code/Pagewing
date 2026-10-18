namespace Pagewing.Core.Text;

/// <summary>How a query is compared with the text of a page.</summary>
/// <param name="MatchCase">Whether upper and lower case must match; otherwise both sides are lowercased (invariant culture).</param>
/// <param name="WholeWord">Whether a match must not have a letter or digit directly before or after it.</param>
public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false);
