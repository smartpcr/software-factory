// Phase 28 — shared per-iteration summary card. Used in the Summary mode of
// both LiveExecutionView and DocConversationPane.
import ReactMarkdown from 'react-markdown';
import type { DeliverableShape } from '@/realtime/useTaskLiveStore';

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
}): JSX.Element {
  const { deliverable, loading, dispatched, notesByIter } = props;
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
