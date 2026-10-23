namespace Pagewing.App.Platform;

/// <summary>
/// Chooses Avalonia's rendering mode: on Windows software rendering (Skia on the CPU, no Direct3D device, no ANGLE and
/// no composition) unless <see cref="OverrideVariable"/> asks for Avalonia's default (ANGLE over Direct3D 11 on the
/// GPU). Other systems keep Avalonia's default. Docs/DECISIONS.md, entry "Software-Rendering als Windows-Standard (M3)".
/// </summary>
/// <remarks>
/// Measured on a PC with a GPU, bringing up the graphics device and the composition costs far more in the startup path
/// than software rendering does, and scrolling showed the same <c>--measure-scroll</c> intervals on one PC.
/// </remarks>
internal static class RenderingModeSelector
{
    /// <summary>
    /// Environment variable for the rendering mode on Windows: <c>default</c> takes Avalonia's default (the GPU path),
    /// <c>software</c> keeps software rendering. Case and surrounding spaces are ignored; any other value is ignored too.
    /// </summary>
    public const string OverrideVariable = "PAGEWING_RENDERING";

    /// <summary>Returns whether to render in software.</summary>
    /// <param name="overrideValue">The value of <see cref="OverrideVariable"/>, or <see langword="null"/> when it is not set.</param>
    /// <param name="isWindows">Whether the process runs on Windows; elsewhere the choice is always Avalonia's default.</param>
    public static bool UseSoftwareRendering(string? overrideValue, bool isWindows) =>
        isWindows && !string.Equals(overrideValue?.Trim(), "default", StringComparison.OrdinalIgnoreCase);
}
