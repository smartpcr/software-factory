// forge_implementation.md Phase 28 — story-driven spawn dialog.
//
// The operator authors a Story on Plan & Design (name + description + points)
// and then spawns it from here:
//   Step 1 — pick a story from the active product's authored stories
//   Step 2 — confirm scope and target repo, then click Spawn
// The story's description becomes the architect agent's user prompt; points
// size the work for sprint planning.

import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useProductsStore } from '@/state/useProductsStore';
import type { Repo, ProductStory } from '@/types';

interface Props {
  open: boolean;
  onClose: () => void;
  onSpawned?: (storyId: string) => void;
}

type Step = 'select' | 'confirm';

export function SpawnStoryModal({ open, onClose, onSpawned }: Props) {
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const products = useProductsStore((s) => s.products);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);

  const activeProduct = useMemo(
    () => products.find((p) => p.id === activeProductId) ?? null,
    [products, activeProductId],
  );

  const repos = useMemo<Repo[]>(
    () => (activeProductId ? reposByProduct[activeProductId] ?? [] : []),
    [activeProductId, reposByProduct],
  );

  const [stories, setStories] = useState<ProductStory[]>([]);
  const [loading, setLoading] = useState(false);

  const [step, setStep] = useState<Step>('select');
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [selected, setSelected] = useState<ProductStory | null>(null);
  const [repoId, setRepoId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [uploadIds, setUploadIds] = useState<string[]>([]);
  // Phase 45 — operator-chosen base branch for every PR this spawn produces.
  // Empty input = default "main"; non-default values are branched from main
  // by the orchestrator on first drive.
  const [baseBranch, setBaseBranch] = useState('');

  // Load stories + repos when the modal opens.
  useEffect(() => {
    if (!open || !activeProductId) return;
    setStep('select');
    setSearch('');
    setStatusFilter('');
    setSelected(null);
    setRepoId(null);
    setErr(null);
    setFiles([]);
    setUploadIds([]);
    setBaseBranch('');

    let cancelled = false;
    setLoading(true);
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setStories(s); })
      .catch((e) => {
        if (cancelled) return;
        setErr(e instanceof Error ? e.message : 'failed to load stories');
        setStories([]);
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    if (!reposByProduct[activeProductId]) {
      void loadReposFor(activeProductId);
    }

    return () => { cancelled = true; };
  }, [open, activeProductId, reposByProduct, loadReposFor]);

  const statusOptions = useMemo(() => {
    const set = new Set<string>();
    for (const s of stories) if (s.status) set.add(s.status);
    return [...set].sort();
  }, [stories]);

  const filteredStories = useMemo(() => {
    const q = search.trim().toLowerCase();
    return stories.filter((s) => {
      if (statusFilter && s.status !== statusFilter) return false;
      if (!q) return true;
      return (
        s.name.toLowerCase().includes(q) ||
        s.id.toLowerCase().includes(q) ||
        (s.description ?? '').toLowerCase().includes(q)
      );
    });
  }, [stories, search, statusFilter]);

  function pickStory(s: ProductStory) {
    setSelected(s);
    // Auto-select repo when there's exactly one in this product.
    setRepoId(repos.length === 1 ? repos[0].id : null);
    setStep('confirm');
  }

  async function spawn() {
    if (!selected) return;
    setBusy(true);
    setErr(null);
    try {
      // Phase 43 — upload files first, then pass uploadIds to spawn.
      let ids = uploadIds;
      if (files.length > 0 && ids.length === 0) {
        const result = await forgeApi.uploadStoryAttachments(files);
        ids = [result.uploadId];
        setUploadIds(ids);
      }
      const trimmedBase = baseBranch.trim();
      await forgeApi.spawnStory(
        selected.id,
        repoId ?? null,
        ids.length > 0 ? ids : undefined,
        trimmedBase.length > 0 ? trimmedBase : undefined,
      );
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
        style={{ maxWidth: 760, width: '90%' }}
      >
        <h3 className="modal-title">
          {step === 'select'
            ? `Spawn story — pick a story${activeProduct ? ` from ${activeProduct.name}` : ''}`
            : 'Spawn story — confirm scope'}
        </h3>

        {step === 'select' && (
          <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
            <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginTop: -4 }}>
              Step 1 of 2 — pick a story below to continue. The next step lets you confirm scope + repo and click <b>Spawn</b>.
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 200px', gap: 8 }}>
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search title, id, or description…"
                autoFocus
              />
              <select
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}
                title="Filter by the story's authored-status (planning / approved / done) — separate from the spawn lifecycle."
              >
                <option value="">Authored status: All</option>
                {statusOptions.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </div>
            <div
              style={{
                maxHeight: 360,
                overflowY: 'auto',
                border: '1px solid var(--border-strong)',
                borderRadius: 'var(--radius-sm)',
                background: 'var(--bg-surface)',
              }}
            >
              {!activeProductId && (
                <div style={emptyMsg}>No active product selected.</div>
              )}
              {activeProductId && loading && (
                <div style={emptyMsg}>Loading stories…</div>
              )}
              {activeProductId && !loading && filteredStories.length === 0 && (
                <div style={emptyMsg}>
                  {stories.length === 0
                    ? 'No stories yet. Create one on Plan & Design via "+ New story".'
                    : 'No stories match your filters.'}
                </div>
              )}
              {filteredStories.map((s) => (
                <button
                  key={s.id}
                  type="button"
                  onClick={() => pickStory(s)}
                  style={rowStyle}
                  title="Click to pick this story and continue to confirm + spawn"
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, alignItems: 'baseline' }}>
                    <span style={{ color: 'var(--text-primary)', fontWeight: 600, fontSize: 13 }}>{s.name}</span>
                    <span style={{ display: 'inline-flex', gap: 8, alignItems: 'baseline', color: 'var(--text-tertiary)', fontSize: 11 }}>
                      <span style={{ fontFamily: 'var(--font-mono)' }}>{s.id}</span>
                      <span>·</span>
                      <span>{s.status}</span>
                      <span>·</span>
                      <span>{s.points} pt</span>
                      <span style={{ marginLeft: 6, color: 'var(--accent)', fontWeight: 600 }}>Pick →</span>
                    </span>
                  </div>
                  {s.description && (
                    <div style={descStyle}>
                      {s.description.length > 220 ? `${s.description.slice(0, 220)}…` : s.description}
                    </div>
                  )}
                </button>
              ))}
            </div>
            {err && <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>{err}</div>}
          </div>
        )}

        {step === 'confirm' && selected && (
          <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
            <Field label="Story id"><span style={{ fontFamily: 'var(--font-mono)' }}>{selected.id}</span></Field>
            <Field label="Title">{selected.name}</Field>
            <Field label="Points">{selected.points}</Field>
            <Field label="Architect prompt (description)">
              <span style={{ whiteSpace: 'pre-wrap', maxHeight: 160, overflow: 'auto', display: 'block' }}>
                {selected.description || <em style={{ color: 'var(--text-tertiary)' }}>(no description — the architect will get an empty prompt)</em>}
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
            <Field label="Base branch (optional, default: main)">
              <input
                value={baseBranch}
                onChange={(e) => setBaseBranch(e.target.value)}
                placeholder="main"
                style={{ width: '100%', fontFamily: 'var(--font-mono)', fontSize: 12 }}
              />
              <span style={{ display: 'block', fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 4 }}>
                Every PR this spawn produces (plan, workstreams, QA, step-defs)
                targets this branch. Non-default values are branched from{' '}
                <code>main</code> automatically.
              </span>
            </Field>
            <Field label="Reference documents (optional)">
              <div style={{ display: 'grid', gap: 6 }}>
                <input
                  type="file"
                  multiple
                  accept=".md,.txt,.json,.csv,.yaml,.yml,.toml,.xml"
                  onChange={(e) => {
                    const picked = e.target.files ? Array.from(e.target.files) : [];
                    setFiles(picked);
                    setUploadIds([]);
                  }}
                  style={{ fontSize: 11 }}
                />
                {files.length > 0 && (
                  <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
                    {files.length} file{files.length > 1 ? 's' : ''} selected
                    ({(files.reduce((s, f) => s + f.size, 0) / 1024).toFixed(1)} KB)
                    — uploaded before spawn so architect agents can read them.
                  </div>
                )}
              </div>
            </Field>
            <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
              Branches and worktrees are derived automatically. The architect crew starts on
              {' '}<code>story/{selected.id}/plan</code> using the description above as its user prompt.
            </div>
            {err && <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>{err}</div>}
          </div>
        )}

        <div className="modal-actions">
          {step === 'select' && (
            <button className="btn btn-secondary" onClick={onClose}>Cancel</button>
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

const rowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  width: '100%',
  padding: '10px 12px',
  border: 0,
  borderBottom: '1px solid var(--border-subtle)',
  background: 'transparent',
  cursor: 'pointer',
  textAlign: 'left',
};

const descStyle: React.CSSProperties = {
  fontSize: 11.5,
  color: 'var(--text-secondary)',
  lineHeight: 1.45,
  whiteSpace: 'pre-wrap',
  overflow: 'hidden',
};

const emptyMsg: React.CSSProperties = {
  padding: 16,
  color: 'var(--text-tertiary)',
  fontSize: 12,
};
