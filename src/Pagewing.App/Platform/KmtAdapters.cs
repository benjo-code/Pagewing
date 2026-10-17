using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.App.Platform;

/// <summary>
/// Lists the graphics adapters of the kernel graphics subsystem (<c>D3DKMTEnumAdapters2</c>) with their type
/// (<c>D3DKMTQueryAdapterInfo</c>, <c>KMTQAITYPE_ADAPTERTYPE</c>), from <c>gdi32.dll</c>. Much cheaper than a DXGI
/// factory, which initializes the Direct3D runtime. Windows 8 or later.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class KmtAdapters
{
    /// <summary>Upper bound of adapters read (a machine has a handful).</summary>
    private const uint MaxAdapters = 32;

    /// <summary><c>KMTQAITYPE_ADAPTERTYPE</c>.</summary>
    private const int AdapterTypeQuery = 15;

    /// <summary>Returns the adapters in the order the kernel lists them, or <see langword="null"/> when they cannot be listed.</summary>
    public static IReadOnlyList<GraphicsAdapter>? TryEnumerate()
    {
        try
        {
            return Enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static List<GraphicsAdapter>? Enumerate()
    {
        var infos = new AdapterInfo[MaxAdapters];
        uint count;
        fixed (AdapterInfo* pointer = infos)
        {
            var request = new EnumAdapters2 { NumAdapters = MaxAdapters, Adapters = pointer };
            if (D3DKMTEnumAdapters2(&request) < 0)
            {
                return null;
            }

            count = Math.Min(request.NumAdapters, MaxAdapters);
        }

        var adapters = new List<GraphicsAdapter>((int)count);
        var failed = false;
        for (var i = 0; i < count; i++)
        {
            var info = infos[i];
            uint type = 0;
            var query = new QueryAdapterInfo { Adapter = info.Adapter, Type = AdapterTypeQuery, PrivateDriverData = &type, PrivateDriverDataSize = sizeof(uint) };
            if (D3DKMTQueryAdapterInfo(&query) < 0)
            {
                failed = true;
            }
            else
            {
                adapters.Add(new GraphicsAdapter(((long)info.LuidHighPart << 32) | info.LuidLowPart, type));
            }

            var close = new CloseAdapter { Adapter = info.Adapter };
            _ = D3DKMTCloseAdapter(&close);
        }

        return failed ? null : adapters;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(EnumAdapters2* request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* query);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(CloseAdapter* close);

    /// <summary><c>D3DKMT_ADAPTERINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Adapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    /// <summary><c>D3DKMT_ENUMADAPTERS2</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public AdapterInfo* Adapters;
    }

    /// <summary><c>D3DKMT_QUERYADAPTERINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary><c>D3DKMT_CLOSEADAPTER</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }
}
