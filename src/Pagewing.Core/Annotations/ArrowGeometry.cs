using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>
/// The shape of lines and arrows and the helpers of the drawing tools that go with it. An arrow is two strokes:
/// the shaft (start, end) and the head as one polyline of three points (head point, shaft end, head point). A line
/// is one stroke of two points. See the decision "Zeichnungen: Erscheinungsbild, Pfeil als Ink, ..." (M10).
/// </summary>
public static class ArrowGeometry
{
    /// <summary>The angle of each side of the head to the shaft, in degrees.</summary>
    public const double HeadAngleDegrees = 25;

    /// <summary>The shortest length of the sides of the head in points.</summary>
    public const double MinimumHeadLength = 8;

    /// <summary>The length of the sides of the head in widths of the stroke.</summary>
    public const double HeadLengthInWidths = 4;

    /// <summary>The largest angle error, in radians, with which a head is still recognised.</summary>
    private const double AngleTolerance = 1e-3;

    /// <summary>The relative tolerance of coordinates that went through a file (single precision).</summary>
    private const double RelativeTolerance = 1e-6;

    /// <summary>Gets the length of the sides of the head: <c>max(8 pt, 4 x width)</c>.</summary>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The length in points.</returns>
    public static double HeadLength(double width) => Math.Max(MinimumHeadLength, HeadLengthInWidths * width);

    /// <summary>Gets the strokes of a line or an arrow pointing at <paramref name="end"/>.</summary>
    /// <param name="start">The tail.</param>
    /// <param name="end">The tip.</param>
    /// <param name="width">The stroke width in points; it sets the size of the head.</param>
    /// <param name="head">Whether to add the head.</param>
    /// <returns>
    /// One stroke of two points, or with a head the shaft and the head polyline. If the shaft has no length there
    /// is no direction for a head, and only the shaft is returned.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes(DisplayPoint start, DisplayPoint end, double width, bool head)
    {
        IReadOnlyList<DisplayPoint> shaft = [start, end];
        var dx = start.X - end.X;
        var dy = start.Y - end.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!head || !(length > 0) || !double.IsFinite(length))
        {
            return [shaft];
        }

