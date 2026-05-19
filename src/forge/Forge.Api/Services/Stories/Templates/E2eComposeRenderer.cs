// -----------------------------------------------------------------------
// <copyright file="E2eComposeRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.Templates;

using System.Text;

/// <summary>
/// docs/forge/e2e.md Stage 7.1 — production-side template renderer for
/// the <c>compose</c> setup type. Renders three files per qa-e2e
/// workstream whose parent phase declares <c>Type: compose</c>:
///
/// <list type="bullet">
///   <item><c>tests/e2e/{phaseSlug}/docker-compose.yml</c> — the
///         compose stack the qa-e2e run brings up locally + on the
///         hosted CI runner. Stage 7.1 ships a deterministic 2-service
///         scaffold (app + redis); the LLM-driven strategy (Stage 4.3
///         + a future polish phase) will substitute the operator's
///         declared services.</item>
///   <item><c>tests/e2e/{phaseSlug}/azure-pipeline.yml</c> — ADO
///         pipeline targeting the hosted <c>ubuntu-latest</c> agent;
///         brings up the compose stack before <c>dotnet test
///         --filter Category=e2e</c>.</item>
///   <item><c>.github/workflows/e2e-{phaseSlug}.yml</c> — matching
///         GitHub Actions workflow on <c>ubuntu-latest</c>.</item>
/// </list>
///
/// <para>Lives under <c>Forge.Api/Services/Stories/Templates/</c> so
/// BOTH the deterministic strategy (Stage 4.2) AND the LLM-driven
/// strategy (Stage 4.3) can consume it — the renderer is the single
/// source of truth for what a compose pipeline looks like across the
/// two producers. Stage 7.2 will add an `E2eLabRenderer` sibling for
/// the lab-wsfc / lab-bare-metal setup types.</para>
/// </summary>
public static class E2eComposeRenderer
{
    /// <summary>
    /// docs/forge/e2e.md Stage 7.1 — file path the renderer uses for
    /// the docker-compose.yml. Public so the deterministic strategy
    /// and the LLM strategy build the same dictionary key, and tests
    /// can compute the expected path without hardcoding the literal.
    /// </summary>
    public static string DockerComposePathFor(string phaseSlug) =>
        $"tests/e2e/{phaseSlug}/docker-compose.yml";

    /// <summary>
    /// docs/forge/e2e.md Stage 7.1 — ADO pipeline YAML path.
    /// </summary>
    public static string AzurePipelinePathFor(string phaseSlug) =>
        $"tests/e2e/{phaseSlug}/azure-pipeline.yml";

    /// <summary>
    /// docs/forge/e2e.md Stage 7.1 — GitHub Actions workflow path.
    /// </summary>
    public static string GitHubWorkflowPathFor(string phaseSlug) =>
        $".github/workflows/e2e-{phaseSlug}.yml";

