// -----------------------------------------------------------------------
// <copyright file="ChildProcessEnv.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Deterministic environment isolation for every child process Forge spawns —
/// the Copilot CLI/SDK server, <c>dotnet build/test/restore</c>, cargo / npm /
/// go, etc. <b>Child build/agent processes MUST NOT inherit Forge.Api's own
/// process environment.</b>
///
/// <para>Forge.Api calls <c>MSBuildLocator.RegisterDefaults()</c> at startup
/// (needed for the in-process Roslyn <c>MSBuildWorkspace</c> used by impacted-
/// test selection), which injects MSBuild SDK-resolver vars
/// (<c>MSBUILD_EXE_PATH</c>, <c>MSBuildSDKsPath</c>, <c>MSBuildExtensionsPath*</c>)
/// pinned to Forge's own net8 SDK. A child <c>dotnet</c> that inherits them is
/// forced to resolve SDKs against that SDK's <c>Sdks</c> dir — which lacks the
/// repo's build SDK (e.g. <c>Microsoft.Build.Traversal</c>) — and dies during
/// evaluation with <c>MSB4276</c>. Root cause of local:UPDATE-HISTORY-IN-CL
/// stage-1.1 burning the full 24-min budget × N iters at score 0 (2026-05-25);
/// the operator's manual build in a fresh shell succeeds precisely because a
/// fresh shell never has those vars set.</para>
///
/// <para><b>The guarantee is structural, enforced in code — never via the
/// agent prompt.</b> Capture the pristine process environment ONCE at the very
/// top of <c>Program.cs</c> (BEFORE <c>RegisterDefaults()</c> or any other in-
/// process mutation) and rebuild every child process's environment from that
/// snapshot. A denylist of the known SDK-resolver vars is stripped at snapshot
/// time as belt-and-suspenders (covers a launching shell that was itself a VS
/// dev prompt with the vars pre-set). Forge.Api's OWN process keeps the
/// registered MSBuild — its in-process Roslyn needs it; only its CHILDREN get
/// the clean env.</para>
/// </summary>
public static class ChildProcessEnv
{
    /// <summary>
    /// MSBuild SDK-resolver vars injected by
    /// <c>MSBuildLocator.RegisterDefaults()</c>. Always stripped from the child
    /// baseline. Public so the isolation test can lock the list verbatim.
    /// </summary>
    public static readonly IReadOnlyList<string> DeniedVars = new[]
    {
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
    };

    private static IReadOnlyDictionary<string, string>? baseline;

    /// <summary>True when <paramref name="name"/> must never reach a child process.</summary>
    public static bool IsDenied(string name)
    {
        foreach (var d in DeniedVars)
        {
            if (string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Snapshot the current environment minus the denied vars. Used by
    /// <see cref="CaptureBaseline"/> (early, pristine) and as the lazy fallback
    /// so the denylist guarantee holds even when capture was skipped (tests).
    /// </summary>
    private static IReadOnlyDictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v && !IsDenied(k))
            {
                d[k] = v;
            }
        }

        return d;
    }

    /// <summary>
    /// Capture the pristine process environment ONCE, at the top of
    /// <c>Program.cs</c>, BEFORE any in-process env mutation. Idempotent.
    /// </summary>
    public static void CaptureBaseline() => baseline ??= Snapshot();

    /// <summary>
    /// The clean child-process environment: the pre-captured pristine baseline
    /// when available, otherwise a fresh denied-stripped snapshot.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Baseline => baseline ?? Snapshot();

