// Phase 28 — shared per-iteration summary card. Used in the Summary mode of
// both LiveExecutionView and DocConversationPane.
import ReactMarkdown from 'react-markdown';
import type { DeliverableShape } from '@/realtime/useTaskLiveStore';

export function SummaryView(props: {
  deliverable: DeliverableShape | null;
  loading: boolean;
  dispatched: boolean;
}): JSX.Element {
  const { deliverable, loading, dispatched } = props;
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
