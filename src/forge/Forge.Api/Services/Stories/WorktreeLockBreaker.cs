// -----------------------------------------------------------------------
// <copyright file="WorktreeLockBreaker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Uses the Windows Restart Manager API to find — and kill — the processes
/// that hold open file handles inside a worktree, so the worktree can be
/// deleted.
///
/// <para><b>Why.</b> A worktree's <c>.forge/memory/session-store.db</c> has
/// the SAME lifespan as the workstream: clearing the worktree (restart,
/// sloppy-reset, merge cleanup) MUST be able to remove it. But the shared,
/// long-lived <c>copilot.exe</c> server keeps that SQLite file open for its
/// process lifetime — the SDK does not release it when Forge disposes the
/// logical session — so <c>Directory.Delete</c> fails with "being used by
/// another process". Other transient holders are orphaned <c>testhost</c> /
/// <c>dotnet</c> from a timed-out test gate (CWD = worktree). The directive
/// (2026-05-30) is: when we clear the worktree, ALWAYS break the lock.</para>
///
/// <para><b>How.</b> Restart Manager (<c>RmStartSession</c> →
/// <c>RmRegisterResources</c> → <c>RmGetList</c>) reports exactly which PIDs
/// lock the registered files — no <c>handle.exe</c> needed. We register the
/// worktree's lock-prone files (the <c>.forge/memory</c> session DBs) and kill
/// the reported holders that are agent infrastructure (the Copilot server,
/// node loader, testhost/dotnet/git/msbuild). We NEVER kill Forge.Api itself
/// and deliberately skip non-infra processes such as an editor
/// (<c>devenv</c>/<c>code</c>) or VS's own <c>copilot-language-server</c> —
/// those are logged so the operator can intervene rather than losing IDE
/// state to an automated kill.</para>
/// </summary>
internal static class WorktreeLockBreaker
{
    /// <summary>
    /// Finds processes locking files under <paramref name="worktreePath"/>
    /// and kills the agent-infrastructure ones. Returns the number of
    /// processes killed. No-op (returns 0) off-Windows or when nothing locks
    /// the worktree. Never throws — best-effort lock relief.
    /// </summary>
    public static int BreakLocks(string worktreePath, ILogger? log)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath)) return 0;

        var files = CollectLockProneFiles(worktreePath);
        if (files.Count == 0) return 0;

        IReadOnlyList<(int Pid, string Name)> lockers;
        try { lockers = FindLockingProcesses(files); }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Restart Manager probe failed for {Path} (non-fatal).", worktreePath);
            return 0;
        }

        var killed = 0;
        foreach (var (pid, rmName) in lockers)
        {
            if (pid == Environment.ProcessId) continue; // never suicide

            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch { continue; } // already gone

            using (proc)
            {
                string name;
                try { name = proc.ProcessName; }
                catch { continue; }

                if (!IsKillableInfraLocker(name))
                {
                    log?.LogWarning(
                        "Worktree {Path} is locked by non-infra process {Name} (PID {Pid}, RM='{Rm}'); "
                        + "NOT auto-killing it. Close it manually if the worktree won't clear.",
                        worktreePath, name, pid, rmName);
                    continue;
                }

                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                    killed++;
                    log?.LogWarning(
                        "Killed process {Name} (PID {Pid}) that was locking worktree {Path} so it can be cleared "
                        + "(session-store.db / test-gate orphan).",
                        name, pid, worktreePath);
                }
                catch (Exception ex)
                {
                    log?.LogWarning(ex, "Failed to kill locking process {Name} (PID {Pid}) for {Path}.", name, pid, worktreePath);
                }
            }
        }
        return killed;
    }

    /// <summary>
    /// The lock-prone files to register with Restart Manager: the Copilot
    /// session store under <c>.forge/memory</c> (the known offender) plus any
    /// other <c>*.db*</c> there. Kept small — RM only needs a representative
    /// handle to surface the locking process.
    /// </summary>
    private static List<string> CollectLockProneFiles(string worktreePath)
    {
        var files = new List<string>();
        var memDir = Path.Combine(worktreePath, ".forge", "memory");
        try
        {
            if (Directory.Exists(memDir))
            {
                files.AddRange(Directory.EnumerateFiles(memDir, "*.db", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(memDir, "*.db-wal", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(memDir, "*.db-shm", SearchOption.AllDirectories));
            }
        }
        catch { /* best-effort enumeration */ }
        return files;
    }

    /// <summary>
    /// Agent-infrastructure process names that are safe to kill when they
    /// hold a worktree open. Excludes editors and VS's own
    /// <c>copilot-language-server</c> (matched by the exact <c>copilot</c>
    /// name, not a substring, so the language server is left alone).
    /// </summary>
    private static bool IsKillableInfraLocker(string processName)
    {
        var n = processName.Trim();
        return n.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            || n.Equals("node", StringComparison.OrdinalIgnoreCase)
            || n.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || n.Equals("testhost", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
            || n.Equals("vstest.console", StringComparison.OrdinalIgnoreCase)
            || n.Equals("MSBuild", StringComparison.OrdinalIgnoreCase)
            || n.Equals("git", StringComparison.OrdinalIgnoreCase);
    }

    // ── Restart Manager interop ────────────────────────────────────────────

    private static IReadOnlyList<(int Pid, string Name)> FindLockingProcesses(IReadOnlyList<string> files)
    {
        const int RmRebootReasonNone = 0;
        var key = new string('\0', CchRmSessionKey + 1);
        var rc = RmStartSession(out var session, 0, key);
        if (rc != 0) return Array.Empty<(int, string)>();

        try
        {
            rc = RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null);
            if (rc != 0) return Array.Empty<(int, string)>();

            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = RmRebootReasonNone;

            // First call discovers how many entries are needed.
            rc = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);
            if (pnProcInfoNeeded == 0) return Array.Empty<(int, string)>();

            var info = new RM_PROCESS_INFO[pnProcInfoNeeded];
            pnProcInfo = pnProcInfoNeeded;
            rc = RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, info, ref lpdwRebootReasons);
            if (rc != 0) return Array.Empty<(int, string)>();

            var result = new List<(int, string)>((int)pnProcInfo);
            for (var i = 0; i < pnProcInfo; i++)
            {
                result.Add((info[i].Process.dwProcessId, info[i].strAppName ?? string.Empty));
            }
            return result;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
