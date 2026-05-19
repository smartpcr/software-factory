// -----------------------------------------------------------------------
// <copyright file="E2eLabRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.Templates;

using System.Text;

/// <summary>
/// docs/forge/e2e.md Stage 7.2 — production-side template renderer for
/// the <c>lab-wsfc</c> + <c>lab-bare-metal</c> setup types. Renders
/// two files per qa-e2e workstream whose parent phase declares a
/// <c>lab-*</c> Type:
///
/// <list type="bullet">
///   <item><c>tests/e2e/{phaseSlug}/azure-pipeline.yml</c> — ADO
///         pipeline targeting the appropriate self-hosted lab pool
///         (<c>forge-lab-wsfc</c> for WSFC, <c>forge-lab-hw</c> for
///         bare-metal hardware) + <c>AzureKeyVault@2</c> task to
///         surface lab credentials + variable group
///         <c>forge-lab-vg</c>.</item>
///   <item><c>.github/workflows/e2e-{phaseSlug}.yml</c> — matching
///         GitHub Actions workflow targeting the right self-hosted
///         runner labels + GH environment for review gating.</item>
/// </list>
///
/// <para>Lab setups DON'T ship a <c>docker-compose.yml</c> — the
/// tests run against pre-provisioned real infrastructure (a Windows
/// Server Failover Cluster, a hardware lab rack), not throwaway
/// containers. The <c>RenderAll</c> result therefore has 2 entries
/// per phase (vs Stage 7.1's 3 for compose).</para>
///
/// <para>When the operator's <c>### Setup</c> block declares a
/// pre-test bootstrap script (e.g. CAU cluster reset), pass
/// <paramref name="includeBootstrap"/>=true to <see cref="RenderAll"/>
/// and the rendered YAMLs include a pre-test step calling
/// <c>tests/e2e/{phaseSlug}/bootstrap.ps1</c>.</para>
/// </summary>
public static class E2eLabRenderer
{
    /// <summary>ADO pipeline YAML path — matches the compose renderer's
    /// convention so the qa-e2e producer doesn't have to switch on
    /// setup type when computing dictionary keys.</summary>
    public static string AzurePipelinePathFor(string phaseSlug) =>
        $"tests/e2e/{phaseSlug}/azure-pipeline.yml";

    /// <summary>GitHub Actions workflow path.</summary>
    public static string GitHubWorkflowPathFor(string phaseSlug) =>
        $".github/workflows/e2e-{phaseSlug}.yml";

    /// <summary>Bootstrap script path. Convention; the file itself is
    /// authored by the operator. The renderer only references it.</summary>
    public static string BootstrapPathFor(string phaseSlug) =>
        $"tests/e2e/{phaseSlug}/bootstrap.ps1";

