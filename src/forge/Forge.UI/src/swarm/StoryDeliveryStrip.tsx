// -----------------------------------------------------------------------
// <copyright file="StoryDeliveryStrip.tsx" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

import { useEffect, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  forgeApi,
  type StorySpawn,
  type StorySpawnDetail,
  type StorySpawnWorkstreamSummary,
} from '@/api/forgeApi';
import { agentInfo } from '@/agentAvatar';

interface Props {
  detail: StorySpawnDetail;
}

/* ─── Plan-doc lifecycle polling ───────────────────────────────────────
 * The four MD plan-docs (architecture, tech-spec, implementation-plan,
 * e2e-scenarios) live in their own gen↔eval loops under
 * `plan-doc:{spawn.Id}:{key}.md`. Stats are surfaced through
 * `getStoryArtifactIterations`. The 5th plan-stage task — `decomposition`
 * (workstreams.yaml) — has no per-iteration loop; its status is
 * synthesised from spawn fields and so does not poll.
 *
 * 15 s cadence is intentionally slower than the master panel's 6 s
 * because this strip is rendered on the story-index list (N rows × M
 * polls = chatty). Operators get a snappier view by drilling into the
 * spawn detail.
 */
const PLAN_DOC_KEYS = [
  'architecture',
  'tech-spec',
  'implementation-plan',
  'e2e-scenarios',
] as const;
type PlanDocKey = (typeof PLAN_DOC_KEYS)[number];

interface DocAgent {
  agentId: string;
  name: string;
  model: string | null;
}

interface DocStats {
  status: 'live' | 'iterating' | 'stalled' | 'awaiting-pr-review' | 'completed' | 'pending';
  iter: number;
  score: number | null;
  verdict: string;
  unansweredQuestions: number;
  generator: DocAgent | null;
  evaluator: DocAgent | null;
}

