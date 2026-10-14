using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnionHopV3.Core.Platform.Windows;

/// <summary>
/// Ties OnionHop's engine processes (Tor, Arti, ArtiHop, and through them their pluggable
/// transports) to OnionHop itself. They join a job object that Windows closes when OnionHop exits,
/// for whatever reason, and closing it ends every process in it. A normal exit stops them anyway;
/// this covers the exits that skip that cleanup (End task, a crash, an exception during shutdown),
/// which otherwise left tor.exe running with its SOCKS port open (#87).
/// </summary>
internal static class WindowsChildProcessJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    // Never closed on purpose: Windows closes it when OnionHop's process ends, which is the trigger.
    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    /// <summary>Put <paramref name="process"/> (and the processes it starts later) in the job. No-op off Windows.</summary>
    public static void Track(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var job = Job.Value;
            if (job != IntPtr.Zero && !process.HasExited)
            {
                AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch
        {
            // Best effort: without the job the process is still stopped on a normal exit.
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JobObjectExtendedLimitInfo
        {
            BasicLimitInformation = new JobObjectBasicLimitInfo { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                return job;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        CloseHandle(job);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInfo
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInfo BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
