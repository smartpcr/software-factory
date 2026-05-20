// -----------------------------------------------------------------------
// <copyright file="IImpactedTestProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests;

/// <summary>
/// Phase 59 Stage C — pluggable backend for impacted-test selection.
/// The original Stage A heuristic (filename / parent-dir / csproj-
/// content) was flagged by the operator on 2026-05-19 as the wrong
/// shape — it silently no-ops on naming-convention drift, can't tell
/// "private helper changed" from "every test in the project is
/// impacted", and has no story for non-C# workspaces.
///
/// <para>The chain (<see cref="ImpactedTestResolverChain"/>) calls
/// each registered provider in order, taking the first non-null
/// result. Providers are responsible for both:
/// <list type="bullet">
///   <item>Saying whether they can handle the workspace (via
///         <see cref="CanHandleAsync"/>) — e.g. Roslyn handles
///         workspaces with a <c>.sln</c> or <c>.csproj</c>; the LSP
///         provider handles whichever languages have a server on
///         PATH; the heuristic handles anything as a last resort.</item>
///   <item>Producing the actual resolution
///         (<see cref="ResolveAsync"/>) — the returned
///         <see cref="ImpactedTestResolverResult.Mode"/> identifies
///         which provider answered so the gate-event payload can
///         tell the UI whether it's looking at a precise
///         call-graph-based answer (`roslyn` / `lsp`) or a
///         conservative heuristic narrowing (`heuristic`).</item>
/// </list></para>
/// </summary>
public interface IImpactedTestProvider
{
    /// <summary>
    /// Short identifier — `"roslyn"`, `"lsp"`, `"heuristic"`. Used as
    /// the <see cref="ImpactedTestResolverResult.Mode"/> stamp so
    /// telemetry + UI can show which provider answered.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True when this provider can answer for the given workspace.
    /// Cheap probe (no expensive Roslyn workspace load, no LSP
    /// server spawn) — defers the heavy work to
    /// <see cref="ResolveAsync"/>. The chain skips providers that
    /// return false here without calling Resolve.
    /// </summary>
    Task<bool> CanHandleAsync(string workspace, CancellationToken ct);

    /// <summary>
    /// Compute the impacted-test rewrite. Inputs match the static
    /// <see cref="ImpactedTestResolver.Resolve"/> facade so call
    /// sites don't have to know about the chain.
    ///
    /// <para>Return value contract:</para>
    /// <list type="bullet">
    ///   <item><c>null</c> — provider claimed it could handle the
    ///         workspace via <see cref="CanHandleAsync"/> but then
    ///         hit a real failure (timeout, server crash, parse
    ///         error). The chain falls through to the next
    ///         provider so a single backend's bad day doesn't take
    ///         the gate down.</item>
    ///   <item>non-null with <see cref="ImpactedTestResolverResult.Skipped"/>
    ///         <c>=true</c> — no changed files touched any test;
    ///         caller emits <c>task.test-gate.skipped</c>.</item>
    ///   <item>non-null with <see cref="ImpactedTestResolverResult.Rewritten"/>
    ///         <c>=true</c> — narrowed command in
    ///         <see cref="ImpactedTestResolverResult.Command"/>.</item>
    ///   <item>non-null with <see cref="ImpactedTestResolverResult.WholeSuiteFallback"/>
    ///         <c>=true</c> — impacted set crossed the 80%
    ///         threshold; original command runs.</item>
    /// </list>
    /// </summary>
    Task<ImpactedTestResolverResult?> ResolveAsync(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        CancellationToken ct);
}
