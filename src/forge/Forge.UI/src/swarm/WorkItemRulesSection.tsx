// Phase 108 — attach catalog rules to one work item execution or workstream.
// Every rule in the product catalog is listed with a checkbox: ticked rules go
// into this item's generator prompt from its next iteration. A rule can be
// ticked because one of its tags applies here, or because the operator
// attached it; unticking a tag-applied rule detaches it from this item only.
// The item's tags (and their Auto / On / Off choice) sit below, folded.

import { useCallback, useEffect, useId, useState } from 'react';
import { Link } from 'react-router-dom';
import { forgeApi, type WorkItemRules } from '@/api/forgeApi';
import { ruleAction, ruleStatus, tagChoice, type TagChoice } from './ruleCatalogText';

const TAG_CHOICES: { value: TagChoice; label: string; title: string }[] = [
  { value: 'auto', label: 'Auto', title: "The tag's matchers decide" },
  { value: 'on', label: 'On', title: 'Always apply this tag to this work item' },
  { value: 'off', label: 'Off', title: 'Never apply this tag to this work item' },
];

/** Pure view of a work item's rules and tags, so it can be rendered in tests. */
export function WorkItemRulesView({ rules, busy, onRule, onResetRule, onTag }: {
  rules: WorkItemRules;
  busy?: boolean;
  onRule?: (ruleId: string, checked: boolean) => void;
  onResetRule?: (ruleId: string) => void;
  onTag?: (tagId: string, choice: TagChoice) => void;
}) {
  const id = useId();
  if (rules.choices.length === 0) {
    return (
      <p className="sc-wr__empty">
        The product rule catalog is empty. Add rules on the <Link to="/rules">Rules page</Link>.
      </p>
    );
  }
  const appliedTags = rules.tags.filter((t) => t.applied).length;
  return (
    <>
      <ul className="sc-wr__rules" aria-label="Catalog rules">
        {rules.choices.map((c) => {
          const boxId = `${id}-${c.id}`;
          const overridden = c.source === 'attached' || c.source === 'detached';
          return (
            <li key={c.id} className={`sc-wr__rule${c.applied ? ' is-on' : ''}`}>
              <div className="sc-wr__rule-row">
                <input
                  id={boxId}
                  type="checkbox"
                  checked={c.applied}
                  disabled={busy || !c.enabled || !onRule}
                  onChange={(e) => onRule?.(c.id, e.target.checked)}
                />
                <label htmlFor={boxId} className="sc-wr__name">{c.title}</label>
                <span className="sc-wr__reason">{ruleStatus(c)}</span>
                {overridden && onResetRule && (
                  <button type="button" className="sc-wr__reset" disabled={busy}
                    title="Drop this choice and let the rule's tags decide" onClick={() => onResetRule(c.id)}>
                    Reset
                  </button>
                )}
              </div>
              <details className="sc-wr__more">
                <summary>Rule text</summary>
                <pre className="sc-wr__text">{c.text}</pre>
              </details>
            </li>
          );
        })}
      </ul>
      {rules.tags.length > 0 && (
        <details className="sc-wr__tag-box">
          <summary>Tags · {appliedTags} of {rules.tags.length} apply</summary>
          <ul className="sc-wr__tags">
            {rules.tags.map((tag) => {
              const choice = tagChoice(tag);
              return (
                <li key={tag.tagId} className={`sc-wr__tag${tag.applied ? ' is-on' : ''}`}>
                  <span className="sc-wr__mark" aria-hidden="true">{tag.applied ? '✓' : '–'}</span>
                  <span className="sc-wr__name">{tag.name}</span>
                  <span className="sc-wr__reason">{tag.reason}</span>
                  {onTag && (
                    <span className="sc-wr__choice" role="group" aria-label={`${tag.name} on this work item`}>
                      {TAG_CHOICES.map((c) => (
                        <button key={c.value} type="button" title={c.title} aria-pressed={choice === c.value}
                          className={choice === c.value ? 'is-active' : undefined}
                          disabled={busy || choice === c.value} onClick={() => onTag(tag.tagId, c.value)}>
                          {c.label}
                        </button>
                      ))}
                    </span>
                  )}
                </li>
              );
            })}
          </ul>
        </details>
      )}
    </>
  );
}

export function WorkItemRulesSection({ productId, workItemId }: { productId: string; workItemId: string }) {
  const [rules, setRules] = useState<WorkItemRules | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRules(await forgeApi.getWorkItemRules(productId, workItemId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Loading the rules failed');
    }
  }, [productId, workItemId]);
  useEffect(() => { void load(); }, [load]);

  async function apply(change: () => Promise<WorkItemRules>) {
    setBusy(true);
    setError(null);
    try {
      setRules(await change());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The change failed');
    } finally {
      setBusy(false);
    }
  }

  function onRule(ruleId: string, checked: boolean) {
    const choice = rules?.choices.find((c) => c.id === ruleId);
    if (!choice) return;
    const action = ruleAction(choice, checked);
    void apply(() => (action === 'clear'
      ? forgeApi.clearWorkItemRule(productId, workItemId, ruleId)
      : forgeApi.setWorkItemRule(productId, workItemId, ruleId, action === 'on')));
  }

  const inPrompt = rules?.rules.length ?? 0;
  return (
    <section className="sc-wr" aria-label="Rules for this work item">
      <div className="sc-wr__head">
        <span>RULES FOR THIS WORK ITEM</span>
        <span className="sc-wr__count">
          {inPrompt} in the next prompt · <Link to="/rules">Manage rules</Link>
        </span>
      </div>
      {error && <p className="sc-wr__error" role="alert">{error}</p>}
      {rules
        ? (
          <WorkItemRulesView
            rules={rules}
            busy={busy}
            onRule={onRule}
            onResetRule={(ruleId) => void apply(() => forgeApi.clearWorkItemRule(productId, workItemId, ruleId))}
            onTag={(tagId, c) => void apply(() => (c === 'auto'
              ? forgeApi.clearWorkItemTag(productId, workItemId, tagId)
              : forgeApi.setWorkItemTag(productId, workItemId, tagId, c === 'on')))}
          />
        )
        : !error && <p className="sc-wr__empty">Loading…</p>}
    </section>
  );
}