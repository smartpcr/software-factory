// Phase 108 — the product rule catalog, edited from the story's RULES panel.
// A tag says which workstreams it applies to (repos, categories, phrases or
// patterns found in the stage text); a rule is attached to tags and goes into
// the generator prompt of every workstream one of its tags applies to, in
// every story of the product. "Check" previews a tag against this story.

import { useCallback, useEffect, useId, useState } from 'react';
import {
  forgeApi,
  type CatalogRule,
  type CatalogRuleInput,
  type RuleCatalog,
  type RuleTag,
  type RuleTagInput,
  type RuleTagMatch,
} from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { emptyTag, items, lines, rulesUsingTag, tagMatchers, tagNames, tagScope } from './ruleCatalogText';

type Matches = { tagId: string; rows: RuleTagMatch[] } | null;

/** The catalog's tags, each with its scope, matchers and (once checked) the workstreams it applies to. */
export function TagList({ catalog, busy, matches, scope = 'this story', onCheck, onEdit, onDelete }: {
  catalog: RuleCatalog;
  busy?: boolean;
  matches?: Matches;
  /** Where "Check" looks: "this story" or "this product". */
  scope?: string;
  onCheck?: (tag: RuleTag) => void;
  onEdit?: (tag: RuleTag) => void;
  onDelete?: (tag: RuleTag) => void;
}) {
  if (catalog.tags.length === 0) {
    return <p className="sc-rules__empty">No tags yet. A tag decides which workstreams a rule reaches.</p>;
  }
  return (
    <ul className="sc-rules__list">
      {catalog.tags.map((tag) => {
        const used = rulesUsingTag(catalog.rules, tag.id).length;
        const shown = matches && matches.tagId === tag.id ? matches.rows : null;
        const hits = shown?.filter((m) => m.applied) ?? [];
        return (
          <li key={tag.id} className="sc-rules__pin sc-rc__tag">
            <div className="sc-rc__row">
              <span className="sc-rc__name">{tag.name}</span>
              <span className="sc-rules__scope">{tagScope(tag)}</span>
              <span className="sc-rc__dim">{used} rule{used === 1 ? '' : 's'}</span>
            </div>
            {tag.description && <div className="sc-rules__pin-text">{tag.description}</div>}
            <div className="sc-rc__dim">{tagMatchers(tag)}</div>
            {(tag.phrases.length > 0 || tag.patterns.length > 0) && (
              <div className="sc-rc__chips">
                {tag.phrases.map((p) => <code key={`p:${p}`} className="sc-rc__chip">{p}</code>)}
                {tag.patterns.map((p) => <code key={`r:${p}`} className="sc-rc__chip sc-rc__chip--re">/{p}/</code>)}
              </div>
            )}
            {(onCheck || onEdit || onDelete) && (
              <div className="sc-rules__pin-meta">
                {onCheck && <button type="button" className="sc-rc__btn" disabled={busy} onClick={() => onCheck(tag)}>Check {scope}</button>}
                {onEdit && <button type="button" className="sc-rc__btn" disabled={busy} onClick={() => onEdit(tag)}>Edit</button>}
                {onDelete && <button type="button" className="sc-rules__remove" disabled={busy} onClick={() => onDelete(tag)}>Delete</button>}
              </div>
            )}
            {shown && (
              <div className="sc-rc__matches">
                <div className="sc-rc__dim">Applies to {hits.length} of {shown.length} workstreams in {scope}</div>
                {hits.length > 0 && (
                  <ul className="sc-rc__match-list">
                    {hits.map((m) => (
                      <li key={m.workstreamId} title={m.workstreamId}>
                        <span className="sc-rc__name">{m.name}</span>{' '}
                        <span className="sc-rc__dim">{m.category} · {m.status} · {m.reason}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}

/** The catalog's rules with the tags that carry them. */
export function RuleList({ catalog, busy, onEdit, onDelete, onToggle }: {
  catalog: RuleCatalog;
  busy?: boolean;
  onEdit?: (rule: CatalogRule) => void;
  onDelete?: (rule: CatalogRule) => void;
  onToggle?: (rule: CatalogRule) => void;
}) {
  if (catalog.rules.length === 0) {
    return <p className="sc-rules__empty">No rules yet.</p>;
  }
  return (
    <ul className="sc-rules__list">
      {catalog.rules.map((rule) => (
        <li key={rule.id} className={`sc-rules__pin sc-rc__rule${rule.enabled ? '' : ' is-off'}`}>
          <div className="sc-rc__row">
            <span className="sc-rc__name">{rule.title}</span>
            {!rule.enabled && <span className="sc-rc__dim">disabled</span>}
          </div>
          <div className="sc-rc__chips">
            {rule.tagIds.length === 0
              ? <span className="sc-rc__dim">No tags: reaches only the work items it is attached to</span>
              : tagNames(catalog.tags, rule.tagIds).map((n) => <span key={n} className="sc-rc__chip sc-rc__chip--tag">{n}</span>)}
          </div>
          <div className="sc-rules__pin-text sc-rc__text">{rule.text}</div>
          {(onEdit || onDelete || onToggle) && (
            <div className="sc-rules__pin-meta">
              {onToggle && (
                <button type="button" className="sc-rc__btn" disabled={busy} onClick={() => onToggle(rule)}>
                  {rule.enabled ? 'Disable' : 'Enable'}
                </button>
              )}
              {onEdit && <button type="button" className="sc-rc__btn" disabled={busy} onClick={() => onEdit(rule)}>Edit</button>}
              {onDelete && <button type="button" className="sc-rules__remove" disabled={busy} onClick={() => onDelete(rule)}>Delete</button>}
            </div>
          )}
        </li>
      ))}
    </ul>
  );
}

export function TagEditor({ initial, busy, onSave, onCancel }: {
  initial: RuleTagInput;
  busy: boolean;
  onSave: (tag: RuleTagInput) => void;
  onCancel: () => void;
}) {
  const id = useId();
  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description);
  const [repos, setRepos] = useState(initial.repoIds.join(', '));
  const [cats, setCats] = useState<string[]>(initial.categories);
  const [phrases, setPhrases] = useState(initial.phrases.join('\n'));
  const [patterns, setPatterns] = useState(initial.patterns.join('\n'));
  const toggleCat = (c: string) => setCats((cur) => (cur.includes(c) ? cur.filter((x) => x !== c) : [...cur, c]));
  return (
    <div className="sc-rc__editor">
      <label className="sc-rules__label" htmlFor={`${id}-name`}>Tag name</label>
      <input id={`${id}-name`} className="sc-rules__input" value={name} maxLength={80}
        placeholder="full harness gate" onChange={(e) => setName(e.target.value)} />
      <label className="sc-rules__label" htmlFor={`${id}-desc`}>What it means</label>
      <input id={`${id}-desc`} className="sc-rules__input" value={description}
        placeholder="The stage needs the full harness gate or a lane run" onChange={(e) => setDescription(e.target.value)} />
      <label className="sc-rules__label" htmlFor={`${id}-repos`}>Repos (comma-separated; empty = every repo)</label>
      <input id={`${id}-repos`} className="sc-rules__input" value={repos}
        placeholder="agent-plugin" onChange={(e) => setRepos(e.target.value)} />
      <fieldset className="sc-rc__fieldset">
        <legend className="sc-rules__label">Workstreams (none ticked = both)</legend>
        {(['impl', 'qa-e2e'] as const).map((c) => (
          <label key={c} className="sc-rc__check">
            <input type="checkbox" checked={cats.includes(c)} onChange={() => toggleCat(c)} /> {c}
          </label>
        ))}
      </fieldset>
      <label className="sc-rules__label" htmlFor={`${id}-phrases`}>Phrases in the stage text, one per line (any one applies the tag)</label>
      <textarea id={`${id}-phrases`} className="sc-rules__input" rows={4} value={phrases}
        placeholder={'both lanes\ntools/harness-gate\nlanes/win.ps1'} onChange={(e) => setPhrases(e.target.value)} />
      <label className="sc-rules__label" htmlFor={`${id}-patterns`}>Regular expressions, one per line (optional)</label>
      <textarea id={`${id}-patterns`} className="sc-rules__input" rows={2} value={patterns}
        placeholder={'Scenarios and Built Record'} onChange={(e) => setPatterns(e.target.value)} />
      <p className="sc-rc__dim">With no phrase and no pattern, the tag applies to every workstream in its repos.</p>
      <div className="sc-rules__actions">
        <button type="button" onClick={onCancel}>Cancel</button>
        <button type="button" className="primary" disabled={busy || !name.trim()}
          onClick={() => onSave({ name: name.trim(), description: description.trim(), repoIds: items(repos), categories: cats, phrases: lines(phrases), patterns: lines(patterns) })}>
          {busy ? 'Saving…' : 'Save tag'}
        </button>
      </div>
    </div>
  );
}

export function RuleEditor({ initial, tags, busy, onSave, onCancel }: {
  initial: CatalogRuleInput;
  tags: RuleTag[];
  busy: boolean;
  onSave: (rule: CatalogRuleInput) => void;
  onCancel: () => void;
}) {
  const id = useId();
  const [title, setTitle] = useState(initial.title);
  const [text, setText] = useState(initial.text);
  const [tagIds, setTagIds] = useState<string[]>(initial.tagIds);
  const [enabled, setEnabled] = useState(initial.enabled);
  const toggle = (t: string) => setTagIds((cur) => (cur.includes(t) ? cur.filter((x) => x !== t) : [...cur, t]));
  const ready = !!title.trim() && !!text.trim();
  return (
    <div className="sc-rc__editor">
      <label className="sc-rules__label" htmlFor={`${id}-title`}>Rule title</label>
      <input id={`${id}-title`} className="sc-rules__input" value={title} maxLength={120}
        placeholder="Long-running checks" onChange={(e) => setTitle(e.target.value)} />
      <label className="sc-rules__label" htmlFor={`${id}-text`}>What the agent must do</label>
      <textarea id={`${id}-text`} className="sc-rules__input" rows={8} maxLength={8000} value={text}
        onChange={(e) => setText(e.target.value)} />
      <fieldset className="sc-rc__fieldset">
        <legend className="sc-rules__label">Attach automatically to work items tagged (optional)</legend>
        {tags.length === 0 && <span className="sc-rc__dim">No tags in the catalog yet.</span>}
        {tags.map((t) => (
          <label key={t.id} className="sc-rc__check">
            <input type="checkbox" checked={tagIds.includes(t.id)} onChange={() => toggle(t.id)} /> {t.name}
          </label>
        ))}
      </fieldset>
      <p className="sc-rc__dim">
        {tagIds.length === 0
          ? 'With no tag, the rule reaches only the work items and workstreams you attach it to.'
          : 'You can still attach or detach it on any one work item or workstream.'}
      </p>
      <label className="sc-rc__check">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled
      </label>
      <div className="sc-rules__actions">
        <button type="button" onClick={onCancel}>Cancel</button>
        <button type="button" className="primary" disabled={busy || !ready}
          onClick={() => onSave({ title: title.trim(), text: text.trim(), tagIds, enabled })}>
          {busy ? 'Saving…' : 'Save rule'}
        </button>
      </div>
    </div>
  );
}

/**
 * The catalog editor. With `storyId` (the story RULES panel) a tag's "Check"
 * looks at that story's workstreams; without it (the Rules page), at every
 * workstream of the product.
 */
export function RuleCatalogView({ productId, storyId }: { productId: string; storyId?: string }) {
  const [catalog, setCatalog] = useState<RuleCatalog | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [matches, setMatches] = useState<Matches>(null);
  const [tagDraft, setTagDraft] = useState<{ id: string | null; value: RuleTagInput } | null>(null);
  const [ruleDraft, setRuleDraft] = useState<{ id: string | null; value: CatalogRuleInput } | null>(null);

  const load = useCallback(async () => {
    try {
      setCatalog(await forgeApi.getRuleCatalog(productId));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Loading the rule catalog failed');
    }
  }, [productId]);
  useEffect(() => { void load(); }, [load]);

  async function run(action: () => Promise<unknown>, after?: () => void) {
    setBusy(true);
    setError(null);
    try {
      await action();
      after?.();
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The change failed');
    } finally {
      setBusy(false);
    }
  }

  async function confirmDelete(title: string, body: string): Promise<boolean> {
    return useConfirmStore.getState().ask({ title, body, tone: 'warn', confirmLabel: 'Delete', cancelLabel: 'Keep' });
  }

  if (!catalog) {
    return error ? <p className="sc-rules__error" role="alert">{error}</p> : <p className="sc-rules__empty">Loading…</p>;
  }

  const scope = storyId ? 'this story' : 'this product';
  const ruleSection = (
    <>
      <div className="sc-rc__head">
        <h3 className="sc-rules__group">Rules · {catalog.rules.length}</h3>
        <button type="button" className="sc-rc__btn" disabled={busy || !!ruleDraft}
          onClick={() => setRuleDraft({ id: null, value: { title: '', text: '', tagIds: [], enabled: true } })}>+ New rule</button>
      </div>
      {ruleDraft && (
        <RuleEditor
          key={ruleDraft.id ?? 'new'}
          initial={ruleDraft.value}
          tags={catalog.tags}
          busy={busy}
          onCancel={() => setRuleDraft(null)}
          onSave={(value) => void run(
            () => (ruleDraft.id ? forgeApi.updateCatalogRule(productId, ruleDraft.id, value) : forgeApi.addCatalogRule(productId, value)),
            () => setRuleDraft(null),
          )}
        />
      )}
      <RuleList
        catalog={catalog}
        busy={busy}
        onEdit={(rule) => setRuleDraft({ id: rule.id, value: rule })}
        onToggle={(rule) => void run(() => forgeApi.updateCatalogRule(productId, rule.id, { ...rule, enabled: !rule.enabled }))}
        onDelete={(rule) => void (async () => {
          if (!await confirmDelete('DELETE THIS RULE?', `${rule.title} — it leaves every work item it reaches, from their next iteration.`)) return;
          await run(() => forgeApi.deleteCatalogRule(productId, rule.id));
        })()}
      />
    </>
  );

  return (
    <div className="sc-rc">
      <p className="sc-rules__help sc-rc__help">
        Rules for product <strong>{catalog.productId}</strong>. A rule goes into a work item&apos;s or
        workstream&apos;s generator prompt from its next iteration when you attach it there (in the work
        item&apos;s <em>Rules for this work item</em> list) or when one of its tags applies there automatically.
      </p>
      {error && <p className="sc-rules__error" role="alert">{error}</p>}

      {ruleSection}

      <div className="sc-rc__head">
        <h3 className="sc-rules__group">Tags · {catalog.tags.length}</h3>
        <button type="button" className="sc-rc__btn" disabled={busy || !!tagDraft}
          onClick={() => setTagDraft({ id: null, value: emptyTag })}>+ New tag</button>
      </div>
      <p className="sc-rc__dim">Optional. A tag attaches its rules automatically to every work item whose repo and text it matches.</p>
      {tagDraft && (
        <TagEditor
          key={tagDraft.id ?? 'new'}
          initial={tagDraft.value}
          busy={busy}
          onCancel={() => setTagDraft(null)}
          onSave={(value) => void run(
            () => (tagDraft.id ? forgeApi.updateRuleTag(productId, tagDraft.id, value) : forgeApi.addRuleTag(productId, value)),
            () => { setTagDraft(null); setMatches(null); },
          )}
        />
      )}
      <TagList
        catalog={catalog}
        busy={busy}
        matches={matches}
        scope={scope}
        onCheck={(tag) => void run(async () => setMatches({ tagId: tag.id, rows: await forgeApi.listRuleTagMatches(productId, tag.id, storyId) }))}
        onEdit={(tag) => setTagDraft({ id: tag.id, value: tag })}
        onDelete={(tag) => void (async () => {
          const used = rulesUsingTag(catalog.rules, tag.id).length;
          if (!await confirmDelete('DELETE THIS TAG?', `${tag.name} — ${used} rule(s) lose this tag and stop reaching the work items it matched, unless attached there directly.`)) return;
          await run(() => forgeApi.deleteRuleTag(productId, tag.id), () => setMatches(null));
        })()}
      />
    </div>
  );
}