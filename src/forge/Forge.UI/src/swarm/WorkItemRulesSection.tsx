// Phase 108 — "Policies & rules" for one work item execution or workstream.
// Every policy and every rule of the product is listed with a checkbox:
// ticked ones go into this item's generator prompt from its next iteration.
// A policy can be ticked because one of its tags matches here, or by hand; a
// rule because a ticked policy contains it, or on its own. Unticking keeps it
// out of this item only; Reset hands the choice back to the tags/policies.
// Styles: styles/rules.css (works on plain pages and in the swarm view).

import { useCallback, useEffect, useId, useState } from 'react';
import { Link } from 'react-router-dom';
import { forgeApi, type PolicyChoice, type RuleChoice, type WorkItemChoiceKind, type WorkItemRules } from '@/api/forgeApi';
import { choiceAction, choiceStatus, tagChoice, type TagChoice } from './ruleCatalogText';

const TAG_CHOICES: { value: TagChoice; label: string; title: string }[] = [
  { value: 'auto', label: 'Auto', title: "The tag's phrases decide" },
  { value: 'on', label: 'On', title: 'Always apply this tag to this work item' },
  { value: 'off', label: 'Off', title: 'Never apply this tag to this work item' },
];

function ChoiceRow({ choice, name, detail, busy, onCheck, onReset }: {
  choice: PolicyChoice | RuleChoice;
  name: string;
  detail?: { summary: string; body: string };
  busy?: boolean;
  onCheck?: (checked: boolean) => void;
  onReset?: () => void;
}) {
  const id = useId();
  const overridden = choice.source === 'attached' || choice.source === 'detached';
  return (
    <li className={`rx-row${choice.applied ? ' is-on' : ''}`}>
      <div className="rx-row-main">
        <input id={id} type="checkbox" checked={choice.applied} disabled={busy || !choice.enabled || !onCheck}
          onChange={(e) => onCheck?.(e.target.checked)} />
        <label htmlFor={id} className="rx-row-name">{name}</label>
        <span className="rx-row-why">{choiceStatus(choice)}</span>
        {overridden && onReset && (
          <button type="button" className="rx-small-btn" disabled={busy} onClick={onReset}
            title="Drop this choice and let tags and policies decide">Reset</button>
        )}
      </div>
      {detail && (
        <details className="rx-row-details">
          <summary>{detail.summary}</summary>
          <pre>{detail.body}</pre>
        </details>
      )}
    </li>
  );
}

/** Pure view of a work item's policies, rules and tags, so it can be rendered in tests. */
export function WorkItemRulesView({ rules, busy, onChoose, onReset, onTag }: {
  rules: WorkItemRules;
  busy?: boolean;
  onChoose?: (kind: 'policies' | 'rules', choice: PolicyChoice | RuleChoice, checked: boolean) => void;
  onReset?: (kind: 'policies' | 'rules', id: string) => void;
  onTag?: (tagId: string, choice: TagChoice) => void;
}) {
  if (rules.choices.length === 0) {
    return (
      <p className="rx-empty">
        This product has no rules yet. Create them on the <Link to="/policies">Policies page</Link>.
      </p>
    );
  }
  const appliedTags = rules.tags.filter((t) => t.applied).length;
  return (
    <>
      <h4 className="rx-group-title">Policies</h4>
      {rules.policies.length === 0
        ? <p className="rx-empty">No policies yet.</p>
        : (
          <ul className="rx-list" aria-label="Policies">
            {rules.policies.map((p) => (
              <ChoiceRow key={p.id} choice={p} busy={busy}
                name={`${p.name} · ${p.ruleCount} rule${p.ruleCount === 1 ? '' : 's'}`}
                onCheck={onChoose && ((checked) => onChoose('policies', p, checked))}
                onReset={onReset && (() => onReset('policies', p.id))} />
            ))}
          </ul>
        )}

      <h4 className="rx-group-title">Rules</h4>
      <ul className="rx-list" aria-label="Rules">
        {rules.choices.map((c) => (
          <ChoiceRow key={c.id} choice={c} busy={busy} name={c.title}
            detail={{ summary: c.policies.length > 0 ? `Text · in ${c.policies.join(', ')}` : 'Text', body: c.text }}
            onCheck={onChoose && ((checked) => onChoose('rules', c, checked))}
            onReset={onReset && (() => onReset('rules', c.id))} />
        ))}
      </ul>

      {rules.tags.length > 0 && (
        <details className="rx-fold">
          <summary>Tags · {appliedTags} of {rules.tags.length} match this work item</summary>
          <ul className="rx-list">
            {rules.tags.map((tag) => {
              const choice = tagChoice(tag);
              return (
                <li key={tag.tagId} className={`rx-row${tag.applied ? ' is-on' : ''}`}>
                  <div className="rx-row-main">
                    <span className="rx-row-name">{tag.applied ? '✓ ' : ''}{tag.name}</span>
                    <span className="rx-row-why">{tag.reason}</span>
                    {onTag && (
                      <span className="rx-seg" role="group" aria-label={`${tag.name} on this work item`}>
                        {TAG_CHOICES.map((c) => (
                          <button key={c.value} type="button" title={c.title} aria-pressed={choice === c.value}
                            className={choice === c.value ? 'is-active' : undefined}
                            disabled={busy || choice === c.value} onClick={() => onTag(tag.tagId, c.value)}>
                            {c.label}
                          </button>
                        ))}
                      </span>
                    )}
                  </div>
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
      setError(e instanceof Error ? e.message : 'Loading the policies failed');
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

  const set = (kind: WorkItemChoiceKind, id: string, action: 'on' | 'off' | 'clear') => apply(() => (action === 'clear'
    ? forgeApi.clearWorkItemChoice(productId, workItemId, kind, id)
    : forgeApi.setWorkItemChoice(productId, workItemId, kind, id, action === 'on')));

  const inPrompt = rules?.rules.length ?? 0;
  return (
    <section className="rx rx-panel" aria-labelledby={`rules-${workItemId}`}>
      <div className="rx-panel-head">
        <h3 id={`rules-${workItemId}`} className="rx-panel-title">Policies &amp; rules</h3>
        <span className="rx-panel-meta">
          {inPrompt} rule{inPrompt === 1 ? '' : 's'} in the next prompt · <Link to="/policies">Manage</Link>
        </span>
      </div>
      {error && <p className="rx-error" role="alert">{error}</p>}
      {rules
        ? (
          <WorkItemRulesView
            rules={rules}
            busy={busy}
            onChoose={(kind, c, checked) => void set(kind, c.id, choiceAction(c, checked))}
            onReset={(kind, id) => void set(kind, id, 'clear')}
            onTag={(tagId, c) => void set('tags', tagId, c === 'auto' ? 'clear' : c)}
          />
        )
        : !error && <p className="rx-empty">Loading…</p>}
    </section>
  );
}