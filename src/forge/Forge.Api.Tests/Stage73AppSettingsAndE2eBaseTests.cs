// -----------------------------------------------------------------------
// <copyright file="Stage73AppSettingsAndE2eBaseTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Reflection;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Services.Stories.Templates;
using Forge.Api.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 7.3 — pin the <see cref="E2eBaseRenderer"/>
/// contract + the per-spawn idempotency of <c>E2eBase.cs</c> emission:
///
/// <list type="bullet">
///   <item>The rendered <c>E2eBase.cs</c> compiles standalone via
///         Roslyn (parse + emit smoke).</item>
///   <item>The rendered <c>appsettings.e2e.json</c> for <c>lab-wsfc</c>
///         declares all three lab env-var names.</item>
///   <item>The <c>SetupSkippedException</c> carries the missing var
///         name in its message (string-format pin AND a
///         Roslyn-reflective behavior check).</item>
///   <item>A spawn with two qa-e2e stages emits exactly one
///         <c>E2eBase.cs</c> across the two <c>ProduceStageE2eAsync</c>
///         calls (per-spawn cache lookup).</item>
/// </list>
///
/// <para>The compile-smoke test is the strongest gate: it proves the
/// generated step-def base class is real C# that the runtime can load
/// — a typo or unresolved reference in the template would otherwise
/// only surface when a real story's test project actually tries to
/// build, which would block the qa-e2e PR with a confusing build
/// error.</para>
/// </summary>
[Collection("Phase28")]
public class Stage73AppSettingsAndE2eBaseTests
{
    private const string StoryId = "WI-stage73";
    private const string SpawnId = "spawn-" + StoryId;
    private const string PhaseSlugA = "foundation";
    private const string PhaseSlugB = "ingest";

    // ── 1. [unit] E2eBase.cs compiles standalone (Roslyn parse + emit smoke) ──

