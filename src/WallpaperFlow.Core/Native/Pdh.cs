using System.Runtime.InteropServices;

namespace WallpaperFlow.Core.Native;

/// <summary>Minimal Performance Data Helper interop (wildcard counter arrays).</summary>
internal static class Pdh
{
    public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    public const uint PDH_MORE_DATA = 0x800007D2, PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; (pad) union { double... } } }
    // On x64: name ptr (8) + CStatus (4) + pad (4) + double (8) = 24 bytes.
    private const int ItemSize = 24;

    public readonly record struct Item(string Name, double Value);

    public static List<Item> ReadArray(nint counter)
    {
        var result = new List<Item>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, 0);
        if (status != PDH_MORE_DATA || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer);
            if (status != 0) return result;
            for (int i = 0; i < count; i++)
            {
                var basePtr = buffer + i * ItemSize;
                var namePtr = Marshal.ReadIntPtr(basePtr);
                var cstatus = (uint)Marshal.ReadInt32(basePtr + 8);
                if (cstatus != PDH_CSTATUS_VALID_DATA && cstatus != PDH_CSTATUS_NEW_DATA) continue;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(basePtr + 16));
                result.Add(new Item(Marshal.PtrToStringUni(namePtr) ?? "", value));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
}
