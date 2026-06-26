// -----------------------------------------------------------------------
// <copyright file="ChildProcessJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// A <b>disposable, per-run</b> Windows JobObject scoped to a SINGLE child
/// process tree — e.g. one build/test gate invocation
/// (<see cref="Forge.Api.Services.Stories.StoryBuildRunner"/>). Created
/// kill-on-close: assign the gate's top process to it immediately after launch
/// and <see cref="Dispose"/> (or a host crash) terminates the ENTIRE tree.
///
/// <para><b>Why this exists alongside <see cref="ChildProcessTracker"/>.</b>
/// <see cref="ChildProcessTracker"/> is a single, process-wide job that reaps
/// every descendant only when <i>Forge.Api itself</i> exits. That does nothing
/// for trees that orphan <i>while Forge.Api keeps running</i>: a gate run shells
/// out <c>cmd → init.cmd → make → sh → go → compile/link workers</c>, and when
/// the top <c>cmd.exe</c> exits, any still-live grandchildren re-parent and
/// linger. <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c>
/// can't reach them once their immediate parent is gone. Over a day of gate runs
/// these orphans accumulate into hundreds of stranded <c>dotnet</c>/<c>conhost</c>/
/// <c>go</c> processes that exhaust the host session's desktop-heap / handle
/// budget — at which point freshly-spawned gate children fail to initialize with
/// <c>0xC0000142</c> (<c>STATUS_DLL_INIT_FAILED</c>) and every build gate breaks.
/// A job per gate run, disposed when the run finishes, reaps that run's whole
/// tree deterministically — orphans included — closing the leak.</para>
///
/// <para><b>Nested-job note.</b> On Windows 8+/Server 2012+ a process can belong
/// to multiple nested jobs, so assigning a child that already inherited
/// <see cref="ChildProcessTracker"/>'s process-wide job into this per-run job is
/// allowed; closing this job terminates only this job's members. On older OSes /
/// restricted parent jobs the assign fails with <c>ERROR_ACCESS_DENIED</c> and
/// this falls back to a no-op (the caller's explicit tree-kill still applies).
/// Best-effort throughout: any failure leaves behavior no worse than before.</para>
///
/// <para><b>Race caveat.</b> The child is assigned just AFTER
/// <see cref="Process.Start(ProcessStartInfo)"/> returns, so a grandchild spawned in the
/// sub-millisecond window before assignment would not inherit the job. This
/// mirrors <see cref="ChildProcessTracker.AddProcess"/> and is acceptable: the
/// dominant leak is the long-lived tree, which is fully captured.</para>
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private IntPtr jobHandle;
    private bool disposed;

    private ChildProcessJob(IntPtr handle)
    {
        this.jobHandle = handle;
    }

    /// <summary>
    /// True when a kill-on-close job was created AND the supplied process was
    /// successfully assigned to it. When false the job is inert and the caller
    /// should rely on its own <c>Process.Kill(entireProcessTree: true)</c> path.
    /// </summary>
    public bool IsActive => this.jobHandle != IntPtr.Zero;

    /// <summary>
    /// Create a kill-on-close job and assign <paramref name="process"/> to it.
    /// Returns an inert (already-disposed-equivalent) instance on non-Windows
    /// hosts, on an already-exited process, or on any Win32 failure — callers
    /// always get a non-null <see cref="IDisposable"/> they can wrap in
    /// <c>using</c> without null checks. Never throws.
    /// </summary>
    public static ChildProcessJob TryCreateAndAssign(Process process)
    {
        if (process is null || !OperatingSystem.IsWindows())
        {
            return new ChildProcessJob(IntPtr.Zero);
        }

        try { if (process.HasExited) return new ChildProcessJob(IntPtr.Zero); }
        catch { return new ChildProcessJob(IntPtr.Zero); }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateJobObject(IntPtr.Zero, lpName: null);
            if (handle == IntPtr.Zero)
            {
                return new ChildProcessJob(IntPtr.Zero);
            }

            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(ext, ptr, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(handle);
                    return new ChildProcessJob(IntPtr.Zero);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                // ERROR_ACCESS_DENIED (5) → already in a non-nestable job, or an
                // OS without nested-job support. Close our job (kill-on-close
                // reaps nothing since the assign failed) and go inert.
                CloseHandle(handle);
                return new ChildProcessJob(IntPtr.Zero);
            }

            return new ChildProcessJob(handle);
        }
        catch
        {
            if (handle != IntPtr.Zero)
            {
                try { CloseHandle(handle); } catch { /* best-effort */ }
            }
            return new ChildProcessJob(IntPtr.Zero);
        }
    }

    /// <summary>
    /// Close the job handle. With <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> this
    /// terminates every process still in the job — the gate run's whole tree,
    /// including re-parented orphans. Idempotent and exception-safe.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        var h = this.jobHandle;
        this.jobHandle = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            try { CloseHandle(h); } catch { /* best-effort */ }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
