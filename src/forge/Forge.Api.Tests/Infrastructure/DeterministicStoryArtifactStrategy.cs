// -----------------------------------------------------------------------
// <copyright file="DeterministicStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using System.Text;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;

/// <summary>
/// TEST-ONLY <see cref="IStoryArtifactStrategy"/> — produces deterministic
/// markdown / feature / step content from templates so the Phase 28
/// orchestrator lifecycle is drivable in xUnit without an LLM.
///
/// <para>
/// This class lives in the test assembly on purpose. Production must never
/// be able to bind it: the spec says every plan / decompose / impl artifact
/// is real LLM output, and a templated stub leaking into prod paths leaves
/// behind on-disk files (under <c>docs/stories/&lt;story&gt;/</c>) that look
/// like real plans but ignore the actual story description. We removed an
/// instance of exactly that pollution by moving this class out of the
/// production assembly.
/// </para>
/// <para>
/// Output is a function of (spawn, story, workstream) so re-runs are
/// byte-identical.
/// </para>
/// </summary>
public sealed class DeterministicStoryArtifactStrategy : IStoryArtifactStrategy
{
    public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        var dir = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}";
        var techSpec = new StringBuilder()
            .AppendLine($"# Tech spec — {story.Title}")
            .AppendLine()
            .AppendLine($"Story: `{story.Id}`")
            .AppendLine()
            .AppendLine("## Goals")
            .AppendLine($"- Deliver {story.Title.ToLowerInvariant()} end-to-end across the four crews.")
            .AppendLine()
            .AppendLine("## Constraints")
            .AppendLine("- Backwards-compatible with the existing public API.")
            .AppendLine("- No new infrastructure dependencies.")
            .AppendLine()
            .AppendLine("## Risks")
            .AppendLine("- Schema migration on the live DB during cut-over.")
            .ToString();

        var architecture = new StringBuilder()
            .AppendLine($"# Architecture — {story.Title}")
            .AppendLine()
            .AppendLine("## Components")
            .AppendLine("- API surface (Forge.Api endpoints)")
            .AppendLine("- Persistence (EF Core entities + idempotent migration)")
            .AppendLine("- UI surface (React components)")
            .AppendLine()
            .AppendLine("## Data flow")
            .AppendLine("Operator → UI → REST → service → SQLite + provider remote.")
            .AppendLine()
            .AppendLine("## API contracts")
            .AppendLine("See `tech-spec.md` for endpoint shapes.")
            .ToString();

        var implPlan = new StringBuilder()
            .AppendLine($"# Implementation plan — {story.Title}")
            .AppendLine()
            .AppendLine("## Ordered work")
            .AppendLine("1. Data model + migration")
            .AppendLine("2. REST endpoints with validation")
            .AppendLine("3. UI component")
            .AppendLine("4. Provider write paths")
            .AppendLine("5. Tests + docs")
            .ToString();

        var e2e = new StringBuilder()
            .AppendLine($"# E2E scenarios — {story.Title}")
            .AppendLine()
            .AppendLine("## AC-1 happy path")
            .AppendLine($"Given an operator selects `{story.Id}`,")
            .AppendLine("When they trigger the primary action,")
            .AppendLine("Then the system completes the workflow end-to-end.")
            .AppendLine()
            .AppendLine("## AC-2 error handling")
            .AppendLine("Given an upstream call fails,")
            .AppendLine("When the agent retries,")
            .AppendLine("Then the failure is surfaced and recovery succeeds.")
            .ToString();

        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{dir}/tech-spec.md"] = techSpec,
            [$"{dir}/architecture.md"] = architecture,
            [$"{dir}/implementation-plan.md"] = implPlan,
            [$"{dir}/e2e-scenarios.md"] = e2e,
        };
        return Task.FromResult<IReadOnlyDictionary<string, string>>(dict);
    }

    public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        // Two-workstream decomposition is the spec's typical example. The
        // first workstream covers the API + data model; the second covers
        // the UI integration. Deterministic, no mutual deps.
        var ws = new List<DecompositionWorkstream>
        {
            new(
                Title: "api",
                Description: $"API surface + data model for {story.Title}",
                AcceptanceCriteria: new[] { "Endpoints live", "Migration idempotent" },
                Deps: Array.Empty<string>(),
                Deliverables: new[] { "REST endpoints", "Migration", "Unit tests" },
                Timeline: "2d",
                EstimatedEffort: "5pts"),
            new(
                Title: "ui",
                Description: $"UI integration for {story.Title}",
                AcceptanceCriteria: new[] { "Modal renders", "API wiring lives" },
                Deps: new[] { "api" },
                Deliverables: new[] { "Modal component", "API client", "Wiring" },
                Timeline: "1d",
                EstimatedEffort: "3pts"),
        };
        return Task.FromResult<IReadOnlyList<DecompositionWorkstream>>(ws);
    }

    public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Each workstream lays a single deterministic file at a path that
        // depends on the workstream slug. Keeps the test fixture small but
        // gives the artifact-existence check + commit/PR flow real content.
        var slug = workstream.Id.Split('-').Last();
        var path = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{slug}.md";
        var body = new StringBuilder()
            .AppendLine($"# {workstream.Name}")
            .AppendLine()
            .AppendLine($"Workstream: `{workstream.Id}`")
            .AppendLine($"Branch: `{workstream.Branch}`")
            .AppendLine()
            .AppendLine($"Implements the `{slug}` slice of story `{spawn.StoryId}` per the merged plan.")
            .ToString();
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal) { [path] = body });
    }

    public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
    {
        // Round 0: post one comment. Round 1+: approve.
        if (round == 0)
        {
            var slug = workstream.Id.Split('-').Last();
            var path = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{slug}.md";
            return Task.FromResult<IReadOnlyList<ReviewComment>>(new[]
            {
                new ReviewComment(path, 1, "Title looks too generic — please clarify the slice.", "info"),
            });
        }
        return Task.FromResult<IReadOnlyList<ReviewComment>>(Array.Empty<ReviewComment>());
    }

    public Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
    {
        // Append a clarification line to the workstream artifact and reply.
        var newContent = new StringBuilder()
            .AppendLine($"# {workstream.Name}")
            .AppendLine()
            .AppendLine($"Workstream: `{workstream.Id}`")
            .AppendLine($"Branch: `{workstream.Branch}`")
            .AppendLine()
            .AppendLine($"Implements the `{workstream.Id.Split('-').Last()}` slice of story `{spawn.StoryId}` per the merged plan.")
            .AppendLine()
            .AppendLine("> Clarification: this slice is the concrete delivery of the contract documented in the plan PR; see `architecture.md` for the component diagram.")
            .ToString();
        return Task.FromResult(new ReviewFix(comment.FilePath, newContent, "Done — clarified the slice in the file."));
    }

    public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ws in workstreams)
        {
            var slug = ws.Id.Split('-').Last();
            var path = $"tests/Features/{StoryPaths.Safe(spawn.StoryId)}/{slug}.feature";
            var body = new StringBuilder()
                .AppendLine($"@story-{spawn.StoryId} @ws-{slug}")
                .AppendLine($"Feature: {ws.Name} — {story.Title}")
                .AppendLine()
                .AppendLine("  Scenario: AC-1 happy path")
                .AppendLine($"    Given the {slug} slice is shipped")
                .AppendLine("    When the operator triggers the primary action")
                .AppendLine("    Then the workflow completes end-to-end")
                .ToString();
            dict[path] = body;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(dict);
    }

    public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var slug = workstream.Id.Split('-').Last();
        var path = $"tests/Steps/{StoryPaths.Safe(spawn.StoryId)}/{slug}Steps.cs";
        var body = new StringBuilder()
            .AppendLine("// Auto-generated step definitions for Phase 28 spawn (deterministic strategy).")
            .AppendLine($"// Workstream: {workstream.Id}")
            .AppendLine()
            .AppendLine($"namespace Forge.Tests.Stories.{StoryPaths.Safe(spawn.StoryId).Replace('-', '_')};")
            .AppendLine()
            .AppendLine("using Reqnroll;")
            .AppendLine()
            .AppendLine("[Binding]")
            .AppendLine($"public class {Pascal(slug)}Steps")
            .AppendLine("{")
            .AppendLine($"    [Given(@\"the {slug} slice is shipped\")]")
            .AppendLine("    public void GivenSliceShipped() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [When(@\"the operator triggers the primary action\")]")
            .AppendLine("    public void WhenOperatorTriggers() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [Then(@\"the workflow completes end-to-end\")]")
            .AppendLine("    public void ThenWorkflowCompletes() { /* deterministic stub */ }")
            .AppendLine("}")
            .ToString();
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal) { [path] = body });
    }

    private static string Pascal(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return "Ws";
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0])).Append(p.AsSpan(1));
        }
        return sb.Length == 0 ? "Ws" : sb.ToString();
    }
}
