// -----------------------------------------------------------------------
// <copyright file="ImpactedTestResolverChain.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 59 Stage C — composes <see cref="IImpactedTestProvider"/>
/// instances and picks the first one that claims the workspace +
/// produces a non-null result. Provider order matters: precise
/// providers (Roslyn for C#, LSP for other languages) come first;
/// the conservative heuristic is last so it acts as the universal
/// fallback when no language-specific backend is available.
///
/// <para>The chain delegates the actual work — it doesn't try to
/// merge results from multiple providers. The first provider whose
/// <see cref="IImpactedTestProvider.CanHandleAsync"/> returns true is
/// asked to resolve; if its <see cref="IImpactedTestProvider.ResolveAsync"/>
/// returns null (real failure inside the provider, e.g. Roslyn
/// workspace load failed mid-query), the chain falls through to the
/// next provider so a single backend's bad day doesn't take the gate
/// down.</para>
///
/// <para>When no provider claims the workspace, returns a
/// <see cref="ImpactedTestResolverResult"/> with
/// <see cref="ImpactedTestResolverResult.Mode"/> = `"none"` and
/// <see cref="ImpactedTestResolverResult.Rewritten"/> = false so the
/// caller runs the original test command verbatim.</para>
/// </summary>
public sealed class ImpactedTestResolverChain
{
    /// <summary>
    /// Per-provider wall-clock budget for <see cref="IImpactedTestProvider.ResolveAsync"/>
    /// (+ a shorter one for <c>CanHandleAsync</c>). A provider that exceeds it is
    /// treated as a soft failure: the chain cancels it and falls through to the next
    /// provider (ultimately the heuristic, which never blocks on a subprocess).
    ///
    /// <para><b>Why this exists (root cause, 2026-05-26):</b> the impacted-test gate ran
    /// in-process on the implement path with a <see cref="CancellationToken"/> that
    /// carried no deadline. On local:UPDATE-HISTORY-IN-CL stage-1.3 the LSP provider
    /// (csharp-ls) issued a <c>textDocument/references</c> request and the server never
    /// answered; <see cref="Lsp.MinimalLspClient.RequestAsync"/> awaited the response
    /// completion-source with no timeout, so the iteration wedged **forever** (build gate
    /// passed at 12:15, hung until a process restart ~8h later — confirmed via a heap dump
    /// `dumpasync` chain ending in <c>MinimalLspClient.ReadLineCrlfAsync</c>). The
    /// per-call comment in <see cref="Lsp.LspImpactedTestProvider"/> claimed "cap by
    /// wall-clock cancellation in case a server hangs" but no deadline was ever wired —
    /// this is that deadline, applied uniformly so a hang in ANY provider (Roslyn
    /// MSBuildWorkspace load included) recovers instead of stranding the workstream.</para>
    /// </summary>
    public static readonly TimeSpan DefaultPerProviderTimeout = TimeSpan.FromSeconds(180);

    private readonly IReadOnlyList<IImpactedTestProvider> providers;
    private readonly TimeSpan perProviderTimeout;
    private readonly ILogger<ImpactedTestResolverChain>? logger;

    /// <summary>
    /// Construct the chain from an ordered list of providers. The
    /// production wiring (in <c>Program.cs</c> or the static facade)
    /// is `[Roslyn, LSP, Heuristic]`; tests may pass a subset to
    /// pin specific selection paths.
    /// </summary>
    /// <param name="providers">Ordered provider list (precise first, heuristic last).</param>
    /// <param name="logger">Optional logger — warns when a provider times out / is skipped.</param>
    /// <param name="perProviderTimeout">Per-provider wall-clock budget; null = <see cref="DefaultPerProviderTimeout"/>.</param>
    public ImpactedTestResolverChain(
        IReadOnlyList<IImpactedTestProvider> providers,
        ILogger<ImpactedTestResolverChain>? logger = null,
        TimeSpan? perProviderTimeout = null)
    {
        this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
        this.logger = logger;
        this.perProviderTimeout = perProviderTimeout is { } t && t > TimeSpan.Zero
            ? t
            : DefaultPerProviderTimeout;
    }

    /// <summary>The per-provider wall-clock budget in effect for this chain (diagnostics).</summary>
    public TimeSpan PerProviderTimeout => this.perProviderTimeout;

    /// <summary>
    /// Run the chain. Returns the first provider's non-null result;
    /// if no provider can handle the workspace, returns a `mode=none`
    /// no-op so the caller runs the original command verbatim.
    /// </summary>
    public async Task<ImpactedTestResolverResult> ResolveAsync(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        CancellationToken ct = default)
    {
        foreach (var provider in this.providers)
        {
            // CanHandle gets a shorter slice of the budget — it's a cheap probe
            // (marker-file / ecosystem detect), but PickLanguageAsync can spawn a
            // subprocess on some paths, so bound it too.
            bool canHandle;
            try
            {
                canHandle = await this.RunBoundedAsync(
                    provider, "CanHandleAsync",
                    pct => provider.CanHandleAsync(workspace, pct),
                    TimeSpan.FromTicks(this.perProviderTimeout.Ticks / 4),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // real caller cancellation — propagate
            }
            catch
            {
                // Provider can't even decide if it should handle (threw or
                // timed out) — skip it rather than failing the gate.
                continue;
            }
            if (!canHandle) continue;

            ImpactedTestResolverResult? result;
            try
            {
                result = await this.RunBoundedAsync(
                    provider, "ResolveAsync",
                    pct => provider.ResolveAsync(workspace, changedFiles, originalCommand, pct),
                    this.perProviderTimeout,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // real caller cancellation — propagate
            }
            catch
            {
                // Provider claimed it could handle but blew up OR exceeded its
                // wall-clock budget during resolution. Fall through to the next
                // provider so a Roslyn workspace-load failure / a hung LSP server
                // doesn't strand the test gate (root cause above).
                continue;
            }

            if (result is not null) return result;
        }

        // No provider produced a result — return a `mode=none`
        // no-op so the caller runs the original command verbatim
        // (the safe default; "I don't know what to narrow" is
        // never worse than "run everything").
        return new ImpactedTestResolverResult(
            Command: originalCommand,
            Rewritten: false,
            ImpactedProjects: Array.Empty<string>(),
            AllTestProjectCount: 0,
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: "no impacted-test provider claimed the workspace",
            Mode: "none");
    }

    /// <summary>
    /// Run one provider call under a wall-clock <paramref name="timeout"/> linked to the
    /// caller's <paramref name="ct"/>. A timeout cancels the call and re-throws the
    /// <see cref="OperationCanceledException"/> (the caller's <c>when (ct.IsCancellationRequested)</c>
    /// guard tells it apart from a real caller-cancellation and falls through to the next
    /// provider). Logs a warning so a hung backend is visible in the logs without a heap dump.
    /// </summary>
    private async Task<T> RunBoundedAsync<T>(
        IImpactedTestProvider provider,
        string op,
        Func<CancellationToken, Task<T>> call,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            return await call(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // OUR deadline fired (not the caller's) — the provider hung.
            this.logger?.LogWarning(
                "Impacted-test provider '{Provider}'.{Op} exceeded its {TimeoutSec:0}s budget "
                + "(elapsed {ElapsedSec:0.0}s); cancelling it and falling through to the next provider "
                + "(ultimately the heuristic). The test gate will not narrow via this backend this iteration.",
                provider.Name, op, timeout.TotalSeconds, sw.Elapsed.TotalSeconds);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex,
                "Impacted-test provider '{Provider}'.{Op} threw after {ElapsedSec:0.0}s; falling through.",
                provider.Name, op, sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
