// -----------------------------------------------------------------------
// <copyright file="DecompositionDepsResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

/// <summary>
/// Phase 46 — projects per-phase + per-stage dependencies declared in
/// <c>implementation-plan.md</c> onto the workstream-title space the
/// orchestrator schedules against.
///
/// <para>Two kinds of dep refs are supported:
/// <list type="bullet">
/// <item><b>Stage anchor</b> — <c>phase-{slug}/stage-{slug}</c>; resolved
/// directly to the matching stage's title.</item>
/// <item><b>Phase anchor</b> — <c>phase-{slug}</c>; expanded to the titles
/// of every stage in that phase (cross-product expansion).</item>
/// </list>
/// Bare workstream titles are accepted as a fallback (Q46.2: "anchor
/// refs are preferred but title-form deps still resolve").</para>
///
/// <para>For each stage the resolver returns the union of:
/// <list type="number">
/// <item>Explicit stage-deps the stage's <c>### Dependencies</c> block names.</item>
/// <item>Phase-expansion: when the stage's parent phase declares phase deps,
/// every stage in those upstream phases is added as an implicit dep
/// (Q46.2 "phase deps gate the entire phase"). The cross-product is
/// taken with all stages in the upstream phases — the smallest set that
/// enforces the phase ordering.</item>
/// </list>
/// </para>
///
/// <para>Cycle detection runs on the effective stage-level DAG. A cycle
/// throws <see cref="InvalidOperationException"/> with a structured
/// message so the orchestrator surfaces it as <c>LastError</c>.</para>
/// </summary>
internal static class DecompositionDepsResolver
{
    public sealed record ResolvedStageDeps(
        IReadOnlyList<string> Deps,
        IReadOnlyList<string> UnresolvedDeps);

    /// <summary>
    /// Resolve and phase-expand the deps for every stage in the parsed plan.
    /// Result keyed by stage anchor.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with code <c>plan.dep_cycle</c> when the effective DAG has a cycle.
    /// </exception>
    public static IReadOnlyDictionary<string, ResolvedStageDeps> ResolveAll(PlanParser.CanonicalPlan plan)
    {
        if (plan.Phases.Count == 0)
        {
            return new Dictionary<string, ResolvedStageDeps>(StringComparer.Ordinal);
        }

        // Build lookups keyed by anchor (case-insensitive — anchors are
        // lower-case slugs but tolerate the architect emitting mixed
        // case in the dep bullet).
        var stagesByAnchor = new Dictionary<string, PlanParser.CanonicalStage>(StringComparer.OrdinalIgnoreCase);
        var stagesByTitle = new Dictionary<string, PlanParser.CanonicalStage>(StringComparer.OrdinalIgnoreCase);
        var stagesByPhase = new Dictionary<string, List<PlanParser.CanonicalStage>>(StringComparer.OrdinalIgnoreCase);
        var phasesByAnchor = new Dictionary<string, PlanParser.CanonicalPhase>(StringComparer.OrdinalIgnoreCase);

        foreach (var phase in plan.Phases)
        {
            phasesByAnchor[phase.Anchor] = phase;
            stagesByPhase[phase.Anchor] = phase.Stages.ToList();
            foreach (var stage in phase.Stages)
            {
                stagesByAnchor[stage.Anchor] = stage;
                // Title collisions across phases would silently overwrite.
                // Keep the first-seen stage so downstream resolution is
                // stable; a future polish pass could namespace by phase.
                stagesByTitle.TryAdd(stage.Name, stage);
            }
        }

        var resolved = new Dictionary<string, ResolvedStageDeps>(StringComparer.Ordinal);
        // Key: stage anchor; Value: set of upstream stage anchors. Used
        // both for the cycle check and to project unique titles.
        var depAnchorsByStage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var phase in plan.Phases)
        {
            // Phase 46 phase-expansion (Q46.2): each stage in this phase
            // implicitly depends on EVERY stage in each upstream phase.
            // Collect the cross-product target anchors once per phase.
            // Unresolved phase-deps are captured per-phase so every stage
            // in the phase reports them in its UnresolvedDeps surface
            // (Q46.5 yellow banner trigger).
            var phaseExpansionAnchors = new HashSet<string>(StringComparer.Ordinal);
            var phaseUnresolved = new List<string>();
            foreach (var phaseDepRef in phase.Dependencies)
            {
                if (TryResolvePhaseAnchor(phaseDepRef, phasesByAnchor, out var upstreamPhase))
                {
                    foreach (var upstreamStage in upstreamPhase.Stages)
                    {
                        phaseExpansionAnchors.Add(upstreamStage.Anchor);
                    }
                }
                else
                {
                    phaseUnresolved.Add(phaseDepRef);
                }
            }

            foreach (var stage in phase.Stages)
            {
                var anchors = new HashSet<string>(StringComparer.Ordinal);
                var unresolved = new List<string>(phaseUnresolved);

                // Phase-expansion edges. Self-edges (a typo'd phase
                // self-dep that fans out to its own stages) are kept
                // intentionally so DFS reports plan.dep_cycle instead
                // of silently dropping the typo.
                foreach (var upstreamAnchor in phaseExpansionAnchors)
                {
                    anchors.Add(upstreamAnchor);
                }

                // Explicit stage deps. Try stage-anchor → phase-anchor → title.
                foreach (var depRef in stage.Dependencies)
                {
                    if (TryResolveStageAnchor(depRef, stagesByAnchor, out var upStage))
                    {
                        anchors.Add(upStage.Anchor);
                        continue;
                    }
                    if (TryResolvePhaseAnchor(depRef, phasesByAnchor, out var upPhase))
                    {
                        foreach (var upStage2 in upPhase.Stages)
                        {
                            anchors.Add(upStage2.Anchor);
                        }
                        continue;
                    }
                    if (stagesByTitle.TryGetValue(depRef, out var titleStage))
                    {
                        anchors.Add(titleStage.Anchor);
                        continue;
                    }
                    unresolved.Add(depRef);
                }

                depAnchorsByStage[stage.Anchor] = anchors;

                // Project anchors → titles for the orchestrator's title-keyed Deps[].
                var titles = anchors
                    .Select(a => stagesByAnchor.TryGetValue(a, out var s) ? s.Name : a)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                resolved[stage.Anchor] = new ResolvedStageDeps(titles, unresolved);
            }
        }

