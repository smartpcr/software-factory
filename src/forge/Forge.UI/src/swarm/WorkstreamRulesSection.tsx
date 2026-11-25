// Phase 108 — a workstream's standing in the product rule catalog, on its
// Overview tab: every tag with whether it applies and why, the operator's
// Auto / On / Off choice for it, and the rules that reach this workstream's
// generator prompt on its next iteration.

import { useCallback, useEffect, useState } from 'react';
import { forgeApi, type WorkstreamRules } from '@/api/forgeApi';
import { tagChoice, type TagChoice } from './ruleCatalogText';

const CHOICES: { value: TagChoice; label: string; title: string }[] = [
  { value: 'auto', label: 'Auto', title: "The tag's matchers decide" },
  { value: 'on', label: 'On', title: 'Always apply this tag to this workstream' },
  { value: 'off', label: 'Off', title: 'Never apply this tag to this workstream' },
];

/** Pure view of a workstream's tags and rules, so it can be rendered in tests. */
export function WorkstreamRulesView({ rules, busy, onChoose }: {
  rules: WorkstreamRules;
  busy?: boolean;
  onChoose?: (tagId: string, choice: TagChoice) => void;
}) {
  if (rules.tags.length === 0) {
    return <p className="sc-wr__empty">The product rule catalog has no tags yet. Add them from the story header&apos;s 📌 RULES.</p>;
  }
  return (
    <>
      <ul className="sc-wr__tags">
        {rules.tags.map((tag) => {
          const choice = tagChoice(tag);
          return (
            <li key={tag.tagId} className={`sc-wr__tag${tag.applied ? ' is-on' : ''}`}>
              <span className="sc-wr__mark" aria-hidden="true">{tag.applied ? '✓' : '·'}</span>
              <span className="sc-wr__name">{tag.name}</span>
              <span className="sc-wr__reason">{tag.reason}</span>
              {onChoose && (
                <span className="sc-wr__choice" role="group" aria-label={`${tag.name} on this workstream`}>
                  {CHOICES.map((c) => (
                    <button
                      key={c.value}
                      type="button"
                      title={c.title}
                      aria-pressed={choice === c.value}
                      className={choice === c.value ? 'is-active' : undefined}
                      disabled={busy || choice === c.value}
                      onClick={() => onChoose(tag.tagId, c.value)}
                    >
                      {c.label}
                    </button>
                  ))}
                </span>
              )}
            </li>
          );
        })}
      </ul>
      {rules.rules.length === 0 ? (
        <p className="sc-wr__empty">No catalog rule reaches this workstream.</p>
      ) : (
        <ul className="sc-wr__rules">
          {rules.rules.map((r) => (
            <li key={r.id}>
              <details>
                <summary>
                  <span className="sc-wr__name">{r.title}</span>{' '}
                  <span className="sc-wr__reason">via {r.via.join(', ')}</span>
                </summary>
                <pre className="sc-wr__text">{r.text}</pre>
              </details>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

export function WorkstreamRulesSection({ productId, workstreamId }: { productId: string; workstreamId: string }) {
  const [rules, setRules] = useState<WorkstreamRules | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRules(await forgeApi.getWorkstreamRules(productId, workstreamId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Loading the rules failed');
    }
  }, [productId, workstreamId]);
  useEffect(() => { void load(); }, [load]);

  async function choose(tagId: string, choice: TagChoice) {
    setBusy(true);
    setError(null);
    try {
      setRules(choice === 'auto'
        ? await forgeApi.clearWorkstreamTag(productId, workstreamId, tagId)
        : await forgeApi.setWorkstreamTag(productId, workstreamId, tagId, choice === 'on'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Changing the tag failed');
    } finally {
      setBusy(false);
    }
  }

  const applied = rules?.rules.length ?? 0;
  return (
    <section className="sc-wr" aria-label="Standing rules">
      <div className="sc-wr__head">
        <span>STANDING RULES</span>
        <span className="sc-wr__count">{applied} rule{applied === 1 ? '' : 's'} in the next prompt</span>
      </div>
      {error && <p className="sc-wr__error" role="alert">{error}</p>}
      {rules ? <WorkstreamRulesView rules={rules} busy={busy} onChoose={(t, c) => void choose(t, c)} /> : !error && <p className="sc-wr__empty">Loading…</p>}
    </section>
  );
}