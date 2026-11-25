// Phase 108 — editors for the Policies page: a rule, a policy (a group of
// rules), and a tag (which attaches policies automatically). Styled with the
// app theme (styles/rules.css), not the swarm view's.

import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import type { CatalogRuleInput, RuleCatalog, RulePolicyInput, RuleTagInput } from '@/api/forgeApi';
import { items, lines, tagScope } from '@/swarm/ruleCatalogText';

export function Dialog({ title, error, onClose, children }: {
  title: string; error: string | null; onClose: () => void; children: ReactNode;
}) {
  const titleId = useId();
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    box.current?.querySelector<HTMLElement>('input, textarea')?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); opener?.focus?.(); };
  }, [onClose]);
  return createPortal(
    <div className="rx rx-layer" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div ref={box} className="rx-dialog" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <header className="rx-dialog-head">
          <h2 id={titleId} className="rx-dialog-title">{title}</h2>
          <button type="button" className="rx-close" aria-label="Close" onClick={onClose}>✕</button>
        </header>
        <div className="rx-dialog-body">
          {error && <p className="rx-error" role="alert">{error}</p>}
          {children}
        </div>
      </div>
    </div>,
    document.body,
  );
}

function Actions({ busy, ready, saveLabel, onCancel, onSave }: {
  busy: boolean; ready: boolean; saveLabel: string; onCancel: () => void; onSave: () => void;
}) {
  return (
    <div className="rx-actions">
      <button type="button" className="btn btn-secondary" onClick={onCancel}>Cancel</button>
      <button type="button" className="btn btn-primary" disabled={busy || !ready} onClick={onSave}>
        {busy ? 'Saving…' : saveLabel}
      </button>
    </div>
  );
}

function toggle(list: string[], id: string): string[] {
  return list.includes(id) ? list.filter((x) => x !== id) : [...list, id];
}

/** A rule: title, the directive, whether it is on, and which policies contain it. */
export function RuleForm({ initial, catalog, busy, onSave, onCancel }: {
  initial: CatalogRuleInput & { policyIds: string[] };
  catalog: RuleCatalog;
  busy: boolean;
  onSave: (rule: CatalogRuleInput) => void;
  onCancel: () => void;
}) {
  const id = useId();
  const [title, setTitle] = useState(initial.title);
  const [text, setText] = useState(initial.text);
  const [enabled, setEnabled] = useState(initial.enabled);
  const [policyIds, setPolicyIds] = useState(initial.policyIds);
  return (
    <>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-title`}>Title</label>
        <input id={`${id}-title`} className="rx-input" value={title} maxLength={120}
          placeholder="e.g. Long-running checks" onChange={(e) => setTitle(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-text`}>What the agent must do</label>
        <textarea id={`${id}-text`} className="rx-textarea" rows={9} maxLength={8000} value={text}
          placeholder="Write the directive the agent follows on every iteration." onChange={(e) => setText(e.target.value)} />
      </div>
      <fieldset className="rx-field" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
        <legend className="rx-label">In policies</legend>
        <p className="rx-hint">A rule can be in several policies. With none, it is added only where you pick it on a work item.</p>
        {catalog.policies.length === 0
          ? <p className="rx-hint">No policies yet.</p>
          : (
            <div className="rx-checks">
              {catalog.policies.map((p) => (
                <label key={p.id} className="rx-check">
                  <input type="checkbox" checked={policyIds.includes(p.id)} onChange={() => setPolicyIds(toggle(policyIds, p.id))} />
                  <span>{p.name}</span>
                  <span className="rx-hint">{p.ruleIds.length} rule{p.ruleIds.length === 1 ? '' : 's'}</span>
                </label>
              ))}
            </div>
          )}
      </fieldset>
      <label className="rx-check" style={{ marginBottom: 12 }}>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <span>Enabled — a disabled rule reaches no work item</span>
      </label>
      <Actions busy={busy} ready={!!title.trim() && !!text.trim()} saveLabel="Save rule" onCancel={onCancel}
        onSave={() => onSave({ title: title.trim(), text: text.trim(), enabled, policyIds })} />
    </>
  );
}

