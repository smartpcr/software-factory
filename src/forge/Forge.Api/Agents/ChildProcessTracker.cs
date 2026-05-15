// -----------------------------------------------------------------------
// <copyright file="ChildProcessTracker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Windows JobObject wrapper that pins every descendant of Forge.Api to the
/// Forge.Api process lifetime via JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. When
/// Forge.Api exits — clean shutdown, crash, or `taskkill /F` — the OS reaps
/// every process in the job. This is the only mechanism that survives an
/// unclean dispatcher death; without it, hung agent invocations leak as
/// orphans, eventually pinning the binary file handle and breaking
/// subsequent Process.Start calls (observed: 22 stale copilot.exe processes
/// after a hung exploration run).
/// <para>
/// At init we assign Forge.Api itself to the job, so every child it spawns
/// (and every grandchild) automatically inherits the job — including the
/// copilot.exe that the in-process Copilot SDK launches internally, which
/// our explicit AddProcess path never sees. Explicit AddProcess calls remain
/// as a belt-and-suspenders safety net for systems where the self-assign
/// failed (already in a non-nestable parent job, etc.).
/// </para>
/// </summary>
internal static class ChildProcessTracker
{
    private static readonly object Sync = new();
    private static IntPtr jobHandle = IntPtr.Zero;
    private static bool initialized;
    private static bool selfAssigned;
    private static string? lastError;

    /// <summary>
    /// True when the JobObject was created AND Forge.Api itself was
    /// assigned to it. When both are true, every descendant Forge.Api
    /// spawns (including SDK-internal copilot.exe) automatically inherits
    /// the job and is reaped on Forge.Api exit. When false, only
    /// processes explicitly registered via <see cref="AddProcess"/> are
    /// in the job — the SDK-spawned ones leak unless
    /// <see cref="CopilotServerManager"/>'s explicit ownership path
    /// kills them on dispose.
    /// </summary>
    public static bool IsActive => initialized && jobHandle != IntPtr.Zero && selfAssigned;

    /// <summary>
    /// Last initialisation error in human-readable form, or <c>null</c>
    /// if init succeeded. Surfaced for health/diagnostic endpoints.
    /// </summary>
    public static string? LastError => lastError;

    /// <summary>
    /// Initialise the tracker eagerly at process startup. Safe to call
    /// multiple times — subsequent calls are no-ops. Calling this from
    /// Program.cs before any agent runs ensures children spawned by the
    /// Copilot SDK during its own start-up are inside the job.
    /// </summary>
    public static void Initialize() => EnsureJob();

    /// <summary>
    /// Adds <paramref name="process"/> to the Forge.Api JobObject. No-op on
    /// non-Windows hosts and on processes that have already exited. Failure
    /// is silent — the caller's own cancellation/finally path still kills
    /// the child explicitly; the job is an additional safety net for the
    /// case where the parent dies before that path runs. Usually redundant
    /// once self-assign succeeds (the child inherits via the parent), but
    /// kept for defence in depth.
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
            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                var err = Marshal.GetLastWin32Error();
                // Win32 0x05 == ERROR_ACCESS_DENIED → process already in a
                // non-nestable job. Caller's explicit kill still applies.
                Console.Error.WriteLine(
                    $"[Forge] ChildProcessTracker.AddProcess: AssignProcessToJobObject(PID {process.Id}) failed: Win32 error {err}");
            }
        }
        catch (Exception ex)
        {
            // Best-effort. Already-jobbed processes (rare in dev) and racing
            // exits surface here; both are handled by the caller's finally.
            Console.Error.WriteLine(
                $"[Forge] ChildProcessTracker.AddProcess: PID {process.Id} threw: {ex.Message}");
        }
    }

    private static void EnsureJob()
    {
        if (initialized) return;
        lock (Sync)
        {
            if (initialized) return;
            initialized = true;
            if (!OperatingSystem.IsWindows())
            {
                lastError = "non-Windows host";
                return;
            }

            try
            {
                var name = $"forge-api-children-{Environment.ProcessId}";
                jobHandle = CreateJobObject(IntPtr.Zero, name);
                if (jobHandle == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    lastError = $"CreateJobObject failed: Win32 error {err}";
                    Console.Error.WriteLine($"[Forge] ChildProcessTracker: {lastError}");
                    return;
                }

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
                    if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                    {
                        var err = Marshal.GetLastWin32Error();
                        lastError = $"SetInformationJobObject failed: Win32 error {err}";
                        Console.Error.WriteLine($"[Forge] ChildProcessTracker: {lastError}");
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                // Self-assign so descendants inherit. On Win8+ this works
                // even when Forge.Api is already in a parent job (nested
                // jobs); on older OSes or restricted parent jobs it fails
                // with ACCESS_DENIED — silent fallback to per-child Add.
                try
                {
                    using var self = Process.GetCurrentProcess();
                    if (AssignProcessToJobObject(jobHandle, self.Handle))
                    {
                        selfAssigned = true;
                        Console.WriteLine(
                            $"[Forge] ChildProcessTracker: JobObject '{name}' active (kill-on-close, self-assigned PID {Environment.ProcessId}).");
                    }
                    else
                    {
                        var err = Marshal.GetLastWin32Error();
                        lastError = $"Self-AssignProcessToJobObject failed: Win32 error {err} (likely ERROR_ACCESS_DENIED — Forge.Api is in a non-nestable parent job; SDK-spawned copilot.exe will rely on CopilotServerManager explicit-kill on shutdown).";
                        Console.Error.WriteLine($"[Forge] ChildProcessTracker: {lastError}");
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"Self-AssignProcessToJobObject threw: {ex.Message}";
                    Console.Error.WriteLine($"[Forge] ChildProcessTracker: {lastError}");
                }
            }
            catch (Exception ex)
            {
                jobHandle = IntPtr.Zero;
                lastError = $"EnsureJob threw: {ex}";
                Console.Error.WriteLine($"[Forge] ChildProcessTracker: {lastError}");
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
