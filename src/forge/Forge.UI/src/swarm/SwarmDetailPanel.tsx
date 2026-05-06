import { useEffect, useMemo, useState } from 'react';
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
    if (detail.spawn.planPrId) tabs.push('pr');
    tabs.push('worktree');
    if (questionCount > 0) tabs.push('questions');
    return tabs;
  }
  // Workstream (phase) selection
  if (sel.kind === 'phase') {
    const tabs: DetailTab[] = ['overview', 'conv'];
    const ws = detail.workstreams.find((w) => w.id === sel.id);
    if (ws && ws.prs.length > 0) tabs.push('pr');
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
  const pairs = detail.workstreams
    .filter((w) => selection.kind !== 'phase' || selection.id === w.id)
    .map((w, i) => ({
      id: `WS·${String(i + 1).padStart(2, '0')}`,
      team: teamFor(w.latestGenAgentId ?? w.agents?.[0]),
      cls: pairClsForStatus(w.status),
      task: w.name,
      gen:  displayName(w.latestGenAgentId ?? w.agents?.[0]),
      genM: modelFor(w.latestGenAgentId ?? w.agents?.[0], 'gen'),
      evl:  displayName(w.latestEvalAgentId ?? w.agents?.[1]),
      evlM: modelFor(w.latestEvalAgentId ?? w.agents?.[1], 'evl'),
      meters: pairMetersForWorkstream(w),
      status: w.status,
    }));

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

  // Agent identity — prefer prompt-derived agent info (matches what the
  // system/user prompt sections display) over snapshot/API data which may
  // be from a different iteration with different round-robin agents.
  const promptGenId = latestPrompts.gen?.agentId ?? null;
  const promptEvalId = latestPrompts.ev?.agentId ?? null;
  const fallbackGenId = isWorkstream ? (snapshot?.generatorAgentId ?? null) : (docGen?.agentId ?? null);
  const fallbackEvalId = isWorkstream ? (snapshot?.evaluatorAgentId ?? null) : (docEval?.agentId ?? null);
  const genId = promptGenId ?? fallbackGenId;
  const evalId = promptEvalId ?? fallbackEvalId;
  // When the prompt carries a different agentId than the snapshot, use the
  // agentNames map (or raw ID) — never mix a prompt-derived ID with a
  // snapshot-derived name from a different agent.
  const genName = promptGenId
    ? (agentNames[promptGenId] ?? promptGenId)
    : (isWorkstream ? (snapshot?.generatorAgentName ?? fallbackGenId) : (docGen?.name ?? fallbackGenId));
  const evalName = promptEvalId
    ? (agentNames[promptEvalId] ?? promptEvalId)
    : (isWorkstream ? (snapshot?.evaluatorAgentName ?? fallbackEvalId) : (docEval?.name ?? fallbackEvalId));
  const genModel = latestPrompts.gen?.model || (isWorkstream ? (snapshot?.generatorModel ?? null) : (docGen?.model ?? null));
  const evalModel = latestPrompts.ev?.model || (isWorkstream ? (snapshot?.evaluatorModel ?? null) : (docEval?.model ?? null));

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
          sameAsOther={!!genModel && genModel === evalModel}
          selected={promptRole === 'evaluator'}
          onClick={() => setPromptRole('evaluator')}
        />
      </div>

      {/* Telemetry row */}
      <div style={{ display: 'flex', justifyContent: 'center', gap: 24, marginBottom: 16, fontFamily: 'var(--sc-font-display)', fontSize: 10, letterSpacing: '0.14em', color: 'var(--sc-text-dim)' }}>
        <span><span style={{ color: 'var(--sc-muted)' }}>COST</span> <strong style={{ color: 'var(--sc-text)' }}>${totalCost.toFixed(2)}</strong></span>
        {effectiveMaxIter > 0 && (
          <span><span style={{ color: 'var(--sc-muted)' }}>ITER</span> <strong style={{ color: 'var(--sc-text)' }}>{currentIter}/{effectiveMaxIter}</strong></span>
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
    if (selection.kind === 'doc' && detail.spawn.planPrId) return [detail.spawn.planPrId];
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
      title: `Merge PR ${prId}?`,
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
        showToast(detail ?? `PR ${prId} merge queued by provider.`, 'info');
      } else {
        showToast(`PR ${prId} merged successfully.`, 'success');
      }
      const d = await forgeApi.getPrDetails(prId);
      setData(d);
    } catch (e) {
      // 409 with conflict=true → offer the destructive force-merge as a
      // second confirm so the user knows what they're agreeing to.
      if (e instanceof ForgeApiError && e.status === 409 && e.problem?.conflict === true) {
        const goForce = await useConfirmStore.getState().ask({
          title: `Merge conflict on PR ${prId}`,
          body:
            (typeof e.problem?.hint === 'string' ? e.problem.hint + '\n\n' : '') +
            'Force-merge will take the source branch\'s version on every conflicting hunk and discard upstream-side changes (-X ours). This is destructive — prefer resolving in the provider UI.',
          tone: 'danger',
          confirmLabel: '⚠ Force-merge (-X ours)',
          cancelLabel: 'Open in provider',
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
        {pr.webUrl && (
          <a href={pr.webUrl} target="_blank" rel="noopener noreferrer"
            style={{ marginLeft: 'auto', fontSize: 10, color: '#569cd6', textDecoration: 'none' }}>
            Open in {pr.id.startsWith('PR-') || pr.id.startsWith('PR ') ? 'ADO' : 'GitHub'} ↗
          </a>
        )}
        {pr.col !== 'merged' && (
          <button
            type="button"
            disabled={merging}
            onClick={() => void handleMerge()}
            style={{
              marginLeft: pr.webUrl ? 8 : 'auto',
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
  stalled:  'var(--sc-red, #ff7caa)',
};

function DecompositionDiagram({ detail }: { detail: StorySpawnDetail }) {
  const ws = detail.workstreams;

  // Build adjacency: each workstream may depend on others via `deps[]`.
  // Assign columns using a topological sort (BFS by dependency depth).
  const depthMap = useMemo(() => {
    const depths = new Map<string, number>();
    const byId = new Map(ws.map((w) => [w.id, w]));

    function getDepth(id: string, visited: Set<string>): number {
      if (depths.has(id)) return depths.get(id)!;
      if (visited.has(id)) return 0; // cycle guard
      visited.add(id);
      const w = byId.get(id);
      if (!w || w.deps.length === 0) { depths.set(id, 0); return 0; }
      const d = 1 + Math.max(...w.deps.map((dep) => getDepth(dep, visited)));
      depths.set(id, d);
      return d;
    }
    for (const w of ws) getDepth(w.id, new Set());
    return depths;
  }, [ws]);

  const maxDepth = Math.max(0, ...Array.from(depthMap.values()));
  const columns = useMemo(() => {
    const cols: Array<typeof ws> = [];
    for (let d = 0; d <= maxDepth; d++) {
      cols.push(ws.filter((w) => (depthMap.get(w.id) ?? 0) === d));
    }
    return cols;
  }, [ws, depthMap, maxDepth]);

  // Layout constants
  const nodeW = 220, nodeH = 80, colGap = 100, rowGap = 32, padX = 40, padY = 60;
  const maxRows = Math.max(1, ...columns.map((c) => c.length));
  const svgW = padX * 2 + (maxDepth + 1) * nodeW + maxDepth * colGap;
  const svgH = padY * 2 + maxRows * nodeH + (maxRows - 1) * rowGap;

  // Position each node
  const positions = useMemo(() => {
    const pos = new Map<string, { x: number; y: number }>();
    for (let col = 0; col <= maxDepth; col++) {
      const items = columns[col];
      const totalH = items.length * nodeH + (items.length - 1) * rowGap;
      const startY = (svgH - totalH) / 2;
      items.forEach((w, row) => {
        pos.set(w.id, {
          x: padX + col * (nodeW + colGap),
          y: startY + row * (nodeH + rowGap),
        });
      });
    }
    return pos;
  }, [columns, maxDepth, svgH]);

  const activeCount = ws.filter((w) => w.status === 'active').length;
  const blockedCount = ws.filter((w) => w.status === 'blocked').length;
  const completeCount = ws.filter((w) => w.status === 'complete').length;

  return (
    <div style={{ padding: 16, overflow: 'auto', height: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
        <span style={{
          fontFamily: 'var(--sc-font-display, monospace)',
          fontSize: 10,
          letterSpacing: '0.16em',
          color: 'var(--sc-text-dim, #888)',
          textTransform: 'uppercase',
        }}>
          IMPLEMENTATION DIAGRAM
        </span>
        <span style={{ fontSize: 11, color: 'var(--sc-text-dim, #888)' }}>
          {ws.length} workstreams · {activeCount} active · {blockedCount} blocked · {completeCount} complete
        </span>
        <span style={{
          marginLeft: 'auto',
          fontSize: 10,
          color: 'var(--sc-text-dim, #888)',
        }}>
          {detail.spawn.decompositionApproved ? '✓ approved' : '⏳ pending approval'}
        </span>
      </div>

      <svg
        width={svgW}
        height={svgH}
        viewBox={`0 0 ${svgW} ${svgH}`}
        style={{ display: 'block' }}
      >
        {/* Arrow marker definitions */}
        <defs>
          <marker id="dep-arrow" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
            <path d="M 0 0 L 10 4 L 0 8 Z" fill="#7cc1ff" />
          </marker>
          <marker id="dep-arrow-blocked" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto">
            <path d="M 0 0 L 10 4 L 0 8 Z" fill="#ffd57c" />
          </marker>
        </defs>

        {/* Column labels */}
        {columns.map((_, col) => (
          <text
            key={col}
            x={padX + col * (nodeW + colGap) + nodeW / 2}
            y={20}
            textAnchor="middle"
            fontSize={9}
            fontFamily="var(--sc-font-display, monospace)"
            letterSpacing="0.14em"
            fill="var(--sc-text-dim, #888)"
          >
            PHASE {String(col + 1).padStart(2, '0')}
          </text>
        ))}

        {/* Workstream nodes */}
        {ws.map((w) => {
          const p = positions.get(w.id);
          if (!p) return null;
          const color = WS_STATUS_COLOR[w.status] ?? 'var(--sc-muted, #888)';
          const shortName = w.name.length > 28 ? w.name.slice(0, 26) + '…' : w.name;
          const shortId = w.id.length > 24 ? '…' + w.id.slice(-20) : w.id;
          return (
            <g key={w.id}>
              <rect
                x={p.x}
                y={p.y}
                width={nodeW}
                height={nodeH}
                rx={6}
                fill="var(--sc-surface-2, #1e1e2e)"
                stroke={color}
                strokeWidth={w.status === 'active' ? 2 : 1}
              />
              {/* Status dot */}
              <circle cx={p.x + 14} cy={p.y + 16} r={4} fill={color} />
              <text x={p.x + 24} y={p.y + 20} fontSize={10} fontWeight={600} fill={color}
                fontFamily="var(--sc-font-display, monospace)" letterSpacing="0.08em">
                {w.status.toUpperCase()}
              </text>
              {/* Name */}
              <text x={p.x + 10} y={p.y + 40} fontSize={12} fontWeight={500}
                fill="var(--sc-text, #eee)">
                {shortName}
              </text>
              {/* ID */}
              <text x={p.x + 10} y={p.y + 58} fontSize={9}
                fill="var(--sc-text-dim, #888)" fontFamily="var(--sc-font-mono, monospace)">
                {shortId}
              </text>
              {/* Dep count */}
              {w.deps.length > 0 && (
                <text x={p.x + nodeW - 10} y={p.y + 20} fontSize={9} textAnchor="end"
                  fill="var(--sc-text-dim, #888)">
                  ⮨ {w.deps.length}
                </text>
              )}
              {/* Branch badge */}
              {w.branch && (
                <text x={p.x + 10} y={p.y + nodeH - 8} fontSize={8}
                  fill="var(--sc-text-dim, #666)" fontFamily="var(--sc-font-mono, monospace)">
                  ⎇ {w.branch.split('/').pop()}
                </text>
              )}
            </g>
          );
        })}

        {/* Dependency arrows — rendered last so they draw on top of nodes */}
        {ws.map((w) =>
          w.deps.map((depId) => {
            const from = positions.get(depId);
            const to = positions.get(w.id);
            if (!from || !to) return null;
            const x1 = from.x + nodeW + 2;
            const y1 = from.y + nodeH / 2;
            const x2 = to.x - 12;
            const y2 = to.y + nodeH / 2;
            const midX = (x1 + x2) / 2;
            const isBlocked = w.status === 'blocked';
            const strokeColor = isBlocked ? '#ffd57c' : '#7cc1ff';
            const markerId = isBlocked ? 'dep-arrow-blocked' : 'dep-arrow';
            return (
              <g key={`${depId}->${w.id}`}>
                <path
                  d={`M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`}
                  fill="none"
                  stroke={strokeColor}
                  strokeWidth="2"
                  strokeDasharray={isBlocked ? '6 4' : undefined}
                  markerEnd={`url(#${markerId})`}
                />
              </g>
            );
          }),
        )}
      </svg>
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
