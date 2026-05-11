// -----------------------------------------------------------------------
// <copyright file="PlanParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 42 + Phase 46 — canonicalises <c>implementation-plan.md</c> into a
/// Phase / Stage / Step tree with stable per-node anchor slugs and content
/// hashes. Both the initial decompose path and <see cref="PlanReconciler"/>
/// consume the same canonical form so artefact + DB stay in lockstep.
///
/// <para>Anchor convention:
/// <list type="bullet">
/// <item>Phase: <c>phase-{phase-name-slug}</c></item>
/// <item>Stage: <c>phase-{phase-name-slug}/stage-{stage-name-slug}</c></item>
/// <item>Step:  <c>phase-{phase-name-slug}/stage-{stage-name-slug}/step-{step-text-slug}</c></item>
/// </list>
/// Anchors are name-derived so renumbering (insert / remove a phase) does NOT
/// break the join. A pure rename (same text edited) shows as delete+add to
/// the reconciler — acceptable at v0; the content hash lets a future polish
/// pass infer "same body, different name" as a rename.</para>
///
/// <para>Content hash: SHA-256 of the canonical body text (heading + bullets
/// in order, line endings normalised). Same anchor + different hash means
/// "same node, scope edited" — the reconciler re-prompts engineers on open
/// PRs without re-creating the row.</para>
///
/// <para>Phase 46 — v2 layout: phases are H1 (was H2), stages are H2 (was
/// H3), subsections are H3 (was H4). Story title moves to YAML front-matter
/// at the top of the file. Each phase has an optional <c>## Dependencies</c>
/// block; each stage has a required ordered triple of subsections —
/// <c>### Implementation Steps</c>, <c>### Dependencies</c>, <c>### Test
/// Scenarios</c>. The parser keeps backward-compat: legacy v1 docs (H2
/// phase, H3 stage, H4 subsection, no dependencies) still parse with empty
/// dependency lists. Format is detected from the first matching phase
/// heading and locked for the rest of the file.</para>
/// </summary>
public static class PlanParser
{
    /// <summary>Phase 16.5 plan-tree validator caps. Same constants as <c>PlanRubricConstants</c>.</summary>
    public const int MaxStagesPerPhase = 20;
    public const int MaxStepsPerStage = 20;

    /// <summary>
    /// Phase 46 — error codes returned in <see cref="CanonicalPlan.ParseErrors"/>.
    /// String constants so callers can branch / count without parsing prose.
    /// </summary>
    public static class ErrorCodes
    {
        public const string FormatUnknown = "plan.format_unknown";
        public const string StageSubsectionOrderInvalid = "plan.stage_subsection_order_invalid";
        public const string DuplicateStageTitle = "plan.duplicate_stage_title";
    }

    /// <summary>
    /// Phase 46 — start-marker italic body. The architect emits this exact
    /// string under a <c>## Dependencies</c> or <c>### Dependencies</c>
    /// subsection to signal "no upstream — this is a start node".
    /// </summary>
    public const string StartPhaseMarker = "_none — start phase_";

    /// <summary>Phase 46 — see <see cref="StartPhaseMarker"/>.</summary>
    public const string StartStageMarker = "_none — start stage_";

    /// <summary>One canonical phase parsed from <c># Phase N: name</c> (v2) or <c>## Phase N: name</c> (v1).</summary>
    public sealed record CanonicalPhase(
        string Anchor,
        string Name,
        int Number,
        IReadOnlyList<CanonicalStage> Stages,
        IReadOnlyList<string> Dependencies,
        bool IsStartPhase,
        string ContentHash);

    /// <summary>One canonical stage parsed from <c>## Stage N.M: name</c> (v2) or <c>### Stage N.M: name</c> (v1).</summary>
    public sealed record CanonicalStage(
        string Anchor,
        string Name,
        int PhaseNumber,
        int StageNumber,
        IReadOnlyList<CanonicalStep> Steps,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<string> TestScenarios,
        bool IsStartStage,
        string ContentHash);

