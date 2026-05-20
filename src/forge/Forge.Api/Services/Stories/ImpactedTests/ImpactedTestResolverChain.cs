// -----------------------------------------------------------------------
// <copyright file="ImpactedTestResolverChain.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests;

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
    private readonly IReadOnlyList<IImpactedTestProvider> providers;

    /// <summary>
    /// Construct the chain from an ordered list of providers. The
    /// production wiring (in <c>Program.cs</c> or the static facade)
    /// is `[Roslyn, LSP, Heuristic]`; tests may pass a subset to
    /// pin specific selection paths.
    /// </summary>
    public ImpactedTestResolverChain(IReadOnlyList<IImpactedTestProvider> providers)
    {
        this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

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
            bool canHandle;
            try
            {
                canHandle = await provider.CanHandleAsync(workspace, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Provider can't even decide if it should handle —
                // skip it rather than failing the gate.
                continue;
            }
            if (!canHandle) continue;

            ImpactedTestResolverResult? result;
            try
            {
                result = await provider.ResolveAsync(workspace, changedFiles, originalCommand, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Provider claimed it could handle but blew up
                // during resolution. Fall through to the next
                // provider so a Roslyn workspace-load failure
                // doesn't strand the test gate.
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
}
