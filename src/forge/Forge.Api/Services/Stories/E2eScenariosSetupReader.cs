// -----------------------------------------------------------------------
// <copyright file="E2eScenariosSetupReader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.RegularExpressions;

/// <summary>
/// docs/forge/e2e.md Stage 4.2 (+ Stage 6.3 expansion) — parses the
/// per-phase <c>### Setup</c> block out of <c>e2e-scenarios.md</c> so
/// the qa-e2e producers (deterministic in Stage 4.2, LLM-driven in
/// Stage 4.3, evaluator-linted in Stage 6.2) all read the operator's
/// declared setup type from one place.
///
/// <para>Schema (per docs/forge/e2e.md §8.1):</para>
/// <code>
/// # Phase 1: {phase name}
///
/// ### Setup
/// - **Type**: inline | compose | lab-wsfc | lab-bare-metal
/// - **Local**: ...
/// - **CI runner**: ...
/// - **Secrets**: ...
/// - **Pre-test bootstrap**: ...
///
/// ### Scenarios
/// - **Scenario:** ...
/// </code>
///
/// <para>Stage 4.2 ships the minimum needed by the deterministic
/// producer: <see cref="ReadSetupTypeForPhase"/> returns the
/// closed-set <see cref="E2eSetupType"/> for a phase slug (defaulting
/// to <see cref="E2eSetupType.Inline"/> when the doc is missing or the
/// phase isn't present). Stage 6.2 / 6.3 will extend this with a
/// full <c>SetupBlock</c> projection (CI runner, secret refs, bootstrap
/// command) that the linter + LLM prompt consume.</para>
/// </summary>
public static class E2eScenariosSetupReader
{
    private static readonly Regex PhaseH1 = new(
        @"^#\s+Phase\s+\d+\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SetupH3 = new(
        @"^###\s+Setup\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnyH1 = new(@"^#\s+\S.*$", RegexOptions.Compiled);
    private static readonly Regex AnyH3OrHigher = new(@"^#{1,3}\s+\S.*$", RegexOptions.Compiled);

    // Tolerant of bullet markers, bold variants (`**Type**`, `__Type__`,
    // plain `Type`), and inter-word spacing. The value side is taken
    // verbatim (lower-trimmed) and matched against the closed set below.
    private static readonly Regex TypeBullet = new(
        @"^\s*[-*]\s*(?:\*\*|__)?\s*Type\s*(?:\*\*|__)?\s*:\s*(?<value>[A-Za-z0-9\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// docs/forge/e2e.md §8.2 — the closed set of recognised setup types.
    /// Anything outside this set (including null / empty) resolves to
    /// <see cref="Inline"/> from <see cref="ReadSetupTypeForPhase"/> so
    /// the producer always has a sensible default; the Stage 6.2 linter
    /// is the layer that REFUSES pass on an unknown value.
    /// </summary>
    public static class TypeNames
    {
        public const string Inline = "inline";
        public const string Compose = "compose";
        public const string LabWsfc = "lab-wsfc";
        public const string LabBareMetal = "lab-bare-metal";
    }

    /// <summary>
    /// Read the setup type declared under the <c>### Setup</c> block of
    /// the phase H1 whose name slug matches <paramref name="phaseSlug"/>.
    /// Returns <see cref="E2eSetupType.Inline"/> when:
    /// <list type="bullet">
    /// <item><paramref name="markdown"/> is null / whitespace-only,</item>
    /// <item>the phase H1 isn't present,</item>
    /// <item>the phase has no <c>### Setup</c> H3,</item>
    /// <item>the Setup block has no <c>- **Type**: …</c> bullet, or</item>
    /// <item>the <c>Type</c> value isn't in the closed set.</item>
    /// </list>
    /// </summary>
    public static E2eSetupType ReadSetupTypeForPhase(string? markdown, string phaseSlug)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(phaseSlug))
        {
            return E2eSetupType.Inline;
        }

        // Walk lines tracking phase + section state. We don't need a
        // full parser — the spec gives us a fixed layout (phase H1 →
        // Setup H3 → bullets → next H1 or H3 ends the block).
        var normalised = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalised.Split('\n');

        string? currentPhaseSlug = null;
        var inSetup = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var phaseMatch = PhaseH1.Match(line);
            if (phaseMatch.Success)
            {
                currentPhaseSlug = PlanParser.Slug(phaseMatch.Groups["name"].Value);
                inSetup = false;
                continue;
            }

            if (currentPhaseSlug is null) continue;
            if (!string.Equals(currentPhaseSlug, phaseSlug, StringComparison.OrdinalIgnoreCase)) continue;

            if (SetupH3.IsMatch(line))
            {
                inSetup = true;
                continue;
            }

            if (inSetup)
            {
                // Any new H1 ends the phase; any new H3 (including
                // `### Scenarios`) ends the Setup block.
                if (AnyH1.IsMatch(line) || (AnyH3OrHigher.IsMatch(line) && !SetupH3.IsMatch(line)))
                {
                    inSetup = false;
                    continue;
                }

                var typeMatch = TypeBullet.Match(line);
                if (typeMatch.Success)
                {
                    return Parse(typeMatch.Groups["value"].Value);
                }
            }
        }

