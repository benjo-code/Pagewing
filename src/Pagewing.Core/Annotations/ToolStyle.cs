namespace Pagewing.Core.Annotations;

/// <summary>The look of a drawing tool: its colour and stroke width.</summary>
/// <param name="Color">The colour.</param>
/// <param name="Width">The stroke width.</param>
public sealed record ToolStyle(DrawColor Color, StrokeWidth Width);
