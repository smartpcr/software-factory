// Phase 39 (Layer C) — open-questions wizard. Reused by both
// DocConversationPane (architect docs) and WorkstreamConversationPane
// (engineer workstreams). Renders pending questions above the iteration
// pager; when the operator submits answers, the orchestrator's next
// iteration prepends a `## Operator answers` block and the evaluator's
// pass-verdict gate releases.
import { useEffect, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

interface QuestionItem {
  id: number;
  externalId: string;
  iter: number;
  text: string;
  kind: 'choice' | 'open';
  choices: string[];
  answer: string | null;
  answeredAt: string | null;
  answeredBy: string | null;
}

interface Props {
  workItemId: string;
  /** Called after a successful submit so the parent can re-fire the resume
   *  flow once the verdict gate has released. */
  onAnswered?: (unansweredCount: number) => void;
  /** Tightens the poll cadence — set to 0 to disable polling and rely on
   *  the parent's existing poll loop. Default 8s mirrors DocConversationPane. */
  pollMs?: number;
}

export function OpenQuestionsWizard({ workItemId, onAnswered, pollMs = 8_000 }: Props) {
  const [items, setItems] = useState<QuestionItem[]>([]);
  const [unansweredCount, setUnansweredCount] = useState(0);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.listQuestions(workItemId);
        if (cancelled) return;
        setItems(r.items);
        setUnansweredCount(r.unansweredCount);
      } catch {
        // Silent — the wizard just stays empty.
      }
    }
    void load();
    if (pollMs > 0) {
      const id = setInterval(() => { void load(); }, pollMs);
      return () => { cancelled = true; clearInterval(id); };
    }
    return () => { cancelled = true; };
  }, [workItemId, pollMs]);

  const unanswered = items.filter((q) => !q.answer);
  if (items.length === 0) return null; // Nothing to show

  async function handleSubmit() {
    const answers = unanswered
      .map((q) => ({ externalId: q.externalId, answer: (drafts[q.externalId] ?? '').trim() }))
      .filter((a) => a.answer.length > 0);
    if (answers.length === 0) {
      showToast('Pick or write an answer for at least one question.', 'info');
      return;
    }
    setSubmitting(true);
    try {
      const r = await forgeApi.answerQuestions(workItemId, answers);
      setUnansweredCount(r.unansweredCount);
      setDrafts({});
      const full = await forgeApi.listQuestions(workItemId);
      setItems(full.items);
      showToast(
        r.unansweredCount === 0
          ? 'All questions answered — click Resume to feed answers into the next iteration.'
          : `${answers.length} answer${answers.length === 1 ? '' : 's'} saved · ${r.unansweredCount} still pending.`,
        'success',
      );
      onAnswered?.(r.unansweredCount);
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Submit failed', 'danger');
    } finally {
      setSubmitting(false);
    }
  }

  async function handleDismiss(externalIds: string[]) {
    if (externalIds.length === 0) return;
    setSubmitting(true);
    try {
      const r = await forgeApi.dismissQuestions(workItemId, externalIds);
      setUnansweredCount(r.unansweredCount);
      const full = await forgeApi.listQuestions(workItemId);
      setItems(full.items);
      showToast(
        `${externalIds.length} question${externalIds.length === 1 ? '' : 's'} dismissed.`,
        'success',
      );
      onAnswered?.(r.unansweredCount);
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Dismiss failed', 'danger');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className={`oq-wizard${unansweredCount > 0 ? ' oq-wizard--blocking' : ''}`}>
      <header
        className="oq-wizard__hd"
        onClick={() => setCollapsed(!collapsed)}
        role="button"
        tabIndex={0}
      >
        <span className="oq-wizard__chevron">{collapsed ? '▸' : '▾'}</span>
        <span className="oq-wizard__title">▌ OPEN QUESTIONS</span>
        <span className="oq-wizard__count" data-blocking={unansweredCount > 0 ? 'true' : undefined}>
          {unansweredCount > 0 ? `${unansweredCount} unanswered · blocking pass` : `all ${items.length} answered`}
        </span>
      </header>
      {!collapsed && (
        <div className="oq-wizard__body">
          {unanswered.length > 0 && (
            <div className="oq-wizard__pending">
              {unanswered.map((q) => (
                <div key={q.externalId} className="oq-wizard__q">
                  <div className="oq-wizard__q-meta">
                    <span className="oq-wizard__q-id">{q.externalId}</span>
                    <span className="oq-wizard__q-iter">iter {q.iter}</span>
                    <button
                      type="button"
                      className="oq-wizard__q-dismiss"
                      title="Dismiss this question — useful for legacy / unanswerable prose"
                      disabled={submitting}
                      onClick={() => void handleDismiss([q.externalId])}
                    >
                      ✕ dismiss
                    </button>
                  </div>
                  <div className="oq-wizard__q-text">{q.text}</div>
                  {q.kind === 'choice' && q.choices.length > 0 ? (
                    <div className="oq-wizard__q-choices">
                      {q.choices.map((c) => (
                        <label key={c} className="oq-wizard__q-choice">
                          <input
                            type="radio"
                            name={`q-${q.externalId}`}
                            value={c}
                            checked={drafts[q.externalId] === c}
                            onChange={() => setDrafts((d) => ({ ...d, [q.externalId]: c }))}
                          />
                          <span>{c}</span>
                        </label>
                      ))}
                      <label className="oq-wizard__q-choice oq-wizard__q-choice--other">
                        <input
                          type="radio"
                          name={`q-${q.externalId}`}
                          value="__other__"
                          checked={drafts[q.externalId]?.startsWith('__other__:') ?? false}
                          onChange={() => setDrafts((d) => ({ ...d, [q.externalId]: '__other__:' }))}
                        />
                        <span>Other:</span>
                        <input
                          type="text"
                          className="oq-wizard__q-other-input"
                          placeholder="custom answer"
                          value={drafts[q.externalId]?.startsWith('__other__:') ? drafts[q.externalId].slice('__other__:'.length) : ''}
                          onChange={(e) => setDrafts((d) => ({ ...d, [q.externalId]: '__other__:' + e.target.value }))}
                          onClick={(e) => e.stopPropagation()}
                        />
                      </label>
                    </div>
                  ) : (
                    <textarea
                      className="oq-wizard__q-textarea"
                      placeholder="Type your answer…"
                      rows={2}
                      value={drafts[q.externalId] ?? ''}
                      onChange={(e) => setDrafts((d) => ({ ...d, [q.externalId]: e.target.value }))}
                    />
                  )}
                </div>
              ))}
              <div className="oq-wizard__actions">
                <button
                  type="button"
                  className="oq-wizard__dismiss-all"
                  disabled={submitting}
                  title="Dismiss every unanswered question — use for legacy prose imports"
                  onClick={() => void handleDismiss(unanswered.map((q) => q.externalId))}
                >
                  ✕ dismiss all
                </button>
                <button
                  type="button"
                  className="oq-wizard__submit"
                  disabled={submitting}
                  onClick={() => void handleSubmit()}
                >
                  {submitting ? '⏳ submitting…' : '✓ submit answers'}
                </button>
              </div>
            </div>
          )}
          {items.some((q) => q.answer) && (
            <details className="oq-wizard__answered">
              <summary>
                {items.filter((q) => q.answer).length} previously answered
              </summary>
              <div className="oq-wizard__answered-list">
                {items.filter((q) => q.answer).map((q) => (
                  <div key={q.id} className="oq-wizard__answered-row">
                    <div className="oq-wizard__q-meta">
                      <span className="oq-wizard__q-id">{q.externalId}</span>
                      <span className="oq-wizard__q-iter">iter {q.iter}</span>
                    </div>
                    <div className="oq-wizard__q-text">{q.text}</div>
                    <div className="oq-wizard__q-answer">→ {q.answer}</div>
                  </div>
                ))}
              </div>
            </details>
          )}
        </div>
      )}
    </section>
  );
}