    /// <summary>One canonical step parsed from a checkbox bullet under <c>### Implementation Steps</c> (v2) or <c>#### Implementation Steps</c> (v1).</summary>
    public sealed record CanonicalStep(
        string Anchor,
        string Text,
        int Index,
        bool Checked,
        string ContentHash);

    /// <summary>Detected layout version. Locked from the first phase heading match.</summary>
    public enum PlanFormat
    {
        /// <summary>Format wasn't determined (file is empty or has no phase headings).</summary>
        Unknown,

        /// <summary>Pre-Phase-46 layout: <c>## Phase</c>, <c>### Stage</c>, <c>#### subsection</c>, no dependencies blocks.</summary>
        V1Legacy,

        /// <summary>Phase 46 layout: <c># Phase</c>, <c>## Stage</c>, <c>### subsection</c>, with <c>## Dependencies</c> per phase and <c>### Dependencies</c> per stage.</summary>
        V2,
    }

    /// <summary>Result of parsing a plan file. <see cref="Phases"/> is empty when the file is missing or unreadable.</summary>
    public sealed record CanonicalPlan(
        IReadOnlyList<CanonicalPhase> Phases,
        IReadOnlyDictionary<string, string> FrontMatter,
        IReadOnlyList<string> ParseErrors,
        PlanFormat Format)
    {
        public IEnumerable<CanonicalStage> AllStages() => this.Phases.SelectMany(p => p.Stages);

        public IEnumerable<CanonicalStep> AllSteps() => this.AllStages().SelectMany(s => s.Steps);

        public static CanonicalPlan Empty { get; } = new(
            Array.Empty<CanonicalPhase>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            PlanFormat.Unknown);
    }

    // --- v2 (Phase 46) regexes -----------------------------------------------------

