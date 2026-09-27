using System.Runtime.InteropServices;
using Microsoft.Win32;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Rendering;

public sealed record GpuAdapter(string Name, uint VendorId, long DedicatedMb, bool IsSoftware, long Luid)
{
    public string Vendor => VendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft", _ => $"0x{VendorId:X4}" };
}

/// <summary>
/// DirectX integration helpers: enumerate DXGI adapters (for the adapter picker, --d3d11-adapter) and set
/// Windows' per-app GPU preference for the renderer (so laptops can keep wallpapers on the iGPU).
/// </summary>
public static unsafe class Gpu
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);
    private static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    public static IReadOnlyList<GpuAdapter> Adapters()
    {
        var list = new List<GpuAdapter>();
        try
        {
            if (CreateDXGIFactory1(ref IID_IDXGIFactory1, out var factory) != 0 || factory == 0) return list;
            try
            {
                var fvt = *(nint**)factory;
                var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)fvt[12];
                for (uint i = 0; ; i++)
                {
                    nint adapter;
                    int hr = enumAdapters1(factory, i, &adapter);
                    if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) break;
                    try
                    {
                        var avt = *(nint**)adapter;
                        var getDesc1 = (delegate* unmanaged<nint, DXGI_ADAPTER_DESC1*, int>)avt[10];
                        DXGI_ADAPTER_DESC1 d;
                        if (getDesc1(adapter, &d) == 0)
                            list.Add(new GpuAdapter(new string(d.Description).TrimEnd('\0'), d.VendorId, (long)(d.DedicatedVideoMemory / 1048576), (d.Flags & 2) != 0, d.AdapterLuid));
                    }
                    finally { Release(adapter); }
                }
            }
            finally { Release(factory); }
        }
        catch (Exception ex) { Log.Warn("DXGI enumeration failed: " + ex.Message); }
        return list;
    }

    private static void Release(nint unk)
    {
        if (unk == 0) return;
        var vt = *(nint**)unk;
        ((delegate* unmanaged<nint, uint>)vt[2])(unk);
    }

    private const string PreferenceKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>0 = let Windows decide, 1 = power saving, 2 = high performance.</summary>
    public static void SetPreference(string exePath, int preference)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PreferenceKey);
            if (preference <= 0) k.DeleteValue(exePath, false);
            else k.SetValue(exePath, $"GpuPreference={preference};");
        }
        catch (Exception ex) { Log.Warn("Could not set GPU preference: " + ex.Message); }
    }
}
