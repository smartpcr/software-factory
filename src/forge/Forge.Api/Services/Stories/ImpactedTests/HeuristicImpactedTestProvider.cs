// -----------------------------------------------------------------------
// <copyright file="HeuristicImpactedTestProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests;

/// <summary>
/// Phase 59 Stage C — fallback provider that wraps the original
/// Stage A heuristic (filename suffix + parent-directory name +
/// `Microsoft.NET.Test.Sdk` content scan + reverse-ProjectReference
/// closure). Kept as the last resort in the chain so an operator
/// without a language server still gets partial narrowing.
///
/// <para>The actual algorithm still lives in
/// <see cref="ImpactedTestResolver.Resolve"/> (kept as a static
/// facade so existing call sites + the 21 Stage A tests don't move).
/// This provider just adapts the static call into the chain
/// interface + stamps the result's <c>Mode</c> as `"heuristic"` so
/// the gate event payload can show the operator which path
/// answered.</para>
///
/// <para>Always returns true from <see cref="CanHandleAsync"/> —
/// the heuristic is the safety net; the chain orders precise
/// providers (Roslyn, LSP) ahead of it so they get first crack at
/// any workspace they can handle.</para>
/// </summary>
public sealed class HeuristicImpactedTestProvider : IImpactedTestProvider
{
    public string Name => "heuristic";

    public Task<bool> CanHandleAsync(string workspace, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<ImpactedTestResolverResult?> ResolveAsync(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        CancellationToken ct)
    {
        // Delegate to the static facade — it runs the filename +
        // directory + content classifier + reverse-ref graph + 80%
        // whole-suite fallback. The `impactedOnly` argument is true
        // here because the chain itself only gets called when the
        // caller has already resolved the impacted-only setting
        // (per-repo override beats global config).
        var result = ImpactedTestResolver.Resolve(
            workspace: workspace,
            changedFiles: changedFiles,
            originalCommand: originalCommand,
            impactedOnly: true);

        // Stamp the mode so downstream consumers see this came
        // from the heuristic (the facade defaults Mode to
        // "heuristic" already, but stamp again defensively in
        // case a future facade refactor changes the default).
        return Task.FromResult<ImpactedTestResolverResult?>(result with { Mode = "heuristic" });
    }
}
