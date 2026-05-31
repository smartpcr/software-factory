// -----------------------------------------------------------------------
// <copyright file="Stage62E2eScenariosLinterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 6.2 — pin every refusal-criterion of the
/// <see cref="E2eScenariosLinter"/>:
/// <list type="bullet">
/// <item><c>e2e.setup_missing</c> when a Phase H1 lacks <c>### Setup</c>.</item>
/// <item><c>e2e.setup_type_invalid</c> when <c>Type</c> is outside the
///   closed set.</item>
/// <item><c>e2e.lab_secrets_incomplete</c> when a <c>lab-*</c> Type's
///   <c>Secrets</c> bullet lacks both Key Vault + GitHub environment.</item>
/// <item><c>e2e.inline_requires_service</c> when a phase declares
///   <c>Type: inline</c> yet its scenarios need a real backing
///   service (DSN env-var / live Postgres / persisted-row proof).</item>
/// </list>
///
/// <para>Plus one integration scenario that pins the wire-up: the
/// architect evaluator prompt for the <c>e2e-scenarios.md</c> doc
/// carries the linter's hard-requirements section so the LLM has the
/// criteria in-context (Stage 6.2 just teaches the LLM; Stage 6.3
/// wires a hard server-side gate).</para>
/// </summary>
public class Stage62E2eScenariosLinterTests
{
    // ── 1. [unit] Missing `### Setup` → e2e.setup_missing ────────────────