        // Unit vector from the tip back along the shaft, turned by plus and minus the head angle.
        var ux = dx / length;
        var uy = dy / length;
        var angle = HeadAngleDegrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var side = HeadLength(width);
        var first = new DisplayPoint(end.X + (side * ((ux * cos) - (uy * sin))), end.Y + (side * ((ux * sin) + (uy * cos))));
        var second = new DisplayPoint(end.X + (side * ((ux * cos) + (uy * sin))), end.Y + (side * ((-ux * sin) + (uy * cos))));
        return [shaft, new DisplayPoint[] { first, end, second }];
    }

    /// <summary>Tells whether strokes are exactly the shape <see cref="Strokes"/> makes, and finds its ends.</summary>
    /// <param name="strokes">The strokes of a drawing, for example read from the file.</param>
    /// <param name="start">Receives the tail.</param>
    /// <param name="end">Receives the tip.</param>
    /// <param name="head">Receives <see langword="true"/> for an arrow, <see langword="false"/> for a line.</param>
    /// <returns>
    /// <see langword="true"/> for a single stroke of two points (a line) and for a shaft of two points followed by a
    /// head polyline of three points whose middle is the shaft end and whose sides are the same length at 25 degrees
    /// to the shaft (an arrow); within a relative tolerance of 1e-6 for coordinates, so single precision is enough.
    /// </returns>
    public static bool TryRecognize(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, out DisplayPoint start, out DisplayPoint end, out bool head)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        start = default;
        end = default;
        head = false;
        if (strokes.Count is not (1 or 2) || strokes[0].Count != 2)
        {
            return false;
        }

        var a = strokes[0][0];
        var b = strokes[0][1];
        if (!IsFinite(a) || !IsFinite(b))
        {
            return false;
        }

        var shaftX = a.X - b.X;
        var shaftY = a.Y - b.Y;
        var shaftLength = Math.Sqrt((shaftX * shaftX) + (shaftY * shaftY));
        var scale = Math.Max(1, Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(b.X), Math.Abs(b.Y))));
        var tolerance = RelativeTolerance * scale;
        if (!(shaftLength > tolerance))
        {
            return false;
        }

        if (strokes.Count == 2)
        {
            var polyline = strokes[1];
            if (polyline.Count != 3 || !IsFinite(polyline[0]) || !IsFinite(polyline[1]) || !IsFinite(polyline[2]))
            {
                return false;
            }

            if (Math.Abs(polyline[1].X - b.X) > tolerance || Math.Abs(polyline[1].Y - b.Y) > tolerance)
            {
                return false;
            }

            // Both sides of the head lead away from the tip at the head angle to the shaft, one to each side.
            var shaftAngle = Math.Atan2(shaftY, shaftX);
            var headAngle = HeadAngleDegrees * Math.PI / 180;
            var firstOffset = Offset(polyline[0], b, shaftAngle);
            var secondOffset = Offset(polyline[2], b, shaftAngle);
            var matches = Math.Abs(firstOffset - headAngle) < AngleTolerance && Math.Abs(secondOffset + headAngle) < AngleTolerance;
            var swapped = Math.Abs(firstOffset + headAngle) < AngleTolerance && Math.Abs(secondOffset - headAngle) < AngleTolerance;
            if (!matches && !swapped)
            {
                return false;
            }

            var firstLength = Distance(polyline[0], b);
            var secondLength = Distance(polyline[2], b);
            if (!(firstLength > tolerance) || Math.Abs(firstLength - secondLength) > tolerance)
            {
                return false;
            }
        }

        start = a;
        end = b;
        head = strokes.Count == 2;
        return true;
    }

    /// <summary>
    /// Moves <paramref name="point"/> onto the nearest ray from <paramref name="anchor"/> whose angle is a multiple
    /// of <paramref name="stepDegrees"/> (Shift while dragging a line or arrow), keeping the distance.
    /// </summary>
    /// <param name="anchor">The fixed end.</param>
    /// <param name="point">The point under the pointer.</param>
    /// <param name="stepDegrees">The step, for example 45; zero or less leaves the point alone.</param>
    /// <returns>The snapped point.</returns>
    public static DisplayPoint SnapToAngle(DisplayPoint anchor, DisplayPoint point, double stepDegrees)
    {
        var dx = point.X - anchor.X;
        var dy = point.Y - anchor.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(stepDegrees > 0) || !(length > 0) || !double.IsFinite(length))
        {
            return point;
        }

        var step = stepDegrees * Math.PI / 180;
        var snapped = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return new DisplayPoint(anchor.X + Clean(length * Math.Cos(snapped), length), anchor.Y + Clean(length * Math.Sin(snapped), length));
    }

    /// <summary>
    /// Gets the square a rectangle or ellipse drag makes with Shift: the corner at <paramref name="anchor"/> stays,
    /// the side is the longer of the two sides of the drag, the square lies on the side of the pointer.
    /// </summary>
    /// <param name="anchor">The corner where the drag started.</param>
    /// <param name="point">The point under the pointer.</param>
    /// <returns>The square.</returns>
    public static PageRect SquareBox(DisplayPoint anchor, DisplayPoint point)
    {
        var dx = point.X - anchor.X;
        var dy = point.Y - anchor.Y;
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var x = dx < 0 ? anchor.X - side : anchor.X + side;
        var y = dy < 0 ? anchor.Y - side : anchor.Y + side;
        return new PageRect(Math.Min(anchor.X, x), Math.Min(anchor.Y, y), Math.Max(anchor.X, x), Math.Max(anchor.Y, y));
    }

    // Removes the rounding noise of sin and cos at the axes (cos 90 degrees is 6e-17, not 0).
    private static double Clean(double value, double length) => Math.Abs(value) < length * 1e-12 ? 0 : value;

    // The signed angle from the direction tip-to-tail of the shaft to the direction tip-to-side-point.
    private static double Offset(DisplayPoint side, DisplayPoint tip, double shaftAngle)
    {
        var angle = Math.Atan2(side.Y - tip.Y, side.X - tip.X) - shaftAngle;
        while (angle > Math.PI)
        {
            angle -= 2 * Math.PI;
        }

        while (angle <= -Math.PI)
        {
            angle += 2 * Math.PI;
        }

        return angle;
    }

    private static double Distance(DisplayPoint a, DisplayPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static bool IsFinite(DisplayPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
}