    /// <summary>
    /// Materialise the clean baseline as a fresh mutable dictionary (for
    /// launchers that pass an explicit env dict, e.g. the Copilot SDK's
    /// <c>CopilotClientOptions.Environment</c>).
    /// </summary>
    public static Dictionary<string, string> ToDictionary() =>
        new(Baseline, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replace a <see cref="ProcessStartInfo"/>'s inherited (possibly polluted)
    /// environment with the clean baseline. Call on every build/test/agent
    /// subprocess so it runs with a fresh-shell environment, not Forge.Api's.
    /// </summary>
    public static void Apply(ProcessStartInfo psi)
    {
        psi.Environment.Clear();
        foreach (var kv in Baseline)
        {
            psi.Environment[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Defense-in-depth: remove only the denied vars from a
    /// <see cref="ProcessStartInfo"/> that intentionally keeps an otherwise
    /// custom-rebuilt environment (e.g. <c>BuildValidationWorker</c>'s
    /// registry-derived interactive shell, which reconstructs PATH from the
    /// registry rather than from the baseline).
    /// </summary>
    public static void StripDenied(ProcessStartInfo psi)
    {
        foreach (var d in DeniedVars)
        {
            psi.Environment.Remove(d);
        }
    }

    /// <summary>
    /// Refresh <c>Path</c> and <c>PSModulePath</c> on a <see cref="ProcessStartInfo"/>
    /// by composing them from the live Machine + User registry environment —
    /// the same way Windows builds them for a NEW interactive logon shell.
    /// Use AFTER <see cref="Apply(ProcessStartInfo)"/> when the child must be
    /// able to resolve tools that were installed (or whose PATH entries were
    /// added) AFTER Forge.Api started — e.g. a fresh <c>go</c> install in
    /// <c>C:\Program Files\Go\bin</c> when Forge.Api was launched from a shell
    /// that hadn't yet picked up that Machine PATH entry.
    ///
    /// <para>Symptom this fixes: the per-iter gate subprocess emits
    /// <c>'go' is not recognized as an internal or external command</c> and
    /// the workstream loops forever even though <c>go</c> is installed and
    /// the operator's own shell finds it. Cause: the baseline captured by
    /// <see cref="CaptureBaseline"/> is the launching shell's PATH at
    /// Forge.Api start; if that shell was stale (predates a toolchain
    /// install, or was launched without the install's PATH entry), every
    /// child inherits the stale PATH for the lifetime of the Forge.Api
    /// process.</para>
    ///
    /// <para>Same pattern as
    /// <c>BuildValidationWorker.ApplyInteractiveEnvironment</c> — extracted
    /// here so the gate path (<c>StoryBuildRunner</c>) and the validate path
    /// share one implementation. <c>Path</c> and <c>PSModulePath</c> are
    /// rebuilt as <c>Machine</c> then <c>User</c> concatenated (the logon-
    /// session order); REG_EXPAND_SZ values are expanded. Best-effort: a
    /// registry read failure leaves the existing (baseline-captured) value
    /// in place rather than throwing.</para>
    /// </summary>
    public static void OverlayLiveRegistryPath(ProcessStartInfo psi)
    {
        var path = ReadRegistryComposed("Path");
        if (!string.IsNullOrEmpty(path)) psi.Environment["Path"] = path;
        var psModulePath = ReadRegistryComposed("PSModulePath");
        if (!string.IsNullOrEmpty(psModulePath)) psi.Environment["PSModulePath"] = psModulePath;
    }

    private static string? ReadRegistryComposed(string name)
    {
        var machine = ReadRegistryOne(EnvironmentVariableTarget.Machine, name);
        var user = ReadRegistryOne(EnvironmentVariableTarget.User, name);
        return (machine, user) switch
        {
            (null, null) => null,
            (string m, null) => m,
            (null, string u) => u,
            (string m, string u) => m.TrimEnd(';') + ";" + u,
        };
    }

    private static string? ReadRegistryOne(EnvironmentVariableTarget target, string name)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(name, target);
            if (string.IsNullOrEmpty(raw)) return null;
            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ensure a POSIX <c>sh</c> is resolvable on the child's <c>Path</c> so a
    /// gate-launched GNU <c>make</c> runs recipe lines through <c>sh</c> instead
    /// of falling back to <c>cmd.exe</c>.
    ///
    /// <para>On Windows, GNU make uses <c>sh.exe</c> when one is found on PATH and
    /// otherwise runs each recipe line via <c>cmd.exe</c>. Under <c>cmd.exe</c> a
    /// POSIX recipe line such as <c>mkdir -p bin</c> fails INSTANTLY — cmd's
    /// <c>mkdir</c> has no <c>-p</c>, so it errors and trips
    /// <c>make: *** Error 1</c> before any compile/scan runs. The git installer
    /// puts <c>git.exe</c> on the system PATH (<c>…\Git\cmd</c>) but NOT its MSYS2
    /// coreutils (<c>…\Git\usr\bin</c>, where <c>sh</c>/<c>mkdir</c> live), so a
    /// gate that shells out to <c>make</c> gets <c>cmd.exe</c> and dies on the
    /// first POSIX-ism — while the engineer agent (running <c>make</c> via
    /// git-bash, with <c>/usr/bin</c> on PATH) passes. The asymmetry makes such a
    /// gate UNWINNABLE and UNDIAGNOSABLE from the agent's side. (Root cause:
    /// code-intelligence 3D-RENDER stage 3.1, 2026-06-26 — <c>make probe-self</c>
    /// exited 2 in 0.14s on <c>mkdir -p bin</c> under cmd.exe, every iter, after
    /// the agent had already fixed the real code and verified it green via sh.)</para>
    ///
    /// <para>Best-effort: appends git's <c>usr\bin</c> to the END of PATH (so
    /// Windows System32 tools like <c>find</c>/<c>sort</c> still win for bare
    /// names — only <c>sh</c>+coreutils, which have no Windows <c>.exe</c>, become
    /// newly resolvable). No-op when a <c>sh.exe</c> is already on PATH or no
    /// git/MSYS2 <c>usr\bin</c> can be located.</para>
    /// </summary>
    public static void EnsurePosixShellOnPath(ProcessStartInfo psi)
    {
        var path = psi.Environment.TryGetValue("Path", out var p) ? (p ?? string.Empty) : string.Empty;
        var usrBin = ComputePosixUsrBinToAppend(path, static (dir, file) =>
        {
            try
            {
                return File.Exists(Path.Combine(dir, file));
            }
            catch
            {
                return false;
            }
        });
        if (usrBin is null)
        {
            return;
        }

        psi.Environment["Path"] = string.IsNullOrEmpty(path) ? usrBin : path.TrimEnd(';') + ";" + usrBin;
    }

    /// <summary>
    /// Pure core of <see cref="EnsurePosixShellOnPath"/> — given a PATH string and
    /// a <paramref name="probe"/> that reports whether a directory contains a file,
    /// return the git <c>usr\bin</c> directory to append, or <c>null</c> when a
    /// <c>sh.exe</c> is already resolvable or none can be derived. Filesystem-free
    /// so it is deterministically unit-testable.
    /// </summary>
    public static string? ComputePosixUsrBinToAppend(string? path, Func<string, string, bool> probe)
    {
        var dirs = (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);

        // Already have a POSIX sh on PATH (covers usr\bin already being present)?
        foreach (var d in dirs)
        {
            if (probe(d.Trim(), "sh.exe"))
            {
                return null;
            }
        }

        // Derive from git.exe on PATH: <gitRoot>\cmd\git.exe → <gitRoot>\usr\bin.
        foreach (var d in dirs)
        {
            var dir = d.Trim();
            if (!probe(dir, "git.exe"))
            {
                continue;
            }

            var gitRoot = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(gitRoot))
            {
                continue;
            }

            var usrBin = Path.Combine(gitRoot, "usr", "bin");
            if (probe(usrBin, "sh.exe"))
            {
                return usrBin;
            }
        }

        return null;
    }
}
