// TEMPORARY diagnostics (reverted after the CI run): prints the graphics adapters as D3DKMT and DXGI list them.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.App.Platform;

[SupportedOSPlatform("windows")]
internal static unsafe partial class DiagAdapters
{
    private static readonly Guid FactoryIid = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    public static int Print()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var kmt = KmtAdapters.TryEnumerate();
        var kmtMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"D3DKMT ({kmtMs:0.00} ms): {(kmt is null ? "null" : kmt.Count.ToString(CultureInfo.InvariantCulture))}"));
        foreach (var adapter in kmt ?? [])
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  luid={adapter.Luid:X} type=0x{adapter.TypeFlags:X} hardwareRenderer={adapter.IsHardwareRenderer} softwareRenderer={adapter.IsSoftwareRenderer}"));
        }

        Console.WriteLine($"decision (no override): software={RenderingModeSelector.UseSoftwareRendering(null, () => kmt)}");

        start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (CreateDXGIFactory1(FactoryIid, out var factory) < 0 || factory == 0)
        {
            Console.WriteLine("DXGI: CreateDXGIFactory1 failed");
            return 0;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DXGI factory: {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.00} ms"));
        var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(void***)factory)[12];
        for (uint index = 0; index < 32; index++)
        {
            nint adapter;
            if (enumAdapters1(factory, index, &adapter) < 0 || adapter == 0)
            {
                break;
            }

            AdapterDesc1 d;
            var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)(*(void***)adapter)[10];
            if (getDesc1(adapter, &d) >= 0)
            {
                var name = new string(d.Description, 0, 128).TrimEnd('\0');
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  DXGI {index}: '{name}' vendor=0x{d.VendorId:X4} device=0x{d.DeviceId:X4} subsys=0x{d.SubSysId:X8} rev={d.Revision} dedicatedVideo={(ulong)d.DedicatedVideoMemory} dedicatedSystem={(ulong)d.DedicatedSystemMemory} sharedSystem={(ulong)d.SharedSystemMemory} luid={((long)d.LuidHigh << 32) | d.LuidLow:X} flags=0x{d.Flags:X}"));
            }

            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)adapter)[2])(adapter);
        }

        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)factory)[2])(factory);
        return 0;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