/** A policy: name, what it is for, its rules, and the tags that attach it automatically. */
export function PolicyForm({ initial, catalog, busy, onSave, onCancel }: {
  initial: RulePolicyInput;
  catalog: RuleCatalog;
  busy: boolean;
  onSave: (policy: RulePolicyInput) => void;
  onCancel: () => void;
}) {
  const id = useId();
  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description);
  const [ruleIds, setRuleIds] = useState(initial.ruleIds);
  const [tagIds, setTagIds] = useState(initial.tagIds);
  const [enabled, setEnabled] = useState(initial.enabled);
  return (
    <>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-name`}>Name</label>
        <input id={`${id}-name`} className="rx-input" value={name} maxLength={80}
          placeholder="e.g. Harness gate" onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-desc`}>What it is for</label>
        <input id={`${id}-desc`} className="rx-input" value={description}
          placeholder="e.g. Work that touches the harness gate or needs lane runs" onChange={(e) => setDescription(e.target.value)} />
      </div>
      <fieldset className="rx-field" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
        <legend className="rx-label">Rules in this policy</legend>
        {catalog.rules.length === 0
          ? <p className="rx-hint">No rules yet. Create rules first, or add them to this policy later.</p>
          : (
            <div className="rx-checks">
              {catalog.rules.map((r) => (
                <label key={r.id} className="rx-check">
                  <input type="checkbox" checked={ruleIds.includes(r.id)} onChange={() => setRuleIds(toggle(ruleIds, r.id))} />
                  <span>{r.title}</span>
                  {!r.enabled && <span className="rx-hint">disabled</span>}
                </label>
              ))}
            </div>
          )}
      </fieldset>
      <fieldset className="rx-field" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
        <legend className="rx-label">Apply automatically where these tags match (optional)</legend>
        <p className="rx-hint">Without a tag, the policy applies only where you pick it on a work item.</p>
        {catalog.tags.length === 0
          ? <p className="rx-hint">No tags yet.</p>
          : (
            <div className="rx-checks">
              {catalog.tags.map((t) => (
                <label key={t.id} className="rx-check">
                  <input type="checkbox" checked={tagIds.includes(t.id)} onChange={() => setTagIds(toggle(tagIds, t.id))} />
                  <span>{t.name}</span>
                  <span className="rx-hint">{tagScope(t)}</span>
                </label>
              ))}
            </div>
          )}
      </fieldset>
      <label className="rx-check" style={{ marginBottom: 12 }}>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <span>Enabled — a disabled policy applies nowhere</span>
      </label>
      <Actions busy={busy} ready={!!name.trim()} saveLabel="Save policy" onCancel={onCancel}
        onSave={() => onSave({ name: name.trim(), description: description.trim(), ruleIds, tagIds, enabled })} />
    </>
  );
}

/** A tag: which work items it matches (repos, kinds, phrases, patterns). */
export function TagForm({ initial, busy, onSave, onCancel }: {
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
  return (
    <>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-name`}>Name</label>
        <input id={`${id}-name`} className="rx-input" value={name} maxLength={80}
          placeholder="e.g. full harness gate" onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-desc`}>What it means</label>
        <input id={`${id}-desc`} className="rx-input" value={description}
          placeholder="e.g. The work needs the full harness gate or a lane run" onChange={(e) => setDescription(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-repos`}>Repos</label>
        <input id={`${id}-repos`} className="rx-input" value={repos}
          placeholder="e.g. agent-plugin (comma-separated; empty = every repo)" onChange={(e) => setRepos(e.target.value)} />
      </div>
      <fieldset className="rx-field" style={{ border: 'none', padding: 0, margin: '0 0 16px' }}>
        <legend className="rx-label">Kinds of work</legend>
        <div className="rx-inline">
          {([['impl', 'Implementation'], ['qa-e2e', 'E2E']] as const).map(([c, label]) => (
            <label key={c} className="rx-check">
              <input type="checkbox" checked={cats.includes(c)} onChange={() => setCats(toggle(cats, c))} />
              <span>{label}</span>
            </label>
          ))}
        </div>
        <p className="rx-hint">None ticked = both.</p>
      </fieldset>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-phrases`}>Phrases in the work item text</label>
        <textarea id={`${id}-phrases`} className="rx-textarea" style={{ minHeight: 96 }} value={phrases}
          placeholder={'One per line, e.g.\nboth lanes\ntools/harness-gate'} onChange={(e) => setPhrases(e.target.value)} />
        <p className="rx-hint">Any one is enough; case does not matter. No phrase and no pattern = every work item in the repos above.</p>
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-patterns`}>Regular expressions (optional)</label>
        <textarea id={`${id}-patterns`} className="rx-textarea" style={{ minHeight: 64 }} value={patterns}
          placeholder="One per line, e.g. lanes/(win|wsl)" onChange={(e) => setPatterns(e.target.value)} />
      </div>
      <Actions busy={busy} ready={!!name.trim()} saveLabel="Save tag" onCancel={onCancel}
        onSave={() => onSave({ name: name.trim(), description: description.trim(), repoIds: items(repos), categories: cats, phrases: lines(phrases), patterns: lines(patterns) })} />
    </>
  );
}