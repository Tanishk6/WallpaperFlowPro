using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WallpaperFlow.Core.Infrastructure;

/// <summary>
/// Puts helper processes (mpv, ffmpeg) in a Windows job object that is closed when WallpaperFlow exits for any reason,
/// including a crash or being ended in Task Manager, so no orphaned renderer keeps playing and using the GPU.
/// </summary>
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Assign(Process p)
    {
        try
        {
            if (Job.Value != IntPtr.Zero && !p.HasExited) AssignProcessToJobObject(Job.Value, p.Handle);
        }
        catch (Exception ex) { Log.Warn("Could not attach helper process to job: " + ex.Message); }
    }

    /// <summary>Ends renderers left behind by an earlier session that could not clean up (e.g. killed during a build).</summary>
    public static void KillOrphans(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        var name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    Log.Info($"Ended orphaned {name} (pid {p.Id})");
                }
            }
            catch { /* another user's process or already gone */ }
            finally { p.Dispose(); }
        }
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size)) { CloseHandle(job); return IntPtr.Zero; }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job; // intentionally never closed: the OS closes it when this process ends, killing the children
        }
        catch { return IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