    [Fact]
    public void Linter_Reports_SetupMissing_When_Phase_Lacks_Setup_Heading()
    {
        // Two phases — only the second has `### Setup`. The first must
        // produce `e2e.setup_missing`; the second must lint clean.
        var md = """
            # Phase 1: Foundation

            ### Scenarios
            - **Scenario:** something happens

            # Phase 2: Platform

            ### Setup
            - **Type**: inline
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** other
            """;

        var result = E2eScenariosLinter.Validate(md);

        var err = Assert.Single(result.Errors);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.SetupMissing, err.Code);
        Assert.Equal("Foundation", err.PhaseName);
        Assert.Contains("Foundation", err.Detail);
        Assert.Contains("### Setup", err.Detail);
    }

    // ── 2. [unit] Type outside closed set → e2e.setup_type_invalid ───────

    [Fact]
    public void Linter_Reports_SetupTypeInvalid_When_Type_Outside_Closed_Set()
    {
        // `Type: lambda` is gibberish — not in the closed set.
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lambda
            - **Local**: ?
            - **CI runner**: ?
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** something
            """;

        var result = E2eScenariosLinter.Validate(md);

        var err = Assert.Single(result.Errors);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.SetupTypeInvalid, err.Code);
        Assert.Equal("Foundation", err.PhaseName);
        Assert.Contains("lambda", err.Detail);
        // The error message reminds the operator of the closed set so
        // the fix is obvious from the log alone.
        Assert.Contains("inline", err.Detail);
        Assert.Contains("compose", err.Detail);
        Assert.Contains("lab-wsfc", err.Detail);
        Assert.Contains("lab-bare-metal", err.Detail);

        // Bonus: a Setup block with NO Type bullet at all is also a
        // SetupTypeInvalid (same code, different sub-message).
        var noTypeMd = """
            # Phase 1: Foundation

            ### Setup
            - **Local**: dotnet test
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** thing
            """;
        var noTypeResult = E2eScenariosLinter.Validate(noTypeMd);
        var noTypeErr = Assert.Single(noTypeResult.Errors);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.SetupTypeInvalid, noTypeErr.Code);
        Assert.Contains("**Type**:", noTypeErr.Detail);
    }

    // ── 3. [unit] lab-wsfc + KeyVault-only → e2e.lab_secrets_incomplete ──

    [Fact]
    public void Linter_Reports_LabSecretsIncomplete_When_LabType_Lacks_Both_Providers()
    {
        // lab-wsfc Type but Secrets bullet only mentions Key Vault — no
        // GitHub environment ref.
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv`

            ### Scenarios
            - **Scenario:** CAU cycles correctly
            """;

        var result = E2eScenariosLinter.Validate(md);
        var err = Assert.Single(result.Errors);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete, err.Code);
        Assert.Equal("Foundation", err.PhaseName);
        Assert.Contains("lab-wsfc", err.Detail);
        Assert.Contains("Key Vault", err.Detail);
        Assert.Contains("GitHub environment", err.Detail);

        // Inverse: GH-env-only also fires.
        var ghOnlyMd = md.Replace(
            "**Secrets**: Azure Key Vault `forge-lab-kv`",
            "**Secrets**: GitHub environment `lab-wsfc`");
        var ghOnly = E2eScenariosLinter.Validate(ghOnlyMd);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete,
            Assert.Single(ghOnly.Errors).Code);

        // Bonus: lab-bare-metal also enforces the rule.
        var bareMetalMd = md.Replace(
            "**Type**: lab-wsfc",
            "**Type**: lab-bare-metal");
        var bareMetal = E2eScenariosLinter.Validate(bareMetalMd);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete,
            Assert.Single(bareMetal.Errors).Code);

        // Inline / compose don't trigger the lab-secrets check.
        var inlineMd = md.Replace("**Type**: lab-wsfc", "**Type**: inline");
        Assert.True(E2eScenariosLinter.Validate(inlineMd).IsClean);
        var composeMd = md.Replace("**Type**: lab-wsfc", "**Type**: compose");
        Assert.True(E2eScenariosLinter.Validate(composeMd).IsClean);
    }

    // ── 3b. [unit] inline + backing-service scenario → e2e.inline_requires_service ──

    [Fact]
    public void Linter_Reports_InlineRequiresService_When_Inline_Phase_Needs_A_Service()
    {
        // The code-intelligence REPO-SCANNER Phase 2 regression: an `inline`
        // setup ("no external services") whose scenario requires a live
        // Postgres instance to prove byte-identity of persisted rows. The
        // contradiction must be caught at plan-authoring time.
        var md = """
            # Phase 2: Identity and ancestry refactor

            ### Setup
            - **Type**: inline
            - **Local**: go test ./...; no external services
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** worker-graph-byte-identical
              - **Given** a Postgres test instance populated by the pre-refactor worker.
              - **When** the post-refactor worker.runFull runs against the same fixture.
              - **Then** every node row matches the pre-refactor run byte-for-byte.
            """;

        var result = E2eScenariosLinter.Validate(md);

        var err = Assert.Single(result.Errors);
        Assert.Equal(E2eScenariosLinter.ErrorCodes.InlineRequiresService, err.Code);
        Assert.Equal("Identity and ancestry refactor", err.PhaseName);
        Assert.Contains("compose", err.Detail);

        // Flipping the Type to `compose` clears the violation — the suite can
        // then self-provision the dependency in the gate.
        var composeMd = md.Replace("**Type**: inline", "**Type**: compose");
        Assert.True(E2eScenariosLinter.Validate(composeMd).IsClean);

        // A DSN env-var reference also trips it under inline.
        var dsnMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: go test ./...
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** reads from AGENT_MEMORY_PG_URL and asserts a row exists
            """;
        Assert.Equal(E2eScenariosLinter.ErrorCodes.InlineRequiresService,
            Assert.Single(E2eScenariosLinter.Validate(dsnMd).Errors).Code);

        // A genuinely in-process inline phase (sqlmock, no live service) stays clean.
        var pureInlineMd = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: inline
            - **Local**: go test ./...; sqlmock covers the writer
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** RepoIDFromURL is deterministic for the same url
            """;
        Assert.True(E2eScenariosLinter.Validate(pureInlineMd).IsClean);
    }

    // ── 4. [unit] Valid `compose` setup → no errors ──────────────────────

    [Fact]
    public void Linter_Returns_Clean_For_Valid_Compose_Setup()
    {
        var md = """
            # Phase 1: Foundation

            ### Setup
            - **Type**: compose
            - **Local**: docker compose up -d
            - **CI runner**: ubuntu-latest
            - **Secrets**: none

            ### Scenarios
            - **Scenario:** Foundation works
            - **Scenario:** Foundation handles errors

            # Phase 2: Lab work

            ### Setup
            - **Type**: lab-wsfc
            - **Local**: requires WSFC cluster
            - **CI runner**: self-hosted forge-lab-wsfc
            - **Secrets**: Azure Key Vault `forge-lab-kv` + GitHub environment `lab-wsfc`

            ### Scenarios
            - **Scenario:** CAU cycles correctly
            """;

        var result = E2eScenariosLinter.Validate(md);

        Assert.True(result.IsClean,
            $"Expected zero linter errors; got [{string.Join(", ", result.Errors.Select(e => e.Code))}]");
        Assert.Empty(result.Errors);

        // Defensive: also verify that an empty / null doc returns clean
        // (the parser layer enforces minimum content; the setup linter
        // doesn't fire on a doc with no Phase H1s).
        Assert.True(E2eScenariosLinter.Validate(null).IsClean);
        Assert.True(E2eScenariosLinter.Validate(string.Empty).IsClean);
        Assert.True(E2eScenariosLinter.Validate("   \n  \n  ").IsClean);
    }

    // ── 5. [integration] Evaluator prompt for e2e-scenarios.md carries the linter rules ──

    [Fact]
    public void Architect_Evaluator_Rules_Section_Lists_All_Three_Error_Codes()
    {
        // The integration concern: when the architect evaluator builds
        // its prompt for the e2e-scenarios.md doc, the prompt MUST
        // include the linter's hard requirements + the three error
        // codes so the LLM has the contract in-context.
        // Stage 6.2 surfaces this via E2eScenariosLinter.BuildEvaluatorRulesSection;
        // Stage 4.3's evaluator-prompt builder picks it up when
        // spec.FileName == "e2e-scenarios.md".
        var section = E2eScenariosLinter.BuildEvaluatorRulesSection();

        Assert.Contains("STAGE 6.2", section);
        Assert.Contains("REFUSAL CRITERIA", section);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupMissing, section);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupTypeInvalid, section);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.LabSecretsIncomplete, section);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.InlineRequiresService, section);

        // Closed-set Type names appear so the LLM doesn't need to look
        // them up elsewhere.
        Assert.Contains("inline", section);
        Assert.Contains("compose", section);
        Assert.Contains("lab-wsfc", section);
        Assert.Contains("lab-bare-metal", section);

        // The pass / verdict guidance: score < 90 + iterate.
        Assert.Contains("verdict: \"iterate\"", section);
        Assert.Contains("Score MUST be < 90", section);

        // When violations exist, they're surfaced under a sub-section
        // so the evaluator sees BOTH the rules AND the current state.
        var bad = E2eScenariosLinter.Validate("""
            # Phase 1: Foundation

            ### Scenarios
            - **Scenario:** orphan with no Setup
            """);
        var withResult = E2eScenariosLinter.BuildEvaluatorRulesSection(bad);
        Assert.Contains("Current violations", withResult);
        Assert.Contains(E2eScenariosLinter.ErrorCodes.SetupMissing, withResult);
        Assert.Contains("Foundation", withResult);
    }
}
