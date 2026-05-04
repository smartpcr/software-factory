import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent, type DeliverableShape } from '@/realtime/useTaskLiveStore';
import { useToastStore } from '@/state/useToastStore';
import {
  type FilterKey,
  FILTERS,
  filterEvents,
  buildPageWindow,
  groupByIteration,
  TranscriptLine,
  SummaryView,
  PairSide,
} from '@/views/conversation';

interface Props {
  storyId: string;
  docId: string;
}

interface IterRow {
  id: number;
  iter: number;
  score: number;
  verdict: string;
  generatorAgentId: string;
  evaluatorAgentId: string;
  generatorSummary: string;
  notes: string;
  genCostUsd: number;
  evalCostUsd: number;
  judgeAgentId: string | null;
  judgeScore: number | null;
  judgeNotes: string | null;
  createdAt: string;
}

interface AgentInfo {
  agentId: string;
  name: string;
  model: string | null;
}

/**
 * Per-document conversation tab. Mirrors LiveExecutionView's center column
 * (filter tabs · iteration pager · paged transcript / summary cards) but
 * scoped to the synthetic plan-doc workItemId
 * (`plan-doc:{spawn.Id}:{filename}`). The architect crew emits the same
 * `task.{wid}` channel shape as work-item runs, so the same shared
 * filterEvents / groupByIteration / TranscriptLine / SummaryView helpers
 * drive both views.
 */
