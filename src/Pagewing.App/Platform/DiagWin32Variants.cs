// DIAG-TEMP: temporary diagnostics for the hardware measurement matrix (Docs/DECISIONS.md, entry "Messmatrix auf
// echter Hardware: Startzeit mit GPU (M3)"). Measurement runs only, never reachable in a normal start. The whole file
// and its callers in Program.cs go again with `git revert` of the commit that added them.
using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Win32;
using Pagewing.App.Measurement;

namespace Pagewing.App.Platform;

/// <summary>
/// DIAG-TEMP. <c>PAGEWING_DIAG_WIN32=&lt;name&gt;</c> in a measurement run picks a variant of Avalonia's Win32 platform
/// options, to find out what the GPU bring-up costs (<see cref="Names"/>). It replaces the rendering choice of
/// <see cref="RenderingModeSelector"/>. Except <c>default</c>, no variant keeps Avalonia's software fallback: a mode
/// that does not work ends the start with an error instead of measuring software rendering under a wrong name.
/// </summary>
internal static class DiagWin32Variants
{
    /// <summary>DIAG-TEMP. The environment variable that picks the variant.</summary>
    public const string Variable = "PAGEWING_DIAG_WIN32";

    /// <summary>DIAG-TEMP. The known variants, in the order the hardware workflow measures them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "default",
        "software",
        "angle-d3d11",
        "angle-d3d9",
        "wgl",
        "vulkan",
        "winui-composition",
        "direct-composition",
        "lowlatency-dxgi",
        "redirection-surface",
        "redirection-surface-ui-thread",
        "adapter-0",
        "adapter-1",
        "adapter-2",
    ];

    /// <summary>
    /// DIAG-TEMP. Reads the variable: no variant outside a measurement run or without a value (also only spaces); in a
    /// measurement run an unknown value, or any value off Windows, is an error.
    /// </summary>
    public static (string? Variant, string? Error) Resolve(string? value, bool isMeasurement, bool isWindows)
    {
        if (!isMeasurement || string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var name = value.Trim();
        if (!isWindows)
        {
            return (null, $"{Variable}={name}: the variants exist only on Windows");
        }

        return Names.Contains(name, StringComparer.Ordinal)
            ? (name, null)
            : (null, $"unknown {Variable} value '{name}' (known: {string.Join(", ", Names)})");
    }

    /// <summary>DIAG-TEMP. Applies <paramref name="variant"/> (one of <see cref="Names"/>) to <paramref name="builder"/>.</summary>
    /// <param name="builder">The builder after <c>UsePlatformDetect</c>.</param>
    /// <param name="variant">The variant.</param>
    /// <param name="log">Receives the adapter list of the <c>adapter-N</c> variants (stderr in the app).</param>
    public static AppBuilder Apply(AppBuilder builder, string variant, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(log);
        Win32RenderingMode[] angle = [Win32RenderingMode.AngleEgl];
        return variant switch
        {
            "default" => builder,
            "software" => builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }),
            "angle-d3d11" => builder
                .With(new Win32PlatformOptions { RenderingMode = angle })
                .With(new AngleOptions { AllowedPlatformApis = [AngleOptions.PlatformApi.DirectX11] }),
            "angle-d3d9" => builder
                .With(new Win32PlatformOptions { RenderingMode = angle })
                .With(new AngleOptions { AllowedPlatformApis = [AngleOptions.PlatformApi.DirectX9] }),
            "wgl" => builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Wgl] }),
            "vulkan" => builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Vulkan] }),
            "winui-composition" => builder.With(Composition(angle, Win32CompositionMode.WinUIComposition)),
            "direct-composition" => builder.With(Composition(angle, Win32CompositionMode.DirectComposition)),
            "lowlatency-dxgi" => builder.With(Composition(angle, Win32CompositionMode.LowLatencyDxgiSwapChain)),
            "redirection-surface" => builder.With(Composition(angle, Win32CompositionMode.RedirectionSurface)),
            "redirection-surface-ui-thread" => builder.With(new Win32PlatformOptions
            {
                RenderingMode = angle,
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
                ShouldRenderOnUIThread = true,
            }),
            "adapter-0" => builder.With(Adapter(angle, 0, log)),
            "adapter-1" => builder.With(Adapter(angle, 1, log)),
            "adapter-2" => builder.With(Adapter(angle, 2, log)),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown variant"),
        };
    }

    private static Win32PlatformOptions Composition(Win32RenderingMode[] rendering, Win32CompositionMode mode) =>
        new() { RenderingMode = rendering, CompositionMode = [mode] };

    /// <summary>
    /// ANGLE over Direct3D 11 on the DXGI adapter with <paramref name="index"/> (in the order of
    /// <c>IDXGIFactory1.EnumAdapters1</c>; Avalonia's default without a callback is the adapter 0). The callback marks
    /// the phase <c>adapter-selection</c> and writes the list as <c>diag adapter &lt;i&gt; …</c>; an index beyond the
    /// list ends the start with an error.
    /// </summary>
    private static Win32PlatformOptions Adapter(Win32RenderingMode[] rendering, int index, TextWriter log) => new()
    {
        RenderingMode = rendering,
        GraphicsAdapterSelectionCallback = adapters =>
        {
            StartupPhases.Current?.Mark("adapter-selection", Stopwatch.GetTimestamp());
            for (var i = 0; i < adapters.Count; i++)
            {
                log.WriteLine($"diag adapter {i}{(i == index ? " (chosen)" : string.Empty)}: {Describe(adapters[i])}");
            }

            if (index >= adapters.Count)
            {
                // Avalonia catches the exception and reports only that no rendering mode worked: say why first.
                log.WriteLine($"diag error: adapter-{index}: DXGI lists only {adapters.Count} adapter(s)");
                log.Flush();
                throw new InvalidOperationException($"adapter-{index}: DXGI lists only {adapters.Count} adapter(s)");
            }

            log.Flush();
            return index;
        },
    };

    private static string Describe(PlatformGraphicsDeviceAdapterDescription adapter) =>
        $"{adapter.Description} (LUID {(adapter.DeviceLuid is { } luid ? Convert.ToHexString(luid) : "-")})";
}
