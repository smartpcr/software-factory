// Phase 39 (Layer C) — open-questions wizard. Reused by both
// DocConversationPane (architect docs) and WorkstreamConversationPane
// (engineer workstreams). Renders pending questions above the iteration
// pager; when the operator submits answers, the orchestrator's next
// iteration prepends a `## Operator answers` block and the evaluator's
// pass-verdict gate releases.
import { useEffect, useRef, useState } from 'react';
import { forgeApi, type QuestionExplanation } from '@/api/forgeApi';
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
  answerRevision?: string | null;
  answeredBy: string | null;
  explanation: QuestionExplanation | null;
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
  // Re-answer state for the "previously answered" list: the externalId whose
  // inline editor is open, plus its draft value. Only one edit is open at a
  // time. Editing an answered question overwrites it in place (the server
  // supports re-answering); the operator then Restarts the workstream, which
  // now preserves answered questions so the new value reaches the fresh run's
  // `## Operator answers` block.
  const [editingFor, setEditingFor] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState('');
  const showToast = useToastStore((s) => s.show);

  // Explain popup state. `explainFor` is the externalId whose popup is open;
  // `explainData` caches resolved explanations (agent-authored or generated);
  // `explainLoading` is the externalId currently generating on demand.
  const [explainFor, setExplainFor] = useState<string | null>(null);
  const [explainData, setExplainData] = useState<Record<string, QuestionExplanation>>({});
  const [explainLoading, setExplainLoading] = useState<string | null>(null);
  const [explainError, setExplainError] = useState<string | null>(null);
  // Tracks externalIds we've already kicked off pre-generation for, so the
  // poll loop doesn't re-fire generation every tick.
  const pregenAttempted = useRef<Set<string>>(new Set());

  // Pre-generate explanations eagerly as soon as the questions load so the
  // popup opens instantly with content (no wait-on-open). Only questions that
  // LACK an explanation are generated; agent-authored ones are already instant.
  // Runs when the Questions tab is open (this component is mounted), bounding
  // the cost to when the operator is actually looking at the questions.
  useEffect(() => {
    let cancelled = false;
    async function pregen() {
      for (const q of items) {
        if (cancelled) break;
        if (q.explanation) continue;                       // agent-authored — instant
        if (explainData[q.externalId]) continue;           // already resolved this session
        if (pregenAttempted.current.has(q.externalId)) continue;
        pregenAttempted.current.add(q.externalId);
        try {
          const r = await forgeApi.explainQuestion(workItemId, q.externalId);
          if (!cancelled) setExplainData((d) => ({ ...d, [q.externalId]: r.explanation }));
        } catch {
          // Leave it — the on-open path will surface the error if the operator opens it.
        }
      }
    }
    void pregen();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items, workItemId]);

  async function openExplain(q: QuestionItem, force = false) {
    setExplainFor(q.externalId);
    setExplainError(null);
    // Prefer the explanation already on the row (agent-authored) or one we
    // resolved earlier this session — unless the operator forced a regenerate.
    const existing = q.explanation ?? explainData[q.externalId];
    if (existing && !force) {
      setExplainData((d) => ({ ...d, [q.externalId]: existing }));
      return;
    }
    // None yet (or forced) — generate on demand.
    setExplainLoading(q.externalId);
    try {
      const r = await forgeApi.explainQuestion(workItemId, q.externalId, force);
      setExplainData((d) => ({ ...d, [q.externalId]: r.explanation }));
    } catch (e) {
      setExplainError(e instanceof Error ? e.message : 'Could not generate an explanation.');
    } finally {
      setExplainLoading(null);
    }
  }

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
      .map((q) => ({ externalId: q.externalId, expectedRevision: q.id, answer: (drafts[q.externalId] ?? '').trim() }))
      .filter((a) => a.answer.length > 0);
    if (answers.length === 0) {
      showToast('Pick an option for at least one question.', 'info');
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

  function beginEdit(q: QuestionItem) {
    setEditingFor(q.externalId);
    setEditDraft(q.answer ?? '');
  }

  function cancelEdit() {
    setEditingFor(null);
    setEditDraft('');
  }

  async function handleEditSave(externalId: string) {
    const answer = editDraft.trim();
    if (answer.length === 0) {
      showToast('Pick an option before saving.', 'info');
      return;
    }
    setSubmitting(true);
    try {
      const r = await forgeApi.answerQuestions(workItemId, [{ externalId, answer, expectedRevision: items.find((q) => q.externalId === externalId)?.id, editExisting: true, expectedAnswerRevision: items.find((q) => q.externalId === externalId)?.answerRevision }]);
      setUnansweredCount(r.unansweredCount);
      const full = await forgeApi.listQuestions(workItemId);
      setItems(full.items);
      cancelEdit();
      showToast(
        'Answer updated — Restart the workstream to feed the new answer into a fresh run.',
        'success',
      );
      onAnswered?.(r.unansweredCount);
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Update failed', 'danger');
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
                      className="oq-wizard__q-explain"
                      title="Explain this question — terms, the logic behind each choice, and the agent's recommendation"
                      onClick={() => void openExplain(q)}
                    >
                      ⓘ explain
                    </button>
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
                      {q.choices.map((c) => {
                        const rec = (q.explanation ?? explainData[q.externalId])?.recommendation;
                        const isRec = rec != null && rec === c;
                        return (
                        <label key={c} className={`oq-wizard__q-choice${isRec ? ' oq-wizard__q-choice--recommended' : ''}`}>
                          <input
                            type="radio"
                            name={`q-${q.externalId}`}
                            value={c}
                            checked={drafts[q.externalId] === c}
                            onChange={() => setDrafts((d) => ({ ...d, [q.externalId]: c }))}
                          />
                          <span>{c}</span>
                          {isRec && (
                            <span className="oq-wizard__q-rec-tag" title="Agent's recommendation">★ recommended</span>
                          )}
                        </label>
                        );
                      })}
                    </div>
                  ) : (
                    // Answers are picked, never typed. The server drafts options for any
                    // question that arrives without them, so this only shows until it has.
                    <div className="oq-wizard__q-pending-options">Preparing options for this question…</div>
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
                      <button
                        type="button"
                        className="oq-wizard__q-explain"
                        title="Explain this question — terms, the logic behind each choice, and the agent's recommendation"
                        onClick={() => void openExplain(q)}
                      >
                        ⓘ explain
                      </button>
                      {/* Answers can only be changed to another option; legacy option-less answers stay as history. */}
                      {editingFor !== q.externalId && q.choices.length > 0 && (
                        <button
                          type="button"
                          className="oq-wizard__q-edit"
                          title="Change this answer — Restart the workstream afterwards to feed the new answer into a fresh run"
                          disabled={submitting}
                          onClick={() => beginEdit(q)}
                        >
                          ✎ edit
                        </button>
                      )}
                    </div>
                    <div className="oq-wizard__q-text">{q.text}</div>
                    {editingFor === q.externalId ? (
                      <div className="oq-wizard__q-edit-panel">
                        <div className="oq-wizard__q-choices">
                          {q.choices.map((c) => (
                            <label key={c} className="oq-wizard__q-choice">
                              <input
                                type="radio"
                                name={`edit-${q.externalId}`}
                                value={c}
                                checked={editDraft === c}
                                disabled={submitting}
                                onChange={() => setEditDraft(c)}
                              />
                              <span>{c}</span>
                            </label>
                          ))}
                        </div>
                        <div className="oq-wizard__actions">
                          <button
                            type="button"
                            className="oq-wizard__dismiss-all"
                            disabled={submitting}
                            onClick={cancelEdit}
                          >
                            cancel
                          </button>
                          <button
                            type="button"
                            className="oq-wizard__submit"
                            disabled={submitting || editDraft.trim().length === 0}
                            onClick={() => void handleEditSave(q.externalId)}
                          >
                            {submitting ? '⏳ saving…' : '✓ save answer'}
                          </button>
                        </div>
                      </div>
                    ) : (
                      <div className="oq-wizard__q-answer">→ {q.answer}</div>
                    )}
                  </div>
                ))}
              </div>
            </details>
          )}
        </div>
      )}
      {explainFor !== null && (() => {
        const q = items.find((it) => it.externalId === explainFor);
        const expl = explainData[explainFor];
        const loading = explainLoading === explainFor;
        // Normalise per-choice analysis: prefer the rich pros/cons table; fall
        // back to the legacy flat map (cons-less) so older data still renders.
        const rows: Array<{ choice: string; pros: string[]; cons: string[] }> =
          expl?.choiceAnalysis && expl.choiceAnalysis.length > 0
            ? expl.choiceAnalysis
            : expl?.choiceExplanations
              ? Object.entries(expl.choiceExplanations).map(([choice, logic]) => ({ choice, pros: [logic], cons: [] }))
              : [];
        return (
          <div
            className="oq-explain__overlay"
            role="dialog"
            aria-modal="true"
            onClick={() => setExplainFor(null)}
          >
            <div className="oq-explain__modal" onClick={(e) => e.stopPropagation()}>
              <header className="oq-explain__hd">
                <span className="oq-explain__title">▌ EXPLAIN · {explainFor}</span>
                {q && (
                  <button
                    type="button"
                    className="oq-explain__regen"
                    title="Regenerate the explanation"
                    disabled={loading}
                    onClick={() => void openExplain(q, true)}
                  >
                    ↻ regenerate
                  </button>
                )}
                <button
                  type="button"
                  className="oq-explain__close"
                  title="Close"
                  onClick={() => setExplainFor(null)}
                >
                  ✕
                </button>
              </header>
              {q && <div className="oq-explain__question">{q.text}</div>}
              <div className="oq-explain__body">
                {loading && <div className="oq-explain__loading">⏳ Generating explanation…</div>}
                {!loading && explainError && (
                  <div className="oq-explain__error">{explainError}</div>
                )}
                {!loading && !explainError && expl && (
                  <>
                    {expl.explanation && (
                      <section className="oq-explain__section">
                        <h4>What this means</h4>
                        <p>{expl.explanation}</p>
                      </section>
                    )}
                    {rows.length > 0 && (
                      <section className="oq-explain__section">
                        <h4>Options — pros &amp; cons</h4>
                        <table className="oq-explain__table">
                          <thead>
                            <tr>
                              <th>Option</th>
                              <th>Pros</th>
                              <th>Cons</th>
                            </tr>
                          </thead>
                          <tbody>
                            {rows.map((r) => {
                              const isRec = expl.recommendation != null && expl.recommendation === r.choice;
                              return (
                                <tr key={r.choice} className={isRec ? 'oq-explain__row--recommended' : undefined}>
                                  <td className="oq-explain__opt">
                                    {r.choice}
                                    {isRec && <span className="oq-explain__rec-tag"> ★</span>}
                                  </td>
                                  <td>
                                    <ul className="oq-explain__pros">
                                      {r.pros.length > 0 ? r.pros.map((p, i) => <li key={i}>{p}</li>) : <li className="oq-explain__muted">—</li>}
                                    </ul>
                                  </td>
                                  <td>
                                    <ul className="oq-explain__cons">
                                      {r.cons.length > 0 ? r.cons.map((c, i) => <li key={i}>{c}</li>) : <li className="oq-explain__muted">—</li>}
                                    </ul>
                                  </td>
                                </tr>
                              );
                            })}
                          </tbody>
                        </table>
                      </section>
                    )}
                    {expl.recommendation && (
                      <section className="oq-explain__section oq-explain__recommendation">
                        <h4>Agent's recommendation</h4>
                        <p><strong>{expl.recommendation}</strong></p>
                        {expl.recommendationRationale && <p>{expl.recommendationRationale}</p>}
                      </section>
                    )}
                    {expl.generated && (
                      <div className="oq-explain__generated-note">
                        ⓘ Generated on demand — not authored by the original agent.
                      </div>
                    )}
                  </>
                )}
                {!loading && !explainError && !expl && (
                  <div className="oq-explain__loading">No explanation available.</div>
                )}
              </div>
            </div>
          </div>
        );
      })()}
    </section>
  );
}