export function DocConversationPane({ storyId, docId }: Props) {
  const key = useMemo(() => (docId.startsWith('doc:') ? docId.slice(4) : docId), [docId]);

  // Iterations + workItemId from the per-doc deliverables endpoint. Used as
  // the source for Summary mode AND to discover the workItemId so we can
  // attach the live-task store for All / Generator / Evaluator / Tools /
  // Commits modes.
  const [rows, setRows] = useState<IterRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [totalCost, setTotalCost] = useState(0);
  const [workItemId, setWorkItemId] = useState<string | null>(null);
  const [generator, setGenerator] = useState<AgentInfo | null>(null);
  const [evaluator, setEvaluator] = useState<AgentInfo | null>(null);
  const [status, setStatus] = useState<'live' | 'iterating' | 'awaiting-pr-review' | 'completed' | 'pending'>('pending');
  const [filter, setFilter] = useState<FilterKey>('summary');
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  const [promptsCollapsed, setPromptsCollapsed] = useState(true);
  const [promptRole, setPromptRole] = useState<'generator' | 'evaluator'>('generator');
  const [resuming, setResuming] = useState(false);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.getStoryArtifactIterations(storyId, key);
        if (cancelled) return;
        setRows(r.iterations);
        setTotalCost(r.totalCostUsd);
        setWorkItemId(r.workItemId);
        setGenerator(r.generator);
        setEvaluator(r.evaluator);
        setStatus(r.status);
        setLoading(false);
      } catch {
        if (!cancelled) setLoading(false);
      }
    }
    setLoading(true);
    void load();
    const id = setInterval(() => { void load(); }, 8_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, key]);

  async function handleResume() {
    setResuming(true);
    try {
      await forgeApi.retryStorySpawn(storyId);
      showToast('Architect crew resuming — iterations will continue against the cached workspace.', 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Resume failed', 'danger');
    } finally {
      setResuming(false);
    }
  }
  // Resume disabled while a run is live (events still flowing) or when the
  // doc has fully shipped (PR merged). Awaiting-pr-review still allows
  // resume — the operator can re-iterate before merge if they want.
  const canResume = status !== 'live' && status !== 'completed';

  // Subscribe to the live transcript channel for this doc's workItemId.
  // useTaskLive is null-safe for null workItemId.
  const liveEntry = useTaskLive(workItemId);
  const transcript = liveEntry?.transcript ?? [];
  const transcriptTotalCount = liveEntry?.transcriptTotalCount ?? 0;
  const loadingOlder = liveEntry?.loadingOlder ?? false;
  const hasOlderEvents = transcript.length < transcriptTotalCount;

  // Reset selection when filter or doc changes so the latest page is sticky.
  useEffect(() => { setSelectedIter(null); }, [filter, key]);

  const filtered = useMemo(
    () => filterEvents(transcript, filter, null, null, generator?.agentId ?? null, evaluator?.agentId ?? null),
    [transcript, filter, generator?.agentId, evaluator?.agentId],
  );
  const iterations = useMemo(() => groupByIteration(filtered), [filtered]);

  // Latest prompt per role — extracted from the most recent task.prompt event
  // matching the role. Backs the collapsible system+user prompt sections.
  const latestPrompts = useMemo(() => {
    type PromptShape = { systemPrompt: string; userPrompt: string; model: string; backend: string; mode: string };
    let gen: PromptShape | null = null;
    let ev: PromptShape | null = null;
    for (let i = transcript.length - 1; i >= 0 && (!gen || !ev); i--) {
      if (transcript[i].kind !== 'task.prompt') continue;
      const p = transcript[i].payload as Record<string, unknown> | null;
      if (!p) continue;
      const role = (p.role as string) ?? '';
      const candidate: PromptShape = {
        systemPrompt: (p.systemPrompt as string) ?? '',
        userPrompt: (p.userPrompt as string) ?? '',
        model: (p.model as string) ?? '',
        backend: (p.backend as string) ?? '',
        mode: (p.invokeMode as string) ?? '',
      };
      if (role === 'evaluator' && !ev) ev = candidate;
      else if (role !== 'evaluator' && !gen) gen = candidate;
    }
    return { gen, ev };
  }, [transcript]);
  const activePrompt = promptRole === 'evaluator' ? latestPrompts.ev : latestPrompts.gen;

  // Summary mode iteration source. Map IterRow → DeliverableShape so the
  // shared SummaryView component renders the same way live-exec does.
  const summaryIters = useMemo<DeliverableShape[]>(() => {
    return rows.map<DeliverableShape>((r) => ({
      id: r.id,
      iter: r.iter,
      score: r.score,
      verdict: r.verdict,
      generatorSummary: r.generatorSummary,
      notes: r.notes,
      dims: {},
      genCostUsd: r.genCostUsd,
      evalCostUsd: r.evalCostUsd,
      judgeAgentId: r.judgeAgentId,
      judgeScore: r.judgeScore,
      judgeNotes: r.judgeNotes,
      createdAt: r.createdAt,
    }));
  }, [rows]);

  const isSummaryMode = filter === 'summary';
  const totalIters = isSummaryMode ? summaryIters.length : iterations.length;
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;

  // Reverse-chronological: newest events on top within the active iteration.
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);

  // Empty / loading guard. The pane is also reachable when the doc hasn't
  // produced any iterations yet — show the same prompt-y empty state as
  // before in that case.
  if (loading && rows.length === 0 && transcript.length === 0) {
    return <div className="tab-placeholder"><em>loading conversation…</em></div>;
  }
  if (rows.length === 0 && transcript.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO ITERATIONS YET</div>
        <div className="sub">
          The architect crew hasn't iterated on this document yet. Once the
          gen↔eval loop starts, each iteration appears here with the generator's
          change summary and the evaluator's score-with-rationale.
        </div>
      </div>
    );
  }

  return (
    <div className="doc-conv">
      {/* Pair header — generator + evaluator avatar + name + model */}
      <header className="doc-conv-pair-bar">
        <PairSide
          role="generator"
          agentId={generator?.agentId ?? null}
          agentName={generator?.name ?? null}
          model={generator?.model ?? null}
          sameAsOther={!!generator && !!evaluator && generator.model != null && generator.model === evaluator.model}
        />
        <span className="doc-conv-pair-link">⇄</span>
        <PairSide
          role="evaluator"
          agentId={evaluator?.agentId ?? null}
          agentName={evaluator?.name ?? null}
          model={evaluator?.model ?? null}
          sameAsOther={!!generator && !!evaluator && generator.model != null && generator.model === evaluator.model}
        />
        <span className="doc-conv-pair-cost">
          TOTAL COST · <b>${totalCost.toFixed(2)}</b>
        </span>
        <button
          type="button"
          className="doc-conv-resume-btn"
          disabled={!canResume || resuming}
          title={
            status === 'live' ? 'Architect crew is currently running — resume disabled.'
            : status === 'completed' ? 'Plan PR merged — doc is shipped.'
            : status === 'awaiting-pr-review' ? 'Verdict passed but PR not merged — Resume re-iterates against the workspace.'
            : 'Resume the gen↔eval loop on the cached workspace.'
          }
          onClick={() => void handleResume()}
        >
          {resuming ? '⏳ resuming…' : '↻ resume'}
        </button>
      </header>

      {/* Collapsible system + user prompt — toggles between generator/evaluator */}
      <section className="live-exec__prompts">
        <div
          className="live-exec__prompts-header"
          onClick={() => setPromptsCollapsed(!promptsCollapsed)}
          role="button"
          tabIndex={0}
        >
          <strong>
            {promptsCollapsed ? '▸' : '▾'} {promptRole === 'generator' ? 'Generator' : 'Evaluator'} Prompts
          </strong>
          {!promptsCollapsed && (
            <div className="live-exec__prompts-roleswitch" onClick={(e) => e.stopPropagation()}>
              <button
                type="button"
                className={promptRole === 'generator' ? 'active' : ''}
                onClick={() => setPromptRole('generator')}
              >
                Generator
              </button>
              <button
                type="button"
                className={promptRole === 'evaluator' ? 'active' : ''}
                onClick={() => setPromptRole('evaluator')}
              >
                Evaluator
              </button>
            </div>
          )}
        </div>
        {!promptsCollapsed && (
          <div className="live-exec__prompts-body">
            {!activePrompt ? (
              <em style={{ color: 'var(--text-tertiary)' }}>
                No {promptRole} prompt captured yet. The first one appears once the architect crew dispatches.
              </em>
            ) : (
              <>
                <div className="live-exec__prompt-standalone">
                  <div className="live-exec__prompt-label">System prompt</div>
                  {activePrompt.systemPrompt
                    ? <pre className="live-exec__prompt-pre">{activePrompt.systemPrompt}</pre>
                    : <em style={{ color: 'var(--text-tertiary)' }}>
                        (no system prompt — agent has no <code>soul.md</code> configured)
                      </em>}
                </div>
                <div className="live-exec__prompt-standalone">
                  <div className="live-exec__prompt-label">User prompt</div>
                  {activePrompt.userPrompt
                    ? <pre className="live-exec__prompt-pre">{activePrompt.userPrompt}</pre>
                    : <em style={{ color: 'var(--text-tertiary)' }}>(empty user prompt)</em>}
                </div>
              </>
            )}
          </div>
        )}
      </section>

      {/* Filter tab strip */}
      <nav className="live-exec__filters" role="tablist">
        {FILTERS.map((f) => (
          <button
            key={f.key}
            type="button"
            role="tab"
            aria-selected={filter === f.key}
            className={`live-exec__filter${filter === f.key ? ' active' : ''}`}
            onClick={() => setFilter(f.key)}
          >
            {f.label}
          </button>
        ))}
      </nav>

      {/* Iteration pager — windowed page list with score chips */}
      {totalIters > 1 && (
        <nav className="live-exec__iter-pager" aria-label="Iteration pager">
          <button
            type="button"
            disabled={activeIter === 0}
            onClick={() => setSelectedIter(Math.max(0, activeIter - 1))}
            title="Previous iteration"
          >
            ◂
          </button>
          {buildPageWindow(totalIters, activeIter).map((slot, i) => {
            if (slot === 'ellipsis') return <span key={`e${i}`} className="live-exec__iter-pager-ellipsis">…</span>;
            const slotLabel  = isSummaryMode ? `Iteration ${summaryIters[slot].iter}` : iterations[slot].label;
            const slotScore  = isSummaryMode ? summaryIters[slot].score   : iterations[slot].score;
            const slotVerdict = isSummaryMode ? summaryIters[slot].verdict : iterations[slot].verdict;
            return (
              <button
                key={slot}
                type="button"
                className={slot === activeIter ? 'active' : ''}
                onClick={() => setSelectedIter(slot === totalIters - 1 ? null : slot)}
                title={slotLabel}
              >
                {slot + 1}
                {slotScore != null && (
                  <span className={`live-exec__iter-pager-score ${slotVerdict ?? ''}`}>{slotScore}</span>
                )}
              </button>
            );
          })}
          <button
            type="button"
            disabled={activeIter >= totalIters - 1}
            onClick={() => {
              const next = activeIter + 1;
              setSelectedIter(next >= totalIters - 1 ? null : next);
            }}
            title="Next iteration"
          >
            ▸
          </button>
          <span className="live-exec__iter-pager-label">
            Iteration {activeIter + 1} of {totalIters}
            {selectedIter === null && ' (latest)'}
          </span>
        </nav>
      )}

      {/* Body — Summary card OR transcript list */}
      <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
        {isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={loading}
            dispatched={rows.length > 0 || transcript.length > 0}
          />
        ) : (
          <>
            {transcriptTotalCount > 0 && (
              <div
                className="live-exec__transcript-count"
                style={{ fontSize: 11, color: 'var(--text-tertiary, #888)', padding: '4px 8px', borderBottom: '1px solid var(--border, #e5e7eb)' }}
              >
                Showing 1–{transcript.length.toLocaleString()} most recent of {transcriptTotalCount.toLocaleString()} total events
              </div>
            )}
            {reversedEvents.length === 0 ? (
              <div className="live-exec__empty">
                {!liveEntry || liveEntry.loading ? 'Loading…' : 'No events yet.'}
              </div>
            ) : (
              reversedEvents.map((evt: TaskTranscriptEvent) => (
                <TranscriptLine
                  key={`${evt.id}:${evt.at}`}
                  evt={evt}
                  generatorCrewId={null}
                  evaluatorCrewId={null}
                />
              ))
            )}
            {hasOlderEvents && workItemId && (
              <div style={{ textAlign: 'center', padding: '8px 0' }}>
                <button
                  type="button"
                  className="btn btn-secondary"
                  style={{ fontSize: 11, padding: '4px 12px' }}
                  disabled={loadingOlder}
                  onClick={() => void useTaskLiveStore.getState().loadOlderEvents(workItemId)}
                >
                  {loadingOlder ? 'Loading…' : '↑ Load older events'}
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
