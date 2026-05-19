// -----------------------------------------------------------------------
// <copyright file="Stage41ProduceStageE2eInterfaceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Reflection;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 4.1 — pin the new
/// <see cref="IStoryArtifactStrategy.ProduceStageE2eAsync"/> contract:
/// every concrete implementation MUST expose the method (Stage 4.2 fills
/// in the deterministic test double, Stage 4.3 fills in the LLM-driven
/// production strategy), and the two legacy methods it supersedes MUST
/// carry <see cref="ObsoleteAttribute"/> with the agreed message so
/// future callers see the deprecation in their IDE / build output.
///
/// <para>Both Facts are pure-reflection (no boot, no DB) and run in the
/// fast-CI bucket.</para>
/// </summary>
public class Stage41ProduceStageE2eInterfaceTests
{
    private const string ExpectedObsoleteMessage =
        "Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.";

    // ── 1. [unit] Interface contract: every impl responds to ProduceStageE2eAsync ──

    [Fact]
    public void Every_IStoryArtifactStrategy_Impl_Exposes_ProduceStageE2eAsync()
    {
        // Walk every loaded assembly in the test process, find every
        // concrete (non-abstract) class that implements
        // IStoryArtifactStrategy, and assert each one has a public
        // instance method named ProduceStageE2eAsync with the agreed
        // signature. The reflection sweep is the contract Stage 4.2 and
        // Stage 4.3 will fail loudly against if either forgets to wire
        // the method, even when the throw-NotImplemented stub gets
        // replaced with the real impl.
        var iface = typeof(IStoryArtifactStrategy);
        var impls = AppDomain.CurrentDomain.GetAssemblies()
            // Skip dynamic assemblies (xUnit generates a few) — GetTypes()
            // on them sometimes throws ReflectionTypeLoadException for
            // unrelated reasons; they never contain our concrete impls.
            .Where(a => !a.IsDynamic)
            .SelectMany(SafeGetTypes)
            .Where(t => !t.IsAbstract && !t.IsInterface && iface.IsAssignableFrom(t))
            .ToList();

        // Sanity floor: at least the two known impls must be present in
        // the test fixture. CrewStoryArtifactStrategy lives in Forge.Api;
        // DeterministicStoryArtifactStrategy lives in Forge.Api.Tests.
        Assert.True(impls.Count >= 2,
            $"Expected at least 2 IStoryArtifactStrategy impls (Crew + Deterministic); "
            + $"found {impls.Count}: [{string.Join(", ", impls.Select(t => t.FullName))}]");
        Assert.Contains(impls, t => t.Name == nameof(CrewStoryArtifactStrategy));
        Assert.Contains(impls, t => t.FullName?.EndsWith("DeterministicStoryArtifactStrategy", StringComparison.Ordinal) == true);

        foreach (var impl in impls)
        {
            var m = impl.GetMethod(
                name: nameof(IStoryArtifactStrategy.ProduceStageE2eAsync),
                bindingAttr: BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(StorySpawnRow), typeof(WorkstreamRow), typeof(CancellationToken) },
                modifiers: null);

            Assert.True(m is not null,
                $"{impl.FullName} implements IStoryArtifactStrategy but lacks a public instance "
                + $"ProduceStageE2eAsync(StorySpawnRow, WorkstreamRow, CancellationToken) method. "
                + "Wire the stub even if the real impl isn't ready yet (docs/forge/e2e.md Stage 4.1).");
            Assert.Equal(typeof(Task<IReadOnlyDictionary<string, string>>), m!.ReturnType);

            // Defensive: the method must NOT carry [Obsolete] — it's the
            // replacement, not a legacy surface.
            Assert.Null(m.GetCustomAttribute<ObsoleteAttribute>());
        }
    }

    // ── 2. [unit] Legacy methods carry [Obsolete] with the agreed message ──

    [Fact]
    public void Legacy_ProduceQa_And_StepDef_Methods_Carry_Obsolete_Attribute()
    {
        var iface = typeof(IStoryArtifactStrategy);

        // Interface-level [Obsolete] — what the IDE / compiler reads when
        // a caller invokes via the interface (the common path).
        AssertObsolete(iface, nameof(IStoryArtifactStrategy.ProduceQaFeatureFilesAsync));
        AssertObsolete(iface, nameof(IStoryArtifactStrategy.ProduceStepDefinitionsAsync));

        // Concrete-impl [Obsolete] — Stage 4.1 also annotates the
        // production + deterministic implementations so callers that
        // reference the concrete type (rare; should always go through
        // the interface) still see the warning.
        AssertObsolete(typeof(CrewStoryArtifactStrategy), nameof(IStoryArtifactStrategy.ProduceQaFeatureFilesAsync));
        AssertObsolete(typeof(CrewStoryArtifactStrategy), nameof(IStoryArtifactStrategy.ProduceStepDefinitionsAsync));

        var deterministic = typeof(IStoryArtifactStrategy).Assembly
            .GetReferencedAssemblies()
            .Select(Assembly.Load)
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Where(a => !a.IsDynamic)
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == "DeterministicStoryArtifactStrategy");
        Assert.NotNull(deterministic);
        AssertObsolete(deterministic!, nameof(IStoryArtifactStrategy.ProduceQaFeatureFilesAsync));
        AssertObsolete(deterministic, nameof(IStoryArtifactStrategy.ProduceStepDefinitionsAsync));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static void AssertObsolete(Type type, string methodName)
    {
        var m = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(m is not null,
            $"Expected {type.FullName} to declare instance method '{methodName}' (interface contract).");
        var attr = m!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.True(attr is not null,
            $"{type.FullName}.{methodName} MUST carry [Obsolete] per docs/forge/e2e.md Stage 4.1. "
            + "Without the attribute new callers won't see the deprecation warning in their IDE.");
        Assert.Equal(ExpectedObsoleteMessage, attr!.Message);
        // Stage 4.1 chose `IsError=false` so callers can still build —
        // Phase R cleanup will flip this to true (then remove the method).
        // Pinning the flag here catches an accidental flip-to-error that
        // would break dependent code mid-refactor.
        Assert.False(attr.IsError,
            $"{type.FullName}.{methodName} [Obsolete] must be IsError=false until Phase R cleanup.");
    }
}