    /// <summary>
    /// Render the <c>docker-compose.yml</c> body for the qa-e2e stack.
    /// When <paramref name="services"/> is null / empty, ships a
    /// deterministic 2-service scaffold (`app` running the .NET SDK
    /// image, `redis` for sidecar tests) so the unit / integration
    /// tests have a non-trivial stack to validate. Operator-declared
    /// services flow through verbatim — name per service.
    /// </summary>
    public static string RenderDockerCompose(
        string phaseSlug,
        IReadOnlyList<string>? services = null)
    {
        var resolvedServices = services is { Count: > 0 } ? services : DefaultServices;

        var sb = new StringBuilder()
            .AppendLine($"# docs/forge/e2e.md Stage 7.1 — compose stack for phase `{phaseSlug}`")
            .AppendLine("# Brought up by the qa-e2e CI step before `dotnet test --filter Category=e2e`.")
            .AppendLine("# Substitute the placeholders with the real services the operator's e2e-scenarios.md")
            .AppendLine("# `### Setup` block names; this scaffold ships compile-and-run defaults.")
            .AppendLine()
            .AppendLine("services:");

        foreach (var name in resolvedServices)
        {
            var safeName = SanitiseServiceName(name);
            sb.AppendLine($"  {safeName}:");
            if (string.Equals(safeName, "redis", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("    image: redis:7-alpine");
                sb.AppendLine("    ports:");
                sb.AppendLine("      - \"6379:6379\"");
                sb.AppendLine("    healthcheck:");
                sb.AppendLine("      test: [\"CMD\", \"redis-cli\", \"ping\"]");
                sb.AppendLine("      interval: 5s");
                sb.AppendLine("      retries: 3");
            }
            else if (string.Equals(safeName, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("    image: postgres:16-alpine");
                sb.AppendLine("    environment:");
                sb.AppendLine("      POSTGRES_PASSWORD: forge-e2e-temp");
                sb.AppendLine("    ports:");
                sb.AppendLine("      - \"5432:5432\"");
                sb.AppendLine("    healthcheck:");
                sb.AppendLine("      test: [\"CMD-SHELL\", \"pg_isready -U postgres\"]");
                sb.AppendLine("      interval: 5s");
                sb.AppendLine("      retries: 5");
            }
            else
            {
                // Generic placeholder — operator replaces `image:` with the
                // real service image. Keeps the file `docker compose
                // config`-valid so a `--dry-run` CI step doesn't reject it.
                sb.AppendLine("    image: mcr.microsoft.com/dotnet/sdk:8.0");
                sb.AppendLine("    command: [\"dotnet\", \"--info\"]");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Render the ADO <c>azure-pipeline.yml</c> for a compose phase.
    /// Always targets the hosted <c>ubuntu-latest</c> agent — compose
    /// stacks run on hosted runners; lab setups go through
    /// <c>E2eLabRenderer</c> (Stage 7.2). Step order:
    /// <list type="number">
    ///   <item>checkout (implicit on first job)</item>
    ///   <item><c>docker compose up -d</c></item>
    ///   <item><c>dotnet test --filter Category=e2e</c></item>
    ///   <item><c>docker compose down</c> always</item>
    /// </list>
    /// </summary>
    public static string RenderAzurePipeline(string phaseSlug, string stageSlug)
    {
        var composePath = DockerComposePathFor(phaseSlug);
        var sb = new StringBuilder()
            .AppendLine($"# docs/forge/e2e.md Stage 7.1 — ADO pipeline for compose phase `{phaseSlug}` stage `{stageSlug}`")
            .AppendLine("# Hosted runner (ubuntu-latest) — compose containers run inline.")
            .AppendLine()
            .AppendLine("trigger: none")
            .AppendLine()
            .AppendLine("pool:")
            .AppendLine("  vmImage: ubuntu-latest")
            .AppendLine()
            .AppendLine("steps:")
            .AppendLine($"  - script: docker compose -f {composePath} up -d")
            .AppendLine($"    displayName: Bring up compose stack for phase {phaseSlug}")
            .AppendLine()
            .AppendLine("  - script: dotnet test --filter Category=e2e")
            .AppendLine($"    displayName: Run e2e for phase {phaseSlug} stage {stageSlug}")
            .AppendLine()
            .AppendLine($"  - script: docker compose -f {composePath} down")
            .AppendLine($"    displayName: Tear down compose stack for phase {phaseSlug}")
            .AppendLine("    condition: always()");
        return sb.ToString();
    }

    /// <summary>
    /// Render the GitHub Actions workflow for a compose phase. Targets
    /// the hosted <c>ubuntu-latest</c> runner. Same step order as
    /// <see cref="RenderAzurePipeline"/> but the GH syntax.
    /// </summary>
    public static string RenderGitHubWorkflow(string phaseSlug, string stageSlug)
    {
        var composePath = DockerComposePathFor(phaseSlug);
        var sb = new StringBuilder()
            .AppendLine($"# docs/forge/e2e.md Stage 7.1 — GitHub Actions workflow for compose phase `{phaseSlug}`")
            .AppendLine()
            .AppendLine($"name: e2e-{phaseSlug}")
            .AppendLine("on:")
            .AppendLine("  workflow_dispatch:")
            .AppendLine()
            .AppendLine("jobs:")
            .AppendLine($"  e2e-{phaseSlug}:")
            .AppendLine("    runs-on: ubuntu-latest")
            .AppendLine("    steps:")
            .AppendLine("      - uses: actions/checkout@v4")
            .AppendLine($"      - name: Bring up compose stack for phase {phaseSlug}")
            .AppendLine($"        run: docker compose -f {composePath} up -d")
            .AppendLine($"      - name: Run e2e for phase {phaseSlug} stage {stageSlug}")
            .AppendLine("        run: dotnet test --filter Category=e2e")
            .AppendLine($"      - name: Tear down compose stack for phase {phaseSlug}")
            .AppendLine($"        if: always()")
            .AppendLine($"        run: docker compose -f {composePath} down");
        return sb.ToString();
    }

    /// <summary>
    /// Render all three compose-setup files for a qa-e2e workstream
    /// (phase + stage). Returns a repo-relative-path-keyed dict the
    /// caller folds into the qa-e2e output. <paramref name="services"/>
    /// flows through to <see cref="RenderDockerCompose"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RenderAll(
        string phaseSlug, string stageSlug, IReadOnlyList<string>? services = null) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerComposePathFor(phaseSlug)] = RenderDockerCompose(phaseSlug, services),
            [AzurePipelinePathFor(phaseSlug)] = RenderAzurePipeline(phaseSlug, stageSlug),
            [GitHubWorkflowPathFor(phaseSlug)] = RenderGitHubWorkflow(phaseSlug, stageSlug),
        };

    // ── internal ─────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> DefaultServices = new[] { "app", "redis" };

    /// <summary>
    /// Compose service names must be lower-kebab-style. Strip anything
    /// outside the safe set; fall back to <c>app</c> for empty.
    /// </summary>
    private static string SanitiseServiceName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "app";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        }
        var s = sb.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(s) ? "app" : s;
    }
}