function useDocStats(storyId: string): Record<PlanDocKey, DocStats | null> {
  const [stats, setStats] = useState<Record<PlanDocKey, DocStats | null>>({
    'architecture': null,
    'tech-spec': null,
    'implementation-plan': null,
    'e2e-scenarios': null,
  });
  useEffect(() => {
    let cancelled = false;
    async function load() {
      // allSettled so one slow / failing doc endpoint doesn't blank the
      // entire strip — fall back to the previous value per-key on reject.
      const results = await Promise.allSettled(
        PLAN_DOC_KEYS.map((k) => forgeApi.getStoryArtifactIterations(storyId, k)),
      );
      if (cancelled) return;
      setStats((prev) => {
        const next = { ...prev };
        results.forEach((r, i) => {
          if (r.status !== 'fulfilled') return;
          const last = r.value.iterations[r.value.iterations.length - 1];
          next[PLAN_DOC_KEYS[i]] = {
            status: r.value.status,
            iter: r.value.iterations.length,
            score: last?.score ?? null,
            verdict: last?.verdict ?? '',
            unansweredQuestions: r.value.unansweredQuestions ?? 0,
            generator: r.value.generator,
            evaluator: r.value.evaluator,
          };
        });
        return next;
      });
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId]);
  return stats;
}

/* ─── Rectangle descriptor ──────────────────────────────────────────── */

type Stage = 1 | 2 | 3;
type RectStatus =
  | 'pending'
  | 'live'
  | 'iterating'
  | 'active'
  | 'review'
  | 'awaiting-pr-review'
  | 'merging'
  | 'complete'
  | 'completed'
  | 'blocked'
  | 'stalled'
  | 'failed'
  | 'paused'
  | 'historic'
  | 'cancelled';

interface Rect {
  key: string;
  stage: Stage;
  status: RectStatus;
  /** First line of the tooltip — the task's display name. */
  title: string;
  /** Optional second line — phase/anchor or sub-label. */
  subtitle?: string;
  /** Git branch this task lives on (null when the branch isn't created). */
  branch: string | null;
  /** Worktree leaf folder under `{cloneRoot}/.worktree/` (null when no worktree). */
  worktreeFolder: string | null;
  /** Latest generator pair member (id · name · model). Null when not yet claimed. */
  generator: { id: string; name: string; model: string | null } | null;
  /** Latest evaluator pair member. Null when not yet claimed. */
  evaluator: { id: string; name: string; model: string | null } | null;
  /** Free-form extra tooltip lines (iter / score / stallReason / …). */
  extras?: string[];
  /** Unanswered operator questions; > 0 draws the "?" dot on the chip. */
  openQuestions?: number;
}

/* ─── Plan-anchored worktree slug for plan-docs ──────────────────────
 * Mirrors `RealStoryGitOps.WorktreePath` + `SanitizeBranchSlug` so the
 * client can derive the folder name for branches that the spawn contract
 * doesn't already expose a `worktreeFolder` for. Returns null when the
 * branch is null/empty (orchestrator hasn't created it yet).
 */
function legacyWorktreeFolder(branch: string | null | undefined): string | null {
  if (!branch) return null;
  let buf = '';
  for (const ch of branch) {
    const isWord = /[a-zA-Z0-9\-_.]/.test(ch);
    buf += isWord ? ch : '-';
  }
  const trimmed = buf.replace(/^-+|-+$/g, '');
  if (trimmed.length === 0) return null;
  return `spawn-${trimmed}`;
}

/* ─── Build rectangles per stage ───────────────────────────────────── */

const DOC_LABELS: Record<string, string> = {
  'architecture':         'architecture.md',
  'tech-spec':            'tech-spec.md',
  'implementation-plan':  'implementation-plan.md',
  'e2e-scenarios':        'e2e-scenarios.md',
  'decomposition':        'workstreams.yaml',
};

function docRectFromStats(
  key: string,
  stats: DocStats | null,
  planBranch: string | null,
): Rect {
  const status: RectStatus = stats?.status ?? 'pending';
  const extras: string[] = [];
  if (stats && stats.iter > 0) extras.push(`iter: ${stats.iter}`);
  if (stats?.score != null) extras.push(`score: ${stats.score}`);
  if (stats?.verdict) extras.push(`verdict: ${stats.verdict}`);
  if (stats && stats.unansweredQuestions > 0) extras.push(`open Qs: ${stats.unansweredQuestions}`);
  return {
    openQuestions: stats?.unansweredQuestions ?? 0,
    key: `plan:${key}`,
    stage: 1,
    status,
    title: DOC_LABELS[key] ?? key,
    subtitle: 'Architect crew · design & plan',
    branch: planBranch,
    worktreeFolder: legacyWorktreeFolder(planBranch),
    generator: stats?.generator
      ? { id: stats.generator.agentId, name: stats.generator.name, model: stats.generator.model }
      : null,
    evaluator: stats?.evaluator
      ? { id: stats.evaluator.agentId, name: stats.evaluator.name, model: stats.evaluator.model }
      : null,
    extras,
  };
}

function decompositionRect(spawn: StorySpawn): Rect {
  // Decomposition doesn't run a gen↔eval loop the way the four MD docs do.
  // Its status is synthesised:
  //   - pending  → no decomposition JSON yet
  //   - review   → JSON present but the operator hasn't approved it
  //   - complete → operator approved → workstreams reconciled
  const hasJson = !!spawn.decompositionJson;
  const approved = spawn.decompositionApproved;
  const status: RectStatus = approved ? 'complete' : hasJson ? 'review' : 'pending';
  return {
    key: 'plan:decomposition',
    stage: 1,
    status,
    title: DOC_LABELS['decomposition'],
    subtitle: 'Architect crew · workstream breakdown',
    branch: spawn.planBranch,
    worktreeFolder: legacyWorktreeFolder(spawn.planBranch),
    generator: null,
    evaluator: null,
    extras: hasJson ? (approved ? ['approved'] : ['awaiting operator approval']) : ['no decomposition yet'],
  };
}

function workstreamRect(w: StorySpawnWorkstreamSummary): Rect {
  const gen = agentInfo(w.latestGenAgentId ?? null);
  const evl = agentInfo(w.latestEvalAgentId ?? null);
  const extras: string[] = [];
  if (typeof w.iter === 'number' && w.iter > 0) extras.push(`iter: ${w.iter}`);
  if (typeof w.latestScore === 'number') extras.push(`score: ${w.latestScore}`);
  if (w.latestVerdict) extras.push(`verdict: ${w.latestVerdict}`);
  if (w.unansweredQuestionCount && w.unansweredQuestionCount > 0) {
    extras.push(`open Qs: ${w.unansweredQuestionCount}`);
  }
  if (w.status === 'stalled' && w.stallReason) extras.push(`reason: ${w.stallReason}`);
  return {
    // Same rule as the story row's "open questions" count: cancelled and
    // historic workstreams will never act on an answer.
    openQuestions: w.status === 'cancelled' || w.status === 'historic' ? 0 : w.unansweredQuestionCount ?? 0,
    key: `impl:${w.id}`,
    stage: 2,
    status: (w.status as RectStatus) ?? 'pending',
    title: w.name || w.id,
    subtitle: w.planAnchor ?? w.phaseAnchor ?? undefined,
    branch: w.branch ?? null,
    worktreeFolder: w.worktreeFolder ?? legacyWorktreeFolder(w.branch),
    generator: gen,
    evaluator: evl,
    extras,
  };
}

/* ─── Tooltip builder ──────────────────────────────────────────────── */

function buildTooltip(r: Rect): string {
  // Multi-line `\n` title text. Chrome/Edge respect newlines; Safari/FF
  // flatten — accepted trade-off for staying consistent with the rest of
  // the swarm-control surface that uses native `title` everywhere.
  const lines: string[] = [];
  lines.push(r.title);
  if (r.subtitle) lines.push(r.subtitle);
  lines.push(`status: ${r.status}`);
  if (r.branch) lines.push(`branch: ${r.branch}`);
  if (r.worktreeFolder) lines.push(`worktree: ${r.worktreeFolder}`);
  if (r.generator) {
    lines.push(`gen: ${r.generator.id} · ${r.generator.name} (${r.generator.model ?? 'unknown'})`);
  }
  if (r.evaluator) {
    lines.push(`eval: ${r.evaluator.id} · ${r.evaluator.name} (${r.evaluator.model ?? 'unknown'})`);
  }
  if (r.extras && r.extras.length > 0) {
    for (const x of r.extras) lines.push(x);
  }
  return lines.join('\n');
}

/* ─── Component ────────────────────────────────────────────────────── */

/**
 * Visual constants. Tuned so a typical XRAFT-shaped DAG (5 plan-doc roots
 * → decomp → ~5 phases of impl, with trailing qa-e2e workstreams rendered
 * inline as Stage-2 siblings) lays out compactly without crowding the
 * edges. Edges live in the gap between columns.
 *
 * docs/forge/e2e.md Phase 58 — the pre-Phase-58 "batched QA + per-ws
 * step-defs" Stage-3 column was removed; real qa-e2e tasks now exist as
 * `Category='qa-e2e'` workstream rows in `detail.workstreams` and flow
 * through `workstreamRect` like any other workstream, gated on real PR
 * existence rather than synthesised from spawn.phase.
 */
const CELL = 10;
const COL_GAP = 18;
const ROW_GAP = 4;
const PAD = 2;

/**
 * Build the dependency map keyed on rect keys. The contract:
 *
 * - Plan-doc rects (`plan:{key}`) are roots — they kick off in parallel
 *   as soon as the spawn lands. No deps.
 * - The decomposition rect depends on every plan-doc — the architect
 *   crew writes `workstreams.yaml` only after the four MD docs reach
 *   pass-verdict and the plan PR merges. (`ProducePlanArtifactsAsync`
 *   in CrewStoryArtifactStrategy.cs.)
 * - Each workstream (impl or qa-e2e) carries its server-resolved
 *   `deps[]` (impl PR ids it must wait on). When a workstream has NO
 *   deps it still depends on the decomposition implicitly — it can't
 *   claim until the plan-derived workstream rows exist. qa-e2e rows
 *   carry their impl sibling's id in `deps[]` (set by
 *   `PersistDecompositionAsync` via the `/e2e` planAnchor suffix), so
 *   they naturally render to the right of their impl sibling here.
 *
 * docs/forge/e2e.md Phase 58 — the legacy `release:qa-pr` +
 * `release:steps:{ws.Id}` synthesised stubs were removed once
 * trailing per-stage qa-e2e workstreams replaced the batched QA pass.
 */
function buildDeps(
  detail: StorySpawnDetail,
): Map<string, string[]> {
  const deps = new Map<string, string[]>();
  for (const k of PLAN_DOC_KEYS) deps.set(`plan:${k}`, []);
  deps.set('plan:decomposition', PLAN_DOC_KEYS.map((k) => `plan:${k}`));

  const wsIds = new Set(detail.workstreams.map((w) => w.id));
  for (const w of detail.workstreams) {
    const wsDeps = (w.deps ?? [])
      .filter((id) => wsIds.has(id))
      .map((id) => `impl:${id}`);
    if (wsDeps.length === 0) wsDeps.push('plan:decomposition');
    deps.set(`impl:${w.id}`, wsDeps);
  }

  return deps;
}

/**
 * Longest-path layering — for every node, `depth(n) = max(depth(parents)) + 1`,
 * with roots at 0. Memoised so an O(V+E) traversal handles even the deepest
 * XRAFT-shaped DAG (~25 columns) without re-walking subgraphs.
 */
function computeDepths(
  keys: string[],
  deps: Map<string, string[]>,
): Map<string, number> {
  const out = new Map<string, number>();
  function depthOf(k: string, stack: Set<string>): number {
    const cached = out.get(k);
    if (cached !== undefined) return cached;
    if (stack.has(k)) {
      // Defensive cycle-break — server-side decomposition has cycle
      // detection (StoriesEndpoints.HasCycle) but the synthetic plan→
      // workstream→QA→step-defs edges I add here are constructed acyclic
      // by definition. Still, guard against future regressions.
      out.set(k, 0);
      return 0;
    }
    stack.add(k);
    const parents = deps.get(k) ?? [];
    let maxParent = -1;
    for (const p of parents) {
      maxParent = Math.max(maxParent, depthOf(p, stack));
    }
    stack.delete(k);
    const d = maxParent + 1;
    out.set(k, d);
    return d;
  }
  for (const k of keys) depthOf(k, new Set<string>());
  return out;
}

/**
 * Barycentric lane assignment — for each column (left → right), order
 * nodes by the mean lane of their parents. Roots (column 0) keep their
 * input order, which is the canonical plan-doc sequence (architecture →
 * tech-spec → implementation-plan → e2e-scenarios). This minimises edge
 * crossings without the cost of a full Sugiyama crossing-reduction pass.
 */
function assignLanes(
  columns: string[][],
  deps: Map<string, string[]>,
): Map<string, number> {
  const laneOf = new Map<string, number>();
  columns.forEach((col, ci) => {
    if (ci === 0) {
      col.forEach((key, li) => laneOf.set(key, li));
      return;
    }
    const withBary = col.map((key) => {
      const parents = deps.get(key) ?? [];
      const lanes = parents.map((p) => laneOf.get(p) ?? 0);
      const bary = lanes.length > 0
        ? lanes.reduce((s, l) => s + l, 0) / lanes.length
        : 0;
      return { key, bary };
    });
    // Stable secondary sort by original index so deterministic across renders.
    const orderById = new Map(col.map((key, i) => [key, i] as const));
    withBary.sort((a, b) => {
      if (a.bary !== b.bary) return a.bary - b.bary;
      return (orderById.get(a.key) ?? 0) - (orderById.get(b.key) ?? 0);
    });
    withBary.forEach((x, li) => laneOf.set(x.key, li));
  });
  return laneOf;
}

function rectX(col: number): number {
  return PAD + col * (CELL + COL_GAP);
}
function rectY(lane: number): number {
  return PAD + lane * (CELL + ROW_GAP);
}

/**
 * Map a rect key to the (mode, selection-kind, selection-id) tuple that
 * SwarmStoryView consumes via URL search params. Centralised so the rect
 * click handler stays declarative.
 *
 * - `plan:{docKey}` → design mode, doc:doc:{docKey} (the master panel
 *   keys doc cards with the `doc:` prefix; the strip's rect keys use
 *   `plan:` so we re-prefix here).
 * - `impl:{wsId}` → impl mode, phase:{wsId} (matches PhaseCard's
 *   selection contract). Applies to both impl and qa-e2e workstream
 *   rows — qa-e2e cards live inside the same phase board, stacked
 *   beneath their impl sibling per docs/forge/e2e.md Stage 8.1.
 */
function rectTarget(rect: Rect): { mode: 'design' | 'impl' | 'release'; selKind: string; selId: string } | null {
  if (rect.key.startsWith('plan:')) {
    const docKey = rect.key.slice('plan:'.length);
    return { mode: 'design', selKind: 'doc', selId: `doc:${docKey}` };
  }
  if (rect.key.startsWith('impl:')) {
    return { mode: 'impl', selKind: 'phase', selId: rect.key.slice('impl:'.length) };
  }
  return null;
}

export function StoryDeliveryStrip({ detail }: Props) {
  const docStats = useDocStats(detail.spawn.storyId);
  const navigate = useNavigate();

  // Stage 1 — 4 design MD docs + 1 decomposition.
  const stage1: Rect[] = [
    ...PLAN_DOC_KEYS.map((k) => docRectFromStats(k, docStats[k], detail.spawn.planBranch)),
    decompositionRect(detail.spawn),
  ];
  // Stage 2 — one rect per workstream (impl + qa-e2e). qa-e2e rows
  // carry `Category='qa-e2e'` and their impl sibling's id in `deps[]`,
  // so `buildDeps` naturally places them to the right of their impl
  // sibling. docs/forge/e2e.md Phase 58 — the pre-58 synthesised QA
  // feature-file PR + per-workstream step-defs Stage-3 column was
  // removed; trailing qa-e2e workstreams replaced that batched flow.
  const stage2: Rect[] = detail.workstreams.map(workstreamRect);

  const allRects = [...stage1, ...stage2];
  if (allRects.length === 0) return null;

  const deps = buildDeps(detail);
  const keys = allRects.map((r) => r.key);
  const depths = computeDepths(keys, deps);

  // Group by depth into columns (preserving the canonical input order
  // inside each column — barycentric pass then re-orders).
  const maxDepth = Math.max(0, ...Array.from(depths.values()));
  const columns: string[][] = Array.from({ length: maxDepth + 1 }, () => []);
  for (const r of allRects) {
    const d = depths.get(r.key) ?? 0;
    columns[d].push(r.key);
  }
  const laneOf = assignLanes(columns, deps);

  // Compute container dimensions from the laid-out grid.
  const maxLane = Math.max(0, ...Array.from(laneOf.values()));
  const widthPx = PAD * 2 + (maxDepth + 1) * CELL + maxDepth * COL_GAP;
  const heightPx = PAD * 2 + (maxLane + 1) * CELL + maxLane * ROW_GAP;

  // Position lookup for both rects and edge endpoints.
  function pos(key: string): { x: number; y: number } {
    const col = depths.get(key) ?? 0;
    const lane = laneOf.get(key) ?? 0;
    return { x: rectX(col), y: rectY(lane) };
  }

  // Build edges. One <path> per (parent → child) pair; the d= attribute
  // is a horizontal cubic bezier with control points pulled toward the
  // mid-gap so the curve eases in/out of each rect.
  type Edge = { id: string; d: string; stage: Stage };
  const edges: Edge[] = [];
  for (const r of allRects) {
    const childPos = pos(r.key);
    const parents = deps.get(r.key) ?? [];
    for (const p of parents) {
      const parentPos = pos(p);
      const x1 = parentPos.x + CELL;
      const y1 = parentPos.y + CELL / 2;
      const x2 = childPos.x;
      const y2 = childPos.y + CELL / 2;
      const dx = (x2 - x1) * 0.55;
      const d = `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`;
      edges.push({ id: `${p}→${r.key}`, d, stage: r.stage });
    }
  }

  function handleRectClick(r: Rect, e: ReactMouseEvent) {
    // The strip lives inside SwarmStoryRow which has its own onClick that
    // navigates without query params. Stop propagation so the more
    // specific rect click wins.
    e.stopPropagation();
    const t = rectTarget(r);
    if (!t) return;
    const params = new URLSearchParams();
    params.set('mode', t.mode);
    params.set('sel', t.selKind);
    params.set('selId', t.selId);
    // Open the master panel by default — operator just chose a specific
    // task and almost certainly wants to see its card. Mirrors the
    // existing "auto-open on selection" behaviour inside SwarmStoryView.
    params.set('master', 'open');
    navigate(`/workstreams/${encodeURIComponent(detail.spawn.storyId)}?${params.toString()}`);
  }

  // The strip's width grows with the DAG depth (80 workstreams is ~2,400px),
  // so it sits in a horizontal scroller that shows a scrollbar only when the
  // strip is wider than the row. A click whose target is the scroller itself
  // is a scrollbar drag, not a click on the row, so it must not bubble to
  // SwarmStoryRow's navigate-on-click.
  return (
    <div
      className="ws-strip-scroll"
      onClick={(e) => { if (e.target === e.currentTarget) e.stopPropagation(); }}
    >
      <div
        className="ws-strip"
        role="group"
        aria-label="Story delivery progress tree"
        style={{ width: widthPx, height: heightPx }}
      >
        <svg
          className="ws-strip-edges"
          width={widthPx}
          height={heightPx}
          aria-hidden="true"
        >
          {edges.map((e) => (
            <path key={e.id} className={`ws-edge stage-${e.stage}`} d={e.d} />
          ))}
        </svg>
        {allRects.map((r) => {
          const p = pos(r.key);
          return (
            <span
              key={r.key}
              className={`ws-chip stage-${r.stage} ${r.status}`}
              style={{ left: p.x, top: p.y, cursor: 'pointer' }}
              title={buildTooltip(r)}
              role="button"
              tabIndex={0}
              onClick={(e) => handleRectClick(r, e)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  handleRectClick(r, e as unknown as ReactMouseEvent);
                }
              }}
            >
              {(r.openQuestions ?? 0) > 0 && (
                <span className="ws-chip-q" data-testid="ws-chip-open-questions" aria-label={`${r.openQuestions} unanswered questions`}>?</span>
              )}
            </span>
          );
        })}
      </div>
    </div>
  );
}
