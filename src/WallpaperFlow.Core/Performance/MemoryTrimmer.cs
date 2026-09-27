using System.Runtime;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Performance;

/// <summary>Returns memory to the OS after the UI is closed or a large operation finishes.</summary>
public static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Win32.TrimWorkingSet(Win32.GetCurrentProcess());
    }
}