    private static readonly Regex V2PhaseHeading = new(
        @"^#\s+Phase\s+(?<num>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex V2StageHeading = new(
        @"^##\s+Stage\s+(?<phase>\d+)\.(?<stage>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Phase-level dependency block — <c>## Dependencies</c>. Distinguished from <see cref="V2StageHeading"/> by the missing <c>Stage N.M:</c> token.</summary>
    private static readonly Regex V2PhaseDepsHeading = new(
        @"^##\s+Dependencies\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex V2StageSubHeading = new(
        @"^###\s+(?<name>Implementation\s+Steps|Dependencies|Test\s+Scenarios)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // --- v1 (legacy) regexes -------------------------------------------------------

    private static readonly Regex V1PhaseHeading = new(
        @"^##\s+Phase\s+(?<num>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex V1StageHeading = new(
        @"^###\s+Stage\s+(?<phase>\d+)\.(?<stage>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex V1StageSubHeading = new(
        @"^####\s+(?<name>Implementation\s+Steps|Test\s+Scenarios)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // --- shared regexes ------------------------------------------------------------

    private static readonly Regex CheckboxBullet = new(
        @"^\s*[-*]\s*\[(?<state>[ xX])\]\s*(?<text>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>Plain bullet — no checkbox. Used for <c>Dependencies</c> entries.</summary>
    private static readonly Regex PlainBullet = new(
        @"^\s*[-*]\s+(?<text>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex SlugStripper = new(
        @"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Parse the contents of an <c>implementation-plan.md</c> file. Tolerates
    /// missing subsections; returns an empty plan when no phases are detected.
    /// </summary>
    public static CanonicalPlan Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return CanonicalPlan.Empty;
        }

        // Normalise line endings so the content hash is stable across CRLF/LF.
        var normalised = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // Phase 46 — split off optional YAML front-matter at the top.
        var (frontMatter, body) = ExtractFrontMatter(normalised);
        var lines = body.Split('\n');

        // Phase 46 — detect format from the first matching phase heading.
        // The H2 + H3 stage shape is the only ambiguity; check H1 first
        // because a v2 doc only emits H2 phase headings as the (rare)
        // mistake of forgetting to shift up — we treat that as v1.
        var format = DetectFormat(lines);
        var errors = new List<string>();

        if (format == PlanFormat.Unknown)
        {
            errors.Add(ErrorCodes.FormatUnknown);
            return new CanonicalPlan(
                Array.Empty<CanonicalPhase>(),
                frontMatter,
                errors,
                PlanFormat.Unknown);
        }

        var (phaseHeading, stageHeading, phaseDepsHeading, stageSubHeading) =
            format == PlanFormat.V2
                ? (V2PhaseHeading, V2StageHeading, (Regex?)V2PhaseDepsHeading, V2StageSubHeading)
                : (V1PhaseHeading, V1StageHeading, (Regex?)null, V1StageSubHeading);

        var phases = new List<MutablePhase>();
        MutablePhase? currentPhase = null;
        MutableStage? currentStage = null;

        // Subsection state machine. PhaseDeps applies between a phase
        // heading and the first stage heading. Stage subsections apply
        // inside a stage. We track the highest-ordered subsection seen
        // for each stage so we can flag out-of-order subsections.
        var currentSection = SectionKind.None;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var phaseMatch = phaseHeading.Match(line);
            if (phaseMatch.Success)
            {
                currentPhase = new MutablePhase
                {
                    Number = int.Parse(phaseMatch.Groups["num"].Value, CultureInfo.InvariantCulture),
                    Name = phaseMatch.Groups["name"].Value.Trim(),
                };
                phases.Add(currentPhase);
                currentStage = null;
                currentSection = SectionKind.None;
                continue;
            }

            // Stage heading must match before phase-deps / subsection
            // checks because stage and phase-deps share the same heading
            // level (both H2 in v2, H3 in v1) — the stage regex's
            // mandatory "Stage N.M:" token disambiguates.
            var stageMatch = stageHeading.Match(line);
            if (stageMatch.Success && currentPhase is not null)
            {
                currentStage = new MutableStage
                {
                    PhaseNumber = int.Parse(stageMatch.Groups["phase"].Value, CultureInfo.InvariantCulture),
                    StageNumber = int.Parse(stageMatch.Groups["stage"].Value, CultureInfo.InvariantCulture),
                    Name = stageMatch.Groups["name"].Value.Trim(),
                };
                currentPhase.Stages.Add(currentStage);
                currentSection = SectionKind.None;
                continue;
            }

            // v2-only — phase-level Dependencies block right under H1.
            if (phaseDepsHeading is not null
                && currentPhase is not null
                && currentStage is null
                && phaseDepsHeading.IsMatch(line))
            {
                currentSection = SectionKind.PhaseDependencies;
                continue;
            }

            var subMatch = stageSubHeading.Match(line);
            if (subMatch.Success && currentStage is not null)
            {
                var name = subMatch.Groups["name"].Value.Trim();
                var nextSection = ResolveStageSubsection(name);

                // Phase 46 Q46.3 — subsections must appear in the fixed
                // order Steps -> Dependencies -> Test Scenarios. Going
                // backwards (next < highest) is the violation. Equal
                // value is treated as a duplicate subsection and the
                // bullets append to the existing collection.
                if (nextSection != SectionKind.None
                    && currentStage.HighestSectionSeen != SectionKind.None
                    && nextSection < currentStage.HighestSectionSeen
                    && !errors.Contains(ErrorCodes.StageSubsectionOrderInvalid))
                {
                    errors.Add(ErrorCodes.StageSubsectionOrderInvalid);
                }

                if (nextSection != SectionKind.None && nextSection > currentStage.HighestSectionSeen)
                {
                    currentStage.HighestSectionSeen = nextSection;
                }

                currentSection = nextSection;
                continue;
            }

            // Bullet handling — depends on which section we're in.
            switch (currentSection)
            {
                case SectionKind.PhaseDependencies when currentPhase is not null:
                    {
                        var bullet = PlainBullet.Match(line);
                        if (!bullet.Success) break;
                        var text = bullet.Groups["text"].Value.Trim();
                        if (text.Length == 0) break;
                        if (IsStartMarker(text, isPhase: true))
                        {
                            currentPhase.IsStartPhase = true;
                            break;
                        }
                        currentPhase.Dependencies.Add(text);
                        break;
                    }

                case SectionKind.StageSteps when currentStage is not null:
                    {
                        var bullet = CheckboxBullet.Match(line);
                        if (!bullet.Success) break;
                        var text = bullet.Groups["text"].Value.Trim();
                        if (text.Length == 0) break;
                        var isChecked = bullet.Groups["state"].Value is "x" or "X";
                        currentStage.Steps.Add((text, isChecked));
                        break;
                    }

                case SectionKind.StageDependencies when currentStage is not null:
                    {
                        var bullet = PlainBullet.Match(line);
                        if (!bullet.Success) break;
                        var text = bullet.Groups["text"].Value.Trim();
                        if (text.Length == 0) break;
                        if (IsStartMarker(text, isPhase: false))
                        {
                            currentStage.IsStartStage = true;
                            break;
                        }
                        currentStage.Dependencies.Add(text);
                        break;
                    }

                case SectionKind.StageTestScenarios when currentStage is not null:
                    {
                        var bullet = CheckboxBullet.Match(line);
                        if (!bullet.Success)
                        {
                            // Tolerate plain-bullet test scenarios — older
                            // architect prompts emitted them without
                            // checkboxes.
                            var plain = PlainBullet.Match(line);
                            if (!plain.Success) break;
                            var fallbackText = plain.Groups["text"].Value.Trim();
                            if (fallbackText.Length == 0) break;
                            currentStage.Scenarios.Add(fallbackText);
                            break;
                        }
                        var text = bullet.Groups["text"].Value.Trim();
                        if (text.Length == 0) break;
                        currentStage.Scenarios.Add(text);
                        break;
                    }
            }
        }

        // A start phase / start stage with explicit deps is contradictory;
        // the explicit deps win and we drop the start flag silently. The
        // architect's evaluator catches the mismatch via the rubric.
        foreach (var phase in phases)
        {
            if (phase.Dependencies.Count > 0) phase.IsStartPhase = false;
            foreach (var stage in phase.Stages)
            {
                if (stage.Dependencies.Count > 0) stage.IsStartStage = false;
            }
        }

        // Phase 46 — duplicate stage titles make title-form deps ambiguous
        // (resolver's stagesByTitle map keeps first-seen). Surface as a
        // parse error so the caller refuses decomposition; the architect's
        // next iteration renames the duplicate.
        var titleSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in phases.SelectMany(p => p.Stages))
        {
            if (!titleSeen.Add(stage.Name))
            {
                if (!errors.Contains(ErrorCodes.DuplicateStageTitle))
                {
                    errors.Add(ErrorCodes.DuplicateStageTitle);
                }
            }
        }

        return new CanonicalPlan(
            phases.Select(BuildPhase).ToList(),
            frontMatter,
            errors,
            format);
    }

    /// <summary>
    /// Phase 46 — extract the YAML front-matter delimited by <c>---</c>
    /// lines at the very top of the file (if present). Returns
    /// (frontMatter, body) where <c>body</c> is the original markdown
    /// minus the front-matter block. Tolerates missing front-matter
    /// (returns empty dictionary + original body).
    /// </summary>
    private static (IReadOnlyDictionary<string, string> FrontMatter, string Body) ExtractFrontMatter(string normalised)
    {
        var frontMatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (frontMatter, normalised);
        }

        // Find the closing --- on its own line. Bail to no-front-matter
        // when there's no closer (treat the whole file as body so the
        // operator's plan is at least partially parseable).
        var closeIdx = normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closeIdx < 0)
        {
            return (frontMatter, normalised);
        }

