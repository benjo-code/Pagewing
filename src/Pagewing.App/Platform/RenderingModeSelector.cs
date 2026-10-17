namespace Pagewing.App.Platform;

/// <summary>A graphics adapter as the kernel graphics subsystem lists it (<see cref="KmtAdapters"/>).</summary>
/// <param name="Luid">The locally unique id of the adapter (the same as in DXGI).</param>
/// <param name="TypeFlags">The bits of <c>D3DKMT_ADAPTERTYPE</c>.</param>
internal readonly record struct GraphicsAdapter(long Luid, uint TypeFlags)
{
    /// <summary><c>D3DKMT_ADAPTERTYPE.RenderSupported</c>: the adapter can render (Direct3D).</summary>
    public const uint RenderSupportedFlag = 1 << 0;

    /// <summary><c>D3DKMT_ADAPTERTYPE.DisplaySupported</c>: the adapter drives displays.</summary>
    public const uint DisplaySupportedFlag = 1 << 1;

    /// <summary><c>D3DKMT_ADAPTERTYPE.SoftwareDevice</c>: a software adapter (Microsoft Basic Render Driver, WARP).</summary>
    public const uint SoftwareDeviceFlag = 1 << 2;

    /// <summary>Gets whether graphics hardware renders on this adapter (a GPU, also an integrated or paravirtualized one).</summary>
    public bool IsHardwareRenderer => (TypeFlags & RenderSupportedFlag) != 0 && (TypeFlags & SoftwareDeviceFlag) == 0;

    /// <summary>Gets whether this adapter renders on the CPU (WARP).</summary>
    public bool IsSoftwareRenderer => (TypeFlags & RenderSupportedFlag) != 0 && (TypeFlags & SoftwareDeviceFlag) != 0;
}

/// <summary>
/// Chooses Avalonia's rendering mode on Windows: software rendering (Skia on the CPU) only when the machine has no
/// hardware graphics adapter, otherwise Avalonia's default (ANGLE over Direct3D 11). Docs/DECISIONS.md, entry
/// "Software-Rendering ohne Hardware-Grafikadapter (M3)".
/// </summary>
/// <remarks>
/// Without a 3D-capable driver (virtual machines without a GPU, the Microsoft Basic Display Adapter) Direct3D runs on
/// WARP, the CPU rasterizer behind ANGLE; the first frame then costs far more than with Skia's own CPU rendering.
/// Display-only adapters do not count. Any uncertainty keeps the default: no adapter list, no software renderer in
/// it, or one hardware renderer.
/// </remarks>
internal static class RenderingModeSelector
{
    /// <summary>
    /// Environment variable that skips the detection: <c>software</c> forces software rendering, <c>default</c> keeps
    /// Avalonia's default. Any other value is ignored.
    /// </summary>
    public const string OverrideVariable = "PAGEWING_RENDERING";

    /// <summary>
    /// Returns whether to render in software. Reads <paramref name="overrideValue"/> first and calls
    /// <paramref name="enumerateAdapters"/> (which returns <see langword="null"/> when it cannot list the adapters)
    /// only without an override.
    /// </summary>
    public static bool UseSoftwareRendering(string? overrideValue, Func<IReadOnlyList<GraphicsAdapter>?> enumerateAdapters)
    {
        ArgumentNullException.ThrowIfNull(enumerateAdapters);
        if (string.Equals(overrideValue, "software", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(overrideValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<GraphicsAdapter>? adapters;
        try
        {
            adapters = enumerateAdapters();
        }
        catch (Exception)
        {
            // A failed detection keeps the default, never software.
            return false;
        }

        return RendersOnlyInSoftware(adapters);
    }

    /// <summary>Gets whether the list has a software renderer (WARP) and no hardware renderer.</summary>
    public static bool RendersOnlyInSoftware(IReadOnlyList<GraphicsAdapter>? adapters)
    {
        if (adapters is null)
        {
            return false;
        }

        var software = false;
        foreach (var adapter in adapters)
        {
            if (adapter.IsHardwareRenderer)
            {
                return false;
            }

            software |= adapter.IsSoftwareRenderer;
        }

        return software;
    }
}
