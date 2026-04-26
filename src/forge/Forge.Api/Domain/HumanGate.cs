// -----------------------------------------------------------------------
// <copyright file="HumanGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Domain;

using Forge.Api.Contracts;

/// <summary>
/// Phase 6.9 — bound from the <c>Forge:HumanGate</c> configuration section.
///
/// A PR is human-gated when ANY of the following match:
///   * its branch contains a substring listed in <see cref="BranchSubstrings"/>;
///   * its title contains a substring listed in <see cref="TitleSubstrings"/>;
///   * one of its tag-derived labels matches an entry in <see cref="Labels"/>;
///   * (later) one of the changed file paths matches a glob in <see cref="PathGlobs"/>.
/// </summary>
public class HumanGateOptions
{
    public const string SectionName = "Forge:HumanGate";

    public List<string> BranchSubstrings { get; set; } = new() { "/security", "/db/", "release/" };

    public List<string> TitleSubstrings { get; set; } = new() { "[security]" };

    public List<string> Labels { get; set; } = new() { "security", "needs-human-review" };

    public List<string> PathGlobs { get; set; } = new() { "db/migrations/**" };
}

public sealed record HumanGateDecision(bool Required, string? Reason);

public interface IHumanGateEvaluator
{
    HumanGateDecision Evaluate(Pr pr);

    /// <summary>Snapshot of the current rule list, used by /api/human-gate/rules.</summary>
    HumanGateOptions Rules();
}

public class HumanGateEvaluator : IHumanGateEvaluator
{
    private readonly HumanGateOptions options;

    public HumanGateEvaluator(Microsoft.Extensions.Options.IOptions<HumanGateOptions> options)
    {
        this.options = options.Value;
    }

    public HumanGateOptions Rules() => this.options;

    public HumanGateDecision Evaluate(Pr pr)
    {
        foreach (var sub in this.options.BranchSubstrings)
        {
            if (!string.IsNullOrEmpty(sub) && pr.Branch.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                return new HumanGateDecision(true, $"branch contains '{sub}'");
            }
        }
        foreach (var sub in this.options.TitleSubstrings)
        {
            if (!string.IsNullOrEmpty(sub) && pr.Title.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                return new HumanGateDecision(true, $"title contains '{sub}'");
            }
        }
        // We don't carry the issue label list on Pr today, but the gate
        // reason ferried by the provider mapper already pre-fills HumanGate
        // for known labels; this keeps that signal authoritative.
        if (pr.HumanGate)
        {
            return new HumanGateDecision(true, pr.GateReason ?? "label rule");
        }
        return new HumanGateDecision(false, null);
    }
}