        // Cycle detection on the effective stage-level DAG. We do a DFS
        // with WHITE/GRAY/BLACK colouring; a back-edge to a GRAY node
        // signals a cycle. Throw with the discovered path so the operator
        // sees exactly which stages cycle.
        DetectCycle(depAnchorsByStage, stagesByAnchor);

        return resolved;
    }

    private static bool TryResolveStageAnchor(
        string depRef,
        IReadOnlyDictionary<string, PlanParser.CanonicalStage> stagesByAnchor,
        out PlanParser.CanonicalStage stage)
    {
        // A stage anchor always contains a slash (phase/stage separator).
        // Cheap pre-check avoids fanning out to phase + title lookups.
        if (depRef.Contains('/') && stagesByAnchor.TryGetValue(depRef, out var s))
        {
            stage = s;
            return true;
        }
        stage = null!;
        return false;
    }

    private static bool TryResolvePhaseAnchor(
        string depRef,
        IReadOnlyDictionary<string, PlanParser.CanonicalPhase> phasesByAnchor,
        out PlanParser.CanonicalPhase phase)
    {
        if (!depRef.Contains('/') && phasesByAnchor.TryGetValue(depRef, out var p))
        {
            phase = p;
            return true;
        }
        phase = null!;
        return false;
    }

    private static void DetectCycle(
        IReadOnlyDictionary<string, HashSet<string>> graph,
        IReadOnlyDictionary<string, PlanParser.CanonicalStage> stagesByAnchor)
    {
        var colour = new Dictionary<string, byte>(StringComparer.Ordinal);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        const byte White = 0;
        const byte Gray = 1;
        const byte Black = 2;

        foreach (var node in graph.Keys)
        {
            colour[node] = White;
        }

        foreach (var node in graph.Keys)
        {
            if (colour[node] == White)
            {
                var stack = new Stack<(string Node, IEnumerator<string> Edges)>();
                colour[node] = Gray;
                stack.Push((node, graph[node].GetEnumerator()));

                while (stack.Count > 0)
                {
                    var (current, edges) = stack.Peek();
                    if (edges.MoveNext())
                    {
                        var next = edges.Current;
                        if (!graph.ContainsKey(next))
                        {
                            // Out-of-graph edge (e.g. phase-expansion to a
                            // stage that's outside this plan's phases —
                            // shouldn't happen but skip safely).
                            continue;
                        }
                        var c = colour[next];
                        if (c == Gray)
                        {
                            // Cycle: walk back through `parent` to build
                            // the path for the error message.
                            var path = new List<string> { next };
                            var cursor = current;
                            while (!string.Equals(cursor, next, StringComparison.Ordinal))
                            {
                                path.Add(cursor);
                                if (!parent.TryGetValue(cursor, out var p)) break;
                                cursor = p;
                            }
                            path.Add(next);
                            path.Reverse();
                            var named = path.Select(a => stagesByAnchor.TryGetValue(a, out var s)
                                ? $"{s.Name} ({a})"
                                : a);
                            throw new InvalidOperationException(
                                $"plan.dep_cycle: stage dependency cycle detected: {string.Join(" -> ", named)}");
                        }
                        if (c == White)
                        {
                            colour[next] = Gray;
                            parent[next] = current;
                            stack.Push((next, graph[next].GetEnumerator()));
                        }
                    }
                    else
                    {
                        colour[current] = Black;
                        stack.Pop();
                    }
                }
            }
        }
    }
}
