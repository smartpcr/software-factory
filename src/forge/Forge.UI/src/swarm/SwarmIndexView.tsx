import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { forgeApi, type StorySpawn, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { SwarmTopbar } from './SwarmTopbar';
import { SwarmNavBar } from './SwarmNavBar';
import { SwarmStoryRow } from './SwarmStoryRow';
import { SpawnStoryModal } from '@/workstreams/SpawnStoryModal';
import { Toaster } from '@/board/Toaster';

type PhaseFilter = 'all' | StorySpawnPhase;

const FILTER_CHIPS: { key: PhaseFilter; label: string }[] = [
  { key: 'all',       label: 'All' },
  { key: 'plan',      label: 'Plan' },
  { key: 'decompose', label: 'Decompose' },
  { key: 'implement', label: 'Implement' },
  { key: 'review',    label: 'Review' },
  { key: 'qa',        label: 'QA' },
  { key: 'done',      label: 'Done' },
];

/**
 * Phase-bucket count for one spawn. The filter chips at the top of the
 * Story Index roll these up across every visible spawn. Mapping rule:
 *
 * - **PLAN** — the four MD plan-docs (architecture / tech-spec /
 *   implementation-plan / e2e-scenarios) are still in the gen↔eval loop
 *   (`spawn.phase === 'plan'`). Once the plan PR merges (phase advances
 *   past `plan`), all four flip into DONE.
 * - **DECOMPOSE** — `spawn.phase === 'decompose'` OR the architect has
 *   produced `workstreams.yaml` but the operator hasn't approved it yet.
 *   After approval the decomp counts into DONE.
 * - **IMPLEMENT** — every workstream not in a terminal/review state
 *   (planning, active, iterating, blocked, stalled, awaiting-pr-review).
 *   Blocked and stalled rows are explicitly counted as `implement`
 *   because they're stuck in the impl lane, not in review.
 * - **REVIEW** — workstreams whose impl PR is in flight (`review`) or
 *   being merged (`merging`).
 * - **QA** — release-stage tasks not yet merged: the QA feature-file PR
 *   (when `qaPrId`/`qaBranch` set but `qaPrMerged === false`) plus every
 *   workstream's step-def PR that exists but hasn't merged.
 * - **DONE** — terminal-state tasks: 4 plan-docs once `phase > 'plan'`,
 *   the decomposition once `phase >= 'implement'`, each `complete`
 *   workstream, the QA PR when `qaPrMerged`, and each merged step-def.
 *
 * A single spawn contributes to multiple buckets (the strip's DAG layout
 * is the visual realisation of the same idea). `ALL` is the count of
 * stories, not tasks — kept as `spawns.length` so the existing top-sticker
 * metric stays correct.
 */
type ChipCounts = Record<Exclude<PhaseFilter, 'all'>, number>;

const ZERO_COUNTS: ChipCounts = {
  plan: 0, decompose: 0, implement: 0, review: 0, qa: 0, done: 0,
};

function countsForSpawn(d: StorySpawnDetail): ChipCounts {
  const c: ChipCounts = { ...ZERO_COUNTS };
  const s = d.spawn;

  // Plan-docs. Four docs run in parallel after spawn lands and merge
  // together when the plan PR lands; they're never partially merged.
  if (s.phase === 'plan') c.plan += 4;
  else c.done += 4;

  // Decomposition (workstreams.yaml). Stays in DECOMPOSE while the
  // architect's YAML is being produced or awaiting operator approval.
  if (s.phase === 'plan') {
    if (s.decompositionJson) c.decompose += 1;
    // else: plan-docs still running, decomp hasn't started yet — no count.
  } else if (s.phase === 'decompose') {
    c.decompose += 1;
  } else {
    c.done += 1;
  }

  // Impl workstreams. We trust the server-derived `status` field, which
  // already overrides persisted state for stalled/external-merge cases
  // (see StoriesEndpoints.GetSpawn). `review` AND `merging` both bucket
  // into REVIEW because both mean "impl PR is in the review pipeline".
  for (const w of d.workstreams) {
    if (w.status === 'complete') c.done += 1;
    else if (w.status === 'review' || w.status === 'merging') c.review += 1;
    else c.implement += 1;
  }

  // QA feature-file PR. Phase-gated so that pre-merge orchestrator
  // side-effects (e.g. base-branch change pre-creating qaBranch) don't
  // bump the QA chip while the spawn is still implementing. The
  // qaPrMerged signal comes from the actual PR row's Col field —
  // authoritative regardless of spawn.phase.
  if (d.qaPrMerged) c.done += 1;
  else if (s.phase === 'qa') c.qa += 1;

  // Step-def PRs (one per workstream). Counted only when the PR row
  // exists — pre-creation we treat the step-def as "not yet a task".
  // Same authority chain as QA: PR-row merged flag wins over phase.
  for (const w of d.workstreams) {
    if (w.stepDefMerged) c.done += 1;
    else if (w.stepDefPrId) c.qa += 1;
  }

  return c;
}

function spawnContributesTo(d: StorySpawnDetail, chip: Exclude<PhaseFilter, 'all'>): boolean {
  return countsForSpawn(d)[chip] > 0;
}

export function SwarmIndexView() {
  const navigate = useNavigate();
  const [spawns, setSpawns] = useState<StorySpawn[]>([]);
  // Per-spawn detail cache. Populated alongside the spawn list refresh
  // so the chip counts and the task-presence filter stay live. Keyed by
  // storyId to match the contract every endpoint uses.
  const [details, setDetails] = useState<Record<string, StorySpawnDetail>>({});
  const [filter, setFilter] = useState<PhaseFilter>('all');
  const [search, setSearch] = useState('');
  const [spawnModalOpen, setSpawnModalOpen] = useState(false);
  // Bumped by handlers (e.g. onDeleted) to force the polling effect to
  // re-fetch immediately instead of waiting for the 15 s tick.
  const [refreshTick, setRefreshTick] = useState(0);
  // Phase 51 — global list tick from the spawn store. Bumped by the
  // SignalR `story-spawn.changed` handler in useForgeHub so list views
  // refetch on every server-side spawn lifecycle event (created,
  // deleted, phase-advanced, decomposed, ws-*, etc.) without waiting
  // for the 15 s poll. Without this dependency, a spawn created from
  // another browser tab — OR the orchestrator finishing a phase
  // transition — would linger in stale form for up to 15 s.
  const listTick = useSpawnStore((s) => s.listTick);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const rows = await forgeApi.listStorySpawns();
        if (cancelled) return;
        setSpawns(rows);
        // Refetch per-spawn detail in parallel. `allSettled` so one slow
        // / failing spawn detail endpoint doesn't blank the chip counts
        // for every other story.
        const fetched = await Promise.allSettled(
          rows.map((r) => forgeApi.getStorySpawn(r.storyId)),
        );
        if (cancelled) return;
        setDetails((prev) => {
          const next: Record<string, StorySpawnDetail> = { ...prev };
          // Drop entries for stories no longer in the list so a deleted
          // spawn doesn't keep contributing to chip counts forever.
          const liveIds = new Set(rows.map((r) => r.storyId));
          for (const k of Object.keys(next)) {
            if (!liveIds.has(k)) delete next[k];
          }
          fetched.forEach((r, i) => {
            if (r.status === 'fulfilled') next[rows[i].storyId] = r.value;
          });
          return next;
        });
      } catch {
        if (!cancelled) setSpawns([]);
      }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [refreshTick, listTick]);

  /**
   * Optimistic-then-refresh pattern when a row deletes its own spawn.
   * Drop the row from local state immediately so the UI snaps before the
   * next list fetch lands, then bump the refresh tick so the effect
   * re-runs (re-syncing chip counts, picking up any side-effect changes
   * from the orchestrator). Without the optimistic drop the row would
   * linger for up to a render cycle while the server's `story-spawn.changed`
   * SignalR event traverses the wire.
   */
  function handleSpawnDeleted(storyId: string) {
    setSpawns((prev) => prev.filter((s) => s.storyId !== storyId));
    setDetails((prev) => {
      if (!(storyId in prev)) return prev;
      const next = { ...prev };
      delete next[storyId];
      return next;
    });
    setRefreshTick((t) => t + 1);
  }

  /**
   * Phase 50 rollback for the optimistic delete. The row fires `onDeleted`
   * before awaiting the cascade-delete (which can run 30 s+ for large
   * spawns); if the server rejects, we bump the refresh tick so the
   * polling effect re-fetches the live list and the row reappears.
   */
  function handleSpawnDeleteFailed(_storyId: string) {
    setRefreshTick((t) => t + 1);
  }


  const counts = useMemo(() => {
    const c: Record<PhaseFilter, number> = {
      all: spawns.length,
      ...ZERO_COUNTS,
    };
    for (const s of spawns) {
      const d = details[s.storyId];
      if (!d) continue;
      const cs = countsForSpawn(d);
      c.plan      += cs.plan;
      c.decompose += cs.decompose;
      c.implement += cs.implement;
      c.review    += cs.review;
      c.qa        += cs.qa;
      c.done      += cs.done;
    }
    return c;
  }, [spawns, details]);

  const filtered = useMemo(() => {
    let next = spawns.slice();
    if (filter !== 'all') {
      next = next.filter((s) => {
        const d = details[s.storyId];
        if (!d) return false;
        return spawnContributesTo(d, filter);
      });
    }
    if (search.trim()) {
      const q = search.toLowerCase();
      next = next.filter((s) => s.storyTitle.toLowerCase().includes(q) || s.storyId.toLowerCase().includes(q));
    }
    return next;
  }, [spawns, details, filter, search]);

  const meters = useMemo(() => {
    // Top-bar meters still describe SPAWN lifecycle (one bucket per
    // story), not task counts. Those numbers are the operator-facing
    // "where is each story today" stat; mixing in task totals here would
    // double-count against the chips below.
    const spawnPhaseCounts: Record<StorySpawnPhase, number> = {
      plan: 0, decompose: 0, implement: 0, review: 0, qa: 0, done: 0,
    };
    for (const s of spawns) spawnPhaseCounts[s.phase]++;
    const totalPhases = spawns.length * 6;
    const advancedPhases = spawns.reduce((acc, s) => {
      const idx = (['plan', 'decompose', 'implement', 'review', 'qa', 'done'] as StorySpawnPhase[]).indexOf(s.phase);
      return acc + idx + 1;
    }, 0);
    const pct = totalPhases > 0 ? Math.round((advancedPhases / totalPhases) * 100) : 0;
    return [
      { k: 'Stories',        v: String(spawns.length),     delta: `${spawnPhaseCounts.implement} active`, tone: undefined },
      { k: 'In Plan',        v: String(spawnPhaseCounts.plan),       delta: `${spawnPhaseCounts.decompose} decomposing`, tone: 'amber' as const },
      { k: 'Implementing',   v: String(spawnPhaseCounts.implement),  delta: `${spawnPhaseCounts.review} in review`, tone: 'cyan' as const },
      { k: 'Done',           v: String(spawnPhaseCounts.done),       delta: 'complete', tone: 'green' as const, deltaTone: 'up' as const },
      { k: 'Pipeline',       v: `${pct}%`,                 delta: 'across all stories' },
      { k: 'QA',             v: String(spawnPhaseCounts.qa),         delta: 'in test', tone: 'green' as const },
    ];
  }, [spawns]);

  return (
    <div className="swarm-ctrl">
      <div className="shell">
        <SwarmTopbar
          eyebrow="SWARM CONTROL // STORY INDEX"
          title="PARALLEL STORY DELIVERY"
          subtitle={`${spawns.length} active spawns · 4 crews × 20 agents · agent-driven lifecycle`}
          meters={meters}
        />
        <SwarmNavBar
          crumbs={[
            { pre: 'ROOT', label: 'Workstreams', href: '/workstreams', active: true },
          ]}
          homeHref="/"
        />

        <div className="index-toolbar">
          <div className="filters">
            {FILTER_CHIPS.map((chip) => (
              <button
                key={chip.key}
                className={`pill${filter === chip.key ? ' active' : ''}`}
                onClick={() => setFilter(chip.key)}
              >
                {chip.label} <span className="ct">{counts[chip.key]}</span>
              </button>
            ))}
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search stories…"
              style={{
                marginLeft: 8,
                background: 'var(--sc-bg-2)',
                border: '1px solid var(--sc-border)',
                color: 'var(--sc-text)',
                padding: '5px 10px',
                fontFamily: 'var(--sc-font-mono)',
                fontSize: 11,
                outline: 'none',
                minWidth: 220,
              }}
            />
          </div>
          <div className="actions">
            <button className="btn" onClick={() => navigate('/work-items')}>▌ WORK ITEMS</button>
            <button className="btn primary" onClick={() => setSpawnModalOpen(true)}>+ SPAWN STORY</button>
          </div>
        </div>

        <div className="index">
          {filtered.length === 0 ? (
            <div className="empty">
              <div className="lbl">▌ NO STORIES { filter !== 'all' ? `IN ${filter.toUpperCase()}` : '' }</div>
              <div className="sub">
                {filter === 'all'
                  ? 'Spawn a story from an ADO Feature / PBI to start a multi-team delivery — Forge orchestrates plan → decompose → implement → review → qa across the four crews.'
                  : `No spawns in ${filter} phase right now.`}
              </div>
              {filter === 'all' && (
                <button className="btn primary" onClick={() => setSpawnModalOpen(true)}>+ SPAWN FIRST STORY</button>
              )}
            </div>
          ) : (
            <div className="story-list">
              {filtered.map((s, i) => (
                <SwarmStoryRow
                  key={s.storyId}
                  spawn={s}
                  index={i}
                  onDeleted={handleSpawnDeleted}
                  onDeleteFailed={handleSpawnDeleteFailed}
                />
              ))}
            </div>
          )}
        </div>
      </div>

      <SpawnStoryModal
        open={spawnModalOpen}
        onClose={() => setSpawnModalOpen(false)}
        onSpawned={(storyId) => {
          setSpawnModalOpen(false);
          navigate(`/workstreams/${encodeURIComponent(storyId)}`);
        }}
      />
      <Toaster />
    </div>
  );
}
