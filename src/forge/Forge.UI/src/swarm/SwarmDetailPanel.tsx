import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { StorySpawnArtifacts, StorySpawnDetail, PrDetailsResponse } from '@/api/forgeApi';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import type { Build, PrComment, StoryAttachment } from '@/types';
import { PrCommentsList } from '@/prs/PrCommentsList';
import { PrApprovalBadge } from '@/prs/PrApprovalBadge';
import { deriveVerdict } from '@/prs/prVerdict';
import { isBuildEvent } from '@/prs/prBuildEvents';
import type { SwarmMode } from './SwarmPipelineStrip';
import type { Selection } from './SwarmMasterPanel';
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
            <SwarmGridPane mode={mode} detail={detail} selection={selection} />
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
            <WorkstreamConversationPane workstreamId={selection.id} />
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
  selection: Selection;
}

function SwarmGridPane({ mode, detail, selection }: SwarmGridProps) {
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
    ? `${detail.workstreams.length} workstreams · phase ${detail.spawn.phase}`
    : selection.id ?? '';

  // Pair cards: one per workstream (the generator / evaluator pair that owns it).
  // All values are sourced from the StorySpawnWorkstreamSummary contract — the
  // backend joins WorkstreamRow.AgentsJson + the deliverables table + the
  // agent_usage cost ledger so we don't need per-card round-trips.
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
  const teamFor = (id: string | null | undefined): string => {
    if (!id) return 'ENG';
    const a = agentsById.get(id);
    if (!a?.crewId) return 'ENG';
    const team = a.crewId.replace(/^crew-/, '').replace(/-night$/, '');
    return team.toUpperCase();
  };
  // Phase 49 follow-up — only show an agent pair when the workstream is
  // currently RUNNING or has shipped (active/review/merging/complete).
  // For pre-claim states (planning/blocked/stalled/cancelled/historic),
  // `latestGen/EvalAgentId` may carry the pair from a prior aborted run;
  // displaying it as if it's the current assignment confused the
  // operator (observed 2026-05-10: stage 2.1 was `blocked` waiting on
  // deps 1.1/1.2/1.3, yet the Overview showed Zhu Rong + Bi Gan from
  // the workstream's earlier iter-1/iter-2 deliverables). Treat those
  // as "no current assignment"; the SwarmMasterPanel + Conversation
  // tab still surface the historical iter / score data so the prior
  // run isn't hidden, just not painted as "currently assigned".
  const isCurrentlyAssigned = (status: string): boolean =>
    status === 'active' || status === 'review' || status === 'merging' || status === 'complete';
  const pairs = detail.workstreams
    .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
    .map((w, i) => {
      const showPair = isCurrentlyAssigned(w.status);
      const genId = showPair ? (w.latestGenAgentId ?? w.agents?.[0]) : undefined;
      const evlId = showPair ? (w.latestEvalAgentId ?? w.agents?.[1]) : undefined;
      return {
        id: `WS·${String(i + 1).padStart(2, '0')}`,
        team: teamFor(genId),
        cls: pairClsForStatus(w.status),
        task: w.name,
        gen:  showPair ? displayName(genId) : '— not assigned —',
        genM: showPair ? modelFor(genId, 'gen') : '',
        evl:  showPair ? displayName(evlId) : '— not assigned —',
        evlM: showPair ? modelFor(evlId, 'evl') : '',
        meters: pairMetersForWorkstream(w),
        status: w.status,
      };
    });

  // Count day-shift only (`crew-{role}`, not `crew-{role}-night`) so the
  // 20-cell swarm grid matches the spec's "~20 agents per team" capacity.
  // Without this filter, day + night roster sums (e.g., 37 architects)
  // produce nonsense ratios like "37/20".
  const teamCounts: Record<string, number> = { arch: 0, eng: 0, rev: 0, qa: 0 };
  for (const a of agents) {
    if (!a.crewId || a.crewId.endsWith('-night')) continue;
    const team = a.crewId.replace(/^crew-/, '');
    if (team in teamCounts) teamCounts[team]++;
  }

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
          {pairs.length === 0 ? (
            <div className="tab-placeholder">
              <div className="lbl">▌ NO ACTIVE PAIRS</div>
              <div className="sub">Pair cards will appear once the architect crew decomposes the story and engineer pairs claim workstreams.</div>
            </div>
          ) : (
            <div className="pairs">
              {pairs.map((p) => (
                <div key={p.id} className={`pair ${p.cls}`}>
                  <div className="pair-hd">
                    <div className="pair-id">
                      <span className="team">{p.team}</span>{p.id}
                    </div>
                    <div className="pair-task" title={p.task}>{p.task}</div>
                  </div>
                  <div className="pair-agents">
                    <div className="agent-tag gen">
                      <span className="role">GENERATOR</span>
                      <span className="name">{p.gen}</span>
                      <span className="model">{p.genM}</span>
                    </div>
                    <div className="pair-link">⇄</div>
                    <div className="agent-tag evl">
                      <span className="role">EVALUATOR</span>
                      <span className="name">{p.evl}</span>
                      <span className="model">{p.evlM}</span>
                    </div>
                  </div>
                  <div className="pair-meters">
                    {p.meters.map((m, i) => (
                      <div className="pair-m" key={i}>
                        <div className="k">{m.k}</div>
                        <div className="v">{m.v}</div>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="ov-split-r">
          <div className="swarm">
            {(['arch', 'eng', 'rev', 'qa'] as const).map((teamKey) => {
              const visible = Math.min(teamCounts[teamKey], 20);
              const pct = Math.round((visible / 20) * 100);
              return (
                <div key={teamKey} className={`team ${teamKey}`}>
                  <div className="team-hd">
                    <span className="team-name">{teamKey}</span>
                    <div className="team-load"><div className="bar" style={{ width: `${pct}%` }} /></div>
                    <span className="team-count">{visible}<span className="tot">/20</span></span>
                    <span style={{ fontFamily: 'var(--sc-font-display)', fontSize: 8, letterSpacing: '0.16em', color: 'var(--sc-muted)' }}>IDLE</span>
                  </div>
                  <div className="team-grid">
                    {Array.from({ length: 20 }).map((_, i) => {
                      const filled = i < visible;
                      return <div key={i} className={`a ${filled ? 'idle' : ''}`} />;
                    })}
                  </div>
                </div>
              );
            })}
          </div>
          <div className="legend" style={{ marginTop: 8 }}>
            <div className="lg"><span className="d idle" /> idle</div>
            <div className="lg"><span className="d gen" /> gen</div>
            <div className="lg"><span className="d evl" /> evl</div>
            <div className="lg"><span className="d rev" /> rev</div>
            <div className="lg"><span className="d e2e" /> e2e</div>
            <div className="lg"><span className="d stl" /> stl</div>
          </div>
        </div>
      </div>
    </div>
  );
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
  // tombstoned states (planning/blocked/stalled/cancelled/historic),
  // the stored `latestGen/EvalAgentId` reflects a PRIOR aborted run and
  // displaying it as if it were the live assignment confused the
  // operator (observed 2026-05-10: stage 2.1 was `blocked` waiting on
  // deps 1.1/1.2/1.3, yet the avatar panel showed Zhu Rong + Bi Gan
  // from iter-1/iter-2 deliverables). For docs (architect plan-doc
  // panels) we keep the legacy display — those have a stable
  // generator/evaluator across iterations.
  const wsRowForOverview = isWorkstream
    ? detail.workstreams.find((w) => w.id === selection.id)
    : null;
  const isWsCurrentlyAssigned = !isWorkstream || (() => {
    const s = wsRowForOverview?.status ?? '';
    return s === 'active' || s === 'review' || s === 'merging' || s === 'complete' || s === 'stalled';
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
    ? (snapshot?.generatorAgentId ?? wsRowForOverview?.latestGenAgentId ?? null)
    : (docGen?.agentId ?? null);
  const fallbackEvalId = isWorkstream
    ? (snapshot?.evaluatorAgentId ?? wsRowForOverview?.latestEvalAgentId ?? null)
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
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ QA / E2E</div>
        <div className="sub">QA phase not started yet.</div>
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
const WS_PAD_X = 14;
const WS_PAD_Y = 14;

interface PhaseLayout {
  cards: Array<{ ws: StorySpawnDetail['workstreams'][number]; x: number; y: number }>;
  arrows: Array<{ d: string; fromId: string; toId: string; tone: 'normal' | 'blocked' }>;
  width: number;
  height: number;
}

/** Topologically position a phase's stages by INTRA-phase deps only.
 *  Cross-phase deps are surfaced separately (chips) rather than as
 *  arrows that would dangle when the upstream phase is collapsed. */
function layoutPhase(items: StorySpawnDetail['workstreams']): PhaseLayout {
  const ids = new Set(items.map((i) => i.id));
  const byId = new Map(items.map((i) => [i.id, i]));
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
  for (const it of items) getDepth(it.id, new Set());

  const maxDepth = Math.max(0, ...Array.from(depths.values()));
  const cols: Array<typeof items> = [];
  for (let d = 0; d <= maxDepth; d++) {
    cols.push(items.filter((i) => (depths.get(i.id) ?? 0) === d));
  }

  const cards = items.map((item) => {
    const d = depths.get(item.id) ?? 0;
    const idx = cols[d].indexOf(item);
    return {
      ws: item,
      x: WS_PAD_X + d * (WS_NODE_W + WS_COL_GAP),
      y: WS_PAD_Y + idx * (WS_NODE_H + WS_ROW_GAP),
    };
  });
  const cardById = new Map(cards.map((c) => [c.ws.id, c]));

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

  const maxRows = Math.max(1, ...cols.map((c) => c.length));
  const width = WS_PAD_X * 2 + (maxDepth + 1) * WS_NODE_W + maxDepth * WS_COL_GAP;
  const height = WS_PAD_Y * 2 + maxRows * WS_NODE_H + (maxRows - 1) * WS_ROW_GAP;
  return { cards, arrows, width, height };
}

function DecompositionDiagram({ detail }: { detail: StorySpawnDetail }) {
  const storyId = detail.spawn.storyId;
  const ws = detail.workstreams;

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

  // Compute a `phaseNum.stageNum` label for every workstream so the
  // cross-phase dep chips read like "1.2", "3.4" — the same numbering
  // the operator sees in implementation-plan.md (Stage N.M). Phase index
  // matches the order of `groups` (which mirrors the spawn's Sort order,
  // which mirrors the plan's `# Phase 1`, `# Phase 2` order). Stage index
  // is the row's position within its phase, also Sort-ordered.
  const stageNumOf = useMemo(() => {
    const m = new Map<string, string>();
    groups.forEach((g, gi) => {
      g.items.forEach((w, si) => {
        m.set(w.id, `${gi + 1}.${si + 1}`);
      });
    });
    return m;
  }, [groups]);

  type ArrowSpec = { d: string; tone: 'normal' | 'blocked'; key: string };
  const arrows = useMemo<ArrowSpec[]>(() => {
    const out: ArrowSpec[] = [];
    for (const w of ws) {
      const myPhase = phaseAnchorOf.get(w.id) ?? WS_DAG_DEFAULT_PHASE;
      const toCollapsed = collapsed.has(myPhase);
      const toBox = toCollapsed ? boxes.headers.get(myPhase) : boxes.cards.get(w.id);
      if (!toBox) continue;
      for (const depId of w.deps ?? []) {
        const upPhase = phaseAnchorOf.get(depId) ?? WS_DAG_DEFAULT_PHASE;
        // Phase 49 follow-up — only draw INTRA-phase arrows. Cross-phase
        // edges are surfaced as `↥ 1.2 2.3 …` chips on each card so the
        // overlay doesn't smother the cards with spaghetti when phases
        // pile up. The chip set is shorter and orders-of-magnitude
        // easier to read than the previous global arrow soup.
        if (upPhase !== myPhase) continue;
        const fromCollapsed = collapsed.has(upPhase);
        const fromBox = fromCollapsed ? boxes.headers.get(upPhase) : boxes.cards.get(depId);
        if (!fromBox) continue;

        // Anchor at the right edge of source, left edge of target so
        // arrows read left-to-right (matches topological flow within a
        // phase). The cubic Bezier with midX control points keeps the
        // line clean and visually parses as a DAG edge rather than
        // overlapping the cards.
        const x1 = fromBox.x + fromBox.w;
        const y1 = fromBox.y + fromBox.h / 2;
        const x2 = toBox.x;
        const y2 = toBox.y + toBox.h / 2;
        const midX = (x1 + x2) / 2;
        const tone: 'normal' | 'blocked' = w.status === 'blocked' ? 'blocked' : 'normal';
        out.push({
          d: `M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`,
          tone,
          key: `${depId}->${w.id}`,
        });
      }
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
          />
        ))}
      </div>
    </div>
  );
}

function WsPhaseContainer({
  group, phaseIndex, byId, phaseAnchorOf, stageNumOf, collapsed, onToggle,
  registerCardRef, registerHeaderRef,
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
            />
          ))}
        </div>
      )}
    </section>
  );
}

function WsStageCard({
  ws, left, top, byId, phaseAnchorOf, stageNumOf, registerCardRef,
}: {
  ws: StorySpawnDetail['workstreams'][number];
  left: number;
  top: number;
  byId: Map<string, StorySpawnDetail['workstreams'][number]>;
  phaseAnchorOf: Map<string, string>;
  stageNumOf: Map<string, string>;
  registerCardRef: (id: string, el: HTMLElement | null) => void;
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

  return (
    <div
      ref={cb}
      className={`ws-stage ws-stage--${ws.status}`}
      style={{
        position: 'absolute',
        left,
        top,
        width: WS_NODE_W,
        height: WS_NODE_H,
        borderColor: color,
      }}
      data-ws-id={ws.id}
      title={
        ws.status === 'stalled' && ws.stallReason
          ? `${ws.name}\n${ws.planAnchor ?? ws.id}\n\nstalled — ${ws.stallReason}`
          : `${ws.name}\n${ws.planAnchor ?? ws.id}`
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
        <div className="ws-stage__cross">
          <span className="ws-stage__cross-label" title="Cross-phase upstream deps">↥ deps</span>
          {crossDeps.map((u) => {
            const num = stageNumOf.get(u.id) ?? '?';
            return (
              <span
                key={u.id}
                className="ws-stage__cross-chip"
                title={`${num} — ${u.name}\n${u.planAnchor ?? u.id}`}
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

/** Real-data meters: TURNS = deliverables.count; SCORE = latest deliverable
 *  (or '—' if none); ELAPSED = since-latest-deliverable; COST = sum of
 *  agent_usage rows for this workstream. */
function pairMetersForWorkstream(w: import('@/api/forgeApi').StorySpawnWorkstreamSummary): { k: string; v: string }[] {
  const iter = w.iter ?? 0;
  const score = w.latestScore;
  const elapsed = w.lastActiveAt ? formatElapsed(w.lastActiveAt) : '—';
  const cost = w.costUsd ?? 0;
  return [
    { k: 'TURNS',   v: iter > 0 ? String(iter) : '—' },
    { k: 'SCORE',   v: score == null ? '—' : String(score) },
    { k: 'ELAPSED', v: elapsed },
    { k: 'COST',    v: cost > 0 ? `$${cost.toFixed(2)}` : '—' },
  ];
}

function formatElapsed(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '—';
  const ms = Date.now() - t;
  if (ms < 0) return '—';
  const sec = Math.floor(ms / 1000);
  if (sec < 60)    return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60)    return `${min}m`;
  const hr  = Math.floor(min / 60);
  if (hr  < 48)    return `${hr}h ${min % 60}m`;
  const day = Math.floor(hr / 24);
  return `${day}d`;
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