    /// <summary>
    /// docs/forge/e2e.md Stage 7.2 — render the ADO pipeline YAML for
    /// a lab phase. Targets the self-hosted lab pool, references the
    /// shared variable group + Key Vault secrets, wires the three
    /// canonical lab env-vars (<c>LAB_CLUSTER_FQDN</c>,
    /// <c>LAB_ADMIN_USER</c>, <c>LAB_ADMIN_PASSWORD</c>) on the test
    /// step. Throws when <paramref name="setupType"/> is not a lab
    /// type — the dispatcher (deterministic strategy + LLM strategy)
    /// is responsible for routing the right setup to the right
    /// renderer.
    /// </summary>
    public static string RenderAzurePipeline(
        E2eSetupType setupType, string phaseSlug, string stageSlug, bool includeBootstrap = false)
    {
        EnsureLabType(setupType);
        var (poolName, _) = ResolveLabConventions(setupType);

        var sb = new StringBuilder()
            .AppendLine($"# docs/forge/e2e.md Stage 7.2 — ADO pipeline for lab phase `{phaseSlug}` stage `{stageSlug}`")
            .AppendLine($"# Setup type: {E2eScenariosSetupReader.ToWire(setupType)} — self-hosted lab pool `{poolName}`.")
            .AppendLine()
            .AppendLine("trigger: none")
            .AppendLine()
            .AppendLine("pool:")
            .AppendLine($"  name: {poolName}")
            .AppendLine()
            .AppendLine("variables:")
            .AppendLine("  - group: forge-lab-vg")
            .AppendLine()
            .AppendLine("steps:")
            .AppendLine("  - task: AzureKeyVault@2")
            .AppendLine($"    displayName: Fetch lab secrets for phase {phaseSlug}")
            .AppendLine("    inputs:")
            .AppendLine("      ConnectedServiceName: forge-lab-sc")
            .AppendLine("      KeyVaultName: forge-lab-kv")
            .AppendLine("      SecretsFilter: 'LAB_CLUSTER_FQDN,LAB_ADMIN_USER,LAB_ADMIN_PASSWORD'");

        if (includeBootstrap)
        {
            sb.AppendLine()
                .AppendLine("  - task: PowerShell@2")
                .AppendLine($"    displayName: Pre-test bootstrap for phase {phaseSlug}")
                .AppendLine("    inputs:")
                .AppendLine("      targetType: filePath")
                .AppendLine($"      filePath: {BootstrapPathFor(phaseSlug)}")
                .AppendLine("    env:")
                .AppendLine("      LAB_CLUSTER_FQDN: $(LAB_CLUSTER_FQDN)")
                .AppendLine("      LAB_ADMIN_USER: $(LAB_ADMIN_USER)")
                .AppendLine("      LAB_ADMIN_PASSWORD: $(LAB_ADMIN_PASSWORD)");
        }

        sb.AppendLine()
            .AppendLine("  - script: dotnet test --filter Category=e2e")
            .AppendLine($"    displayName: Run e2e for phase {phaseSlug} stage {stageSlug}")
            .AppendLine("    env:")
            .AppendLine("      LAB_CLUSTER_FQDN: $(LAB_CLUSTER_FQDN)")
            .AppendLine("      LAB_ADMIN_USER: $(LAB_ADMIN_USER)")
            .AppendLine("      LAB_ADMIN_PASSWORD: $(LAB_ADMIN_PASSWORD)");
        return sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 7.2 — render the GitHub Actions
    /// workflow for a lab phase. Targets the self-hosted runner with
    /// the right label set + GH environment (so the operator's review
    /// gate fires before secrets are released), wires the three lab
    /// env-vars from environment secrets.
    /// </summary>
    public static string RenderGitHubWorkflow(
        E2eSetupType setupType, string phaseSlug, string stageSlug, bool includeBootstrap = false)
    {
        EnsureLabType(setupType);
        var (_, runner) = ResolveLabConventions(setupType);

        var sb = new StringBuilder()
            .AppendLine($"# docs/forge/e2e.md Stage 7.2 — GitHub Actions workflow for lab phase `{phaseSlug}`")
            .AppendLine($"# Setup type: {E2eScenariosSetupReader.ToWire(setupType)} — self-hosted runner labels {runner.LabelsLiteral}.")
            .AppendLine()
            .AppendLine($"name: e2e-{phaseSlug}")
            .AppendLine("on:")
            .AppendLine("  workflow_dispatch:")
            .AppendLine()
            .AppendLine("jobs:")
            .AppendLine($"  e2e-{phaseSlug}:")
            .AppendLine($"    runs-on: {runner.LabelsLiteral}")
            .AppendLine($"    environment: {runner.EnvironmentName}")
            .AppendLine("    steps:")
            .AppendLine("      - uses: actions/checkout@v4");

        if (includeBootstrap)
        {
            sb.AppendLine($"      - name: Pre-test bootstrap for phase {phaseSlug}")
                .AppendLine($"        run: ./{BootstrapPathFor(phaseSlug)}")
                .AppendLine("        shell: pwsh")
                .AppendLine("        env:")
                .AppendLine("          LAB_CLUSTER_FQDN: ${{ secrets.LAB_CLUSTER_FQDN }}")
                .AppendLine("          LAB_ADMIN_USER: ${{ secrets.LAB_ADMIN_USER }}")
                .AppendLine("          LAB_ADMIN_PASSWORD: ${{ secrets.LAB_ADMIN_PASSWORD }}");
        }

        sb.AppendLine($"      - name: Run e2e for phase {phaseSlug} stage {stageSlug}")
            .AppendLine("        run: dotnet test --filter Category=e2e")
            .AppendLine("        env:")
            .AppendLine("          LAB_CLUSTER_FQDN: ${{ secrets.LAB_CLUSTER_FQDN }}")
            .AppendLine("          LAB_ADMIN_USER: ${{ secrets.LAB_ADMIN_USER }}")
            .AppendLine("          LAB_ADMIN_PASSWORD: ${{ secrets.LAB_ADMIN_PASSWORD }}");
        return sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 7.2 — render both lab pipeline files
    /// for a qa-e2e workstream. Returns the 2-entry dict the caller
    /// folds into the qa-e2e output. Set
    /// <paramref name="includeBootstrap"/>=true when the operator's
    /// <c>### Setup</c> block declares a <c>Pre-test bootstrap</c>
    /// script (the deterministic strategy can pass this based on
    /// parsed-setup-block content; the LLM strategy can prompt the
    /// agent to set the flag).
    /// </summary>
    public static IReadOnlyDictionary<string, string> RenderAll(
        E2eSetupType setupType, string phaseSlug, string stageSlug, bool includeBootstrap = false)
    {
        EnsureLabType(setupType);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzurePipelinePathFor(phaseSlug)] = RenderAzurePipeline(setupType, phaseSlug, stageSlug, includeBootstrap),
            [GitHubWorkflowPathFor(phaseSlug)] = RenderGitHubWorkflow(setupType, phaseSlug, stageSlug, includeBootstrap),
        };
    }

    // ── internal ─────────────────────────────────────────────────────────

    /// <summary>Pool name + runner labels + GH environment for a lab type.</summary>
    private static (string PoolName, GhRunner Runner) ResolveLabConventions(E2eSetupType setupType) =>
        setupType switch
        {
            E2eSetupType.LabWsfc => ("forge-lab-wsfc",
                new GhRunner("[self-hosted, wsfc-lab]", "lab-wsfc")),
            E2eSetupType.LabBareMetal => ("forge-lab-hw",
                new GhRunner("[self-hosted, lab-hw]", "lab-hw")),
            _ => throw new ArgumentOutOfRangeException(nameof(setupType),
                $"E2eLabRenderer does not handle setup type '{setupType}' — only lab-wsfc / lab-bare-metal."),
        };

    private static void EnsureLabType(E2eSetupType setupType)
    {
        if (setupType is not (E2eSetupType.LabWsfc or E2eSetupType.LabBareMetal))
        {
            throw new ArgumentOutOfRangeException(nameof(setupType),
                $"E2eLabRenderer only handles lab-wsfc / lab-bare-metal; got '{setupType}'. "
                + "Use E2eComposeRenderer for `compose` or no renderer for `inline`.");
        }
    }

    private sealed record GhRunner(string LabelsLiteral, string EnvironmentName);
}
