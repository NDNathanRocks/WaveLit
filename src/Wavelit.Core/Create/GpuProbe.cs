using System.Runtime.InteropServices;

namespace Wavelit.Core.Create;

public enum GpuVerdict { Great, Slow, Unsupported }

/// <param name="AdapterIndex">DXGI adapter index; DirectML uses the same numbering for its device id.</param>
public sealed record GpuInfo(int AdapterIndex, string Name, long DedicatedVideoMemory)
{
    public double VideoMemoryGb => DedicatedVideoMemory / (1024.0 * 1024 * 1024);

    /// <summary>SDXL-Lightning in fp16 needs ~7 GB of weights on the GPU; less still runs, but swaps and crawls.</summary>
    public GpuVerdict Verdict => VideoMemoryGb >= 7.5 ? GpuVerdict.Great : VideoMemoryGb >= 3.5 ? GpuVerdict.Slow : GpuVerdict.Unsupported;
}

/// <summary>Finds the best graphics card for local generation (most dedicated video memory) through DXGI.</summary>
public static class GpuProbe
{
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    public static GpuInfo? Best() => All().OrderByDescending(g => g.DedicatedVideoMemory).FirstOrDefault();

    public static IReadOnlyList<GpuInfo> All()
    {
        var list = new List<GpuInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryObj) != 0 || factoryObj is not IDXGIFactory1 factory) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) == 0 && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                        list.Add(new GpuInfo((int)i, desc.Description.TrimEnd('\0'), (long)desc.DedicatedVideoMemory));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return list;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // Only the vtable slots we call are declared with real signatures; the rest keep the order.
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();                                // IDXGIAdapter
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter(); // IDXGIFactory
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        void IsCurrent();
    }
}
