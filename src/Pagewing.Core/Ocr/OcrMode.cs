namespace Pagewing.Core.Ocr;

/// <summary>Which pages a run recognizes.</summary>
public enum OcrMode
{
    /// <summary>Pages that already have text (or Pagewing's own layer) are skipped, with the reason in the report.</summary>
    Skip,

    /// <summary>Pagewing's own layer is replaced; pages with text of their own are still skipped.</summary>
    Redo,

    /// <summary>Every chosen page is recognized, with text or not; the text that is there stays untouched.</summary>
    Force,
}

/// <summary>What a run is asked to do.</summary>
/// <param name="Languages">One or more language names joined by <c>+</c> (<see cref="OcrLanguages.Normalize"/>).</param>
/// <param name="Mode">Which pages are recognized.</param>
/// <param name="Dpi">The resolution of the pages as rendered, or 0 for automatic (<see cref="OcrResolution"/>).</param>
/// <param name="Pages">The 0-based pages to look at, or <see langword="null"/> for all.</param>
/// <param name="Jobs">The number of pages recognized at once, or 0 for <see cref="DefaultJobs"/>.</param>
public sealed record OcrOptions(string Languages, OcrMode Mode = OcrMode.Skip, int Dpi = 0, IReadOnlySet<int>? Pages = null, int Jobs = 0)
{
    /// <summary>The largest resolution that can be asked for.</summary>
    public const int MaxDpi = 600;

    /// <summary>The most pages recognized at once that can be asked for.</summary>
    public const int MaxJobs = 16;

    /// <summary>The number of pages recognized at once when nothing is asked for: one less than the cores, at most four, at least one.</summary>
    public static int DefaultJobs() => Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    /// <summary>Gets the number of pages recognized at once.</summary>
    public int EffectiveJobs => Jobs > 0 ? Math.Min(Jobs, MaxJobs) : DefaultJobs();

    /// <summary>Checks the options.</summary>
    /// <exception cref="ArgumentException">The languages are not a valid selection.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode, the resolution or the jobs are out of range.</exception>
    public void Validate()
    {
        if (OcrLanguages.Normalize(Languages) is null)
        {
            throw new ArgumentException("The languages are one or more names of the letters a to z and _, joined with +.", nameof(Languages));
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (Dpi != 0 && (Dpi < OcrResolution.MinDpi || Dpi > MaxDpi))
        {
            throw new ArgumentOutOfRangeException(nameof(Dpi), $"The resolution is 0 (automatic) or {OcrResolution.MinDpi} to {MaxDpi}.");
        }

        if (Jobs < 0 || Jobs > MaxJobs)
        {
            throw new ArgumentOutOfRangeException(nameof(Jobs));
        }
    }
}
