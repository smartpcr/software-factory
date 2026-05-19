// -----------------------------------------------------------------------
// <copyright file="E2eScenariosLinter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// docs/forge/e2e.md Stage 6.2 — pure-helper linter for
/// <c>e2e-scenarios.md</c>. The architect crew's evaluator MUST refuse
/// pass on any markdown that fails one of the four hard-requirements
/// the operator + Forge agreed on in §8.4:
///
/// <list type="bullet">
/// <item><c>e2e.setup_missing</c> — a Phase H1 has no <c>### Setup</c> H3.</item>
/// <item><c>e2e.setup_type_invalid</c> — a Setup block declares a
///   Type outside the closed set <c>{inline, compose, lab-wsfc, lab-bare-metal}</c>.</item>
/// <item><c>e2e.lab_secrets_incomplete</c> — a <c>lab-*</c> Type's
///   Secrets bullet lacks BOTH a KeyVault entry AND a GitHub
///   environment ref.</item>
/// </list>
///
/// <para>The linter is also surfaced as evaluator prompt text via
/// <see cref="BuildEvaluatorRulesSection"/> so the LLM has the
/// criteria in-context; Stage 6.3 will run the linter as a server-side
/// gate after each iter's commit so a generator that ignores the rules
/// can't slip past the evaluator's scoring.</para>
/// </summary>
public static class E2eScenariosLinter
{
    private static readonly Regex PhaseH1 = new(
        @"^#\s+Phase\s+\d+\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SetupH3 = new(
        @"^###\s+Setup\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnyH1 = new(@"^#\s+\S.*$", RegexOptions.Compiled);
    private static readonly Regex AnyHigherHeading = new(@"^#{1,3}\s+\S.*$", RegexOptions.Compiled);

    private static readonly Regex TypeBullet = new(
        @"^\s*[-*]\s*(?:\*\*|__)?\s*Type\s*(?:\*\*|__)?\s*:\s*(?<value>[A-Za-z0-9\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecretsBullet = new(
        @"^\s*[-*]\s*(?:\*\*|__)?\s*Secrets\s*(?:\*\*|__)?\s*:\s*(?<body>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// docs/forge/e2e.md Stage 6.2 — error code constants. Follow the
    /// existing <c>plan.*</c> family naming (see
    /// <see cref="PlanParser.ErrorCodes"/>); callers can branch on the
    /// literal string + format their own UI / log message.
    /// </summary>
    public static class ErrorCodes
    {
        public const string SetupMissing = "e2e.setup_missing";
        public const string SetupTypeInvalid = "e2e.setup_type_invalid";
        public const string LabSecretsIncomplete = "e2e.lab_secrets_incomplete";
    }

    /// <summary>
    /// One linter violation. <see cref="PhaseName"/> identifies the
    /// offending phase (verbatim from the H1 heading); <see cref="Detail"/>
    /// is a one-line operator-readable explanation.
    /// </summary>
    public sealed record LintError(string Code, string PhaseName, string Detail);

    /// <summary>Result of running <see cref="Validate"/> over a markdown body.</summary>
    public sealed record LintResult(IReadOnlyList<LintError> Errors)
    {
        public bool IsClean => this.Errors.Count == 0;
        public static LintResult Clean { get; } = new(Array.Empty<LintError>());
    }

    /// <summary>
    /// Walk the markdown body, group by Phase H1, and report every
    /// violation in the same single pass. Returns
    /// <see cref="LintResult.Clean"/> when no Phase H1 is present
    /// (degenerate / empty doc — the parser layer is the right place
    /// to enforce minimum content, not the setup-block linter).
    /// </summary>
    public static LintResult Validate(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return LintResult.Clean;

        var normalised = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalised.Split('\n');

        var errors = new List<LintError>();
        var phases = ScanPhases(lines);

        foreach (var phase in phases)
        {
            if (!phase.HasSetupHeading)
            {
                errors.Add(new LintError(
                    ErrorCodes.SetupMissing,
                    phase.Name,
                    $"Phase `{phase.Name}` is missing the required `### Setup` H3 directly before `### Scenarios`."));
                continue;
            }

            if (phase.TypeValue is null)
            {
                errors.Add(new LintError(
                    ErrorCodes.SetupTypeInvalid,
                    phase.Name,
                    $"Phase `{phase.Name}` `### Setup` block has no `- **Type**: ...` bullet."));
                continue;
            }

            if (!E2eScenariosSetupReader.TypeNames.Inline.Equals(phase.TypeValue, StringComparison.OrdinalIgnoreCase)
                && !E2eScenariosSetupReader.TypeNames.Compose.Equals(phase.TypeValue, StringComparison.OrdinalIgnoreCase)
                && !E2eScenariosSetupReader.TypeNames.LabWsfc.Equals(phase.TypeValue, StringComparison.OrdinalIgnoreCase)
                && !E2eScenariosSetupReader.TypeNames.LabBareMetal.Equals(phase.TypeValue, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new LintError(
                    ErrorCodes.SetupTypeInvalid,
                    phase.Name,
                    $"Phase `{phase.Name}` declares `Type: {phase.TypeValue}` which is not in the closed set "
                    + "(`inline`, `compose`, `lab-wsfc`, `lab-bare-metal`)."));
                continue;
            }

            // lab-* types need BOTH a KeyVault reference AND a GitHub
            // environment reference in the Secrets bullet so the
            // pipeline-YAML emitter has both providers to fill in.
            var setupType = E2eScenariosSetupReader.Parse(phase.TypeValue);
            if (E2eScenariosSetupReader.RequiresLabSecrets(setupType))
            {
                var secretsBody = phase.SecretsBody ?? string.Empty;
                var hasKeyVault = secretsBody.Contains("keyvault", StringComparison.OrdinalIgnoreCase)
                    || secretsBody.Contains("key vault", StringComparison.OrdinalIgnoreCase);
                var hasGitHubEnv = secretsBody.Contains("github environment", StringComparison.OrdinalIgnoreCase)
                    || secretsBody.Contains("gh environment", StringComparison.OrdinalIgnoreCase)
                    // Tolerate the `${{ secrets.* }}` literal as evidence
                    // of GH-environment intent — operators often list the
                    // secret names that way.
                    || secretsBody.Contains("secrets.", StringComparison.OrdinalIgnoreCase);
                if (!hasKeyVault || !hasGitHubEnv)
                {
                    errors.Add(new LintError(
                        ErrorCodes.LabSecretsIncomplete,
                        phase.Name,
                        $"Phase `{phase.Name}` declares `Type: {phase.TypeValue}` (lab) but the `Secrets` bullet "
                        + "must name BOTH a Key Vault entry AND a GitHub environment (Forge supports both providers). "
                        + $"Found: keyvault={hasKeyVault}, githubEnvironment={hasGitHubEnv}."));
                }
            }
        }

        return errors.Count == 0 ? LintResult.Clean : new LintResult(errors);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 6.2 — render a prompt section listing the
    /// linter's hard requirements + (optionally) the current violations.
    /// Injected into the architect's evaluator prompt for the
    /// <c>e2e-scenarios.md</c> doc so the LLM sees the same criteria the
    /// server-side gate enforces. When <paramref name="result"/> is null,
    /// only the rules are listed (use during prompt construction before
    /// the file exists); when populated, the current violations are
    /// surfaced under a `## Current violations` sub-section.
    /// </summary>
    public static string BuildEvaluatorRulesSection(LintResult? result = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## STAGE 6.2 e2e-scenarios linter — REFUSAL CRITERIA");
        sb.AppendLine();
        sb.AppendLine("Every Phase H1 in this doc MUST satisfy ALL of:");
        sb.AppendLine("- A `### Setup` H3 appears directly before `### Scenarios`.");
        sb.AppendLine("  Violation → `" + ErrorCodes.SetupMissing + "`.");
        sb.AppendLine("- The `### Setup` block has a `- **Type**: ...` first bullet whose value is one of");
        sb.AppendLine("  `inline`, `compose`, `lab-wsfc`, `lab-bare-metal`.");
        sb.AppendLine("  Violation → `" + ErrorCodes.SetupTypeInvalid + "`.");
        sb.AppendLine("- When Type is `lab-wsfc` or `lab-bare-metal`, the `- **Secrets**: ...` bullet names");
        sb.AppendLine("  BOTH a Key Vault entry (e.g. `forge-lab-kv`) AND a GitHub environment (e.g. ");
        sb.AppendLine("  `lab-wsfc` / `lab-hw`). Forge supports both providers; either alone is incomplete.");
        sb.AppendLine("  Violation → `" + ErrorCodes.LabSecretsIncomplete + "`.");
        sb.AppendLine();
        sb.AppendLine("You MUST return `verdict: \"iterate\"` (or `reject` if iter ≥ 3) when ANY of these");
        sb.AppendLine("hard requirements is violated, regardless of how good the doc looks on other");
        sb.AppendLine("dimensions. Score MUST be < 90 until every violation is resolved.");
        if (result is not null && !result.IsClean)
        {
            sb.AppendLine();
            sb.AppendLine("### Current violations (Stage 6.2 lint pass)");
            foreach (var err in result.Errors)
            {
                sb.AppendLine($"- `{err.Code}` — {err.Detail}");
            }
        }
        return sb.ToString();
    }

    // ── internal scan ────────────────────────────────────────────────────

    private sealed record PhaseScan(string Name, bool HasSetupHeading, string? TypeValue, string? SecretsBody);

    private static IReadOnlyList<PhaseScan> ScanPhases(string[] lines)
    {
        var phases = new List<PhaseScan>();
        string? currentName = null;
        var hasSetup = false;
        string? typeValue = null;
        string? secretsBody = null;
        var inSetup = false;

        void Flush()
        {
            if (currentName is null) return;
            phases.Add(new PhaseScan(currentName, hasSetup, typeValue, secretsBody));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var phaseMatch = PhaseH1.Match(line);
            if (phaseMatch.Success)
            {
                Flush();
                currentName = phaseMatch.Groups["name"].Value.Trim();
                hasSetup = false;
                typeValue = null;
                secretsBody = null;
                inSetup = false;
                continue;
            }

            if (currentName is null) continue;

            if (SetupH3.IsMatch(line))
            {
                hasSetup = true;
                inSetup = true;
                continue;
            }

            if (inSetup)
            {
                // A new H1 ends the phase (already handled by the phase
                // re-match above on the next loop tick). A new H3 ends
                // the Setup block (e.g. `### Scenarios`).
                if (AnyHigherHeading.IsMatch(line) && !SetupH3.IsMatch(line))
                {
                    inSetup = false;
                    continue;
                }

                if (typeValue is null)
                {
                    var typeMatch = TypeBullet.Match(line);
                    if (typeMatch.Success) typeValue = typeMatch.Groups["value"].Value.Trim();
                }

                if (secretsBody is null)
                {
                    var secretsMatch = SecretsBullet.Match(line);
                    if (secretsMatch.Success) secretsBody = secretsMatch.Groups["body"].Value.Trim();
                }
            }
        }

        Flush();
        return phases;
    }
}
