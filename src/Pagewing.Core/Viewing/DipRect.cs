namespace Pagewing.Core.Viewing;

/// <summary>A rectangle in device-independent pixels (DIPs; 1/96 inch).</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    /// <summary>Gets the right edge, <see cref="X"/> + <see cref="Width"/>.</summary>
    public double Right => X + Width;

    /// <summary>Gets the bottom edge, <see cref="Y"/> + <see cref="Height"/>.</summary>
    public double Bottom => Y + Height;
}
