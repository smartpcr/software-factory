// -----------------------------------------------------------------------
// <copyright file="WindowsProcessTree.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Runtime.InteropServices;

/// <summary>
/// Windows-only Toolhelp32 wrapper that snapshots the live process table
/// and walks parent/child relationships without taking a NuGet dependency
/// on <c>System.Management</c>. Used by <see cref="CopilotServerManager"/>
/// to identify the SDK-spawned <c>copilot.exe</c> root after
/// <c>CopilotClient.StartAsync</c> returns — the SDK does not expose the
/// underlying <see cref="System.Diagnostics.Process"/> handle, so we have
/// to discover it ourselves to do explicit lifecycle management.
/// </summary>
internal static class WindowsProcessTree
{
    /// <summary>One row from a Toolhelp32 snapshot.</summary>
    public readonly record struct ProcessRow(int Pid, int ParentPid, string Name);

    /// <summary>
    /// Returns the live process table at this instant, or empty list on
    /// non-Windows / API failure. Each row contains PID, parent PID, and
    /// executable basename (no path). Snapshot is best-effort: a process
    /// can exit between enumeration and the caller's use.
    /// </summary>
    public static IReadOnlyList<ProcessRow> Snapshot()
    {
        var rows = new List<ProcessRow>();
        if (!OperatingSystem.IsWindows()) return rows;

        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return rows;
        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(),
            };

            if (!Process32First(snap, ref entry)) return rows;
            do
            {
                rows.Add(new ProcessRow(
                    Pid: (int)entry.th32ProcessID,
                    ParentPid: (int)entry.th32ParentProcessID,
                    Name: entry.szExeFile ?? string.Empty));
            }
            while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return rows;
    }

    /// <summary>
    /// BFS-walk descendants of <paramref name="rootPid"/> in the snapshot
    /// and return the topmost (closest to root) <c>copilot.exe</c> whose
    /// PID is not in <paramref name="baselinePids"/>. Returns <c>null</c>
    /// if no such descendant exists. The "topmost newly-spawned" rule
    /// handles the case where the SDK shells via an intermediate
    /// (cmd.exe / node.exe) before invoking copilot.exe — we want the
    /// root of the Copilot subtree, not the shim.
    /// </summary>
    public static int? FindNewCopilotDescendant(
        int rootPid,
        IReadOnlySet<int> baselinePids,
        IReadOnlyList<ProcessRow>? snapshot = null)
    {
        var rows = snapshot ?? Snapshot();
        if (rows.Count == 0) return null;

        // Index parent → children once for O(N) traversal.
        var byParent = new Dictionary<int, List<ProcessRow>>(rows.Count);
        foreach (var r in rows)
        {
            if (!byParent.TryGetValue(r.ParentPid, out var list))
            {
                list = new List<ProcessRow>();
                byParent[r.ParentPid] = list;
            }
            list.Add(r);
        }

        // BFS — first new copilot.exe encountered wins (topmost in chain).
        var bfs = new Queue<int>();
        var visited = new HashSet<int> { rootPid };
        bfs.Enqueue(rootPid);
        while (bfs.TryDequeue(out var pid))
        {
            if (!byParent.TryGetValue(pid, out var children)) continue;
            foreach (var child in children)
            {
                if (!visited.Add(child.Pid)) continue;
                if (IsCopilotExe(child.Name) && !baselinePids.Contains(child.Pid))
                {
                    return child.Pid;
                }
                bfs.Enqueue(child.Pid);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the PIDs of every <c>copilot.exe</c> in the snapshot. Used
    /// by <see cref="CopilotServerManager"/> to baseline the table before
    /// spawning the SDK server so we can later identify the new spawn.
    /// </summary>
    public static IReadOnlySet<int> SnapshotCopilotPids(
        IReadOnlyList<ProcessRow>? snapshot = null)
    {
        var rows = snapshot ?? Snapshot();
        var set = new HashSet<int>();
        foreach (var r in rows)
        {
            if (IsCopilotExe(r.Name)) set.Add(r.Pid);
        }
        return set;
    }

    private static bool IsCopilotExe(string name) =>
        string.Equals(name, "copilot.exe", StringComparison.OrdinalIgnoreCase);

    // ── P/Invoke ──────────────────────────────────────────────────────────

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int MAX_PATH = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