    [Fact]
    public void RenderE2eBaseClass_Compiles_Via_Roslyn()
    {
        var code = E2eBaseRenderer.RenderE2eBaseClass(StoryId);
        Assert.False(string.IsNullOrWhiteSpace(code));

        // Parse first — surfaces syntax errors with crisper diagnostics
        // than the full compile gauntlet.
        var tree = CSharpSyntaxTree.ParseText(code);
        var syntaxErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(syntaxErrors.Count == 0,
            $"E2eBase.cs has syntax errors:\n  {string.Join("\n  ", syntaxErrors.Select(d => d.ToString()))}");

        // Full compile + emit to MemoryStream. References pulled from
        // the currently-loaded assemblies (Trimmed of dynamic ones) —
        // .NET 8 host has the BCL we need (object, Exception,
        // Environment) loaded already, so we don't need to ship a
        // reference-assemblies package.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        // Ensure System.Runtime + System.Private.CoreLib are present
        // even if the test host hasn't touched them yet (defensive).
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Environment).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "Stage73.E2eBase.Smoke",
            syntaxTrees: new[] { tree },
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        EmitResult emit = compilation.Emit(ms);
        var errors = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(emit.Success,
            $"E2eBase.cs failed to compile:\n  {string.Join("\n  ", errors.Select(d => d.ToString()))}");
    }

    // ── 2. [unit] appsettings.e2e.json for lab-wsfc declares 3 lab env-vars ──

    [Fact]
    public void RenderAppSettings_LabWsfc_Declares_Three_Lab_EnvVars()
    {
        var json = E2eBaseRenderer.RenderAppSettings(E2eSetupType.LabWsfc, PhaseSlugA);
        Assert.False(string.IsNullOrWhiteSpace(json));

        // Parses as real JSON (defensive — the renderer hand-formats
        // the doc but malformed JSON would silently nuke the file when
        // the operator opens it).
        using var doc = JsonDocument.Parse(json);
        var e2eSection = doc.RootElement.GetProperty("E2e");

        Assert.True(e2eSection.TryGetProperty("LAB_CLUSTER_FQDN", out var fqdn));
        Assert.Equal(string.Empty, fqdn.GetString());
        Assert.True(e2eSection.TryGetProperty("LAB_ADMIN_USER", out var user));
        Assert.Equal(string.Empty, user.GetString());
        Assert.True(e2eSection.TryGetProperty("LAB_ADMIN_PASSWORD", out var pwd));
        Assert.Equal(string.Empty, pwd.GetString());

        // Lab-bare-metal shares the same 3-var contract (both lab types
        // pull from the same Key Vault / GH env secrets).
        var bareJson = E2eBaseRenderer.RenderAppSettings(E2eSetupType.LabBareMetal, PhaseSlugA);
        using var bareDoc = JsonDocument.Parse(bareJson);
        var bareSection = bareDoc.RootElement.GetProperty("E2e");
        Assert.True(bareSection.TryGetProperty("LAB_CLUSTER_FQDN", out _));
        Assert.True(bareSection.TryGetProperty("LAB_ADMIN_USER", out _));
        Assert.True(bareSection.TryGetProperty("LAB_ADMIN_PASSWORD", out _));

        // Inline emits an empty-but-valid E2e section (no env vars to
        // declare), proving the renderer covers the no-infra case.
        var inlineJson = E2eBaseRenderer.RenderAppSettings(E2eSetupType.Inline, PhaseSlugA);
        using var inlineDoc = JsonDocument.Parse(inlineJson);
        var inlineSection = inlineDoc.RootElement.GetProperty("E2e");
        Assert.False(inlineSection.TryGetProperty("LAB_CLUSTER_FQDN", out _));
        Assert.False(inlineSection.TryGetProperty("COMPOSE_REDIS_HOST", out _));

        // EnvVarNames helper agrees with the renderer's output.
        Assert.Equal(
            new[] { "LAB_CLUSTER_FQDN", "LAB_ADMIN_USER", "LAB_ADMIN_PASSWORD" },
            E2eBaseRenderer.EnvVarNames(E2eSetupType.LabWsfc));
        Assert.Equal(
            new[] { "LAB_CLUSTER_FQDN", "LAB_ADMIN_USER", "LAB_ADMIN_PASSWORD" },
            E2eBaseRenderer.EnvVarNames(E2eSetupType.LabBareMetal));
        Assert.Empty(E2eBaseRenderer.EnvVarNames(E2eSetupType.Inline));
    }

    // ── 3. [unit] SetupSkippedException carries the missing var name ─────

    [Fact]
    public void SetupSkippedException_Message_Carries_VarName()
    {
        // 3a. String-shape pin on the rendered template — guarantees
        // the constructor signature + the message format remain stable
        // even if a future refactor moves the type into a separate
        // file.
        var code = E2eBaseRenderer.RenderE2eBaseClass(StoryId);
        Assert.Contains("public sealed class SetupSkippedException : Exception", code);
        Assert.Contains("public string VarName { get; }", code);
        Assert.Contains("public SetupSkippedException(string varName)", code);
        Assert.Contains("required env var '{varName}' is not set", code);

        // 3b. Reflective behavior check — compile the template, load
        // the assembly, instantiate SetupSkippedException("LAB_CLUSTER_FQDN"),
        // and prove the Message + VarName carry the var. This is the
        // strongest pin — proves the rendered C# WORKS, not just that
        // its source contains the right tokens.
        var (asm, _) = CompileTemplate(code);
        var exType = asm.GetTypes().Single(t => t.Name == "SetupSkippedException");
        var baseType = asm.GetTypes().Single(t => t.Name == "E2eBase");

        var ex = (Exception)Activator.CreateInstance(exType, "LAB_CLUSTER_FQDN")!;
        Assert.Contains("LAB_CLUSTER_FQDN", ex.Message);
        // The VarName property exposes the missing var name verbatim so
        // the reporter can render an actionable diagnostic.
        var varNameProp = exType.GetProperty("VarName", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal("LAB_CLUSTER_FQDN", (string?)varNameProp.GetValue(ex));

        // E2eBase is abstract — derive a one-off subclass via Reflection
        // and prove RequireEnv throws SetupSkippedException with the
        // var name when the env var is unset.
        Assert.True(baseType.IsAbstract);
        var requireEnv = baseType.GetMethod("RequireEnv",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(requireEnv);

        // Ensure the env var is unset, then invoke.
        const string missingVar = "FORGE_STAGE73_DEFINITELY_NOT_SET";
        Environment.SetEnvironmentVariable(missingVar, null);
        try
        {
            requireEnv!.Invoke(null, new object[] { missingVar });
            Assert.Fail("RequireEnv must throw when the env var is unset.");
        }
        catch (TargetInvocationException tie) when (tie.InnerException is { } inner)
        {
            Assert.Equal("SetupSkippedException", inner.GetType().Name);
            Assert.Contains(missingVar, inner.Message);
        }
    }

    // ── 4. [integration] Two qa-e2e stages emit exactly one E2eBase.cs ───

    [Fact]
    public async Task Two_QaE2e_Stages_For_Same_Spawn_Emit_Exactly_One_E2eBase()
    {
        // Same spawn, two qa-e2e workstreams for two different stages.
        // The strategy's per-spawn cache must include E2eBase.cs in
        // exactly ONE of the two output dicts.
        var setupMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Key Vault `forge-lab-kv`

            ### Scenarios
            - **Scenario:** CAU cycles correctly

            # Phase 2: Ingest

            ### Setup
            - **Type**: lab-bare-metal
            - **Local**: NIC-X hardware
            - **CI runner**: self-hosted forge-lab-hw

            ### Scenarios
            - **Scenario:** NIC-X loads firmware
            """;

        var git = new InMemoryStoryGitOps();
        var repo = new RepoRow { Id = "repo-stage73", DefaultBranch = "main", Kind = "local" };
        await git.EnsureBranchAsync(repo, "main", "main");
        await git.CommitFilesAsync(repo, "main", "stage73-seed",
            new Dictionary<string, string>
            {
                [$"docs/stories/{StoryId}/e2e-scenarios.md"] = setupMd,
            },
            "seed e2e-scenarios");

        // ONE strategy instance — the per-spawn cache lives on the
        // instance, so a second strategy would defeat the test.
        var strategy = new DeterministicStoryArtifactStrategy(git);

        var spawn = new StorySpawnRow
        {
            Id = SpawnId,
            StoryId = StoryId,
            StoryTitle = "Stage 7.3",
            Phase = StorySpawnPhases.Implement,
            RepoId = repo.Id,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var ws1 = QaE2eRow(PhaseSlugA, "setup");
        var ws2 = QaE2eRow(PhaseSlugB, "loader");

        // Pre-condition: no E2eBase emitted yet for this spawn.
        Assert.False(strategy.HasEmittedE2eBaseFor(SpawnId));

        var files1 = await strategy.ProduceStageE2eAsync(spawn, ws1, default);
        var files2 = await strategy.ProduceStageE2eAsync(spawn, ws2, default);

        // Post-condition: the strategy now remembers the spawn.
        Assert.True(strategy.HasEmittedE2eBaseFor(SpawnId));

        // E2eBase.cs appears in EXACTLY ONE of the two dicts. Across
        // both dicts combined the count is 1.
        var basePath = E2eBaseRenderer.E2eBasePathFor(StoryId);
        var baseCount =
            (files1.ContainsKey(basePath) ? 1 : 0) +
            (files2.ContainsKey(basePath) ? 1 : 0);
        Assert.Equal(1, baseCount);

        // The FIRST call (ws1) wins TryAdd, so it carries the base
        // class; the second call sees the cache miss and skips.
        Assert.True(files1.ContainsKey(basePath),
            "First qa-e2e of a spawn must include E2eBase.cs (cache miss).");
        Assert.False(files2.ContainsKey(basePath),
            "Second qa-e2e of the same spawn must NOT re-emit E2eBase.cs (cache hit).");

        // Each call DOES emit its own appsettings.e2e.json for its
        // own phase — appsettings is per-phase, not per-spawn.
        var apps1 = E2eBaseRenderer.AppSettingsPathFor(PhaseSlugA);
        var apps2 = E2eBaseRenderer.AppSettingsPathFor(PhaseSlugB);
        Assert.True(files1.ContainsKey(apps1));
        Assert.True(files2.ContainsKey(apps2));
        Assert.NotEqual(apps1, apps2);

        // The E2eBase.cs body carries the story-id-based namespace
        // segment so step-def files (under the same per-story root)
        // can inherit without a cross-namespace using.
        var baseBody = files1[basePath];
        Assert.Contains($"namespace Forge.Tests.Stories.{StoryId.Replace('-', '_')};", baseBody);

        // Reset hook clears the marker so a follow-up call re-emits —
        // proves the cache key is the spawn id (not e.g. a static
        // singleton).
        strategy.ResetE2eBaseEmittedFor(SpawnId);
        Assert.False(strategy.HasEmittedE2eBaseFor(SpawnId));
        var files3 = await strategy.ProduceStageE2eAsync(spawn, ws2, default);
        Assert.True(files3.ContainsKey(basePath),
            "After reset, the next ProduceStageE2eAsync call must re-emit E2eBase.cs.");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static WorkstreamRow QaE2eRow(string phaseSlug, string stageSlug) => new()
    {
        Id = $"ws-stage73-{phaseSlug}-{stageSlug}-e2e",
        Name = $"{stageSlug} — E2E",
        Branch = $"ws/stage73/{phaseSlug}/{stageSlug}-e2e",
        Base = "main",
        Status = "planning",
        Category = WorkstreamCategories.QaE2e,
        StorySpawnId = SpawnId,
        PlanAnchor = $"phase-{phaseSlug}/stage-{stageSlug}{CrewStoryArtifactStrategy.QaE2eAnchorSuffix}",
        PhaseAnchor = $"phase-{phaseSlug}",
        WorktreeJson = "[]",
        AgentsJson = "[]",
        PrsJson = "[]",
        CardsJson = "[]",
        Summary = string.Empty,
    };

    private static (Assembly Assembly, CSharpCompilation Compilation) CompileTemplate(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Environment).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "Stage73.E2eBase.Reflect." + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { tree },
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            $"Compile failed:\n  {string.Join("\n  ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()))}");
        ms.Position = 0;
        return (Assembly.Load(ms.ToArray()), compilation);
    }
}
