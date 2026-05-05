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
/// Phase 42 — canonicalises <c>implementation-plan.md</c> into a Phase / Stage /
/// Step tree with stable per-node anchor slugs and content hashes. Both the
/// initial decompose path and <see cref="PlanReconciler"/> consume the same
/// canonical form so artefact + DB stay in lockstep.
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
/// </summary>
public static class PlanParser
{
    /// <summary>Phase 16.5 plan-tree validator caps. Same constants as <c>PlanRubricConstants</c>.</summary>
    public const int MaxStagesPerPhase = 20;
    public const int MaxStepsPerStage = 20;

    /// <summary>One canonical phase parsed from <c>## Phase N: name</c>.</summary>
    public sealed record CanonicalPhase(
        string Anchor,
        string Name,
        int Number,
        IReadOnlyList<CanonicalStage> Stages,
        string ContentHash);

    /// <summary>One canonical stage parsed from <c>### Stage N.M: name</c>.</summary>
    public sealed record CanonicalStage(
        string Anchor,
        string Name,
        int PhaseNumber,
        int StageNumber,
        IReadOnlyList<CanonicalStep> Steps,
        IReadOnlyList<string> TestScenarios,
        string ContentHash);

    /// <summary>One canonical step parsed from a checkbox bullet under <c>#### Implementation Steps</c>.</summary>
    public sealed record CanonicalStep(
        string Anchor,
        string Text,
        int Index,
        bool Checked,
        string ContentHash);

    /// <summary>Result of parsing a plan file. <see cref="Phases"/> is empty when the file is missing or unreadable.</summary>
    public sealed record CanonicalPlan(IReadOnlyList<CanonicalPhase> Phases)
    {
        public IEnumerable<CanonicalStage> AllStages() => this.Phases.SelectMany(p => p.Stages);

        public IEnumerable<CanonicalStep> AllSteps() => this.AllStages().SelectMany(s => s.Steps);
    }

    private static readonly Regex PhaseHeading = new(
        @"^##\s+Phase\s+(?<num>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StageHeading = new(
        @"^###\s+Stage\s+(?<phase>\d+)\.(?<stage>\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SubsectionHeading = new(
        @"^####\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex CheckboxBullet = new(
        @"^\s*[-*]\s*\[(?<state>[ xX])\]\s*(?<text>.+?)\s*$",
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
            return new CanonicalPlan(Array.Empty<CanonicalPhase>());
        }

        // Normalise line endings so the content hash is stable across CRLF/LF.
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var phases = new List<MutablePhase>();
        MutablePhase? currentPhase = null;
        MutableStage? currentStage = null;
        var currentSubsection = SubsectionKind.None;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var phaseMatch = PhaseHeading.Match(line);
            if (phaseMatch.Success)
            {
                currentPhase = new MutablePhase
                {
                    Number = int.Parse(phaseMatch.Groups["num"].Value, CultureInfo.InvariantCulture),
                    Name = phaseMatch.Groups["name"].Value.Trim(),
                };
                phases.Add(currentPhase);
                currentStage = null;
                currentSubsection = SubsectionKind.None;
                continue;
            }

            var stageMatch = StageHeading.Match(line);
            if (stageMatch.Success && currentPhase is not null)
            {
                currentStage = new MutableStage
                {
                    PhaseNumber = int.Parse(stageMatch.Groups["phase"].Value, CultureInfo.InvariantCulture),
                    StageNumber = int.Parse(stageMatch.Groups["stage"].Value, CultureInfo.InvariantCulture),
                    Name = stageMatch.Groups["name"].Value.Trim(),
                };
                currentPhase.Stages.Add(currentStage);
                currentSubsection = SubsectionKind.None;
                continue;
            }

            var subMatch = SubsectionHeading.Match(line);
            if (subMatch.Success)
            {
                var name = subMatch.Groups["name"].Value.Trim();
                currentSubsection = name.StartsWith("Implementation Steps", StringComparison.OrdinalIgnoreCase)
                    ? SubsectionKind.Steps
                    : name.StartsWith("Test Scenarios", StringComparison.OrdinalIgnoreCase)
                        ? SubsectionKind.Scenarios
                        : SubsectionKind.None;
                continue;
            }

            if (currentStage is null) continue;

            var bulletMatch = CheckboxBullet.Match(line);
            if (!bulletMatch.Success) continue;

            var text = bulletMatch.Groups["text"].Value.Trim();
            if (text.Length == 0) continue;
            var isChecked = bulletMatch.Groups["state"].Value is "x" or "X";

            switch (currentSubsection)
            {
                case SubsectionKind.Steps:
                    currentStage.Steps.Add((text, isChecked));
                    break;
                case SubsectionKind.Scenarios:
                    currentStage.Scenarios.Add(text);
                    break;
            }
        }

        return new CanonicalPlan(phases.Select(BuildPhase).ToList());
    }

    private static CanonicalPhase BuildPhase(MutablePhase p)
    {
        var phaseSlug = Slug(p.Name);
        var phaseAnchor = $"phase-{phaseSlug}";

        // Cap to MaxStagesPerPhase per Phase 16.5; extras are ignored (the
        // architect's evaluator should have rejected them upstream, but we
        // refuse to hold more than 20 to keep DB rows bounded).
        var stages = p.Stages.Take(MaxStagesPerPhase).Select(s => BuildStage(phaseAnchor, s)).ToList();

        var hash = HashOf(
            $"phase|{p.Number}|{p.Name}",
            string.Join("\n", stages.Select(s => s.ContentHash)));

        return new CanonicalPhase(
            Anchor: phaseAnchor,
            Name: p.Name,
            Number: p.Number,
            Stages: stages,
            ContentHash: hash);
    }

    private static CanonicalStage BuildStage(string phaseAnchor, MutableStage s)
    {
        var stageSlug = Slug(s.Name);
        var stageAnchor = $"{phaseAnchor}/stage-{stageSlug}";

        var steps = s.Steps.Take(MaxStepsPerStage)
            .Select((t, idx) => BuildStep(stageAnchor, idx, t.text, t.isChecked))
            .ToList();

        var bodyKey = string.Join("\n", new[]
        {
            $"stage|{s.PhaseNumber}.{s.StageNumber}|{s.Name}",
            string.Join("\n", steps.Select(st => st.ContentHash)),
            string.Join("\n", s.Scenarios),
        });

        return new CanonicalStage(
            Anchor: stageAnchor,
            Name: s.Name,
            PhaseNumber: s.PhaseNumber,
            StageNumber: s.StageNumber,
            Steps: steps,
            TestScenarios: s.Scenarios.ToList(),
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
        var joined = string.Join("", parts);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private enum SubsectionKind
    {
        None,
        Steps,
        Scenarios,
    }

    private sealed class MutablePhase
    {
        public int Number { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<MutableStage> Stages { get; } = new();
    }

    private sealed class MutableStage
    {
        public int PhaseNumber { get; set; }

        public int StageNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<(string text, bool isChecked)> Steps { get; } = new();

        public List<string> Scenarios { get; } = new();
    }
}
