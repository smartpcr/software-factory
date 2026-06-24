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
        IReadOnlyList<string> UnresolvedDeps,
        // docs/forge/e2e.md Stage 2.1 — anchor refs from the stage's
        // `### Cross-Stage Dependencies` H3, projected to upstream stage
        // titles (same shape as <see cref="Deps"/>). Empty when the H3 is
        // absent. Stage 3.1 will consume this when emitting the trailing
        // qa-e2e workstream's Deps[]. Cycle detection (this resolver
        // already throws <c>plan.dep_cycle</c> on offence) treats these
        // edges as first-class — a cycle that goes through a cross-stage
        // e2e dep fires the same throw, with the cross-stage edge named in
        // the path. An UNRESOLVED cross-stage e2e ref is fatal: the
        // resolver throws <c>plan.dep_unresolved</c> with a structured
        // message, mirroring the cycle path. Additive default keeps every
        // existing caller of <c>new ResolvedStageDeps(...)</c> compiling.
        IReadOnlyList<string>? CrossStageE2eDeps = null);

    /// <summary>
    /// Suffix appended to a stage title to name its trailing qa-e2e
    /// companion workstream. Mirrors the literal used by
    /// <c>CrewStoryArtifactStrategy.BuildDecompositionFromPlan</c> and
    /// <c>PlanReconciler</c> when minting qa-e2e rows; the title→id resolver
    /// (<c>PersistDecompositionAsync</c>) uses this exact string to route a
    /// rewritten dep onto the qa-e2e <see cref="WorkstreamRow"/>'s id.
    /// </summary>
    public const string QaE2eTitleSuffix = " — E2E";

    /// <summary>
    /// docs/forge/e2e.md — operator-gated dependency rewrite
    /// (<c>Forge:Stories:GateImplOnPriorStageE2e</c>). For every IMPL-stage
    /// dependency on an upstream stage that mints a trailing qa-e2e companion
    /// (i.e. the upstream stage declares <c>### Test Scenarios</c>), rewrite
    /// the dep title <c>D</c> → <c>"{D} — E2E"</c>. Because the qa-e2e row
    /// already deps on its impl sibling, the dependent stage now waits for
    /// the upstream stage's impl AND its e2e to be green — not just the
    /// upstream impl PR.
    ///
    /// <para>Flag on (default): a stage cannot start until the prior stage it
    /// depends on has passed its e2e. Off: the legacy parallel behaviour
    /// where e2e trails and gates only story completion (Phase 58).</para>
    ///
    /// <para><see cref="ResolvedStageDeps.CrossStageE2eDeps"/> is left
    /// untouched — those edges point a qa-e2e row at upstream IMPL stages by
    /// design; rewriting them would make a qa-e2e depend on another qa-e2e.
    /// Acyclicity is preserved: each rewritten edge routes through the qa-e2e
    /// node (a strict trailer of its impl), so the stage-level DAG stays a
    /// DAG. Both the orchestrator (<c>ProduceDecompositionAsync</c>) and the
    /// <c>PlanReconciler</c> apply this post-pass so persisted
    /// <c>DepsJson</c> stays in lockstep — otherwise the reconciler would
    /// re-derive un-gated deps and fight the orchestrator.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, ResolvedStageDeps> GateImplDepsOnPriorStageE2e(
        PlanParser.CanonicalPlan plan,
        IReadOnlyDictionary<string, ResolvedStageDeps> resolved)
    {
        // Titles of stages that mint a qa-e2e companion (≥1 test scenario).
        var hasE2e = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in plan.AllStages())
        {
            if (s.TestScenarios.Count > 0)
            {
                hasE2e.Add(s.Name);
            }
        }

        if (hasE2e.Count == 0)
        {
            return resolved; // no e2e companions exist — nothing to gate on.
        }

        var rewritten = new Dictionary<string, ResolvedStageDeps>(resolved.Count, StringComparer.Ordinal);
        foreach (var kvp in resolved)
        {
            var rd = kvp.Value;
            if (rd.Deps.Count == 0)
            {
                rewritten[kvp.Key] = rd;
                continue;
            }

            var newDeps = new List<string>(rd.Deps.Count);
            foreach (var d in rd.Deps)
            {
                newDeps.Add(hasE2e.Contains(d) ? d + QaE2eTitleSuffix : d);
            }

            rewritten[kvp.Key] = rd with { Deps = newDeps };
        }

        return rewritten;
    }

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

        // Dash-collapsed fallback lookups (docs/forge/e2e.md slug sharp-edge).
        // PlanParser.Slug turns every non-alphanumeric RUN into a single dash,
        // so a dotted token like "ASP.NET" becomes "asp-net" — but the
        // architect naturally writes the dep anchor as "aspnet", dropping the
        // dot. Indexing by the anchor with ALL dashes removed lets
        // "aspnet-core-controller-port" resolve to the real
        // "asp-net-core-controller-port". A collapsed key that two DISTINCT
        // anchors share is excluded (tracked in the ambiguous set) so the
        // fallback never silently picks the wrong stage/phase; those fall
        // through to the existing unresolved/title handling.
        var stagesByCollapsedAnchor = new Dictionary<string, PlanParser.CanonicalStage>(StringComparer.OrdinalIgnoreCase);
        var ambiguousStageCollapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phasesByCollapsedAnchor = new Dictionary<string, PlanParser.CanonicalPhase>(StringComparer.OrdinalIgnoreCase);
        var ambiguousPhaseCollapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phase in plan.Phases)
        {
            phasesByAnchor[phase.Anchor] = phase;
            stagesByPhase[phase.Anchor] = phase.Stages.ToList();
            IndexCollapsed(phasesByCollapsedAnchor, ambiguousPhaseCollapsed, phase.Anchor, phase);
            foreach (var stage in phase.Stages)
            {
                stagesByAnchor[stage.Anchor] = stage;
                // Title collisions across phases would silently overwrite.
                // Keep the first-seen stage so downstream resolution is
                // stable; a future polish pass could namespace by phase.
                stagesByTitle.TryAdd(stage.Name, stage);
                IndexCollapsed(stagesByCollapsedAnchor, ambiguousStageCollapsed, stage.Anchor, stage);
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
                if (TryResolvePhaseAnchor(phaseDepRef, phasesByAnchor, phasesByCollapsedAnchor, ambiguousPhaseCollapsed, out var upstreamPhase))
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
                    if (TryResolveStageAnchor(depRef, stagesByAnchor, stagesByCollapsedAnchor, ambiguousStageCollapsed, out var upStage))
                    {
                        anchors.Add(upStage.Anchor);
                        continue;
                    }
                    if (TryResolvePhaseAnchor(depRef, phasesByAnchor, phasesByCollapsedAnchor, ambiguousPhaseCollapsed, out var upPhase))
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

                // docs/forge/e2e.md Stage 2.1 — cross-stage e2e deps.
                // Resolved with the same anchor/title fallback chain as
                // ordinary deps, BUT an unresolved ref is fatal because a
                // trailing qa-e2e workstream cannot wait on a non-existent
                // upstream. Resolved cross-stage e2e anchors ALSO join the
                // cycle-detection DAG (depAnchorsByStage) so a cycle that
                // goes through a cross-stage edge fires the same
                // plan.dep_cycle throw.
                var crossStageAnchors = new HashSet<string>(StringComparer.Ordinal);
                foreach (var depRef in stage.CrossStageE2eDeps ?? Array.Empty<string>())
                {
                    if (TryResolveStageAnchor(depRef, stagesByAnchor, stagesByCollapsedAnchor, ambiguousStageCollapsed, out var upStage))
                    {
                        crossStageAnchors.Add(upStage.Anchor);
                        anchors.Add(upStage.Anchor);
                        continue;
                    }
                    if (TryResolvePhaseAnchor(depRef, phasesByAnchor, phasesByCollapsedAnchor, ambiguousPhaseCollapsed, out var upPhase))
                    {
                        foreach (var upStage2 in upPhase.Stages)
                        {
                            crossStageAnchors.Add(upStage2.Anchor);
                            anchors.Add(upStage2.Anchor);
                        }
                        continue;
                    }
                    if (stagesByTitle.TryGetValue(depRef, out var titleStage))
                    {
                        crossStageAnchors.Add(titleStage.Anchor);
                        anchors.Add(titleStage.Anchor);
                        continue;
                    }

                    // Fatal. Throw with the structured code so the
                    // orchestrator's LastError + the operator's UI both see
                    // the same string. Includes the offending stage's
                    // anchor + the unresolved ref so the operator can fix
                    // the plan without grepping.
                    throw new InvalidOperationException(
                        $"{PlanParser.ErrorCodes.DepUnresolved}: cross-stage e2e dep '{depRef}' " +
                        $"on stage '{stage.Name}' ({stage.Anchor}) does not resolve to any phase " +
                        $"or stage anchor in the plan.");
                }

                depAnchorsByStage[stage.Anchor] = anchors;

                // Project anchors → titles for the orchestrator's title-keyed Deps[].
                var titles = anchors
                    .Select(a => stagesByAnchor.TryGetValue(a, out var s) ? s.Name : a)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Project cross-stage anchors → titles too, keeping them
                // distinct from the ordinary Deps so Stage 3.1 can wire
                // them to the trailing qa-e2e workstream specifically.
                var crossStageTitles = crossStageAnchors.Count == 0
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : crossStageAnchors
                        .Select(a => stagesByAnchor.TryGetValue(a, out var s) ? s.Name : a)
                        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                resolved[stage.Anchor] = new ResolvedStageDeps(titles, unresolved, crossStageTitles);
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
        IReadOnlyDictionary<string, PlanParser.CanonicalStage> stagesByCollapsedAnchor,
        IReadOnlySet<string> ambiguousCollapsed,
        out PlanParser.CanonicalStage stage)
    {
        // A stage anchor always contains a slash (phase/stage separator).
        // Cheap pre-check avoids fanning out to phase + title lookups.
        if (depRef.Contains('/'))
        {
            if (stagesByAnchor.TryGetValue(depRef, out var s))
            {
                stage = s;
                return true;
            }

            // Dash-collapsed fallback: "aspnet-core-..." → "asp-net-core-...".
            // Skipped when the collapsed key is ambiguous (two anchors share it).
            var collapsed = CollapseDashes(depRef);
            if (!ambiguousCollapsed.Contains(collapsed)
                && stagesByCollapsedAnchor.TryGetValue(collapsed, out var s2))
            {
                stage = s2;
                return true;
            }
        }
        stage = null!;
        return false;
    }

    private static bool TryResolvePhaseAnchor(
        string depRef,
        IReadOnlyDictionary<string, PlanParser.CanonicalPhase> phasesByAnchor,
        IReadOnlyDictionary<string, PlanParser.CanonicalPhase> phasesByCollapsedAnchor,
        IReadOnlySet<string> ambiguousCollapsed,
        out PlanParser.CanonicalPhase phase)
    {
        if (!depRef.Contains('/'))
        {
            if (phasesByAnchor.TryGetValue(depRef, out var p))
            {
                phase = p;
                return true;
            }

            // Dash-collapsed fallback (same slug sharp-edge as stages).
            var collapsed = CollapseDashes(depRef);
            if (!ambiguousCollapsed.Contains(collapsed)
                && phasesByCollapsedAnchor.TryGetValue(collapsed, out var p2))
            {
                phase = p2;
                return true;
            }
        }
        phase = null!;
        return false;
    }

    /// <summary>Anchor with every dash removed, so dotted-token slug variants
    /// (the Slug() regex renders "ASP.NET" as "asp-net" while the architect
    /// writes "aspnet") collapse to one canonical form. The phase/stage
    /// <c>/</c> separator is preserved so stage and phase anchors stay
    /// distinguishable.</summary>
    private static string CollapseDashes(string anchor) => anchor.Replace("-", string.Empty);

    /// <summary>Index <paramref name="value"/> by its dash-collapsed anchor,
    /// demoting the key to <paramref name="ambiguous"/> (and removing it from
    /// the index) if a DISTINCT anchor already collapsed to the same form — so
    /// the fuzzy fallback never guesses between two real anchors.</summary>
    private static void IndexCollapsed<T>(
        Dictionary<string, T> byCollapsed,
        HashSet<string> ambiguous,
        string anchor,
        T value)
    {
        var key = CollapseDashes(anchor);
        if (ambiguous.Contains(key))
        {
            return;
        }
        if (!byCollapsed.TryAdd(key, value))
        {
            byCollapsed.Remove(key);
            ambiguous.Add(key);
        }
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
                                $"{PlanParser.ErrorCodes.DepCycle}: stage dependency cycle detected: {string.Join(" -> ", named)}");
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
