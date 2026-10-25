namespace Pagewing.Core.Annotations;

/// <summary>Simplifies and smooths a freehand stroke before it is written to the file.</summary>
public static class StrokeSimplifier
{
    /// <summary>
    /// Thins a stroke with the Douglas-Peucker algorithm: a point stays only if it lies farther than
    /// <paramref name="tolerance"/> from the line through its neighbours that stay. The first and the last
    /// point always stay. The algorithm runs without recursion, so a stroke of any length is safe.
    /// </summary>
    /// <param name="points">The points of the stroke.</param>
    /// <param name="tolerance">The largest distance in points a dropped point may have from the result.</param>
    /// <returns>The kept points in their original order; fewer than three points are returned unchanged.</returns>
    public static IReadOnlyList<DisplayPoint> Simplify(IReadOnlyList<DisplayPoint> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return [.. points];
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, points.Count - 1));
        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            var farthest = -1;
            var farthestDistance = tolerance;
            for (var i = first + 1; i < last; i++)
            {
                var distance = DistanceToSegment(points[i], points[first], points[last]);
                if (distance > farthestDistance)
                {
                    farthestDistance = distance;
                    farthest = i;
                }
            }

            if (farthest >= 0)
            {
                keep[farthest] = true;
                pending.Push((first, farthest));
                pending.Push((farthest, last));
            }
        }

        var result = new List<DisplayPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Rounds the corners of a stroke with one pass of Chaikin's algorithm. The first and the last point stay, so
    /// the stroke starts and ends where the user put the pen; the points are not rounded to a grid.
    /// </summary>
    /// <param name="points">The points of the stroke.</param>
    /// <returns>For <c>n</c> points (at least three) <c>2n</c> points; shorter strokes are returned unchanged.</returns>
    public static IReadOnlyList<DisplayPoint> Smooth(IReadOnlyList<DisplayPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return [.. points];
        }

        var result = new List<DisplayPoint>((points.Count * 2) + 2) { points[0] };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            result.Add(new DisplayPoint((0.75 * a.X) + (0.25 * b.X), (0.75 * a.Y) + (0.25 * b.Y)));
            result.Add(new DisplayPoint((0.25 * a.X) + (0.75 * b.X), (0.25 * a.Y) + (0.75 * b.Y)));
        }

        result.Add(points[^1]);
        return result;
    }

    private static double DistanceToSegment(DisplayPoint p, DisplayPoint a, DisplayPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared > 0 ? Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1) : 0;
        var cx = a.X + (t * dx) - p.X;
        var cy = a.Y + (t * dy) - p.Y;
        return Math.Sqrt((cx * cx) + (cy * cy));
    }
}
