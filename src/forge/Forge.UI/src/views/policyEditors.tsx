// Phase 108 — the tag editor, shown inside the Policies page detail panel
// (views/policyPanels.tsx). Styled with the app theme (styles/rules.css).

import { useId, useState } from 'react';
import type { RuleTagInput } from '@/api/forgeApi';
import { items, lines } from '@/swarm/ruleCatalogText';

function Actions({ busy, ready, saveLabel, cancelLabel, onCancel, onSave }: {
  busy: boolean; ready: boolean; saveLabel: string; cancelLabel: string; onCancel: () => void; onSave: () => void;
}) {
  return (
    <div className="rx-actions">
      <button type="button" className="btn btn-secondary" onClick={onCancel}>{cancelLabel}</button>
      <button type="button" className="btn btn-primary" disabled={busy || !ready} onClick={onSave}>
        {busy ? 'Saving…' : saveLabel}
      </button>
    </div>
  );
}

function toggle(list: string[], id: string): string[] {
  return list.includes(id) ? list.filter((x) => x !== id) : [...list, id];
}

/** A tag: which work items it matches (repos, kinds, phrases, patterns). */
export function TagForm({ initial, busy, onSave, onCancel, cancelLabel = 'Revert' }: {
  initial: RuleTagInput;
  busy: boolean;
  cancelLabel?: string;
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
      <Actions busy={busy} ready={!!name.trim()} saveLabel="Save tag" cancelLabel={cancelLabel} onCancel={onCancel}
        onSave={() => onSave({ name: name.trim(), description: description.trim(), repoIds: items(repos), categories: cats, phrases: lines(phrases), patterns: lines(patterns) })} />
    </>
  );
}