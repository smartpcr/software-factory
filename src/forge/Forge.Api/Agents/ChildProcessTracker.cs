// -----------------------------------------------------------------------
// <copyright file="ChildProcessTracker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Windows JobObject wrapper that pins every spawned CLI child (copilot.exe,
/// claude.exe, gemini.exe, ...) to the Forge.Api process lifetime via
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. When Forge.Api exits — clean shutdown,
/// crash, or `taskkill /F` — the OS reaps every process in the job. This is
/// the only mechanism that survives an unclean dispatcher death; without it,
/// hung agent invocations leak as orphans, eventually pinning the binary file
/// handle and breaking subsequent Process.Start calls (observed: 22 stale
/// copilot.exe processes after a hung exploration run).
/// </summary>
internal static class ChildProcessTracker
{
    private static readonly object Sync = new();
    private static IntPtr jobHandle = IntPtr.Zero;
    private static bool initialized;

    /// <summary>
    /// Adds <paramref name="process"/> to the Forge.Api JobObject. No-op on
    /// non-Windows hosts and on processes that have already exited. Failure
    /// is silent — the caller's own cancellation/finally path still kills
    /// the child explicitly; the job is an additional safety net for the
    /// case where the parent dies before that path runs.
    /// </summary>
    public static void AddProcess(Process process)
    {
        if (process is null) return;
        if (!OperatingSystem.IsWindows()) return;
        try { if (process.HasExited) return; }
        catch { return; }

        EnsureJob();
        if (jobHandle == IntPtr.Zero) return;

        try
        {
            AssignProcessToJobObject(jobHandle, process.Handle);
        }
        catch
        {
            // Best-effort. Already-jobbed processes (rare in dev) and racing
            // exits surface here; both are handled by the caller's finally.
        }
    }

    private static void EnsureJob()
    {
        if (initialized) return;
        lock (Sync)
        {
            if (initialized) return;
            initialized = true;
            try
            {
                var name = $"forge-api-children-{Environment.ProcessId}";
                jobHandle = CreateJobObject(IntPtr.Zero, name);
                if (jobHandle == IntPtr.Zero) return;

                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                };
                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = info,
                };

                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(ext, ptr, fDeleteOld: false);
                    SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch
            {
                jobHandle = IntPtr.Zero;
            }
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