        var fmBody = normalised.Substring(4, closeIdx - 4);
        var rest = normalised.Substring(closeIdx + 5);

        foreach (var raw in fmBody.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            // Strip optional surrounding quotes.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }
            frontMatter[key] = value;
        }

        return (frontMatter, rest);
    }

    /// <summary>
    /// Phase 46 — sniff format from the first phase heading match. v2 (H1)
    /// wins if seen before v1 (H2). Files with no phase heading return
    /// <see cref="PlanFormat.Unknown"/>.
    /// </summary>
    private static PlanFormat DetectFormat(string[] lines)
    {
        foreach (var raw in lines)
        {
            if (V2PhaseHeading.IsMatch(raw))
            {
                return PlanFormat.V2;
            }
            if (V1PhaseHeading.IsMatch(raw))
            {
                return PlanFormat.V1Legacy;
            }
        }
        return PlanFormat.Unknown;
    }

    private static SectionKind ResolveStageSubsection(string name)
    {
        // Whitespace inside the heading (e.g. "Implementation  Steps")
        // is collapsed at the regex level via \s+, but the captured name
        // preserves multiple spaces; normalise here for the comparison.
        var compact = Regex.Replace(name, @"\s+", " ");
        if (compact.StartsWith("Implementation Steps", StringComparison.OrdinalIgnoreCase))
            return SectionKind.StageSteps;
        if (compact.StartsWith("Dependencies", StringComparison.OrdinalIgnoreCase))
            return SectionKind.StageDependencies;
        if (compact.StartsWith("Test Scenarios", StringComparison.OrdinalIgnoreCase))
            return SectionKind.StageTestScenarios;
        return SectionKind.None;
    }

    private static bool IsStartMarker(string text, bool isPhase)
    {
        // Tolerate variants the architect might emit: with/without italic
        // underscores, with/without the em-dash, and the alternate noun.
        var compact = Regex.Replace(text, @"\s+", " ").Trim().Trim('_').Trim();
        if (compact.Length == 0) return false;
        var marker = isPhase ? "none — start phase" : "none — start stage";
        var altMarker = isPhase ? "none - start phase" : "none - start stage";
        return string.Equals(compact, marker, StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, altMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static CanonicalPhase BuildPhase(MutablePhase p)
    {
        var phaseSlug = Slug(p.Name);
        var phaseAnchor = $"phase-{phaseSlug}";

        // Cap to MaxStagesPerPhase per Phase 16.5; extras are ignored (the
        // architect's evaluator should have rejected them upstream, but we
        // refuse to hold more than 20 to keep DB rows bounded).
        var stages = p.Stages.Take(MaxStagesPerPhase).Select(s => BuildStage(phaseAnchor, s)).ToList();

        // Phase 46 — phase deps + start flag participate in the content
        // hash WHEN PRESENT so the reconciler treats a dep edit like a step
        // edit (re-prompts engineers on open PRs without re-creating the
        // row). v1 docs (no Dependencies block, no start marker) skip these
        // additions so their hash matches the legacy format byte-for-byte
        // and existing rows don't churn through "scope edited" on first
        // post-Phase-46 reconcile.
        var hashParts = new List<string>
        {
            $"phase|{p.Number}|{p.Name}",
            string.Join("\n", stages.Select(s => s.ContentHash)),
        };
        if (p.Dependencies.Count > 0)
        {
            hashParts.Add($"deps|{string.Join(",", p.Dependencies)}");
        }
        if (p.IsStartPhase)
        {
            hashParts.Add("start|phase");
        }

        return new CanonicalPhase(
            Anchor: phaseAnchor,
            Name: p.Name,
            Number: p.Number,
            Stages: stages,
            Dependencies: p.Dependencies.ToList(),
            IsStartPhase: p.IsStartPhase,
            ContentHash: HashOf(hashParts.ToArray()));
    }

    private static CanonicalStage BuildStage(string phaseAnchor, MutableStage s)
    {
        var stageSlug = Slug(s.Name);
        var stageAnchor = $"{phaseAnchor}/stage-{stageSlug}";

        var steps = s.Steps.Take(MaxStepsPerStage)
            .Select((t, idx) => BuildStep(stageAnchor, idx, t.text, t.isChecked))
            .ToList();

        // Phase 46 — same back-compat rule as BuildPhase: deps/start flags
        // only enter the body key when present, so v1 stage rows hash the
        // same as before.
        var bodyParts = new List<string>
        {
            $"stage|{s.PhaseNumber}.{s.StageNumber}|{s.Name}",
            string.Join("\n", steps.Select(st => st.ContentHash)),
            string.Join("\n", s.Scenarios),
        };
        if (s.Dependencies.Count > 0)
        {
            bodyParts.Add($"deps|{string.Join(",", s.Dependencies)}");
        }
        if (s.IsStartStage)
        {
            bodyParts.Add("start|stage");
        }
        var bodyKey = string.Join("\n", bodyParts);

        return new CanonicalStage(
            Anchor: stageAnchor,
            Name: s.Name,
            PhaseNumber: s.PhaseNumber,
            StageNumber: s.StageNumber,
            Steps: steps,
            Dependencies: s.Dependencies.ToList(),
            TestScenarios: s.Scenarios.ToList(),
            IsStartStage: s.IsStartStage,
            ContentHash: HashOf(bodyKey));
    }

    private static CanonicalStep BuildStep(string stageAnchor, int index, string text, bool isChecked)
    {
        var slug = Slug(text);
        // Truncate the slug component so anchors stay readable and bounded.
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        // Step anchor includes the index so two steps with similar wording stay distinct.
        var anchor = $"{stageAnchor}/step-{index + 1}-{slug}";
        return new CanonicalStep(
            Anchor: anchor,
            Text: text,
            Index: index,
            Checked: isChecked,
            ContentHash: HashOf($"step|{index}|{text}"));
    }

    /// <summary>
    /// Lower-case kebab-style slug. Drops anything that isn't <c>[a-z0-9]</c>;
    /// collapses runs of separators; trims leading/trailing dashes; falls
    /// back to <c>untitled</c> when the input is empty after cleaning.
    /// </summary>
    public static string Slug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "untitled";
        var lower = s.ToLowerInvariant();
        var collapsed = SlugStripper.Replace(lower, "-").Trim('-');
        return collapsed.Length == 0 ? "untitled" : collapsed;
    }

    /// <summary>SHA-256 of the concatenated parts, hex-encoded. Stable across runs.</summary>
    public static string HashOf(params string[] parts)
    {
        var joined = string.Join("", parts);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Phase 46 — section the parser is currently inside. Ordering is the
    /// contract from Q46.3: Steps -> Dependencies -> Test Scenarios; an
    /// out-of-order transition records <see cref="ErrorCodes.StageSubsectionOrderInvalid"/>.
    /// </summary>
    private enum SectionKind
    {
        None = 0,
        PhaseDependencies = 1,
        StageSteps = 2,
        StageDependencies = 3,
        StageTestScenarios = 4,
    }

    private sealed class MutablePhase
    {
        public int Number { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<MutableStage> Stages { get; } = new();

        public List<string> Dependencies { get; } = new();

        public bool IsStartPhase { get; set; }
    }

    private sealed class MutableStage
    {
        public int PhaseNumber { get; set; }

        public int StageNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<(string text, bool isChecked)> Steps { get; } = new();

        public List<string> Dependencies { get; } = new();

        public List<string> Scenarios { get; } = new();

        public bool IsStartStage { get; set; }

        /// <summary>Tracks the highest-ordered subsection seen so the order rule can fire on regressions.</summary>
        public SectionKind HighestSectionSeen { get; set; } = SectionKind.None;
    }
}
