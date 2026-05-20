// -----------------------------------------------------------------------
// <copyright file="Phase59CChainTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Forge.Api.Services.Stories.ImpactedTests;
using Xunit;

/// <summary>
/// Phase 59 Stage C — chain composition + provider selection. The
/// chain (Roslyn → LSP → Heuristic in production) picks the first
/// provider that claims the workspace and produces a non-null
/// result. These tests use hand-built fake providers so the
/// selection logic is the contract under test, not Roslyn's
/// behaviour (covered in Phase59CRoslynImpactedTestsTests).
/// </summary>
public class Phase59CChainTests
{
    // ── Fake providers for deterministic selection tests ────────────────

    private sealed class FakeProvider : IImpactedTestProvider
    {
        public string Name { get; }
        public bool CanHandle { get; init; } = true;
        public ImpactedTestResolverResult? ResultToReturn { get; init; }
        public bool ThrowOnCanHandle { get; init; }
        public bool ThrowOnResolve { get; init; }
        public int CanHandleCalls { get; private set; }
        public int ResolveCalls { get; private set; }

        public FakeProvider(string name) { this.Name = name; }

        public Task<bool> CanHandleAsync(string workspace, CancellationToken ct)
        {
            this.CanHandleCalls++;
            if (this.ThrowOnCanHandle) throw new InvalidOperationException("fake CanHandle blow-up");
            return Task.FromResult(this.CanHandle);
        }

        public Task<ImpactedTestResolverResult?> ResolveAsync(
            string workspace, IReadOnlyList<string> changedFiles, string originalCommand, CancellationToken ct)
        {
            this.ResolveCalls++;
            if (this.ThrowOnResolve) throw new InvalidOperationException("fake Resolve blow-up");
            return Task.FromResult(this.ResultToReturn);
        }
    }

    private static ImpactedTestResolverResult OkResult(string mode, string command = "dotnet test foo.csproj") =>
        new(
            Command: command,
            Rewritten: true,
            ImpactedProjects: new[] { "foo.csproj" },
            AllTestProjectCount: 3,
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: $"matched by {mode}",
            Mode: mode);

    // ── T1 — chain picks first provider that claims + resolves ──────────

    [Fact]
    public async Task C_T1_Chain_PicksFirstProviderThatClaimsAndResolves()
    {
        var roslyn = new FakeProvider("roslyn") { CanHandle = true, ResultToReturn = OkResult("roslyn") };
        var heuristic = new FakeProvider("heuristic") { CanHandle = true, ResultToReturn = OkResult("heuristic") };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("roslyn", result.Mode);
        Assert.Equal(1, roslyn.CanHandleCalls);
        Assert.Equal(1, roslyn.ResolveCalls);
        Assert.Equal(0, heuristic.CanHandleCalls); // never reached
        Assert.Equal(0, heuristic.ResolveCalls);
    }

    // ── T2 — falls back when first provider returns null ────────────────

    [Fact]
    public async Task C_T2_Chain_FallsThroughWhenProviderReturnsNull()
    {
        // Roslyn claims it can handle but its ResolveAsync returns
        // null (real failure inside the provider — e.g. workspace
        // load blew up). Chain falls to heuristic.
        var roslyn = new FakeProvider("roslyn") { CanHandle = true, ResultToReturn = null };
        var heuristic = new FakeProvider("heuristic") { CanHandle = true, ResultToReturn = OkResult("heuristic") };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("heuristic", result.Mode);
        Assert.Equal(1, roslyn.ResolveCalls); // tried
        Assert.Equal(1, heuristic.ResolveCalls); // also tried
    }

    [Fact]
    public async Task C_T2b_Chain_SkipsProviderThatCantHandle()
    {
        // Roslyn says "can't handle this workspace" (no .sln/.csproj
        // present); chain skips it without calling Resolve and goes
        // straight to heuristic.
        var roslyn = new FakeProvider("roslyn") { CanHandle = false };
        var heuristic = new FakeProvider("heuristic") { CanHandle = true, ResultToReturn = OkResult("heuristic") };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("heuristic", result.Mode);
        Assert.Equal(1, roslyn.CanHandleCalls);
        Assert.Equal(0, roslyn.ResolveCalls); // skipped — saved expensive workspace load
        Assert.Equal(1, heuristic.ResolveCalls);
    }