        return E2eSetupType.Inline;
    }

    /// <summary>
    /// Parse a free-text type value into the closed-set enum. Unknown
    /// values collapse to <see cref="E2eSetupType.Inline"/> so the
    /// downstream producer never sees an undefined value; the Stage 6.2
    /// linter is the layer that flags unknown values to the operator.
    /// </summary>
    public static E2eSetupType Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return E2eSetupType.Inline;
        var normalised = value.Trim().ToLowerInvariant();
        return normalised switch
        {
            TypeNames.Inline => E2eSetupType.Inline,
            TypeNames.Compose => E2eSetupType.Compose,
            TypeNames.LabWsfc => E2eSetupType.LabWsfc,
            TypeNames.LabBareMetal => E2eSetupType.LabBareMetal,
            _ => E2eSetupType.Inline,
        };
    }

    /// <summary>
    /// Inverse of <see cref="Parse"/> — the canonical wire-format string
    /// for a given enum value. Used by the deterministic emitter when
    /// rendering pipeline YAML so the operator's diff matches the
    /// closed-set vocabulary exactly.
    /// </summary>
    public static string ToWire(E2eSetupType type) => type switch
    {
        E2eSetupType.Inline => TypeNames.Inline,
        E2eSetupType.Compose => TypeNames.Compose,
        E2eSetupType.LabWsfc => TypeNames.LabWsfc,
        E2eSetupType.LabBareMetal => TypeNames.LabBareMetal,
        _ => TypeNames.Inline,
    };

    /// <summary>True when the setup type needs a docker-compose stack
    /// (CI host runs containers) or a self-hosted lab runner — i.e. when
    /// the qa-e2e producer should emit pipeline YAML stubs alongside the
    /// feature + step-def files.</summary>
    public static bool RequiresPipelineYaml(E2eSetupType type) =>
        type is E2eSetupType.Compose or E2eSetupType.LabWsfc or E2eSetupType.LabBareMetal;

    /// <summary>True when the setup type targets a self-hosted lab runner
    /// (Failover Cluster, bare-metal hardware) — i.e. when the generated
    /// pipeline YAML must reference KeyVault / GitHub-environment secrets
    /// for the lab connection.</summary>
    public static bool RequiresLabSecrets(E2eSetupType type) =>
        type is E2eSetupType.LabWsfc or E2eSetupType.LabBareMetal;
}

/// <summary>
/// docs/forge/e2e.md §8.2 closed set of setup types. The deterministic
/// producer (Stage 4.2) branches on this; the LLM prompt (Stage 4.3)
/// surfaces the wire string; the evaluator linter (Stage 6.2) refuses
/// pass on anything that maps to <c>Inline</c> via the fallback.
/// </summary>
public enum E2eSetupType
{
    /// <summary>Runs against the process under test in the same CI host;
    /// no infrastructure prereq. Default when nothing is declared.</summary>
    Inline,

    /// <summary>Spins up a docker-compose stack on the CI runner before
    /// the test step. Hosted runner is sufficient.</summary>
    Compose,

    /// <summary>Windows Server Failover Cluster with Cluster-Aware
    /// Updating enabled. Requires the self-hosted <c>forge-lab-wsfc</c>
    /// runner pool + lab Key Vault secrets.</summary>
    LabWsfc,

    /// <summary>Hardware-specific lab (NICs, drivers). Requires the
    /// self-hosted <c>forge-lab-hw</c> runner pool + lab Key Vault
    /// secrets.</summary>
    LabBareMetal,
}
