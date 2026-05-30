// Phase 28 — shared per-iteration summary card. Used in the Summary mode of
// both LiveExecutionView and DocConversationPane.
import ReactMarkdown from 'react-markdown';
import type { DeliverableShape } from '@/realtime/useTaskLiveStore';

/** Compact token formatter (mirrors IterKpiStrip). */
function formatTok(v: number): string {
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 1_000) return `${(v / 1_000).toFixed(1)}K`;
  return `${v}`;
}

export function SummaryView(props: {
  deliverable: DeliverableShape | null;
  loading: boolean;
  dispatched: boolean;
  /**
   * Insight I.2 (docs/forge/workstream-insights.md) — agent-authored
   * working notes for this iter, sourced from
   * `.forge/notes/iter-{N}.md` via
   * `GET /api/workstreams/{id}/iter-notes`. Keyed by iter number; the
   * "Working notes" section renders only when an entry exists for the
   * active deliverable's iter. Omit the prop entirely on surfaces
   * where the archive isn't available (e.g. plan-doc spawns).
   */
  notesByIter?: Record<number, { content: string; truncated?: boolean }>;
  /**
   * Reasoning ("thinking") tokens the model spent on THIS iteration, from
   * the iter-kpis rollup. Copilot only exposes the reasoning token COUNT,
   * not the chain-of-thought text — so this renders as a header stat next
   * to the score, not as a prose section like the generator/evaluator
   * summaries (there is no reasoning text to render). Null/undefined or 0
   * → the stat is omitted (backend didn't report reasoning this iter).
   */
  reasoningTokens?: number | null;
}): JSX.Element {
  const { deliverable, loading, dispatched, notesByIter, reasoningTokens } = props;
  if (!deliverable) {
    return (
      <div className="live-exec__empty">
        {loading ? 'Loading…' : 'No iteration summaries yet.'}
        {!loading && !dispatched && (
          <div className="live-exec__empty-cta">Waiting for the lead to delegate…</div>
        )}
        {!loading && dispatched && (
          <div className="live-exec__empty-cta">The first iteration's summary appears once it completes.</div>
        )}
      </div>
    );
  }

  const d = deliverable;
  const hasGen  = !!(d.generatorSummary && d.generatorSummary.trim());
  const hasEval = !!(d.notes && d.notes.trim());
  const iterNote = notesByIter?.[d.iter];
  const hasIterNote = !!(iterNote && iterNote.content && iterNote.content.trim());

  return (
    <div className="live-exec__summary-view">
      <div className="live-exec__summary-iter__header">
        <span className={`live-exec__score-chip ${d.verdict}`}>iter {d.iter}: {d.score}</span>
        <span style={{ color: 'var(--text-secondary)', flex: 1, textAlign: 'right' }}>{d.verdict}</span>
        {!!(reasoningTokens && reasoningTokens > 0) && (
          <span
            style={{ color: 'var(--sc-purple, #c4b5fd)', fontSize: 11 }}
            title="Thinking / reasoning tokens spent on this iteration. Copilot exposes the count only — the chain-of-thought text is not returned by the provider."
          >
            🧠 {formatTok(reasoningTokens)} reasoning
          </span>
        )}
        <span style={{ color: 'var(--text-tertiary)', fontSize: 11 }}>{new Date(d.createdAt).toLocaleString()}</span>
      </div>
      <section className="live-exec__summary-section" data-role="generator">
        <div className="live-exec__summary-section__label">Generator summary</div>
        <div className="live-exec__summary-section__body markdown-body">
          {hasGen
            ? <ReactMarkdown>{d.generatorSummary ?? ''}</ReactMarkdown>
            : <em>(not provided this iteration)</em>}
        </div>
      </section>
      {hasIterNote && (
        <section className="live-exec__summary-section" data-role="working-notes">
          <div className="live-exec__summary-section__label">
            Working notes (iter {d.iter})
            {iterNote!.truncated && (
              <span style={{ marginLeft: 8, color: 'var(--text-tertiary)', fontSize: 10, fontWeight: 'normal' }}>
                · truncated to 16&nbsp;KB
              </span>
            )}
          </div>
          <div className="live-exec__summary-section__body markdown-body">
            <ReactMarkdown>{iterNote!.content}</ReactMarkdown>
          </div>
        </section>
      )}
      <section className="live-exec__summary-section" data-role="evaluator">
        <div className="live-exec__summary-section__label">Evaluator summary</div>
        <div className="live-exec__summary-section__body markdown-body">
          {hasEval
            ? <ReactMarkdown>{d.notes ?? ''}</ReactMarkdown>
            : <em>(no evaluator notes)</em>}
        </div>
      </section>
      {d.judgeNotes && (
        <section className="live-exec__summary-section" data-role="judge">
          <div className="live-exec__summary-section__label">Judge override</div>
          <div className="live-exec__summary-section__body markdown-body">
            <ReactMarkdown>{d.judgeNotes}</ReactMarkdown>
          </div>
        </section>
      )}
    </div>
  );
}