    // ── T3 — no provider claims → "none" no-op ──────────────────────────

    [Fact]
    public async Task C_T3_Chain_ReturnsNoneWhenNoProviderClaims()
    {
        var roslyn = new FakeProvider("roslyn") { CanHandle = false };
        var heuristic = new FakeProvider("heuristic") { CanHandle = false };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("none", result.Mode);
        Assert.False(result.Rewritten);
        Assert.False(result.Skipped);
        Assert.Equal("dotnet test", result.Command);
    }

    // ── T4 — provider exceptions don't take the chain down ──────────────

    [Fact]
    public async Task C_T4_Chain_SwallowsCanHandleExceptions()
    {
        var roslyn = new FakeProvider("roslyn") { ThrowOnCanHandle = true };
        var heuristic = new FakeProvider("heuristic") { CanHandle = true, ResultToReturn = OkResult("heuristic") };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("heuristic", result.Mode);
    }

    [Fact]
    public async Task C_T4b_Chain_SwallowsResolveExceptions()
    {
        var roslyn = new FakeProvider("roslyn") { CanHandle = true, ThrowOnResolve = true };
        var heuristic = new FakeProvider("heuristic") { CanHandle = true, ResultToReturn = OkResult("heuristic") };

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        var result = await chain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", CancellationToken.None);

        Assert.Equal("heuristic", result.Mode);
        Assert.Equal(1, roslyn.ResolveCalls); // tried but threw
        Assert.Equal(1, heuristic.ResolveCalls); // chain fell through
    }

    // ── T5 — Cancellation propagates (NOT swallowed) ────────────────────

    [Fact]
    public async Task C_T5_Chain_PropagatesCancellation()
    {
        // Cancellation MUST propagate so the caller's CancellationToken
        // contract is respected. If the chain swallowed
        // OperationCanceledException the gate could keep running after
        // a Cancel/Restart request.
        var roslyn = new FakeProvider("roslyn");
        var heuristic = new FakeProvider("heuristic");

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { roslyn, heuristic });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // CanHandleAsync's Task<bool> will observe the cancellation
        // when we await it. The chain's try/catch only catches
        // non-cancellation exceptions; OCE bubbles up.
        // Our fake providers don't check the token — but the
        // chain wraps any throws other than OCE. Cancel directly
        // via the chain's await loop by throwing OCE from the
        // fake.
        var throwingFake = new ThrowingProvider();
        var directChain = new ImpactedTestResolverChain(new IImpactedTestProvider[] { throwingFake });
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => directChain.ResolveAsync("/workspace", Array.Empty<string>(), "dotnet test", cts.Token));
    }

    private sealed class ThrowingProvider : IImpactedTestProvider
    {
        public string Name => "throwing";
        public Task<bool> CanHandleAsync(string workspace, CancellationToken ct) =>
            throw new OperationCanceledException(ct);
        public Task<ImpactedTestResolverResult?> ResolveAsync(
            string workspace, IReadOnlyList<string> changedFiles, string originalCommand, CancellationToken ct) =>
            throw new InvalidOperationException("should not reach");
    }

    // ── T6 — HeuristicImpactedTestProvider delegates to the static facade ──

    [Fact]
    public async Task C_T6_HeuristicProvider_DelegatesToStaticFacadeAndStampsMode()
    {
        var heuristic = new HeuristicImpactedTestProvider();
        Assert.Equal("heuristic", heuristic.Name);
        Assert.True(await heuristic.CanHandleAsync("/anything", CancellationToken.None));

        // Build a fixture so the facade has something to chew on.
        var root = Path.Combine(Path.GetTempPath(), "phase59c-heuristic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // No csproj files → facade returns a NoOp; provider stamps
        // Mode = "heuristic".
        var result = await heuristic.ResolveAsync(
            workspace: root,
            changedFiles: Array.Empty<string>(),
            originalCommand: "dotnet test",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("heuristic", result!.Mode);
        Assert.False(result.Rewritten);
    }
}
