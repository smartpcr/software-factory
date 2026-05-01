// forge_implementation.md Phase 28 — story-driven spawn dialog.
//
// Replaces the ad-hoc "New workstream" flow when the operator wants to start
// a multi-team orchestrated delivery from an upstream Feature / PBI:
//   Step 1 — search ADO features / PBIs (filtered by area path / state)
//   Step 2 — confirm scope and target repo, then click Spawn
// No manual branch / worktree / epic fields — every value is derived from
// the selected story (28.9).

import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useProductsStore } from '@/state/useProductsStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import type { WorkItem } from '@/workitems/workItemTypes';
import type { Repo } from '@/types';

interface Props {
  open: boolean;
  onClose: () => void;
  onSpawned?: (storyId: string) => void;
}

type Step = 'select' | 'confirm';

export function SpawnStoryModal({ open, onClose, onSpawned }: Props) {
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const repos = useMemo<Repo[]>(() => {
    const all: Repo[] = [];
    for (const list of Object.values(reposByProduct)) all.push(...list);
    return all;
  }, [reposByProduct]);

  const allWorkItems = useWorkItemsStore((s) => s.items);
  const refreshWorkItems = useWorkItemsStore((s) => s.refresh);

  const [step, setStep] = useState<Step>('select');
  const [search, setSearch] = useState('');
  const [areaPathFilter, setAreaPathFilter] = useState('');
  const [stateFilter, setStateFilter] = useState('');
  const [selected, setSelected] = useState<WorkItem | null>(null);
  const [repoId, setRepoId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setStep('select');
    setSearch('');
    setAreaPathFilter('');
    setStateFilter('');
    setSelected(null);
    setRepoId(null);
    setErr(null);
    void refreshWorkItems();
  }, [open, refreshWorkItems]);

  // Stories = features / epics / pbis. We surface the dev-track containers
  // that match the spec's "Feature / Epic / PBI" notion.
  const stories = useMemo(() => {
    return allWorkItems.filter((w) => w.type === 'feature' || w.type === 'epic');
  }, [allWorkItems]);

  const areaPathOptions = useMemo(() => {
    const set = new Set<string>();
    for (const s of stories) if (s.areaPath) set.add(s.areaPath);
    return [...set].sort();
  }, [stories]);

  const stateOptions = useMemo(() => {
    const set = new Set<string>();
    for (const s of stories) if (s.state) set.add(s.state);
    return [...set].sort();
  }, [stories]);

  const filteredStories = useMemo(() => {
    const q = search.trim().toLowerCase();
    return stories.filter((s) => {
      if (q && !s.title.toLowerCase().includes(q) && !s.id.toLowerCase().includes(q)) return false;
      if (areaPathFilter && s.areaPath !== areaPathFilter) return false;
      if (stateFilter && s.state !== stateFilter) return false;
      return true;
    });
  }, [stories, search, areaPathFilter, stateFilter]);

  function pickStory(s: WorkItem) {
    setSelected(s);
    // Auto-select the story's repo when there is exactly one.
    setRepoId(s.repoId ?? (repos.length === 1 ? repos[0].id : null));
    setStep('confirm');
  }

  async function spawn() {
    if (!selected) return;
    setBusy(true);
    setErr(null);
    try {
      await forgeApi.spawnStory(selected.id, repoId ?? null);
      setBusy(false);
      onSpawned?.(selected.id);
      onClose();
    } catch (e: unknown) {
      setBusy(false);
      const msg = e instanceof Error ? e.message : 'Spawn failed';
      setErr(msg);
    }
  }

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={() => { if (!busy) onClose(); }}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        style={{ maxWidth: 720, width: '90%' }}
      >
        <h3 className="modal-title">
          {step === 'select' ? 'Spawn story — pick a Feature / PBI' : 'Spawn story — confirm scope'}
        </h3>

        {step === 'select' && (
          <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 160px 140px', gap: 8 }}>
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search title or id…"
                autoFocus
              />
              <select value={areaPathFilter} onChange={(e) => setAreaPathFilter(e.target.value)}>
                <option value="">Area path: All</option>
                {areaPathOptions.map((a) => <option key={a} value={a}>{a}</option>)}
              </select>
              <select value={stateFilter} onChange={(e) => setStateFilter(e.target.value)}>
                <option value="">State: All</option>
                {stateOptions.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </div>
            <div
              style={{
                maxHeight: 320,
                overflowY: 'auto',
                border: '1px solid var(--border-strong)',
                borderRadius: 'var(--radius-sm)',
                background: 'var(--bg-surface)',
              }}
            >
              {filteredStories.length === 0 && (
                <div style={{ padding: 16, color: 'var(--text-tertiary)', fontSize: 12 }}>
                  No stories match your filters.
                </div>
              )}
              {filteredStories.map((s) => (
                <button
                  key={s.id}
                  type="button"
                  onClick={() => pickStory(s)}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '90px 1fr 100px 90px',
                    width: '100%',
                    gap: 8,
                    padding: '8px 12px',
                    border: 0,
                    borderBottom: '1px solid var(--border-subtle)',
                    background: 'transparent',
                    cursor: 'pointer',
                    textAlign: 'left',
                    fontSize: 12,
                  }}
                >
                  <span style={{ fontFamily: 'monospace', color: 'var(--text-tertiary)' }}>{s.id}</span>
                  <span style={{ color: 'var(--text-primary)' }}>{s.title}</span>
                  <span style={{ color: 'var(--text-tertiary)' }}>{s.state}</span>
                  <span style={{ color: 'var(--text-tertiary)' }}>{s.areaPath}</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {step === 'confirm' && selected && (
          <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
            <Field label="Story id">
              <span style={{ fontFamily: 'monospace' }}>{selected.id}</span>
            </Field>
            <Field label="Title">{selected.title}</Field>
            <Field label="Description">
              <span style={{ whiteSpace: 'pre-wrap', maxHeight: 120, overflow: 'auto', display: 'block' }}>
                {selected.descriptionMd || <em style={{ color: 'var(--text-tertiary)' }}>(none)</em>}
              </span>
            </Field>
            <Field label="Repo">
              <select value={repoId ?? ''} onChange={(e) => setRepoId(e.target.value || null)}>
                <option value="">— pick a repo —</option>
                {repos.map((r: Repo) => (
                  <option key={r.id} value={r.id}>{r.displayName ?? r.id}</option>
                ))}
              </select>
            </Field>
            <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
              Branches, worktrees, and the planning epic are derived automatically from the
              selected story. The architect team starts on <code>story/{selected.id}/plan</code>.
            </div>
            {err && <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>{err}</div>}
          </div>
        )}

        <div className="modal-actions">
          {step === 'select' && (
            <>
              <button className="btn btn-secondary" onClick={onClose}>Cancel</button>
            </>
          )}
          {step === 'confirm' && (
            <>
              <button className="btn btn-secondary" onClick={() => setStep('select')} disabled={busy}>Back</button>
              <button
                className="btn btn-primary"
                onClick={() => void spawn()}
                disabled={busy || !selected || !repoId}
              >
                {busy ? 'Spawning…' : 'Spawn'}
              </button>
            </>
          )}
        </div>
      </div>
    </>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4, fontSize: 11, color: 'var(--text-secondary)' }}>
      <span>{label}</span>
      <span
        style={{
          display: 'block',
          padding: '6px 10px',
          borderRadius: 'var(--radius-sm)',
          border: '1px solid var(--border-strong)',
          background: 'var(--bg-surface)',
          color: 'var(--text-primary)',
          fontSize: 12,
        }}
      >
        {children}
      </span>
    </label>
  );
}
