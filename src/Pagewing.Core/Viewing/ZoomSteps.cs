namespace Pagewing.Core.Viewing;

/// <summary>The zoom range of the viewer and its fixed steps (10 % to 800 %). A zoom of 1 is 100 % = 96 dpi.</summary>
public static class ZoomSteps
{
    /// <summary>The smallest zoom (10 %).</summary>
    public const double Min = 0.1;

    /// <summary>The largest zoom (800 %).</summary>
    public const double Max = 8.0;

    /// <summary>The zoom of a freshly opened document (100 %).</summary>
    public const double Default = 1.0;

    /// <summary>Tolerance when comparing a zoom with a step, so that 1.0000000001 still counts as the 100 % step.</summary>
    private const double Epsilon = 1e-9;

    private static readonly double[] Steps = [Min, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, Max];

    /// <summary>Limits <paramref name="zoom"/> to <see cref="Min"/> to <see cref="Max"/>; NaN gives <see cref="Default"/>.</summary>
    public static double Clamp(double zoom) => double.IsNaN(zoom) ? Default : Math.Clamp(zoom, Min, Max);

    /// <summary>
    /// Gets the smallest step above <paramref name="zoom"/>, or <see cref="Max"/> when there is none.
    /// NaN gives <see cref="Default"/>.
    /// </summary>
    public static double Next(double zoom)
    {
        if (double.IsNaN(zoom))
        {
            return Default;
        }

        foreach (var step in Steps)
        {
            if (step > zoom + Epsilon)
            {
                return step;
            }
        }

        return Max;
    }

    /// <summary>
    /// Gets the largest step below <paramref name="zoom"/>, or <see cref="Min"/> when there is none.
    /// NaN gives <see cref="Default"/>.
    /// </summary>
    public static double Previous(double zoom)
    {
        if (double.IsNaN(zoom))
        {
            return Default;
        }

        for (var i = Steps.Length - 1; i >= 0; i--)
        {
            if (Steps[i] < zoom - Epsilon)
            {
                return Steps[i];
            }
        }

        return Min;
    }
}
