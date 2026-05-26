// -----------------------------------------------------------------------
// <copyright file="ChildProcessEnv.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections;
using System.Diagnostics;

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
}
