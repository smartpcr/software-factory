import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import type { StorySpawnArtifacts, StorySpawnDetail, PrDetailsResponse } from '@/api/forgeApi';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import type { Build, PrComment, StoryAttachment } from '@/types';
import { PrCommentsList } from '@/prs/PrCommentsList';
import { PrApprovalBadge } from '@/prs/PrApprovalBadge';
import { deriveVerdict } from '@/prs/prVerdict';
import { isBuildEvent } from '@/prs/prBuildEvents';
import type { SwarmMode } from './SwarmPipelineStrip';
import type { Selection } from './SwarmMasterPanel';
import { isQaE2e, qaE2eByImplId } from './qaE2eSelector';
import { computeStageNumbers } from './stageNumbering';
import { avatarFor } from '@/agentAvatar';
import type { Pr } from '@/types';
import { DocumentPane } from './DocumentPane';
import { DocConversationPane } from './DocConversationPane';
import { WorkstreamConversationPane } from './WorkstreamConversationPane';
import { OpenQuestionsWizard } from './OpenQuestionsWizard';
import { WorktreePane } from './WorktreePane';
import { useTaskLive, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { useToastStore } from '@/state/useToastStore';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import { PairSide } from '@/views/conversation';

export type DetailTab =
  | 'overview' | 'document' | 'conv' | 'pr' | 'worktree' | 'questions'
  | 'qa' | 'trace';

interface Props {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  artifacts: StorySpawnArtifacts | null;
  selection: Selection;
  tab: DetailTab;
  onTab: (t: DetailTab) => void;
}

const TAB_LABELS: { key: DetailTab; label: string }[] = [
  { key: 'overview',   label: 'Overview' },
  { key: 'document',   label: 'Document' },
  { key: 'conv',       label: 'Conversation' },
  { key: 'pr',         label: 'PR' },
  { key: 'worktree',   label: 'Worktree' },
  { key: 'questions',  label: 'Questions' },
  { key: 'qa',         label: 'QA / E2E' },
  { key: 'trace',      label: 'Trace' },
];

export function SwarmDetailPanel({ mode, detail, artifacts, selection, tab, onTab }: Props) {
  // Resolve workItemId for question polling + Overview data.
  // For workstream selections: workItemId = selection.id.
  // For doc selections: fetched from the artifact iterations endpoint.
  const [docWorkItemId, setDocWorkItemId] = useState<string | null>(null);
  useEffect(() => {
    if (selection.kind !== 'doc' || !selection.id || selection.id === 'doc:decomposition') {
      setDocWorkItemId(null);
      return;
    }
    const key = selection.id.startsWith('doc:') ? selection.id.slice(4) : selection.id;
    let cancelled = false;
    forgeApi.getStoryArtifactIterations(detail.spawn.storyId, key)
      .then((r) => { if (!cancelled) setDocWorkItemId(r.workItemId); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [selection, detail.spawn.storyId]);

  const effectiveWorkItemId = selection.kind === 'phase' ? (selection.id ?? null) : docWorkItemId;

  // Unanswered question count — polled above tab content for badge rendering.
  const [questionCount, setQuestionCount] = useState(0);
  useEffect(() => {
    if (!effectiveWorkItemId) { setQuestionCount(0); return; }
    let cancelled = false;
    async function poll() {
      try {
        const r = await forgeApi.listQuestions(effectiveWorkItemId!);
        if (!cancelled) setQuestionCount(r.unansweredCount);
      } catch { /* silent */ }
    }
    void poll();
    const id = setInterval(poll, 10_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [effectiveWorkItemId]);

  const tabsAvailable = useMemo(() => availableTabs(mode, selection, detail, questionCount), [mode, selection, detail, questionCount]);

  // If the current tab isn't available for this selection, jump to the first available.
  useEffect(() => {
    if (tabsAvailable.length > 0 && !tabsAvailable.includes(tab)) {
      const next = tabsAvailable[0];
      if (next) onTab(next);
    }
  }, [tabsAvailable, tab, onTab]);

  // Decomposition selection → full-width diagram, no tab strip.
  if (selection.kind === 'doc' && selection.id === 'doc:decomposition') {
    return (
      <section className="panel sc-detail-panel">
        <div className="tab-body">
          <div className="tab-pane active">
            <DecompositionDiagram detail={detail} />
          </div>
        </div>
      </section>
    );
  }

  // Order tabs visually as they appear in `tabsAvailable` so the doc-mode
  // tab strip reads `Document · Conversation · PR · Impl Progress · Contributors`
  // rather than the canonical order from TAB_LABELS.
  const tabsToRender = tabsAvailable
    .map((k) => TAB_LABELS.find((t) => t.key === k))
    .filter((t): t is { key: DetailTab; label: string } => !!t);

  return (
    <section className="panel sc-detail-panel">
      <div className="tabs">
        {tabsToRender.map((t) => (
          <button
            key={t.key}
            className={`tab${tab === t.key ? ' active' : ''}`}
            onClick={() => onTab(t.key)}
            type="button"
          >
            {t.label}
            {t.key === 'questions' && questionCount > 0 && (
              <span
                className="ct ct--badge"
                style={{ background: '#f44747', color: '#fff' }}
                title={`${questionCount} unanswered question(s) blocking progress`}
              >
                {questionCount}
              </span>
            )}
            {t.key === 'document' && questionCount > 0 && (
              <span
                className="ct ct--badge"
                style={{ background: '#ffd57c', color: '#1e1e2e' }}
                title={`${questionCount} unanswered question(s) — answer them in the Questions tab`}
              >
                ⚠ {questionCount}
              </span>
            )}
          </button>
        ))}
      </div>
      <div className="tab-body">
        <div className={`tab-pane${tab === 'overview' ? ' active' : ''}`}>
          {tab === 'overview' && (selection.kind === 'doc' || selection.kind === 'phase') ? (
            <AgentOverviewPane
              detail={detail}
              selection={selection}
              workItemId={effectiveWorkItemId}
            />
          ) : tab === 'overview' ? (
            <SwarmGridPane mode={mode} detail={detail} artifacts={artifacts} selection={selection} />
          ) : null}
        </div>
        <div className={`tab-pane${tab === 'document' ? ' active' : ''}`}>
          {tab === 'document' && selection.kind === 'doc' && selection.id && (
            <DocumentPane
              storyId={detail.spawn.storyId}
              docId={selection.id}
              planPr={artifacts?.plan.pr ?? null}
              planBranch={artifacts?.plan.branch ?? null}
            />
          )}
        </div>
        <div className={`tab-pane${tab === 'conv' ? ' active' : ''}`}>
          {tab === 'conv' && selection.kind === 'doc' && selection.id ? (
            <DocConversationPane
              storyId={detail.spawn.storyId}
              docId={selection.id}
            />
          ) : tab === 'conv' && selection.kind === 'phase' && selection.id ? (
            <WorkstreamConversationPane
              workstreamId={selection.id}
              wsStatus={detail.workstreams.find((w) => w.id === selection.id)?.status}
            />
          ) : tab === 'conv' && (
            <Placeholder
              label="conversation tab"
              sub="Select a doc (Design mode) or a workstream (Impl/Review mode) to see its paired-agent transcript."
            />
          )}
        </div>
        <div className={`tab-pane${tab === 'pr' ? ' active' : ''}`}>
          {tab === 'pr' && <PrPane detail={detail} selection={selection} />}
        </div>
        <div className={`tab-pane${tab === 'worktree' ? ' active' : ''}`}>
          {tab === 'worktree' && (
            <WorktreePane workItemId={effectiveWorkItemId} />
          )}
        </div>
        <div className={`tab-pane${tab === 'questions' ? ' active' : ''}`}>
          {tab === 'questions' && effectiveWorkItemId ? (
            <QuestionsPane workItemId={effectiveWorkItemId} />
          ) : tab === 'questions' ? (
            <Placeholder
              label="open questions"
              sub="No work item selected — questions appear once the gen↔eval loop has started."
            />
          ) : null}
        </div>
        <div className={`tab-pane${tab === 'qa' ? ' active' : ''}`}>
          {tab === 'qa' && <QaPane detail={detail} artifacts={artifacts} />}
        </div>
        <div className={`tab-pane${tab === 'trace' ? ' active' : ''}`}>
          {tab === 'trace' && <TracePane detail={detail} />}
        </div>
      </div>
    </section>
  );
}

/* ─── TAB AVAILABILITY ───────────────────────────────────────────────── */

function availableTabs(mode: SwarmMode, sel: Selection, detail: StorySpawnDetail, questionCount: number): DetailTab[] {
  // Decomposition gets no tabs — it renders a full-width diagram instead.
  if (sel.kind === 'doc' && sel.id === 'doc:decomposition') {
    return [];
  }
  if (sel.kind === 'doc') {
    const tabs: DetailTab[] = ['overview', 'document', 'conv'];
    // Phase 53 — sequential mode stores per-doc PRs in planDocPrs; legacy
    // mode uses a single planPrId. Show the PR tab when either source has a
    // PR for the selected doc.
    const docKey = sel.id?.startsWith('doc:') ? sel.id.slice(4) : sel.id;
    const hasDocPr = detail.spawn.planDocPrs?.some(
      (d) => d.key === docKey && d.prId,
    );
    if (hasDocPr || detail.spawn.planPrId) tabs.push('pr');
    tabs.push('worktree');
    if (questionCount > 0) tabs.push('questions');
    return tabs;
  }
  // Workstream (phase) selection — always show PR tab so the operator
  // sees "No PR linked yet" rather than a missing tab.
  if (sel.kind === 'phase') {
    const tabs: DetailTab[] = ['overview', 'conv', 'pr'];
    tabs.push('worktree');
    if (questionCount > 0) tabs.push('questions');
    if (mode === 'impl') tabs.push('qa', 'trace');
    return tabs;
  }
  // Gate or overview-level
  const tabs: DetailTab[] = ['overview'];
  if (sel.kind === 'gate') tabs.push('trace');
  else tabs.push('trace');
  return tabs;
}

/* ─── SWARM GRID (default overview for non-doc/workstream selections) ── */

interface AgentRow {
  id: string;
  name: string;
  role: string;
  crewId: string | null;
  state: string;
  generatorModel: string | null;
  evaluatorModel: string | null;
}

interface SwarmGridProps {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  artifacts: StorySpawnArtifacts | null;
  selection: Selection;
}

/** Phase 53 — canonical plan-doc keys driving the design-mode pair list.
 *  These map to the architect-crew work items spawned by the planning loop.
 *  Order mirrors the StoryDeliveryStrip so design-mode pair cards read in the
 *  same sequence the operator already knows from the master panel. */
const PLAN_DOC_KEYS = ['architecture', 'tech-spec', 'implementation-plan', 'e2e-scenarios'] as const;

interface PairCard {
  kind: 'design' | 'impl' | 'qa-e2e';
  key: string;
  team: 'arch' | 'eng' | 'qa';
  cls: string;
  task: string;
  workItemId: string | null;
  genId: string | null;
  genName: string;
  genModel: string;
  evlId: string | null;
  evlName: string;
  evlModel: string;
  iter: number;
  maxScore: number | null;
  hours: number | null;
  costUsd: number;
  status: string;
}

interface PrRow {
  id: string;
  title: string;
  branch: string;
  col: string;
  additions: number;
  deletions: number;
  files: number;
  webUrl: string | null;
  tone: 'arch' | 'eng' | 'rev' | 'qa';
  groupLabel: string;
}

function SwarmGridPane({ mode, detail, artifacts, selection }: SwarmGridProps) {
  const showToast = useToastStore((s) => s.show);
  const bumpSpawn = useSpawnStore((s) => s.bump);
  const [, setSearchParams] = useSearchParams();
  const [deleting, setDeleting] = useState(false);

  /**
   * Cascading delete for the selected workstream. Backend wipes:
   *  - WorkstreamRow + matching WorkItemRow + board card
   *  - Deliverables, transcripts, queue rows, dep edges, links, agent assignments
   *  - On-disk impl/review/fix workspaces
   *  - Worktree mount + local git branch
   * Returns 422 with `requireConfirm` when open PRs exist; we re-prompt and
   * pass `force=true`.
   */
  async function handleDeleteWorkstream(workstreamId: string, workstreamName: string) {
    if (deleting) return;
    const ok = await useConfirmStore.getState().ask({
      title: `Delete workstream "${workstreamName}"?`,
      body: 'Closed/merged PRs and the cost ledger are kept for history. Everything else is wiped.',
      tone: 'danger',
      confirmLabel: '✕ Delete',
      cancelLabel: 'Cancel',
      checklist: [
        'Workstream row + synthesized work-item',
        'All deliverables, transcripts, queue + dep rows',
        'Board card + agent assignments + work-item links',
        'impl / review / fix workspaces on disk',
        'Worktree mount + local git branch',
      ],
    });
    if (!ok) return;

    setDeleting(true);
    try {
      const r = await forgeApi.deleteWorkstream(workstreamId, false);
      const totalRows = Object.values(r.counts ?? {}).reduce((a, b) => a + b, 0);
      showToast(`Deleted ${workstreamId} — ${totalRows} rows / artifacts removed.`, 'success');
      // Drop selection back to overview so the now-stale id doesn't dangle.
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.delete('sel'); next.delete('selId');
        return next;
      }, { replace: true });
      // Refresh spawn detail so the workstreams list re-fetches.
      bumpSpawn(detail.spawn.storyId);
    } catch (err) {
      // 422 → workstream has open PRs; re-prompt for force-delete.
      const is422 = err instanceof ForgeApiError && err.status === 422;
      if (is422) {
        const force = await useConfirmStore.getState().ask({
          title: `"${workstreamName}" has open PR(s)`,
          body: 'Force-delete anyway? The PRs stay on the provider, but their local branch and workspace are wiped here.',
          tone: 'danger',
          confirmLabel: '✕ Force delete',
          cancelLabel: 'Keep workstream',
        });
        if (force) {
          try {
            const r = await forgeApi.deleteWorkstream(workstreamId, true);
            const totalRows = Object.values(r.counts ?? {}).reduce((a, b) => a + b, 0);
            showToast(`Force-deleted ${workstreamId} — ${totalRows} rows / artifacts removed.`, 'success');
            setSearchParams((prev) => {
              const next = new URLSearchParams(prev);
              next.delete('sel'); next.delete('selId');
              return next;
            }, { replace: true });
            bumpSpawn(detail.spawn.storyId);
          } catch (e2) {
            showToast(e2 instanceof Error ? e2.message : 'Force-delete failed', 'danger');
          }
        }
      } else {
        showToast(err instanceof Error ? err.message : 'Delete failed', 'danger');
      }
    } finally {
      setDeleting(false);
    }
  }

  const [agents, setAgents] = useState<AgentRow[]>([]);
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await fetch('/api/agents', { credentials: 'include' });
        if (!r.ok) return;
        const json = await r.json();
        if (!cancelled && Array.isArray(json)) setAgents(json as AgentRow[]);
      } catch { /* swallow */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 30_000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  const headlinePre = selection.kind === 'phase' ? 'WORKSTREAM' : selection.kind === 'doc' ? 'DOC' : selection.kind === 'gate' ? 'GATE' : 'STORY';
  const headlineId  = selection.id ?? detail.spawn.storyId;
  const subline = selection.kind === 'overview'
    ? sublineForMode(mode, detail)
    : selection.id ?? '';

  // Pair cards: agent generator/evaluator pairs working on the selected
  // mode's stage. Mode-aware:
  //  - design:  plan-doc work items (architecture, tech-spec, …)
  //  - impl:    impl-category workstreams (engineer / reviewer pairs)
  //  - release: qa-e2e-category workstreams (QA pairs)
  // All values are sourced from existing endpoints; per-pair iter-kpis
  // are fetched in parallel below so we can show real "hours spent" +
  // max score (not just the latest iter's score).
  const agentsById = new Map(agents.map((a) => [a.id, a]));
  const displayName = (id: string | null | undefined): string => {
    if (!id) return '—';
    const a = agentsById.get(id);
    return a?.name ?? id;
  };
  const modelFor = (id: string | null | undefined, role: 'gen' | 'evl'): string => {
    if (!id) return '—';
    const a = agentsById.get(id);
    if (!a) return '—';
    return (role === 'gen' ? a.generatorModel : a.evaluatorModel) ?? '—';
  };
  // Phase 49 follow-up — only show an agent pair when the workstream is
  // currently RUNNING or has shipped (active/review/merging/complete/historic).
  // For pre-claim or aborted states (planning/blocked/stalled/cancelled),
  // `latestGen/EvalAgentId` may carry the pair from a prior aborted run;
  // displaying it as if it's the current assignment confused the
  // operator. Treat those as "no current assignment".
  const isCurrentlyAssigned = (status: string): boolean =>
    status === 'active' || status === 'review' || status === 'merging' || status === 'complete' || status === 'historic';

  // ─── per-workstream iter-kpis (impl + release modes) ─────────────────
  // Sum of iter durationMs gives wall-clock hours the pair has spent
  // running gen/eval iterations; max of iter scores gives the pair's
  // peak rubric score (the deliverable strip already shows the *latest*
  // — which is misleading when the pair regressed in a later iter).
  const wantedWsIds = useMemo(() => {
    if (mode === 'design') return [] as string[];
    const list = mode === 'release'
      ? detail.workstreams.filter(isQaE2e)
      : detail.workstreams.filter((w) => !isQaE2e(w));
    return list
      .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
      .map((w) => w.id);
  }, [mode, detail.workstreams, selection]);
  const wantedWsKey = wantedWsIds.join('|');
  const [wsKpis, setWsKpis] = useState<Record<string, { hours: number | null; maxScore: number | null }>>({});
  useEffect(() => {
    if (wantedWsIds.length === 0) { setWsKpis({}); return; }
    let cancelled = false;
    async function load() {
      try {
        const results = await Promise.allSettled(
          wantedWsIds.map((id) => forgeApi.fetchWorkstreamIterKpis(id)),
        );
        if (cancelled) return;
        const next: Record<string, { hours: number | null; maxScore: number | null }> = {};
        results.forEach((r, i) => {
          if (r.status !== 'fulfilled') { next[wantedWsIds[i]] = { hours: null, maxScore: null }; return; }
          const iters = r.value.iters ?? [];
          const durSum = iters.reduce((acc, it) => acc + (it.durationMs ?? 0), 0);
          const hours = durSum > 0 ? durSum / 3_600_000 : null;
          const max = iters.length === 0
            ? null
            : iters.reduce((m, it) => Math.max(m, it.score ?? 0), -Infinity);
          next[wantedWsIds[i]] = { hours, maxScore: max === -Infinity ? null : max };
        });
        setWsKpis(next);
      } catch { /* silent */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 30_000);
    return () => { cancelled = true; clearInterval(id); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wantedWsKey]);

  // ─── per-doc artifact iterations (design mode) ───────────────────────
  // Architect crew owns one work item per plan-doc; we poll the same
  // `getStoryArtifactIterations` endpoint the master panel uses so the
  // overview reflects the same pair / score / cost the strip shows. We
  // additionally fetch iter-kpis to get durationMs (hours).
  const [docIters, setDocIters] = useState<Record<string, {
    workItemId: string | null;
    generator: { agentId: string; name: string; model: string | null } | null;
    evaluator: { agentId: string; name: string; model: string | null } | null;
    iter: number;
    maxScore: number | null;
    hours: number | null;
    costUsd: number;
    status: string;
  } | null>>({});
  useEffect(() => {
    if (mode !== 'design') { setDocIters({}); return; }
    let cancelled = false;
    async function load() {
      try {
        const iters = await Promise.allSettled(
          PLAN_DOC_KEYS.map((k) => forgeApi.getStoryArtifactIterations(detail.spawn.storyId, k)),
        );
        const kpis = await Promise.allSettled(
          PLAN_DOC_KEYS.map((k) => forgeApi.fetchStoryArtifactIterKpis(detail.spawn.storyId, k)),
        );
        if (cancelled) return;
        const next: Record<string, typeof docIters[string]> = {};
        PLAN_DOC_KEYS.forEach((k, i) => {
          const it = iters[i];
          const kp = kpis[i];
          if (it.status !== 'fulfilled') { next[k] = null; return; }
          const v = it.value;
          const ks = kp.status === 'fulfilled' ? (kp.value.iters ?? []) : [];
          const durSum = ks.reduce((acc, x) => acc + (x.durationMs ?? 0), 0);
          const hours = durSum > 0 ? durSum / 3_600_000 : null;
          const maxFromIters = v.iterations.length === 0
            ? null
            : v.iterations.reduce((m, x) => Math.max(m, x.score ?? 0), -Infinity);
          next[k] = {
            workItemId: v.workItemId,
            generator: v.generator,
            evaluator: v.evaluator,
            iter: v.iterations.length,
            maxScore: maxFromIters === -Infinity ? null : maxFromIters,
            hours,
            costUsd: v.totalCostUsd,
            status: v.status,
          };
        });
        setDocIters(next);
      } catch { /* silent */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [mode, detail.spawn.storyId]);

  // ─── PRs (mode-filtered list for the right column) ───────────────────
  const [allPrs, setAllPrs] = useState<Pr[]>([]);
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const prs = await forgeApi.getPrs();
        if (!cancelled) setAllPrs(prs);
      } catch { /* silent */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 20_000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  // ─── Build pairs[] (mode-aware) ──────────────────────────────────────
  const pairs: PairCard[] = useMemo(() => {
    if (mode === 'design') {
      return PLAN_DOC_KEYS.map<PairCard>((k) => {
        const d = docIters[k];
        const cls = d?.status === 'completed' || d?.status === 'awaiting-pr-review' ? 'e2e'
                  : d?.status === 'stalled' ? 'stalled'
                  : d?.status === 'live' || d?.status === 'iterating' ? '' : '';
        return {
          kind: 'design',
          key: `design:${k}`,
          team: 'arch',
          cls,
          task: prettyDocKey(k),
          workItemId: d?.workItemId ?? null,
          genId: d?.generator?.agentId ?? null,
          genName: d?.generator?.name ?? '— not assigned —',
          genModel: d?.generator?.model ?? '',
          evlId: d?.evaluator?.agentId ?? null,
          evlName: d?.evaluator?.name ?? '— not assigned —',
          evlModel: d?.evaluator?.model ?? '',
          iter: d?.iter ?? 0,
          maxScore: d?.maxScore ?? null,
          hours: d?.hours ?? null,
          costUsd: d?.costUsd ?? 0,
          status: d?.status ?? 'pending',
        };
      });
    }
    // impl / release
    const list = mode === 'release'
      ? detail.workstreams.filter(isQaE2e)
      : detail.workstreams.filter((w) => !isQaE2e(w));
    return list
      .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
      .map<PairCard>((w, i) => {
        const showPair = isCurrentlyAssigned(w.status);
        const genId = showPair ? (w.latestGenAgentId ?? w.agents?.[0] ?? null) : null;
        const evlId = showPair ? (w.latestEvalAgentId ?? w.agents?.[1] ?? null) : null;
        const kpi = wsKpis[w.id];
        const team: PairCard['team'] = mode === 'release' ? 'qa' : 'eng';
        return {
          kind: mode === 'release' ? 'qa-e2e' : 'impl',
          key: w.id,
          team,
          cls: pairClsForStatus(w.status),
          task: `${String(i + 1).padStart(2, '0')} · ${w.name}`,
          workItemId: w.id,
          genId: genId ?? null,
          genName: showPair && genId ? displayName(genId) : '— not assigned —',
          genModel: showPair && genId ? modelFor(genId, 'gen') : '',
          evlId: evlId ?? null,
          evlName: showPair && evlId ? displayName(evlId) : '— not assigned —',
          evlModel: showPair && evlId ? modelFor(evlId, 'evl') : '',
          iter: w.iter ?? 0,
          maxScore: kpi?.maxScore ?? w.latestScore ?? null,
          hours: kpi?.hours ?? null,
          costUsd: w.costUsd ?? 0,
          status: w.status,
        };
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, detail.workstreams, selection, docIters, wsKpis, agents]);

  // ─── Build PR rows (mode-aware) ──────────────────────────────────────
  const prRows: PrRow[] = useMemo(() => {
    const prById = new Map(allPrs.map((p) => [p.id, p]));
    const out: PrRow[] = [];
    const push = (
      id: string | null | undefined,
      tone: PrRow['tone'],
      groupLabel: string,
    ) => {
      if (!id) return;
      const pr = prById.get(id);
      if (!pr) {
        // PR is referenced (e.g. workstream.prId) but not yet in the
        // cached /api/prs list. Surface a placeholder so the operator
        // sees the link exists even before details land.
        out.push({
          id,
          title: id,
          branch: '',
          col: 'pending',
          additions: 0,
          deletions: 0,
          files: 0,
          webUrl: null,
          tone,
          groupLabel,
        });
        return;
      }
      out.push({
        id: pr.id,
        title: pr.title,
        branch: pr.branch,
        col: pr.col,
        additions: pr.additions ?? 0,
        deletions: pr.deletions ?? 0,
        files: pr.files ?? 0,
        webUrl: pr.webUrl ?? null,
        tone,
        groupLabel,
      });
    };
    if (mode === 'design') {
      // Per-doc plan PRs first (sequential plan mode), then legacy single planPrId.
      const seen = new Set<string>();
      for (const d of detail.spawn.planDocPrs ?? []) {
        if (d.prId && !seen.has(d.prId)) {
          seen.add(d.prId);
          push(d.prId, 'arch', `plan · ${prettyDocKey(d.key)}`);
        }
      }
      if (detail.spawn.planPrId && !seen.has(detail.spawn.planPrId)) {
        push(detail.spawn.planPrId, 'arch', 'plan');
      }
    } else if (mode === 'release') {
      const qaList = detail.workstreams.filter(isQaE2e);
      for (const w of qaList) {
        for (const prId of w.prs ?? []) push(prId, 'qa', `qa-e2e · ${w.name}`);
      }
      if (detail.spawn.qaPrId) push(detail.spawn.qaPrId, 'qa', 'qa (legacy)');
    } else {
      // impl mode — workstream impl PRs (eng tone) + step-def PRs (rev tone) +
      // artifacts.review.prs (reviewer feedback PRs, rev tone).
      const implList = detail.workstreams.filter((w) => !isQaE2e(w));
      for (const w of implList) {
        for (const prId of w.prs ?? []) push(prId, 'eng', w.name);
        if (w.stepDefPrId) push(w.stepDefPrId, 'rev', `${w.name} · step-defs`);
      }
      for (const prId of artifacts?.review.prs ?? []) push(prId, 'rev', 'review');
    }
    // Sort: open before merged so action items are first; within each group preserve insertion order.
    const order: Record<string, number> = { implementation: 0, build: 1, review: 2, pending: 3, merged: 4 };
    return out.slice().sort((a, b) => (order[a.col] ?? 9) - (order[b.col] ?? 9));
  }, [mode, allPrs, detail, artifacts]);

  const mergedCount = prRows.filter((r) => r.col === 'merged').length;
  const activeCount = prRows.length - mergedCount;

  return (
    <div className="ov-grid">
      <div className="ov-meta">
        <div>
          <div className="ov-headline"><span className="pre">{headlinePre}</span>{prettyId(headlineId)}</div>
          <div className="ov-sub">{subline}</div>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          {selection.kind === 'phase' && selection.id && (() => {
            const ws = detail.workstreams.find((w) => w.id === selection.id);
            const wsName = ws?.name ?? selection.id;
            return (
              <button
                type="button"
                disabled={deleting}
                onClick={() => void handleDeleteWorkstream(selection.id!, wsName)}
                title="Delete this workstream and all associated data (cascading)."
                style={{
                  background: 'transparent',
                  border: '1px solid var(--sc-red-d, #9f3d4d)',
                  color: 'var(--sc-red, #ff6b80)',
                  fontFamily: 'var(--sc-font-display)',
                  fontSize: 9,
                  letterSpacing: '0.22em',
                  textTransform: 'uppercase',
                  padding: '5px 10px',
                  borderRadius: 3,
                  cursor: deleting ? 'wait' : 'pointer',
                  opacity: deleting ? 0.5 : 1,
                  transition: 'background 120ms ease, color 120ms ease',
                }}
                onMouseEnter={(e) => { if (!deleting) e.currentTarget.style.background = 'rgba(255, 107, 128, 0.08)'; }}
                onMouseLeave={(e) => { e.currentTarget.style.background = 'transparent'; }}
              >
                {deleting ? '⏳ deleting…' : '✕ Delete workstream'}
              </button>
            );
          })()}
          <div style={{ fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.18em', color: 'var(--sc-text-dim)' }}>
            MODE · {mode.toUpperCase()}
          </div>
        </div>
      </div>

      <div className="ov-split">
        <div className="ov-split-l">
          <div className="ov-section-hd">
            <span className="ov-section-name">{pairsSectionLabel(mode)}</span>
            <span className="ov-section-count">{pairs.length}</span>
          </div>
          {pairs.length === 0 ? (
            <div className="tab-placeholder">
              <div className="lbl">▌ NO ACTIVE PAIRS</div>
              <div className="sub">{noPairsHint(mode)}</div>
            </div>
          ) : (
            <div className="pairs">
              {pairs.map((p) => (
                <PairCardRow key={p.key} pair={p} />
              ))}
            </div>
          )}
        </div>

        <div className="ov-split-r">
          <div className="ov-section-hd">
            <span className="ov-section-name">{prsSectionLabel(mode)}</span>
            <span className="ov-section-count">
              {activeCount > 0 && <span className="ov-pr-count-active">{activeCount} open</span>}
              {activeCount > 0 && mergedCount > 0 && <span className="ov-pr-count-sep"> · </span>}
              {mergedCount > 0 && <span className="ov-pr-count-merged">{mergedCount} merged</span>}
            </span>
          </div>
          {prRows.length === 0 ? (
            <div className="tab-placeholder">
              <div className="lbl">▌ NO PRS YET</div>
              <div className="sub">{noPrsHint(mode)}</div>
            </div>
          ) : (
            <div className="ov-pr-list">
              {prRows.map((r) => <PrListRow key={r.id} row={r} />)}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* ─── helpers for SwarmGridPane (Phase 81 overview redesign) ────────── */

function pairsSectionLabel(mode: SwarmMode): string {
  if (mode === 'design') return 'ARCHITECT PAIRS · DESIGN';
  if (mode === 'release') return 'QA PAIRS · RELEASE';
  return 'ENGINEER PAIRS · IMPL';
}

function prsSectionLabel(mode: SwarmMode): string {
  if (mode === 'design') return 'PLAN PRs';
  if (mode === 'release') return 'QA E2E PRs';
  return 'IMPL / REVIEW PRs';
}

function noPairsHint(mode: SwarmMode): string {
  if (mode === 'design') return 'Plan-doc pairs appear once the architect crew starts the design loop.';
  if (mode === 'release') return 'QA pairs appear after impl workstreams ship and qa-e2e siblings spawn.';
  return 'Pair cards will appear once the architect crew decomposes the story and engineer pairs claim workstreams.';
}

function noPrsHint(mode: SwarmMode): string {
  if (mode === 'design') return 'Per-doc plan PRs open as the architect crew finalises each plan document.';
  if (mode === 'release') return 'QA E2E PRs open after impl PRs merge and the qa-e2e workstream lands tests.';
  return 'Impl PRs open as engineer pairs land code; review/step-def PRs follow.';
}

function sublineForMode(mode: SwarmMode, detail: StorySpawnDetail): string {
  if (mode === 'design') {
    return `${PLAN_DOC_KEYS.length} plan docs · phase ${detail.spawn.phase}`;
  }
  if (mode === 'release') {
    const qa = detail.workstreams.filter((w) => isQaE2e(w)).length;
    return `${qa} qa-e2e workstream${qa === 1 ? '' : 's'} · phase ${detail.spawn.phase}`;
  }
  const impl = detail.workstreams.filter((w) => !isQaE2e(w)).length;
  return `${impl} impl workstream${impl === 1 ? '' : 's'} · phase ${detail.spawn.phase}`;
}

function prettyDocKey(key: string): string {
  return key
    .replace(/-/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

function formatHours(hours: number | null): string {
  if (hours == null || !Number.isFinite(hours)) return '—';
  if (hours < 0.05) return '<5m';
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 10) return `${hours.toFixed(1)}h`;
  return `${Math.round(hours)}h`;
}

function PairCardRow({ pair }: { pair: PairCard }) {
  return (
    <div className={`pair pair--ov ${pair.cls}`} title={pair.task}>
      <div className="pair-hd">
        <div className="pair-id">
          <span className={`team team--${pair.team}`}>{pair.team.toUpperCase()}</span>
          <span className="pair-status">{pair.status}</span>
        </div>
        <div className="pair-task">{pair.task}</div>
      </div>
      <div className="pair-agents pair-agents--with-avatar">
        <PairAgentSlot role="gen" id={pair.genId} name={pair.genName} model={pair.genModel} />
        <div className="pair-link">⇄</div>
        <PairAgentSlot role="evl" id={pair.evlId} name={pair.evlName} model={pair.evlModel} />
      </div>
      <div className="pair-meters">
        <PairMeter k="HRS"   v={formatHours(pair.hours)} />
        <PairMeter k="ITER"  v={pair.iter > 0 ? String(pair.iter) : '—'} />
        <PairMeter k="MAX"   v={pair.maxScore == null ? '—' : String(pair.maxScore)} highlight={pair.maxScore != null && pair.maxScore >= 90} />
        <PairMeter k="COST"  v={pair.costUsd > 0 ? `$${pair.costUsd.toFixed(2)}` : '—'} />
      </div>
    </div>
  );
}

function PairAgentSlot({
  role,
  id,
  name,
  model,
}: { role: 'gen' | 'evl'; id: string | null; name: string; model: string }) {
  const url = id ? avatarFor(id, name) : null;
  const roleLabel = role === 'gen' ? 'GEN' : 'EVL';
  return (
    <div className={`agent-tag agent-tag--ov ${role}`} title={id ? `${id} · ${name}\n${role === 'gen' ? 'generator' : 'evaluator'} · ${model || 'unknown model'}` : `no ${role} yet`}>
      <div className="agent-avatar">
        {url ? (
          <img src={url} alt={name} onError={(e) => { (e.currentTarget as HTMLImageElement).style.display = 'none'; }} />
        ) : (
          <span className="agent-avatar-fallback">{role === 'gen' ? '◇' : '◈'}</span>
        )}
      </div>
      <div className="agent-text">
        <span className="role">{roleLabel}</span>
        <span className="name">{name}</span>
        {model && <span className="model">{model}</span>}
      </div>
    </div>
  );
}

function PairMeter({ k, v, highlight }: { k: string; v: string; highlight?: boolean }) {
  return (
    <div className={`pair-m${highlight ? ' pair-m--good' : ''}`}>
      <div className="k">{k}</div>
      <div className="v">{v}</div>
    </div>
  );
}

function PrListRow({ row }: { row: PrRow }) {
  const status = colClass(row.col);
  const content = (
    <>
      <div className="ov-pr-hd">
        <span className={`ov-pr-tone ov-pr-tone--${row.tone}`} />
        <span className="ov-pr-id">{row.id}</span>
        <span className={`ov-pr-col ov-pr-col--${status}`}>{status}</span>
      </div>
      <div className="ov-pr-title">{row.title}</div>
      <div className="ov-pr-meta">
        <span className="ov-pr-group">{row.groupLabel}</span>
        {row.branch && <span className="ov-pr-branch" title={row.branch}>{shortenBranch(row.branch)}</span>}
        <span className="ov-pr-diff">
          <span className="ov-pr-add">+{row.additions.toLocaleString()}</span>
          <span className="ov-pr-del">−{row.deletions.toLocaleString()}</span>
          <span className="ov-pr-files">{row.files} file{row.files === 1 ? '' : 's'}</span>
        </span>
      </div>
    </>
  );
  if (row.webUrl) {
    return (
      <a className="ov-pr-row" href={row.webUrl} target="_blank" rel="noreferrer">
        {content}
      </a>
    );
  }
  return <div className="ov-pr-row ov-pr-row--no-link">{content}</div>;
}

function colClass(col: string): string {
  // Normalise the four Pr.col enum values to a short slug for CSS.
  if (col === 'implementation') return 'impl';
  if (col === 'merged') return 'merged';
  if (col === 'review') return 'review';
  if (col === 'build') return 'build';
  return 'open';
}

function shortenBranch(b: string): string {
  if (b.length <= 28) return b;
  return b.slice(0, 12) + '…' + b.slice(-12);
}

/* ─── ARCH DOC PANE ──────────────────────────────────────────────────── */

/* ─── AGENT OVERVIEW PANE (doc / workstream selection) ───────────────── */

type PromptShape = { agentId: string | null; systemPrompt: string; userPrompt: string; model: string; backend: string; mode: string; eventId: number | null };

function useLatestPrompts(transcript: TaskTranscriptEvent[]): { gen: PromptShape | null; ev: PromptShape | null } {
  return useMemo(() => {
    let gen: PromptShape | null = null;
    let ev: PromptShape | null = null;
    for (let i = transcript.length - 1; i >= 0 && (!gen || !ev); i--) {
      if (transcript[i].kind !== 'task.prompt') continue;
      const p = transcript[i].payload as Record<string, unknown> | null;
      if (!p) continue;
      const role = (p.role as string) ?? '';
      const candidate: PromptShape = {
        agentId: (p.agentId as string) ?? null,
        systemPrompt: (p.systemPrompt as string) ?? '',
        userPrompt: (p.userPrompt as string) ?? '',
        model: (p.model as string) ?? '',
        backend: (p.backend as string) ?? '',
        mode: (p.invokeMode as string) ?? '',
        eventId: (transcript[i].id as number | undefined) ?? null,
      };
      if (role === 'evaluator' && !ev) ev = candidate;
      else if (role !== 'evaluator' && !gen) gen = candidate;
    }
    return { gen, ev };
  }, [transcript]);
}

/**
 * The transcript stream truncates string fields > 320 chars and appends a
 * marker like "…[truncated 8440 → 320 — GET …]". We detect that marker so
 * the UI can offer a "Show full" affordance that fetches the untruncated
 * payload from the per-event endpoint.
 */
const TRUNCATION_MARKER_RE = /\[truncated \d+ → 320/;
function isTruncated(s: string | undefined | null): boolean {
  return !!s && TRUNCATION_MARKER_RE.test(s);
}

/* ── Attachment badge strip + content popup ── */
function AttachmentBadges({ attachments }: { attachments?: StoryAttachment[] }) {
  const [viewing, setViewing] = useState<{ fileName: string; content: string } | null>(null);
  const [loading, setLoading] = useState(false);

  if (!attachments || attachments.length === 0) return null;

  async function handleClick(a: StoryAttachment) {
    setLoading(true);
    try {
      const res = await fetch(
        `/api/stories/attachments/${encodeURIComponent(a.id)}/${encodeURIComponent(a.fileName)}`,
      );
      if (!res.ok) throw new Error(`${res.status}`);
      const text = await res.text();
      setViewing({ fileName: a.fileName, content: text });
    } catch {
      setViewing({ fileName: a.fileName, content: '(failed to load content)' });
    } finally {
      setLoading(false);
    }
  }

  function formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  return (
    <>
      <div style={{ marginBottom: 16 }}>
        <span style={{
          fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.18em',
          color: 'var(--sc-text-dim)', display: 'block', marginBottom: 6,
        }}>ATTACHMENTS</span>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {attachments.map((a) => (
            <button
              key={`${a.id}-${a.fileName}`}
              onClick={() => void handleClick(a)}
              disabled={loading}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                padding: '3px 10px', border: '1px solid var(--sc-border)',
                borderRadius: 4, background: 'var(--sc-bg-raised)',
                fontFamily: 'var(--sc-font-mono)', fontSize: 10,
                color: 'var(--sc-cyan)', cursor: 'pointer',
                transition: 'border-color .15s',
              }}
            >
              📎 {a.fileName}{' '}
              <span style={{ color: 'var(--sc-text-dim)' }}>({formatSize(a.sizeBytes)})</span>
            </button>
          ))}
        </div>
      </div>

      {viewing && (
        <div
          onClick={() => setViewing(null)}
          style={{
            position: 'fixed', inset: 0, zIndex: 9000,
            background: 'rgba(0,0,0,0.72)', display: 'flex',
            alignItems: 'center', justifyContent: 'center',
          }}
        >
          <div
            onClick={(e) => e.stopPropagation()}
            style={{
              width: '70vw', maxHeight: '80vh', display: 'flex', flexDirection: 'column',
              background: 'var(--sc-bg)', border: '1px solid var(--sc-border)', borderRadius: 6,
            }}
          >
            <div style={{
              display: 'flex', justifyContent: 'space-between', alignItems: 'center',
              padding: '8px 12px', borderBottom: '1px solid var(--sc-border)',
              fontFamily: 'var(--sc-font-display)', fontSize: 11, color: 'var(--sc-amber)',
              letterSpacing: '0.14em',
            }}>
              <span>📎 {viewing.fileName}</span>
              <button
                onClick={() => setViewing(null)}
                style={{
                  background: 'none', border: 'none', color: 'var(--sc-text-dim)',
                  fontSize: 16, cursor: 'pointer', lineHeight: 1,
                }}
              >✕</button>
            </div>
            <pre style={{
              margin: 0, padding: 12, overflow: 'auto', flex: 1,
              fontFamily: 'var(--sc-font-mono)', fontSize: 11, color: 'var(--sc-text)',
              whiteSpace: 'pre-wrap', wordBreak: 'break-word',
            }}>{viewing.content}</pre>
          </div>
        </div>
      )}
    </>
  );
}

interface AgentOverviewProps {
  detail: StorySpawnDetail;
  selection: Selection;
  workItemId: string | null;
}

function AgentOverviewPane({ detail, selection, workItemId }: AgentOverviewProps) {
  const showToast = useToastStore((s) => s.show);
  const isWorkstream = selection.kind === 'phase';

  // ── Doc-specific state (loaded from artifact iterations API) ──
  const [docGen, setDocGen] = useState<{ agentId: string; name: string; model: string | null } | null>(null);
  const [docEval, setDocEval] = useState<{ agentId: string; name: string; model: string | null } | null>(null);
  const [docCost, setDocCost] = useState(0);
  const [docStatus, setDocStatus] = useState<string>('pending');
  const [docMaxIter, setDocMaxIter] = useState(0);
  const [docCurrentIter, setDocCurrentIter] = useState(0);
  // Phase 52 — sloppy meters for the highlighted telemetry row.
  const [docPairAttempt, setDocPairAttempt] = useState(1);
  const [docMaxAttempts, setDocMaxAttempts] = useState(3);
  const [docTokens, setDocTokens] = useState(0);

  useEffect(() => {
    if (isWorkstream || !selection.id || selection.id === 'doc:decomposition') return;
    const key = selection.id.startsWith('doc:') ? selection.id.slice(4) : selection.id;
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.getStoryArtifactIterations(detail.spawn.storyId, key);
        if (cancelled) return;
        setDocGen(r.generator);
        setDocEval(r.evaluator);
        setDocCost(r.totalCostUsd);
        setDocStatus(r.status);
        setDocMaxIter(r.maxIterations);
        setDocCurrentIter(r.iterations.length > 0 ? r.iterations[r.iterations.length - 1].iter : 0);
        setDocPairAttempt(r.pairAttemptCount);
        setDocMaxAttempts(r.maxPairAttempts);
        setDocTokens(r.generatorTokensSinceWindow);
      } catch { /* silent */ }
    }
    void load();
    const id = setInterval(load, 8_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [isWorkstream, selection.id, detail.spawn.storyId]);

  // ── Workstream-specific state (from live store) ──
  const live = useTaskLive(isWorkstream ? (selection.id ?? null) : null);
  const snapshot = live?.snapshot ?? null;
  const transcript = live?.transcript ?? [];

  // Costs and iteration
  const totalCost = isWorkstream ? (snapshot?.totalCostUsd ?? 0) : docCost;
  const lastDeliverable = snapshot?.deliverables?.[snapshot.deliverables.length - 1] ?? null;
  const currentIter = isWorkstream ? (snapshot?.currentIteration ?? lastDeliverable?.iter ?? 0) : docCurrentIter;
  const executionStatus = isWorkstream ? (snapshot?.executionStatus ?? null) : docStatus;
  const effectiveMaxIter = isWorkstream ? (snapshot?.maxIterations ?? 5) : docMaxIter;
  // Phase 52 — sloppy meters. For workstreams, sourced from the snapshot;
  // for plan docs, from the artifact-iterations API.
  const pairAttempt = isWorkstream ? (snapshot?.pairAttemptCount ?? 1) : docPairAttempt;
  const maxAttempts = isWorkstream ? (snapshot?.maxPairAttempts ?? 3) : docMaxAttempts;
  const tokensUsed = isWorkstream ? (snapshot?.generatorTokensSinceWindow ?? 0) : docTokens;

  // Prompts from transcript
  const liveForDoc = useTaskLive(!isWorkstream ? workItemId : null);
  const docTranscript = liveForDoc?.transcript ?? [];
  const allTranscript = isWorkstream ? transcript : docTranscript;
  const latestPrompts = useLatestPrompts(allTranscript);
  const [promptRole, setPromptRole] = useState<'generator' | 'evaluator'>('generator');
  const activePrompt = promptRole === 'evaluator' ? latestPrompts.ev : latestPrompts.gen;

  // Agent name lookup — resolve agent IDs to display names so the avatar
  // matches the prompt's agent, not the snapshot's (which may be stale).
  const [agentNames, setAgentNames] = useState<Record<string, string>>({});
  useEffect(() => {
    let cancelled = false;
    fetch('/api/agents', { credentials: 'include' })
      .then((r) => r.ok ? r.json() : [])
      .then((arr: Array<{ id: string; name: string }>) => {
        if (cancelled || !Array.isArray(arr)) return;
        const map: Record<string, string> = {};
        for (const a of arr) map[a.id] = a.name;
        setAgentNames(map);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  // Phase 49 follow-up — gate the Overview's agent-pair display on the
  // workstream's CURRENT status. When status is one of the pre-claim or
  // tombstoned states (planning/blocked/stalled/cancelled), the stored
  // `latestGen/EvalAgentId` reflects a PRIOR aborted run and
  // displaying it as if it were the live assignment confused the
  // operator (observed 2026-05-10: stage 2.1 was `blocked` waiting on
  // deps 1.1/1.2/1.3, yet the avatar panel showed Zhu Rong + Bi Gan
  // from iter-1/iter-2 deliverables). For docs (architect plan-doc
  // panels) we keep the legacy display — those have a stable
  // generator/evaluator across iterations.
  //
  // `historic` IS shown as assigned: it's a shipped row (PR merged)
  // whose plan version was later superseded, so its latestDeliverable
  // is the legitimate agent pair that completed the work. The backend
  // groups `historic` alongside `complete` as terminal-done at
  // StoriesEndpoints.cs:1664 (observed 2026-05-14: stage 3.2 Leader
  // Election was `historic` with a Mang Liu deliverable but the chips
  // read "— not assigned —").
  const wsRowForOverview = isWorkstream
    ? detail.workstreams.find((w) => w.id === selection.id)
    : null;
  const isWsCurrentlyAssigned = !isWorkstream || (() => {
    const s = wsRowForOverview?.status ?? '';
    return s === 'active' || s === 'review' || s === 'merging' || s === 'complete' || s === 'stalled' || s === 'historic';
  })();

  // Agent identity — prefer prompt-derived agent info (matches what the
  // system/user prompt sections display) over snapshot/API data which may
  // be from a different iteration with different round-robin agents.
  // Phase 53 follow-up: when the live snapshot hasn't loaded yet (worktree
  // setup failed before gen/eval started), fall back to the workstream
  // summary's latestGenAgentId / latestEvalAgentId so the overview always
  // shows the last known or assigned agent pair.
  const promptGenId = latestPrompts.gen?.agentId ?? null;
  const promptEvalId = latestPrompts.ev?.agentId ?? null;
  const fallbackGenId = isWorkstream
    ? (snapshot?.generatorAgentId ?? wsRowForOverview?.latestGenAgentId ?? wsRowForOverview?.agents?.[0] ?? null)
    : (docGen?.agentId ?? null);
  const fallbackEvalId = isWorkstream
    ? (snapshot?.evaluatorAgentId ?? wsRowForOverview?.latestEvalAgentId ?? wsRowForOverview?.agents?.[1] ?? null)
    : (docEval?.agentId ?? null);
  const rawGenId = promptGenId ?? fallbackGenId;
  const rawEvalId = promptEvalId ?? fallbackEvalId;
  const genId = isWsCurrentlyAssigned ? rawGenId : null;
  const evalId = isWsCurrentlyAssigned ? rawEvalId : null;
  // When the prompt carries a different agentId than the snapshot, use the
  // agentNames map (or raw ID) — never mix a prompt-derived ID with a
  // snapshot-derived name from a different agent.
  const genName = !isWsCurrentlyAssigned
    ? '— not assigned —'
    : promptGenId
      ? (agentNames[promptGenId] ?? promptGenId)
      : (isWorkstream ? (snapshot?.generatorAgentName ?? agentNames[fallbackGenId ?? ''] ?? fallbackGenId) : (docGen?.name ?? fallbackGenId));
  const evalName = !isWsCurrentlyAssigned
    ? '— not assigned —'
    : promptEvalId
      ? (agentNames[promptEvalId] ?? promptEvalId)
      : (isWorkstream ? (snapshot?.evaluatorAgentName ?? agentNames[fallbackEvalId ?? ''] ?? fallbackEvalId) : (docEval?.name ?? fallbackEvalId));
  const genModel = !isWsCurrentlyAssigned
    ? null
    : latestPrompts.gen?.model || (isWorkstream ? (snapshot?.generatorModel ?? null) : (docGen?.model ?? null));
  const evalModel = !isWsCurrentlyAssigned
    ? null
    : latestPrompts.ev?.model || (isWorkstream ? (snapshot?.evaluatorModel ?? null) : (docEval?.model ?? null));
  // Phase 47c — surface backend + invoke-mode on the Overview chips so
  // operators can see which usage cap each agent is hitting (Anthropic vs
  // GitHub Copilot). Only available from the latest prompt event; the
  // snapshot doesn't currently carry it. Null when no iter has dispatched
  // yet — chip falls back to model-only.
  const genBackend = latestPrompts.gen?.backend || null;
  const evalBackend = latestPrompts.ev?.backend || null;
  const genInvoke = latestPrompts.gen?.mode || null;
  const evalInvoke = latestPrompts.ev?.mode || null;

  // Cache of `eventId → { systemPrompt, userPrompt }` after the operator
  // clicks "Show full". The transcript-stream truncates strings >320 chars
  // per field; this hits /transcript/{eventId} for the full payload.
  const [expandedPrompts, setExpandedPrompts] = useState<Record<number, { systemPrompt?: string; userPrompt?: string }>>({});
  const expandedActive = activePrompt?.eventId != null ? expandedPrompts[activePrompt.eventId] : undefined;
  const effectiveSystemPrompt = expandedActive?.systemPrompt ?? activePrompt?.systemPrompt ?? '';
  const effectiveUserPrompt = expandedActive?.userPrompt ?? activePrompt?.userPrompt ?? '';

  async function handleShowFullPrompt() {
    if (!workItemId || activePrompt?.eventId == null) return;
    try {
      const evt = await forgeApi.getWorkItemTranscriptEvent(workItemId, activePrompt.eventId);
      const p = evt.payload as Record<string, unknown> | null | undefined;
      setExpandedPrompts((prev) => ({
        ...prev,
        [activePrompt.eventId!]: {
          systemPrompt: (p?.systemPrompt as string) ?? '',
          userPrompt: (p?.userPrompt as string) ?? '',
        },
      }));
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to fetch full prompt', 'danger');
    }
  }

  const showsTruncationBanner =
    activePrompt && (isTruncated(activePrompt.systemPrompt) || isTruncated(activePrompt.userPrompt))
    && !expandedActive;

  return (
    <div style={{ padding: 16, overflow: 'auto' }}>
      <AttachmentBadges attachments={detail.spawn.attachments} />
      {/* Agent pair — centered */}
      <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: 16, marginBottom: 16 }}>
        <PairSide
          role="generator"
          agentId={genId}
          agentName={genName}
          model={genModel}
          backend={genBackend}
          invokeMode={genInvoke}
          sameAsOther={!!genModel && genModel === evalModel}
          selected={promptRole === 'generator'}
          onClick={() => setPromptRole('generator')}
        />
        <span style={{ fontFamily: 'var(--sc-font-display)', fontSize: 14, color: 'var(--sc-text-dim)' }}>⇄</span>
        <PairSide
          role="evaluator"
          agentId={evalId}
          agentName={evalName}
          model={evalModel}
          backend={evalBackend}
          invokeMode={evalInvoke}
          sameAsOther={!!genModel && genModel === evalModel}
          selected={promptRole === 'evaluator'}
          onClick={() => setPromptRole('evaluator')}
        />
      </div>

      {/* Telemetry row */}
      <div style={{ display: 'flex', justifyContent: 'center', gap: 24, marginBottom: 16, fontFamily: 'var(--sc-font-display)', fontSize: 10, letterSpacing: '0.14em', color: 'var(--sc-text-dim)' }}>
        <span><span style={{ color: 'var(--sc-muted)' }}>COST</span> <strong style={{ color: 'var(--sc-text)' }}>${totalCost.toFixed(2)}</strong></span>
        <span title="Generator input+output tokens since the budget window opened.">
          <span style={{ color: 'var(--sc-muted)' }}>TOKENS</span>{' '}
          <strong style={{ color: 'var(--sc-text)' }}>
            {formatTokens(tokensUsed)}
          </strong>
        </span>
        {effectiveMaxIter > 0 && (
          <span><span style={{ color: 'var(--sc-muted)' }}>ITER</span> <strong style={{ color: 'var(--sc-text)' }}>{currentIter}/{effectiveMaxIter}</strong></span>
        )}
        {pairAttempt > 1 && (
          <span title={`This work item has been reassigned to a fresh agent pair ${pairAttempt - 1} time(s). After attempt ${maxAttempts} fails sloppy, the reconciler stamps terminal failure.`}>
            <span style={{ color: 'var(--sc-muted)' }}>ATTEMPT</span>{' '}
            <strong style={{ color: pairAttempt >= maxAttempts ? 'var(--sc-red)' : 'var(--sc-amber)' }}>
              {pairAttempt}/{maxAttempts}
            </strong>
          </span>
        )}
        {executionStatus && executionStatus !== 'idle' && (
          <span>
            <span style={{ color: 'var(--sc-muted)' }}>STATUS</span>{' '}
            <strong style={{ color: executionStatus === 'completed' ? 'var(--sc-green)' : executionStatus === 'live' ? 'var(--sc-cyan)' : executionStatus === 'failed' || executionStatus === 'stalled' ? 'var(--sc-red)' : 'var(--sc-text)' }}>
              {executionStatus.toUpperCase()}
            </strong>
          </span>
        )}
      </div>

      {/* Prompt role switcher */}
      <div style={{ display: 'flex', gap: 0, marginBottom: 8, borderBottom: '1px solid var(--sc-border)' }}>
        {(['generator', 'evaluator'] as const).map((role) => (
          <button
            key={role}
            type="button"
            onClick={() => setPromptRole(role)}
            style={{
              flex: 1,
              padding: '8px 0',
              background: 'transparent',
              border: 'none',
              borderBottom: promptRole === role ? '2px solid var(--sc-amber)' : '2px solid transparent',
              fontFamily: 'var(--sc-font-display)',
              fontSize: 10,
              letterSpacing: '0.16em',
              textTransform: 'uppercase',
              color: promptRole === role ? 'var(--sc-amber)' : 'var(--sc-text-dim)',
              cursor: 'pointer',
            }}
          >
            {role}
          </button>
        ))}
      </div>

      {/* Prompts — always shown */}
      <div>
        {!activePrompt ? (
          <em style={{ color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono)', fontSize: 11 }}>
            No {promptRole} prompt captured yet. Prompts appear once the crew dispatches.
          </em>
        ) : (
          <>
            {/* Truncation banner — backend trims systemPrompt/userPrompt to
             * 320 chars per field on the bulk transcript stream when an
             * event payload exceeds 4 KB. Click "Show full" to fetch the
             * untruncated event from /transcript/{eventId}. */}
            {showsTruncationBanner && (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 10,
                padding: '6px 10px', marginBottom: 10,
                background: 'rgba(255, 179, 71, 0.06)',
                border: '1px solid var(--sc-amber-d)',
                borderRadius: 3,
                fontFamily: 'var(--sc-font-mono)', fontSize: 10.5, color: 'var(--sc-text-dim)',
              }}>
                <span style={{ color: 'var(--sc-amber)', fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.18em' }}>
                  TRUNCATED
                </span>
                <span style={{ flex: 1 }}>
                  Prompt fields over 320 chars are clipped on the live stream. Fetch the full payload to verify.
                </span>
                <button
                  type="button"
                  onClick={() => void handleShowFullPrompt()}
                  style={{
                    background: 'transparent',
                    border: '1px solid var(--sc-amber-d)',
                    color: 'var(--sc-amber)',
                    fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.18em',
                    textTransform: 'uppercase',
                    padding: '4px 10px', borderRadius: 3, cursor: 'pointer',
                  }}
                >
                  ↗ Show full
                </button>
              </div>
            )}
            <div style={{ marginBottom: 12 }}>
              <div style={{ fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.16em', color: 'var(--sc-muted)', marginBottom: 4, textTransform: 'uppercase' }}>
                System prompt
                {expandedActive?.systemPrompt && <span style={{ color: 'var(--sc-green)', marginLeft: 6 }}>· FULL</span>}
              </div>
              {effectiveSystemPrompt
                ? <pre style={{ fontFamily: 'var(--sc-font-mono)', fontSize: 11, color: 'var(--sc-text)', whiteSpace: 'pre-wrap', wordBreak: 'break-word', margin: 0, lineHeight: 1.5 }}>{effectiveSystemPrompt}</pre>
                : <em style={{ color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono)', fontSize: 11 }}>(no system prompt)</em>}
            </div>
            <div>
              <div style={{ fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.16em', color: 'var(--sc-muted)', marginBottom: 4, textTransform: 'uppercase' }}>
                User prompt
                {expandedActive?.userPrompt && <span style={{ color: 'var(--sc-green)', marginLeft: 6 }}>· FULL</span>}
              </div>
              {effectiveUserPrompt
                ? <pre style={{ fontFamily: 'var(--sc-font-mono)', fontSize: 11, color: 'var(--sc-text)', whiteSpace: 'pre-wrap', wordBreak: 'break-word', margin: 0, lineHeight: 1.5 }}>{effectiveUserPrompt}</pre>
                : <em style={{ color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono)', fontSize: 11 }}>(empty user prompt)</em>}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/* ─── QUESTIONS PANE ─────────────────────────────────────────────────── */

function QuestionsPane({ workItemId }: { workItemId: string }) {
  const showToast = useToastStore((s) => s.show);

  return (
    <div style={{ padding: 12 }}>
      <OpenQuestionsWizard
        workItemId={workItemId}
        onAnswered={(left) => {
          if (left === 0) {
            showToast('All questions answered — the next iteration can proceed.', 'success');
          }
        }}
      />
    </div>
  );
}

/* ─── PR PANE ────────────────────────────────────────────────────────── */

function PrPane({ detail, selection }: { detail: StorySpawnDetail; selection: Selection }) {
  const prIds = useMemo(() => {
    if (selection.kind === 'doc') {
      // Phase 53 — sequential mode: look up the per-doc PR first.
      const docKey = selection.id?.startsWith('doc:') ? selection.id.slice(4) : selection.id;
      const docEntry = detail.spawn.planDocPrs?.find((d) => d.key === docKey);
      if (docEntry?.prId) return [docEntry.prId];
      // Legacy single-PR fallback.
      if (detail.spawn.planPrId) return [detail.spawn.planPrId];
      return [];
    }
    if (selection.kind === 'phase') {
      const ws = detail.workstreams.find((w) => w.id === selection.id);
      return ws?.prs ?? [];
    }
    return [];
  }, [detail, selection]);

  if (prIds.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ PR</div>
        <div className="sub">No PR linked yet.</div>
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16, overflow: 'auto', padding: '4px 0' }}>
      {prIds.map((id) => <PrCard key={id} prId={id} />)}
    </div>
  );
}

function PrCard({ prId }: { prId: string }) {
  const [data, setData] = useState<PrDetailsResponse | null>(null);
  const [err, setErr] = useState(false);
  const [comments, setComments] = useState<PrComment[]>([]);
  const [commentsErr, setCommentsErr] = useState<string | null>(null);
  const [builds, setBuilds] = useState<Build[]>([]);
  const [merging, setMerging] = useState(false);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const d = await forgeApi.getPrDetails(prId);
        if (!cancelled) setData(d);
      } catch { if (!cancelled) setErr(true); }
      try {
        const b = await forgeApi.listPrBuilds(prId, { limit: 5 });
        if (!cancelled) setBuilds(b.items);
      } catch { /* ignore */ }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [prId]);

  async function handleMerge() {
    const ok = await useConfirmStore.getState().ask({
      title: `Merge ${prId}?`,
      body: 'This will merge the PR into its base branch. If there are conflicts, Forge will surface them so you can resolve in the provider UI.',
      tone: 'warn',
      confirmLabel: '⛙ Merge',
      cancelLabel: 'Cancel',
    });
    if (!ok) return;
    setMerging(true);
    try {
      await runMerge(false);
    } finally {
      setMerging(false);
    }
  }

  async function runMerge(force: boolean) {
    try {
      const { queued, detail } = await forgeApi.mergePr(prId, { force });
      if (queued) {
        showToast(detail ?? `${prId} merge queued by provider.`, 'info');
      } else {
        showToast(`${prId} merged successfully.`, 'success');
      }
      const d = await forgeApi.getPrDetails(prId);
      setData(d);
    } catch (e) {
      // 409 with conflict=true → branch on `resolution` so the operator
      // sees the right next step:
      //   - "force-available": local rebase / first provider PUT failed
      //     and force has not been attempted; offer the destructive
      //     -X ours auto-resolve as a confirm.
      //   - "manual-required": Forge already attempted the destructive
      //     auto-resolve (either via inherited AutoMerge or an explicit
      //     ?force=true) and the provider still rejects the merge.
      //     There's no auto path left — redirect the operator to the
      //     provider UI to resolve manually.
      if (e instanceof ForgeApiError && e.status === 409 && e.problem?.conflict === true) {
        const resolution = typeof e.problem?.resolution === 'string' ? e.problem.resolution : 'force-available';
        const webUrl = data?.pr.webUrl;
        const hint = typeof e.problem?.hint === 'string' ? e.problem.hint : null;
        const detail = typeof e.problem?.detail === 'string' ? e.problem.detail : null;

        if (resolution === 'manual-required') {
          // Confirm = "Open in provider" (actually opens the URL); cancel = close.
          // Falls back to a plain "Close" confirm when no provider URL is known.
          const openProvider = await useConfirmStore.getState().ask({
            title: `Merge conflict on ${prId} — manual resolution required`,
            body:
              (detail ? detail + '\n\n' : '') +
              (hint ?? 'Resolve the conflict in the provider UI (web editor or fetch + merge locally with manual resolution, then push), then retry the merge here.'),
            tone: 'danger',
            confirmLabel: webUrl ? 'Open in provider ↗' : 'OK',
            cancelLabel: 'Close',
          });
          if (openProvider && webUrl) {
            window.open(webUrl, '_blank', 'noopener,noreferrer');
          }
          return;
        }

        // resolution === 'force-available' (default).
        const goForce = await useConfirmStore.getState().ask({
          title: `Merge conflict on ${prId}`,
          body:
            (hint ? hint + '\n\n' : '') +
            'Force-merge will take the source branch\'s version on every conflicting hunk and discard upstream-side changes (-X ours). This is destructive — prefer resolving in the provider UI.',
          tone: 'danger',
          confirmLabel: '⚠ Force-merge (-X ours)',
          // The previous label "Open in provider" was misleading: clicking
          // cancel did nothing. Operators who want the provider UI use the
          // dedicated "Open in <provider> ↗" link in the PR header.
          cancelLabel: 'Cancel',
        });
        if (goForce) {
          await runMerge(true);
        }
        return;
      }
      showToast(e instanceof Error ? e.message : 'Merge failed', 'danger');
    }
  }

  const refreshComments = async () => {
    try {
      const r = await forgeApi.listPrComments(prId, { take: 200 });
      setComments(r.items);
      setCommentsErr(null);
    } catch (e) {
      setCommentsErr(e instanceof Error ? e.message : 'Failed to load comments');
    }
  };

  useEffect(() => {
    void refreshComments();
    // 12s — comments are slower-moving than the live transcript; this
    // matches the cadence PrDetailPanel uses on the dedicated PR page.
    const id = setInterval(() => { void refreshComments(); }, 12_000);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prId]);

  // Approval verdict — derived from review comments (skips bot/CI events).
  const verdict = useMemo(() => deriveVerdict(comments), [comments]);

  // Split human review comments from CI-bot build/policy events. Build events
  // get their own compact strip below the header; review comments + threads
  // render via the shared PrCommentsList component.
  const buildEventComments = useMemo(() => comments.filter(isBuildEvent), [comments]);

  if (err) return <div style={dimStyle}>Failed to load PR {prId}</div>;
  if (!data) return <div style={dimStyle}>Loading PR {prId}…</div>;

  const { pr, description, reviewers } = data;
  const colColor = pr.col === 'merged' ? '#4ec9b0'
    : pr.col === 'review' ? '#dcdcaa'
    : pr.col === 'build'  ? '#569cd6'
    : 'var(--sc-text-dim)';

  return (
    <div style={{ border: '1px solid var(--sc-border)', borderRadius: 4, padding: 12 }}>
      {/* ── Header ───────────────────────────────────────────────────── */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 8 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--sc-text)' }}>{pr.title}</div>
            <PrApprovalBadge verdict={verdict} showPending />
          </div>
          <div style={{ fontSize: 10.5, color: 'var(--sc-text-dim)', marginTop: 3, fontFamily: 'var(--sc-font-mono, monospace)' }}>
            <strong style={{ color: 'var(--sc-text)' }}>{pr.id}</strong>
            {' · '}{pr.author?.name ?? '—'}
            {' · '}{pr.branch} → {pr.base}
          </div>
        </div>
        <span style={{ fontSize: 9, fontFamily: 'var(--sc-font-display)', letterSpacing: '0.16em', color: colColor, flexShrink: 0 }}>
          {pr.col.toUpperCase()}
        </span>
      </div>

      {/* ── Stats strip ──────────────────────────────────────────────── */}
      <div style={{ display: 'flex', gap: 16, marginTop: 10, fontSize: 11, fontFamily: 'var(--sc-font-mono, monospace)', color: 'var(--sc-text-dim)', flexWrap: 'wrap' }}>
        <span><b style={{ color: 'var(--sc-text)' }}>{pr.files}</b> files</span>
        <span><b style={{ color: 'var(--sc-text)' }}>{pr.commits}</b> commits</span>
        <span style={{ color: '#4ec9b0' }}>+{pr.additions}</span>
        <span style={{ color: '#f44747' }}>−{pr.deletions}</span>
        <span>{comments.length} comment{comments.length === 1 ? '' : 's'}</span>
        {reviewers.length > 0 && <span>reviewers: {reviewers.join(', ')}</span>}
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8 }}>
          {pr.webUrl && (
            <a href={pr.webUrl} target="_blank" rel="noopener noreferrer"
              style={{ fontSize: 10, color: '#569cd6', textDecoration: 'none' }}>
              {/* GitHub PR ids look like "PR owner/repo#42" (contain '/'),
                  ADO is "PR #15634971". The previous heuristic checked
                  only the "PR " prefix, which both formats share, so every
                  GitHub PR was mis-labelled "ADO". Fall through to webUrl
                  as the secondary signal for safety. */}
              Open in {(pr.id.includes('/') || pr.webUrl.includes('github.com')) ? 'GitHub' : 'ADO'} ↗
            </a>
          )}
          {pr.col !== 'merged' && (
            <button
              type="button"
              disabled={merging}
              onClick={() => void handleMerge()}
              style={{
                fontSize: 10, padding: '2px 10px',
                background: 'transparent',
                border: '1px solid #4ec9b0',
                borderRadius: 3,
                color: '#4ec9b0',
                cursor: merging ? 'wait' : 'pointer',
                fontFamily: 'var(--sc-font-display, monospace)',
                letterSpacing: '0.1em',
                opacity: merging ? 0.5 : 1,
              }}
            >
              {merging ? '⏳ Merging…' : '⛙ MERGE'}
            </button>
          )}
        </div>
      </div>

      {/* ── Description ─────────────────────────────────────────────── */}
      {description && (
        <details style={{ marginTop: 10 }}>
          <summary style={{
            fontSize: 9.5, fontFamily: 'var(--sc-font-display, monospace)', letterSpacing: '0.18em',
            color: 'var(--sc-muted)', cursor: 'pointer', textTransform: 'uppercase',
          }}>
            Description
          </summary>
          <div style={{
            fontSize: 11.5, color: 'var(--sc-text-dim)', marginTop: 6,
            whiteSpace: 'pre-wrap', maxHeight: 200, overflow: 'auto', lineHeight: 1.55,
            fontFamily: 'var(--sc-font-mono, monospace)',
            paddingLeft: 8, borderLeft: '1px solid var(--sc-hairline)',
          }}>
            {description}
          </div>
        </details>
      )}

      {/* ── Builds ──────────────────────────────────────────────────── */}
      {builds.length > 0 && (
        <div style={{ marginTop: 14 }}>
          <div style={{
            fontSize: 9.5, fontFamily: 'var(--sc-font-display, monospace)', letterSpacing: '0.18em',
            color: 'var(--sc-muted)', textTransform: 'uppercase', marginBottom: 6,
          }}>
            Builds <span style={{ color: 'var(--sc-text-dim)', letterSpacing: '0.04em', textTransform: 'none' }}>· latest {builds.length}</span>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {builds.map((b) => (
              <div key={b.id} style={{
                display: 'flex', alignItems: 'center', gap: 12,
                padding: '4px 8px',
                borderLeft: `2px solid ${buildBorderColor(b.state, b.result)}`,
                background: 'rgba(255,255,255,0.02)',
                fontSize: 11, fontFamily: 'var(--sc-font-mono, monospace)',
              }}>
                <span style={{ color: 'var(--sc-text-dim)', minWidth: 90 }}>#{b.providerBuildId}</span>
                <span style={{
                  fontSize: 9, fontFamily: 'var(--sc-font-display, monospace)',
                  letterSpacing: '0.14em', textTransform: 'uppercase',
                  padding: '1px 6px', borderRadius: 2,
                  color: buildBorderColor(b.state, b.result),
                  border: `1px solid ${buildBorderColor(b.state, b.result)}`,
                }}>
                  {(b.result ?? b.state).toUpperCase()}
                </span>
                {b.startedAt && (
                  <span style={{ color: 'var(--sc-muted)', fontSize: 10 }}>
                    {new Date(b.startedAt).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
                  </span>
                )}
                {b.webUrl && (
                  <a href={b.webUrl} target="_blank" rel="noopener noreferrer"
                    style={{ marginLeft: 'auto', fontSize: 10, color: '#569cd6', textDecoration: 'none' }}>
                    logs ↗
                  </a>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* ── Comments (review + thread) ──────────────────────────────── */}
      <div style={{ marginTop: 16, paddingTop: 12, borderTop: '1px solid var(--sc-hairline)' }}>
        <div style={{
          fontSize: 9.5, fontFamily: 'var(--sc-font-display, monospace)', letterSpacing: '0.18em',
          color: 'var(--sc-muted)', textTransform: 'uppercase', marginBottom: 8,
          display: 'flex', alignItems: 'center', gap: 8,
        }}>
          <span>Comments</span>
          {comments.length > 0 && (
            <span style={{ color: 'var(--sc-text-dim)', letterSpacing: '0.04em', textTransform: 'none', fontFamily: 'var(--sc-font-mono, monospace)' }}>
              · {comments.length - buildEventComments.length} review · {buildEventComments.length} CI events
            </span>
          )}
        </div>
        <PrCommentsList
          prId={prId}
          comments={comments}
          error={commentsErr}
          onRefresh={refreshComments}
          callerIsHuman
        />
      </div>
    </div>
  );
}

function buildBorderColor(state: string, result: string | null | undefined): string {
  if (result === 'succeeded' || state === 'completed' && result === 'succeeded') return '#4ec9b0';
  if (result === 'failed' || state === 'failed') return '#f44747';
  if (state === 'inProgress' || state === 'running' || state === 'queued') return '#569cd6';
  return 'var(--sc-text-dim)';
}

/* ─── QA PANE ────────────────────────────────────────────────────────── */

function QaPane({ detail, artifacts }: { detail: StorySpawnDetail; artifacts: StorySpawnArtifacts | null }) {
  const qaPrId = artifacts?.qa.pr ?? detail.spawn.qaPrId ?? null;
  const qaFiles = artifacts?.qa.files ?? [];
  const [builds, setBuilds] = useState<Build[]>([]);
  const [prData, setPrData] = useState<PrDetailsResponse | null>(null);

  useEffect(() => {
    if (!qaPrId) return;
    let cancelled = false;
    forgeApi.getPrDetails(qaPrId)
      .then((d) => { if (!cancelled) setPrData(d); })
      .catch(() => {});
    forgeApi.listPrBuilds(qaPrId, { limit: 5 })
      .then((d) => { if (!cancelled) setBuilds(d.items); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [qaPrId]);

  if (!qaPrId) {
    // docs/forge/e2e.md Stage 9.3 — new-shape spawns never enter the
    // legacy `Phase='qa'` and never populate `spawn.QaPrId`. The
    // per-stage qa-e2e workstreams open `[e2e]` PRs visible in the
    // SwarmMasterPanel stacked rows + the workstream-detail tabs;
    // this story-level QA pane is now a legacy-only surface that
    // only renders for spawns parked at the old QA batch flow.
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ QA / E2E</div>
        <div className="sub">
          No story-level QA PR for this spawn — per-stage `qa-e2e`
          workstreams ship their own `[e2e]` PRs (see the stacked rows in
          the phase board). The legacy story-level QA batch flow only
          fires for spawns paused under the pre-Stage-3.1 orchestrator
          (`Phase='qa'` + `QaPrId` set).
        </div>
      </div>
    );
  }

  return (
    <div style={{ overflow: 'auto', padding: '4px 0' }}>
      {/* QA PR summary */}
      {prData && (
        <div style={{ border: '1px solid var(--sc-border)', borderRadius: 4, padding: 10, marginBottom: 12 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--sc-text)' }}>{prData.pr.title}</div>
          <div style={{ fontSize: 10, color: 'var(--sc-text-dim)', marginTop: 2 }}>
            {prData.pr.author?.name ?? '—'} · {prData.pr.branch}
          </div>
          <div style={{ display: 'flex', gap: 16, marginTop: 6, fontSize: 11, fontFamily: 'var(--sc-font-mono, monospace)', color: 'var(--sc-text-dim)' }}>
            <span>{prData.pr.files} files</span>
            <span style={{ color: '#4ec9b0' }}>+{prData.pr.additions}</span>
            <span style={{ color: '#f44747' }}>−{prData.pr.deletions}</span>
          </div>
          {prData.pr.webUrl && (
            <a href={prData.pr.webUrl} target="_blank" rel="noopener noreferrer"
              style={{ display: 'inline-block', marginTop: 6, fontSize: 10, color: '#569cd6' }}>
              Open QA PR ↗
            </a>
          )}
        </div>
      )}
      {!prData && <div style={dimStyle}>Loading QA PR {qaPrId}…</div>}

      {/* Builds */}
      {builds.length > 0 && (
        <div style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 9, fontFamily: 'var(--sc-font-display)', letterSpacing: '0.14em', color: 'var(--sc-text-dim)', marginBottom: 4 }}>BUILDS</div>
          {builds.map((b) => (
            <div key={b.id} style={{ ...rowStyle, borderBottom: '1px solid var(--sc-border)', gap: 12 }}>
              <span style={{ fontFamily: 'var(--sc-font-mono, monospace)', fontSize: 11, color: 'var(--sc-text-dim)' }}>#{b.providerBuildId}</span>
              <span style={{ fontSize: 9, fontFamily: 'var(--sc-font-display)', letterSpacing: '0.1em', padding: '1px 6px', borderRadius: 3, background: buildBg(b.state), color: buildFg(b.state) }}>
                {b.state.toUpperCase()}
              </span>
              <span style={{ fontSize: 10, color: 'var(--sc-text-dim)' }}>{b.result ?? ''}</span>
              {b.webUrl && (
                <a href={b.webUrl} target="_blank" rel="noopener noreferrer" style={{ fontSize: 10, color: '#569cd6', marginLeft: 'auto' }}>
                  logs ↗
                </a>
              )}
            </div>
          ))}
        </div>
      )}

      {/* QA file list */}
      {qaFiles.length > 0 && (
        <div>
          <div style={{ fontSize: 9, fontFamily: 'var(--sc-font-display)', letterSpacing: '0.14em', color: 'var(--sc-text-dim)', marginBottom: 4 }}>QA FILES</div>
          {qaFiles.map((f) => (
            <div key={f} style={{ fontSize: 11, fontFamily: 'var(--sc-font-mono, monospace)', color: 'var(--sc-text-dim)', padding: '2px 0' }}>
              {f}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/* ─── TRACE PANE ─────────────────────────────────────────────────────── */

function TracePane({ detail }: { detail: StorySpawnDetail }) {
  const entries = useMemo(() => {
    return detail.workstreams
      .filter((w) => w.lastActiveAt)
      .map((w) => ({
        ts: w.lastActiveAt!,
        name: w.name,
        status: w.status,
        verdict: w.latestVerdict ?? null,
        score: w.latestScore ?? null,
      }))
      .sort((a, b) => Date.parse(b.ts) - Date.parse(a.ts));
  }, [detail.workstreams]);

  if (entries.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ Trace</div>
        <div className="sub">No activity recorded yet.</div>
      </div>
    );
  }

  return (
    <div style={{ overflow: 'auto', padding: '4px 0' }}>
      <div style={{ ...rowStyle, fontFamily: 'var(--sc-font-display)', fontSize: 9, letterSpacing: '0.14em', color: 'var(--sc-text-dim)', borderBottom: '1px solid var(--sc-border)' }}>
        <span style={{ width: 70 }}>TIME</span>
        <span style={{ flex: 2, minWidth: 0 }}>WORKSTREAM</span>
        <span style={{ width: 80, textAlign: 'center' }}>STATUS</span>
        <span style={{ width: 70, textAlign: 'center' }}>VERDICT</span>
        <span style={{ width: 50, textAlign: 'right' }}>SCORE</span>
      </div>
      {entries.map((e, i) => {
        const verdictColor = e.verdict === 'pass' ? '#4ec9b0' : e.verdict === 'fail' ? '#f44747' : 'var(--sc-text-dim)';
        return (
          <div key={i} style={{ ...rowStyle, borderBottom: '1px solid var(--sc-border)' }}>
            <span style={{ width: 70, fontFamily: 'var(--sc-font-mono, monospace)', fontSize: 10, color: 'var(--sc-text-dim)' }}>
              {formatTimestamp(e.ts)}
            </span>
            <span style={{ flex: 2, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 11, color: 'var(--sc-text)' }} title={e.name}>
              {e.name}
            </span>
            <span style={{ width: 80, textAlign: 'center' }}>
              <span style={{ fontSize: 9, fontFamily: 'var(--sc-font-display)', letterSpacing: '0.1em', padding: '1px 6px', borderRadius: 3, background: statusBg(e.status), color: statusFg(e.status) }}>
                {e.status.toUpperCase()}
              </span>
            </span>
            <span style={{ width: 70, textAlign: 'center', fontFamily: 'var(--sc-font-mono, monospace)', fontSize: 11, color: verdictColor }}>
              {e.verdict ?? '—'}
            </span>
            <span style={{ width: 50, textAlign: 'right', fontFamily: 'var(--sc-font-mono, monospace)', fontSize: 11, color: 'var(--sc-text-dim)' }}>
              {e.score ?? '—'}
            </span>
          </div>
        );
      })}
    </div>
  );
}

/* ─── SHARED STYLE/COLOR HELPERS ─────────────────────────────────────── */

const dimStyle: React.CSSProperties = { fontSize: 11, color: 'var(--sc-text-dim)', padding: 8 };

const rowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8, padding: '5px 4px',
};

function statusBg(s: string): string {
  if (s === 'complete') return 'rgba(78,201,176,0.15)';
  if (s === 'review' || s === 'merging') return 'rgba(220,220,170,0.15)';
  if (s === 'stalled') return 'rgba(244,71,71,0.15)';
  if (s === 'coding' || s === 'building' || s === 'testing') return 'rgba(86,156,214,0.15)';
  return 'rgba(128,128,128,0.10)';
}

function statusFg(s: string): string {
  if (s === 'complete') return '#4ec9b0';
  if (s === 'review' || s === 'merging') return '#dcdcaa';
  if (s === 'stalled') return '#f44747';
  if (s === 'coding' || s === 'building' || s === 'testing') return '#569cd6';
  return 'var(--sc-text-dim)';
}

function buildBg(s: string): string {
  if (s === 'succeeded' || s === 'completed') return 'rgba(78,201,176,0.15)';
  if (s === 'failed') return 'rgba(244,71,71,0.15)';
  if (s === 'inProgress' || s === 'running') return 'rgba(86,156,214,0.15)';
  return 'rgba(128,128,128,0.10)';
}

function buildFg(s: string): string {
  if (s === 'succeeded' || s === 'completed') return '#4ec9b0';
  if (s === 'failed') return '#f44747';
  if (s === 'inProgress' || s === 'running') return '#569cd6';
  return 'var(--sc-text-dim)';
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '—';
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  return `${hh}:${mm}:${ss}`;
}

/* ─── Decomposition diagram ─────────────────────────────────────────── */

const WS_STATUS_COLOR: Record<string, string> = {
  active:   'var(--sc-green, #66e5b8)',
  blocked:  'var(--sc-amber, #ffd57c)',
  planning: 'var(--sc-blue, #7cc1ff)',
  review:   'var(--sc-amber, #ffd57c)',
  merging:  'var(--sc-green, #66e5b8)',
  complete: 'var(--sc-muted, #888)',
  cancelled:'var(--sc-muted, #888)',
  stalled:  'var(--sc-red, #ff7caa)',
};

const WS_DAG_DEFAULT_PHASE = '__workstreams__';

function loadWsDagCollapsed(storyId: string): Set<string> {
  if (typeof window === 'undefined') return new Set();
  try {
    const raw = window.localStorage.getItem(`forge.ws-dag.collapsed.${storyId}`);
    if (!raw) return new Set();
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? new Set(parsed.filter((x): x is string => typeof x === 'string')) : new Set();
  } catch { return new Set(); }
}

function persistWsDagCollapsed(storyId: string, collapsed: Set<string>) {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(`forge.ws-dag.collapsed.${storyId}`, JSON.stringify([...collapsed]));
  } catch { /* quota / insecure context — silent */ }
}

/** Convert `phase-network-transport` → `Network Transport`. */
function wsPhaseLabel(anchor: string): string {
  if (anchor === WS_DAG_DEFAULT_PHASE) return 'Workstreams';
  const stem = anchor.startsWith('phase-') ? anchor.slice(6) : anchor;
  return stem.split('-').filter((w) => w.length > 0)
    .map((w) => w[0].toUpperCase() + w.slice(1)).join(' ');
}

/** Convert `phase-foo/stage-bar-baz` → `Bar Baz`; fallback to ws.name. */
function wsStageLabel(planAnchor: string | null | undefined, fallback: string): string {
  if (!planAnchor) return fallback;
  const slash = planAnchor.indexOf('/');
  if (slash < 0) return fallback;
  const stagePart = planAnchor.slice(slash + 1);
  const stem = stagePart.startsWith('stage-') ? stagePart.slice(6) : stagePart;
  if (stem.length === 0) return fallback;
  return stem.split('-').filter((w) => w.length > 0)
    .map((w) => w[0].toUpperCase() + w.slice(1)).join(' ');
}

interface WsPhaseGroup {
  anchor: string;
  label: string;
  items: StorySpawnDetail['workstreams'];
}

function groupWsByPhase(ws: StorySpawnDetail['workstreams']): WsPhaseGroup[] {
  const order: string[] = [];
  const buckets = new Map<string, StorySpawnDetail['workstreams']>();
  for (const w of ws) {
    const anchor = w.phaseAnchor && w.phaseAnchor.length > 0 ? w.phaseAnchor : WS_DAG_DEFAULT_PHASE;
    if (!buckets.has(anchor)) {
      order.push(anchor);
      buckets.set(anchor, []);
    }
    buckets.get(anchor)!.push(w);
  }
  return order.map((anchor) => ({
    anchor,
    label: wsPhaseLabel(anchor),
    items: buckets.get(anchor)!,
  }));
}

const WS_NODE_W = 224;
const WS_NODE_H = 96;
const WS_COL_GAP = 56;
const WS_ROW_GAP = 14;
// Left padding doubles as the gutter the orthogonal edge rail lives in;
// wide enough to fit a few nested lanes (see WS_RAIL_* below) without the
// rail clipping past the phase box's left border.
const WS_PAD_X = 36;
const WS_PAD_Y = 14;

// docs/forge/e2e.md — orthogonal side-rail edge router geometry. Same-column
// (and leftward) dep edges leave a card's left edge, drop down a vertical
// rail in the left gutter, then elbow back into the target's left edge so a
// curve never crosses over a card's text. Nested / skip edges get pushed to
// an outer lane so they don't overlap.
const WS_RAIL_GAP = 12;      // lane-0 rail offset left of the card's left edge
const WS_RAIL_LANE_GAP = 8;  // additional left offset per nested lane
const WS_RAIL_MIN_X = 3;     // clamp so the rail never leaves the phase box
const WS_RAIL_CORNER_R = 6;  // rounded-elbow radius

interface PhaseLayout {
  cards: Array<{ ws: StorySpawnDetail['workstreams'][number]; x: number; y: number }>;
  arrows: Array<{ d: string; fromId: string; toId: string; tone: 'normal' | 'blocked' }>;
  width: number;
  height: number;
}

/** docs/forge/e2e.md Stage 8.2 — vertical gap between an impl card
 *  and its stacked qa-e2e child. Smaller than `WS_ROW_GAP` so the pair
 *  visually reads as ONE logical stage (the impl above + its trailing
 *  qa-e2e below), not two unrelated rows. */
const WS_QAE2E_STACK_GAP = 8;

/** Build an SVG path through `points` with rounded corners of radius `r`
 *  at each interior vertex. Drives the workstream DAG's orthogonal
 *  side-rail edges so elbows read as soft right-angles instead of the
 *  harsh pixel notch a pure `L` join produces. Degenerate corners (where
 *  a segment is shorter than the radius) fall back to a sharp join. */
function orthogonalPath(points: ReadonlyArray<{ x: number; y: number }>, r: number): string {
  if (points.length === 0) return '';
  if (points.length === 1) return `M ${points[0].x} ${points[0].y}`;
  let d = `M ${points[0].x} ${points[0].y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const p0 = points[i - 1];
    const p1 = points[i];
    const p2 = points[i + 1];
    const len1 = Math.hypot(p1.x - p0.x, p1.y - p0.y) || 1;
    const len2 = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
    const rr = Math.min(r, len1 / 2, len2 / 2);
    if (rr <= 0.5) { d += ` L ${p1.x} ${p1.y}`; continue; }
    const ax = p1.x - ((p1.x - p0.x) / len1) * rr;
    const ay = p1.y - ((p1.y - p0.y) / len1) * rr;
    const bx = p1.x + ((p2.x - p1.x) / len2) * rr;
    const by = p1.y + ((p2.y - p1.y) / len2) * rr;
    d += ` L ${ax} ${ay} Q ${p1.x} ${p1.y} ${bx} ${by}`;
  }
  const last = points[points.length - 1];
  d += ` L ${last.x} ${last.y}`;
  return d;
}

/** Topologically position a phase's stages by INTRA-phase deps only.
 *  Cross-phase deps are surfaced separately (chips) rather than as
 *  arrows that would dangle when the upstream phase is collapsed.
 *
 *  docs/forge/e2e.md Stage 8.2 — qa-e2e workstreams are NOT positioned
 *  as standalone column nodes (which previously bunched every qa-e2e
 *  in the rightmost column of its phase since their depth = max).
 *  Instead, the function partitions items into impl + qa-e2e BEFORE
 *  depth computation, runs topological layout on impl rows only, then
 *  appends each qa-e2e card directly beneath its impl sibling
 *  (matched via the `qaE2eByImplId` selector). The impl-to-qa-e2e
 *  edge that the qa-e2e row's `deps` array carries is automatically
 *  drawn by the GLOBAL arrow overlay (see `DecompositionDiagram`'s
 *  `arrows` useMemo) — the qa-e2e card's DOM ref registers like any
 *  other stage, and the intra-phase predicate is satisfied (both
 *  share the same `phaseAnchor`). */
function layoutPhase(items: StorySpawnDetail['workstreams']): PhaseLayout {
  const implItems = items.filter((i) => !isQaE2e(i));
  const qaByImpl = qaE2eByImplId(items);

  const ids = new Set(implItems.map((i) => i.id));
  const byId = new Map(implItems.map((i) => [i.id, i]));
  const depths = new Map<string, number>();

  function getDepth(id: string, visiting: Set<string>): number {
    const cached = depths.get(id);
    if (cached !== undefined) return cached;
    if (visiting.has(id)) return 0; // cycle guard
    visiting.add(id);
    const item = byId.get(id);
    if (!item) return 0;
    const intra = (item.deps ?? []).filter((d) => ids.has(d));
    if (intra.length === 0) { depths.set(id, 0); return 0; }
    const d = 1 + Math.max(0, ...intra.map((dep) => getDepth(dep, visiting)));
    depths.set(id, d);
    return d;
  }
  for (const it of implItems) getDepth(it.id, new Set());

  const maxDepth = Math.max(0, ...Array.from(depths.values()));
  const cols: Array<typeof implItems> = [];
  for (let d = 0; d <= maxDepth; d++) {
    cols.push(implItems.filter((i) => (depths.get(i.id) ?? 0) === d));
  }

  // docs/forge/e2e.md Stage 8.2 — variable row heights per column.
  // An impl row whose qa-e2e sibling is present takes a "double-tall"
  // slot (impl + small gap + qa-e2e); a lone-impl row takes the
  // standard single height. Track per-column slot offsets so
  // subsequent rows in the same column don't overlap their upstream
  // sibling's qa-e2e child.
  const cards: PhaseLayout['cards'] = [];
  const colOffsetY = new Array<number>(maxDepth + 1).fill(WS_PAD_Y);
  // Iterate column-by-column so within-column slot offsets accumulate
  // correctly (a column with [A-with-qa, B] needs A at offset 0 and B
  // at offset = WS_NODE_H + WS_QAE2E_STACK_GAP + WS_NODE_H + WS_ROW_GAP).
  for (let d = 0; d <= maxDepth; d++) {
    for (const item of cols[d]) {
      const x = WS_PAD_X + d * (WS_NODE_W + WS_COL_GAP);
      const y = colOffsetY[d];
      cards.push({ ws: item, x, y });

      const qa = qaByImpl.get(item.id);
      let advance = WS_NODE_H + WS_ROW_GAP;
      if (qa) {
        const qaY = y + WS_NODE_H + WS_QAE2E_STACK_GAP;
        cards.push({ ws: qa, x, y: qaY });
        advance = WS_NODE_H + WS_QAE2E_STACK_GAP + WS_NODE_H + WS_ROW_GAP;
      }
      colOffsetY[d] = y + advance;
    }
  }
  const cardById = new Map(cards.map((c) => [c.ws.id, c]));

  // `arrows` here is unused by the consumer (the global SVG overlay
  // in `DecompositionDiagram` re-derives arrows from DOM-measured
  // positions to handle cross-phase routing). Kept for historical
  // shape compatibility AND so a future consumer that wants a
  // pre-laid-out arrow set can read from the return value. Includes
  // the new impl→qa-e2e edges directly so any future consumer doesn't
  // have to re-derive them.
  const arrows: PhaseLayout['arrows'] = [];
  for (const item of items) {
    const to = cardById.get(item.id);
    if (!to) continue;
    for (const depId of item.deps ?? []) {
      const from = cardById.get(depId);
      if (!from) continue; // cross-phase, rendered as chip elsewhere
      const x1 = from.x + WS_NODE_W;
      const y1 = from.y + WS_NODE_H / 2;
      const x2 = to.x;
      const y2 = to.y + WS_NODE_H / 2;
      const midX = (x1 + x2) / 2;
      const tone = item.status === 'blocked' ? 'blocked' : 'normal';
      arrows.push({
        d: `M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`,
        fromId: from.ws.id,
        toId: to.ws.id,
        tone,
      });
    }
  }

  // Width: impl-only column count, NOT total items. qa-e2e cards
  // never push the phase wider (they stack vertically inside an
  // existing column).
  const width = WS_PAD_X * 2 + (maxDepth + 1) * WS_NODE_W + maxDepth * WS_COL_GAP;
  // Height: tallest column's accumulated offset (already includes
  // qa-e2e double-slot expansion). Final WS_ROW_GAP shouldn't count
  // as bottom padding so subtract it back off.
  const tallestColEnd = Math.max(WS_PAD_Y + WS_NODE_H, ...colOffsetY);
  const height = Math.max(WS_PAD_Y * 2 + WS_NODE_H, tallestColEnd - WS_ROW_GAP + WS_PAD_Y);
  return { cards, arrows, width, height };
}

function DecompositionDiagram({ detail }: { detail: StorySpawnDetail }) {
  const storyId = detail.spawn.storyId;
  const ws = detail.workstreams;
  const navigate = useNavigate();

  // Clicking a stage card jumps to the workstream-detail page with the
  // master pane open and that workstream pre-selected on the conversation
  // tab. URL shape mirrors the WorkItemDetailPanel "◇ Workstream" link at
  // workitems/WorkItemDetailPanel.tsx:732 so deep-links from anywhere in
  // the app converge on the same target. Param order matches the
  // operator-specified template in the Phase 63 ask
  // (mode=impl → master=open → sel=phase → selId).
  const openWorkstream = useCallback((workstreamId: string) => {
    const target = `/workstreams/${encodeURIComponent(storyId)}`
      + `?mode=impl&master=open&sel=phase&selId=${encodeURIComponent(workstreamId)}`;
    navigate(target);
  }, [navigate, storyId]);

  const [collapsed, setCollapsed] = useState<Set<string>>(() => loadWsDagCollapsed(storyId));
  useEffect(() => { setCollapsed(loadWsDagCollapsed(storyId)); }, [storyId]);

  const groups = useMemo(() => groupWsByPhase(ws), [ws]);
  const byId = useMemo(() => new Map(ws.map((w) => [w.id, w])), [ws]);
  const phaseAnchorOf = useMemo(() => {
    const m = new Map<string, string>();
    for (const w of ws) m.set(w.id, w.phaseAnchor && w.phaseAnchor.length > 0 ? w.phaseAnchor : WS_DAG_DEFAULT_PHASE);
    return m;
  }, [ws]);

  // Refs and live position state for the global arrow overlay. Each card
  // and each phase header registers its DOM node here; useLayoutEffect
  // measures every node's position relative to the phases container so
  // arrows draw correctly across phase boundaries (and route to the
  // phase header when the dep's phase is collapsed). Re-runs whenever
  // collapse state changes (cards unmount → ref nulls → measurement
  // refreshes).
  const phasesRef = useRef<HTMLDivElement | null>(null);
  const cardRefs = useRef<Map<string, HTMLElement>>(new Map());
  const headerRefs = useRef<Map<string, HTMLElement>>(new Map());
  const [layoutTick, setLayoutTick] = useState(0);
  const registerCardRef = useCallback((id: string, el: HTMLElement | null) => {
    if (el) cardRefs.current.set(id, el);
    else cardRefs.current.delete(id);
    setLayoutTick((t) => t + 1);
  }, []);
  const registerHeaderRef = useCallback((anchor: string, el: HTMLElement | null) => {
    if (el) headerRefs.current.set(anchor, el);
    else headerRefs.current.delete(anchor);
    setLayoutTick((t) => t + 1);
  }, []);

  type Anchor = { x: number; y: number; w: number; h: number };
  const [boxes, setBoxes] = useState<{ cards: Map<string, Anchor>; headers: Map<string, Anchor>; total: { w: number; h: number } }>({
    cards: new Map(), headers: new Map(), total: { w: 0, h: 0 },
  });
  useLayoutEffect(() => {
    const container = phasesRef.current;
    if (!container) return;
    const containerRect = container.getBoundingClientRect();
    const cardMap = new Map<string, Anchor>();
    cardRefs.current.forEach((el, id) => {
      const r = el.getBoundingClientRect();
      cardMap.set(id, {
        x: r.left - containerRect.left,
        y: r.top - containerRect.top,
        w: r.width,
        h: r.height,
      });
    });
    const headerMap = new Map<string, Anchor>();
    headerRefs.current.forEach((el, anchor) => {
      const r = el.getBoundingClientRect();
      headerMap.set(anchor, {
        x: r.left - containerRect.left,
        y: r.top - containerRect.top,
        w: r.width,
        h: r.height,
      });
    });
    setBoxes({ cards: cardMap, headers: headerMap, total: { w: containerRect.width, h: containerRect.height } });
  }, [collapsed, ws, layoutTick]);

  // Re-measure on container resize so window resize / sidebar collapse keep
  // the SVG aligned with the phases stack.
  useEffect(() => {
    const container = phasesRef.current;
    if (!container || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setLayoutTick((t) => t + 1));
    ro.observe(container);
    return () => ro.disconnect();
  }, []);

  // `phaseNum.stageNum` label for every workstream so the cross-phase dep
  // chips read like "1.2", "3.4" — the same numbering the operator sees in
  // implementation-plan.md (Stage N.M). Shared with the phase board
  // (SwarmMasterPanel) via `computeStageNumbers`; qa-e2e rows are labelled
  // `{implNum}-e2e` and do not consume a stage slot (docs/forge/e2e.md
  // Stage 8.1 — fixes the `1.1 → 1.1-e2e → 1.3` gap).
  const stageNumOf = useMemo(() => computeStageNumbers(ws), [ws]);

  type ArrowSpec = { d: string; tone: 'normal' | 'blocked'; key: string };
  // RawEdge carries the resolved source/target boxes plus a routing mode so
  // we can assign rail lanes (which need a global view of every edge's
  // vertical span) before emitting the final SVG `d` strings.
  type RawEdge = {
    from: Anchor;
    to: Anchor;
    tone: 'normal' | 'blocked';
    key: string;
    mode: 'rail' | 'horiz';
  };
  const arrows = useMemo<ArrowSpec[]>(() => {
    // 1) Collect every intra-phase edge with its DOM-measured boxes. Cross-
    //    phase edges stay as `↥ deps` chips (Phase 49 follow-up) so the
    //    overlay never turns into spaghetti when phases pile up.
    const raw: RawEdge[] = [];
    for (const w of ws) {
      const myPhase = phaseAnchorOf.get(w.id) ?? WS_DAG_DEFAULT_PHASE;
      const toCollapsed = collapsed.has(myPhase);
      const toBox = toCollapsed ? boxes.headers.get(myPhase) : boxes.cards.get(w.id);
      if (!toBox) continue;
      for (const depId of w.deps ?? []) {
        const upPhase = phaseAnchorOf.get(depId) ?? WS_DAG_DEFAULT_PHASE;
        if (upPhase !== myPhase) continue;
        const fromCollapsed = collapsed.has(upPhase);
        const fromBox = fromCollapsed ? boxes.headers.get(upPhase) : boxes.cards.get(depId);
        if (!fromBox) continue;
        const tone: 'normal' | 'blocked' = w.status === 'blocked' ? 'blocked' : 'normal';
        // Target clearly to the right (a deeper column) → keep the
        // left→right orthogonal route through the column gap. Otherwise
        // (same column, the common vertically-stacked chain) ride the
        // left rail so the edge never crosses an intervening card.
        const mode: RawEdge['mode'] =
          toBox.x > fromBox.x + fromBox.w / 2 ? 'horiz' : 'rail';
        raw.push({ from: fromBox, to: toBox, tone, key: `${depId}->${w.id}`, mode });
      }
    }

    // 2) Lane assignment for rail edges — greedy interval scheduling on the
    //    vertical span so overlapping (skip / nested) edges nest outward
    //    onto distinct lanes instead of drawing on top of each other.
    const railSpan = (e: RawEdge): [number, number] => {
      const a = e.from.y + e.from.h / 2;
      const b = e.to.y + e.to.h / 2;
      return a <= b ? [a, b] : [b, a];
    };
    const railEdges = raw.filter((e) => e.mode === 'rail')
      .sort((a, b) => railSpan(a)[0] - railSpan(b)[0]);
    const laneEnds: number[] = []; // yBottom currently occupying each lane
    const laneOf = new Map<RawEdge, number>();
    for (const e of railEdges) {
      const [top, bottom] = railSpan(e);
      let lane = laneEnds.findIndex((end) => end <= top);
      if (lane === -1) { lane = laneEnds.length; laneEnds.push(bottom); }
      else laneEnds[lane] = bottom;
      laneOf.set(e, lane);
    }

    // 3) Emit the orthogonal path for each edge.
    const out: ArrowSpec[] = [];
    for (const e of raw) {
      const fromY = e.from.y + e.from.h / 2;
      const toY = e.to.y + e.to.h / 2;
      let pts: Array<{ x: number; y: number }>;
      if (e.mode === 'horiz') {
        const x1 = e.from.x + e.from.w;
        const midX = (x1 + e.to.x) / 2;
        pts = [
          { x: x1, y: fromY },
          { x: midX, y: fromY },
          { x: midX, y: toY },
          { x: e.to.x, y: toY },
        ];
      } else {
        const lane = laneOf.get(e) ?? 0;
        const cardLeft = Math.min(e.from.x, e.to.x);
        const railX = Math.max(WS_RAIL_MIN_X, cardLeft - WS_RAIL_GAP - lane * WS_RAIL_LANE_GAP);
        pts = [
          { x: e.from.x, y: fromY },
          { x: railX, y: fromY },
          { x: railX, y: toY },
          { x: e.to.x, y: toY },
        ];
      }
      out.push({ d: orthogonalPath(pts, WS_RAIL_CORNER_R), tone: e.tone, key: e.key });
    }
    return out;
  }, [ws, phaseAnchorOf, collapsed, boxes]);

  function togglePhase(anchor: string) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(anchor)) next.delete(anchor);
      else next.add(anchor);
      persistWsDagCollapsed(storyId, next);
      return next;
    });
  }

  function expandAll() {
    setCollapsed(() => {
      const next = new Set<string>();
      persistWsDagCollapsed(storyId, next);
      return next;
    });
  }

  function collapseAll() {
    setCollapsed(() => {
      const next = new Set(groups.map((g) => g.anchor));
      persistWsDagCollapsed(storyId, next);
      return next;
    });
  }

  const activeCount = ws.filter((w) => w.status === 'active').length;
  const blockedCount = ws.filter((w) => w.status === 'blocked').length;
  const completeCount = ws.filter((w) => w.status === 'complete' || w.status === 'merging').length;
  const cancelledCount = ws.filter((w) => w.status === 'cancelled').length;

  return (
    <div className="ws-dag" style={{ padding: 16, overflow: 'auto', height: '100%' }}>
      <div className="ws-dag__hd">
        <span className="ws-dag__title">▌ WORKSTREAM DAG</span>
        <span className="ws-dag__counts">
          {ws.length} stages · {groups.length} phase{groups.length === 1 ? '' : 's'} · {activeCount} active
          {blockedCount > 0 ? ` · ${blockedCount} blocked` : ''}
          {completeCount > 0 ? ` · ${completeCount} complete/merging` : ''}
          {cancelledCount > 0 ? ` · ${cancelledCount} cancelled` : ''}
        </span>
        <span className="ws-dag__approval">{detail.spawn.decompositionApproved ? '✓ approved' : '⏳ pending approval'}</span>
        <span className="ws-dag__spacer" />
        <button type="button" className="ws-dag__btn" onClick={expandAll} title="Expand every phase">expand all</button>
        <button type="button" className="ws-dag__btn" onClick={collapseAll} title="Collapse every phase">collapse all</button>
      </div>

      <div className="ws-dag__phases" ref={phasesRef}>
        {/* Global arrow overlay — draws all dep edges (intra- and cross-
            phase) in a single absolutely-positioned SVG covering the
            entire phases stack. Dep targets that live in a collapsed
            phase route to the phase header so the operator still sees
            a visual signal that the workstream depends on something
            up there. */}
        <svg
          className="ws-dag__arrows-overlay"
          width={boxes.total.w}
          height={boxes.total.h}
          viewBox={`0 0 ${boxes.total.w} ${boxes.total.h}`}
          aria-hidden
        >
          <defs>
            <marker id="ws-arrow" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
              <path d="M 0 0 L 10 4 L 0 8 Z" fill="#7cc1ff" />
            </marker>
            <marker id="ws-arrow-blocked" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
              <path d="M 0 0 L 10 4 L 0 8 Z" fill="#ffd57c" />
            </marker>
          </defs>
          {arrows.map((a) => (
            <path
              key={a.key}
              d={a.d}
              fill="none"
              stroke={a.tone === 'blocked' ? '#ffd57c' : '#7cc1ff'}
              strokeWidth={2}
              strokeDasharray={a.tone === 'blocked' ? '6 4' : undefined}
              markerEnd={`url(#${a.tone === 'blocked' ? 'ws-arrow-blocked' : 'ws-arrow'})`}
            />
          ))}
        </svg>

        {groups.map((g, idx) => (
          <WsPhaseContainer
            key={g.anchor}
            group={g}
            phaseIndex={idx + 1}
            byId={byId}
            phaseAnchorOf={phaseAnchorOf}
            stageNumOf={stageNumOf}
            collapsed={collapsed.has(g.anchor)}
            onToggle={() => togglePhase(g.anchor)}
            registerCardRef={registerCardRef}
            registerHeaderRef={registerHeaderRef}
            onStageClick={openWorkstream}
          />
        ))}
      </div>
    </div>
  );
}

function WsPhaseContainer({
  group, phaseIndex, byId, phaseAnchorOf, stageNumOf, collapsed, onToggle,
  registerCardRef, registerHeaderRef, onStageClick,
}: {
  group: WsPhaseGroup;
  phaseIndex: number;
  byId: Map<string, StorySpawnDetail['workstreams'][number]>;
  phaseAnchorOf: Map<string, string>;
  stageNumOf: Map<string, string>;
  collapsed: boolean;
  onToggle: () => void;
  registerCardRef: (id: string, el: HTMLElement | null) => void;
  registerHeaderRef: (anchor: string, el: HTMLElement | null) => void;
  onStageClick: (workstreamId: string) => void;
}) {
  const layout = useMemo(() => layoutPhase(group.items), [group.items]);
  const activeInPhase = group.items.filter((w) => w.status === 'active').length;
  const blockedInPhase = group.items.filter((w) => w.status === 'blocked').length;
  const cancelledInPhase = group.items.filter((w) => w.status === 'cancelled').length;

  const headerCb = useCallback((el: HTMLElement | null) => registerHeaderRef(group.anchor, el),
    [registerHeaderRef, group.anchor]);

  return (
    <section className={`ws-phase${collapsed ? ' is-collapsed' : ''}`}>
      <header
        className="ws-phase__hd"
        ref={headerCb}
      >
        <button
          type="button"
          className="ws-phase__chevron"
          aria-expanded={!collapsed}
          aria-controls={`ws-phase-body-${group.anchor}`}
          onClick={onToggle}
          title={collapsed ? 'expand phase' : 'collapse phase'}
        >{collapsed ? '▸' : '▾'}</button>
        <span className="ws-phase__num">PHASE {phaseIndex}</span>
        <span className="ws-phase__title">{group.label}</span>
        <span className="ws-phase__count">
          {group.items.length} stage{group.items.length === 1 ? '' : 's'}
          {activeInPhase > 0 ? ` · ${activeInPhase} active` : ''}
          {blockedInPhase > 0 ? ` · ${blockedInPhase} blocked` : ''}
          {cancelledInPhase > 0 ? ` · ${cancelledInPhase} cancelled` : ''}
        </span>
      </header>
      {!collapsed && (
        <div
          id={`ws-phase-body-${group.anchor}`}
          className="ws-phase__body"
          style={{ width: layout.width, height: layout.height }}
        >
          {layout.cards.map((c) => (
            <WsStageCard
              key={c.ws.id}
              ws={c.ws}
              left={c.x}
              top={c.y}
              byId={byId}
              phaseAnchorOf={phaseAnchorOf}
              stageNumOf={stageNumOf}
              registerCardRef={registerCardRef}
              onClick={onStageClick}
            />
          ))}
        </div>
      )}
    </section>
  );
}

function WsStageCard({
  ws, left, top, byId, phaseAnchorOf, stageNumOf, registerCardRef, onClick,
}: {
  ws: StorySpawnDetail['workstreams'][number];
  left: number;
  top: number;
  byId: Map<string, StorySpawnDetail['workstreams'][number]>;
  phaseAnchorOf: Map<string, string>;
  stageNumOf: Map<string, string>;
  registerCardRef: (id: string, el: HTMLElement | null) => void;
  onClick: (workstreamId: string) => void;
}) {
  const stageLabel = wsStageLabel(ws.planAnchor, ws.name);
  const myStageNum = stageNumOf.get(ws.id);
  const color = WS_STATUS_COLOR[ws.status] ?? 'var(--sc-muted, #888)';
  const myPhase = phaseAnchorOf.get(ws.id) ?? WS_DAG_DEFAULT_PHASE;
  const crossDeps = (ws.deps ?? [])
    .map((d) => byId.get(d))
    .filter((u): u is StorySpawnDetail['workstreams'][number] => !!u)
    .filter((u) => (phaseAnchorOf.get(u.id) ?? WS_DAG_DEFAULT_PHASE) !== myPhase);

  const cb = useCallback((el: HTMLElement | null) => registerCardRef(ws.id, el),
    [registerCardRef, ws.id]);

  // Cross-dep chip clicks must not bubble up and re-trigger the card-level
  // navigation; without stopPropagation a click on a dep chip would jump
  // to the parent stage's workstream rather than feel like an inert label.
  const handleCardClick = useCallback(() => onClick(ws.id), [onClick, ws.id]);
  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onClick(ws.id);
    }
  }, [onClick, ws.id]);

  // docs/forge/e2e.md Stage 8.2 — surface the workstream category as
  // a data attribute so the Playwright spec can locate qa-e2e cards
  // without relying on the visual style. Also drives the optional
  // `ws-stage--qae2e` class which tints the card's border with the
  // green QA accent (mirrors the SwarmMasterPanel stacked-row look
  // from Stage 8.1 so the operator's mental model carries across).
  const category = isQaE2e(ws) ? 'qa-e2e' : 'impl';
  const variantCls = category === 'qa-e2e' ? ' ws-stage--qae2e' : '';
  return (
    <div
      ref={cb}
      className={`ws-stage ws-stage--${ws.status}${variantCls}`}
      style={{
        position: 'absolute',
        left,
        top,
        width: WS_NODE_W,
        height: WS_NODE_H,
        borderColor: color,
        cursor: 'pointer',
      }}
      data-ws-id={ws.id}
      data-ws-category={category}
      role="button"
      tabIndex={0}
      onClick={handleCardClick}
      onKeyDown={handleKeyDown}
      title={
        ws.status === 'stalled' && ws.stallReason
          ? `${ws.name}\n${ws.planAnchor ?? ws.id}\n\nstalled — ${ws.stallReason}\n\nClick to open workstream`
          : `${ws.name}\n${ws.planAnchor ?? ws.id}\n\nClick to open workstream`
      }
    >
      <div className="ws-stage__hd">
        <span className="ws-stage__pill" style={{ color, borderColor: color }}>
          <span className="dot" style={{ background: color }} />
          {ws.status.toUpperCase()}
        </span>
        {myStageNum && (
          <span className="ws-stage__stagenum" title="Stage number — phase.stage">{myStageNum}</span>
        )}
        {typeof ws.iter === 'number' && ws.iter > 0 && (
          <span className="ws-stage__iter">iter {ws.iter}</span>
        )}
        {typeof ws.latestScore === 'number' && (
          <span className={`ws-stage__score${ws.latestVerdict === 'pass' ? ' is-pass' : ''}`}>
            {ws.latestScore}
          </span>
        )}
      </div>
      <div className="ws-stage__stage">STAGE · {stageLabel}</div>
      <div className="ws-stage__name">{ws.name}</div>
      {crossDeps.length > 0 && (
        <div className="ws-stage__cross" onClick={(e) => e.stopPropagation()}>
          <span className="ws-stage__cross-label" title="Cross-phase upstream deps">↥ deps</span>
          {crossDeps.map((u) => {
            const num = stageNumOf.get(u.id) ?? '?';
            return (
              <span
                key={u.id}
                className="ws-stage__cross-chip"
                title={`${num} — ${u.name}\n${u.planAnchor ?? u.id}\n\nClick to open this dep's workstream`}
                style={{ cursor: 'pointer' }}
                onClick={(e) => {
                  e.stopPropagation();
                  onClick(u.id);
                }}
              >
                {num}
              </span>
            );
          })}
        </div>
      )}
    </div>
  );
}
/* ─── helpers ───────────────────────────────────────────────────────── */

function pairClsForStatus(status: string): string {
  if (status === 'review' || status === 'merging') return 'review';
  if (status === 'complete') return 'e2e';
  if (status === 'stalled') return 'stalled';
  return '';
}

/** Compact token formatter for the sloppy-meter readout.
 *  - >= 1M  → "13.99M" (2 decimals when < 10M, else 1)
 *  - >= 1K  → "500K" (no decimal when >= 10K, 1 decimal below)
 *  - else   → raw integer */
function formatTokens(n: number): string {
  if (!Number.isFinite(n) || n < 0) return '0';
  if (n >= 1_000_000) {
    const m = n / 1_000_000;
    return `${m.toFixed(m < 10 ? 2 : 1)}M`;
  }
  if (n >= 1_000) {
    const k = n / 1_000;
    return `${k.toFixed(k < 10 ? 1 : 0)}K`;
  }
  return String(Math.round(n));
}

function prettyId(id: string): string {
  // Trim long workstream ids for the headline, leaving a recognizable suffix.
  if (id.length <= 36) return id;
  return id.slice(0, 18) + '…' + id.slice(-16);
}

function Placeholder({ label, sub }: { label: string; sub: string }) {
  return (
    <div className="tab-placeholder">
      <div className="lbl">▌ {label}</div>
      <div className="sub">{sub}</div>
    </div>
  );
}
