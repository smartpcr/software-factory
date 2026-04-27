import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { useSprintsStore, selectIterations } from '@/state/useSprintsStore';
import { StoryPicker } from '@/board/StoryPicker';
import type { ProductStory } from '@/types';
import type { WorkItemType, WorkItemPriority } from '@/workitems/workItemTypes';
import { typeLabel, trackOf } from '@/workitems/workItemTypes';

interface Props {
  open: boolean;
  onClose: () => void;
}

const DEV_TYPES: WorkItemType[] = ['epic', 'feature', 'task', 'bug'];

export function NewWorkItemModal({ open, onClose }: Props) {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [type, setType] = useState<WorkItemType>('task');
  const [priority, setPriority] = useState<WorkItemPriority>('med');
  const [repoId, setRepoId] = useState<string>('');
  const [iterationRef, setIterationRef] = useState<string>('');
  const [featureRef, setFeatureRef] = useState<string>('');
  const [storyPickerOpen, setStoryPickerOpen] = useState(false);
  const [stories, setStories] = useState<ProductStory[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const titleRef = useRef<HTMLInputElement | null>(null);

  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);

  const ensureSprints = useSprintsStore((s) => s.ensure);
  const iterations = useSprintsStore(selectIterations(activeProductId ?? ''));

  // Load repos for active product.
  useEffect(() => {
    if (activeProductId && !reposByProduct[activeProductId]) {
      void loadReposFor(activeProductId);
    }
  }, [activeProductId, reposByProduct, loadReposFor]);

  // Load iterations for active product.
  useEffect(() => {
    if (activeProductId) void ensureSprints(activeProductId);
  }, [activeProductId, ensureSprints]);

  // Load stories for active product.
  useEffect(() => {
    if (!open || !activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setStories(s); })
      .catch(() => { /* best effort */ });
    return () => { cancelled = true; };
  }, [open, activeProductId]);

  const repos = useMemo(
    () => (activeProductId ? reposByProduct[activeProductId] ?? [] : []),
    [activeProductId, reposByProduct],
  );

  // Auto-select first repo when repos load.
  useEffect(() => {
    if (open && repos.length > 0 && !repoId) {
      setRepoId(repos[0].id);
    }
  }, [open, repos, repoId]);

  // Auto-select current iteration.
  useEffect(() => {
    if (open && iterations.length > 0 && !iterationRef) {
      const current = iterations.find((i) => i.state === 'current');
      if (current) setIterationRef(current.id);
    }
  }, [open, iterations, iterationRef]);

  // Reset form on open.
  useEffect(() => {
    if (!open) return;
    setTitle('');
    setDescription('');
    setType('task');
    setPriority('med');
    setRepoId('');
    setIterationRef('');
    setFeatureRef('');
    setStoryPickerOpen(false);
    setStories([]);
    setBusy(false);
    setErr(null);
    const t = window.requestAnimationFrame(() => titleRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open]);

  // Esc to close.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanTitle = title.trim();
    if (!cleanTitle) { setErr('Title is required'); return; }
    if (!activeProductId) { setErr('No active product selected'); return; }
    setBusy(true);
    setErr(null);
    try {
      const id = `WI-${Date.now()}${Math.floor(Math.random() * 1000)}`;
      const created = await forgeApi.createWorkItem({
        id,
        track: trackOf(type),
        type,
        title: cleanTitle,
        descriptionMd: description.trim() || undefined,
        repoId: repoId || null,
        featureRef: featureRef || null,
        state: 'proposed',
        priority,
        iterationRef: iterationRef || undefined,
        tags: [],
      });
      useWorkItemsStore.getState().upsertFromRealtime(created);
      useToastStore.getState().show(`Work item "${cleanTitle}" created`, 'success');
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Create failed';
      setErr(msg);
    } finally {
      setBusy(false);
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey && !busy && e.target instanceof HTMLInputElement) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={onClose}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-wi-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 480 }}
      >
        <h3 className="modal-title" id="new-wi-title" style={{ margin: '0 0 12px' }}>
          New work item
        </h3>
        <p className="modal-text" style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-tertiary)' }}>
          Create a dev-track work item under the active product.
          {activeProductId ? '' : ' Select a product first.'}
        </p>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          {/* Title */}
          <label style={labelStyle}>
            <span style={labelTextStyle}>Title</span>
            <input
              ref={titleRef}
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="e.g. Add retry logic for token refresh"
              maxLength={200}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
            />
          </label>

          {/* Type + Priority row */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <label style={labelStyle}>
              <span style={labelTextStyle}>Type</span>
              <select
                value={type}
                onChange={(e) => setType(e.target.value as WorkItemType)}
                disabled={busy}
                style={inputStyle}
              >
                {DEV_TYPES.map((t) => (
                  <option key={t} value={t}>{typeLabel(t)}</option>
                ))}
              </select>
            </label>
            <label style={labelStyle}>
              <span style={labelTextStyle}>Priority</span>
              <select
                value={priority}
                onChange={(e) => setPriority(e.target.value as WorkItemPriority)}
                disabled={busy}
                style={inputStyle}
              >
                <option value="high">High</option>
                <option value="med">Medium</option>
                <option value="low">Low</option>
              </select>
            </label>
          </div>

          {/* Repo */}
          <label style={labelStyle}>
            <span style={labelTextStyle}>Repository</span>
            <select
              value={repoId}
              onChange={(e) => setRepoId(e.target.value)}
              disabled={busy || repos.length === 0}
              style={inputStyle}
            >
              {repos.length === 0 && <option value="">No repos available</option>}
              {repos.map((r) => (
                <option key={r.id} value={r.id}>{r.displayName}</option>
              ))}
            </select>
          </label>

          {/* Story (featureRef) */}
          <div style={{ ...labelStyle, position: 'relative' }}>
            <span style={labelTextStyle}>User story</span>
            <button
              type="button"
              disabled={busy}
              onClick={() => setStoryPickerOpen(!storyPickerOpen)}
              style={{
                ...inputStyle,
                cursor: 'pointer',
                textAlign: 'left',
                color: featureRef ? 'var(--text-primary)' : 'var(--text-tertiary)',
                fontStyle: featureRef ? 'normal' : 'italic',
              }}
            >
              {featureRef
                ? (() => { const s = stories.find((st) => st.id === featureRef); return s ? s.name : featureRef; })()
                : 'No story — click to assign or create'}
            </button>
            <StoryPicker
              open={storyPickerOpen}
              currentStory={featureRef}
              onSelect={(storyId) => {
                setFeatureRef(storyId);
                // Refresh stories list in case a new one was created inline.
                if (activeProductId) {
                  forgeApi.listProductStories(activeProductId)
                    .then((s) => setStories(s))
                    .catch(() => { /* best effort */ });
                }
              }}
              onClose={() => setStoryPickerOpen(false)}
            />
          </div>

          {/* Iteration */}
          {iterations.length > 0 && (
            <label style={labelStyle}>
              <span style={labelTextStyle}>Iteration</span>
              <select
                value={iterationRef}
                onChange={(e) => setIterationRef(e.target.value)}
                disabled={busy}
                style={inputStyle}
              >
                <option value="">None</option>
                {iterations.map((it) => (
                  <option key={it.id} value={it.id}>
                    {it.name}{it.state === 'current' ? ' (current)' : ''}
                  </option>
                ))}
              </select>
            </label>
          )}

          {/* Description */}
          <label style={labelStyle}>
            <span style={labelTextStyle}>Description (optional)</span>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Markdown description…"
              rows={4}
              disabled={busy}
              style={{ ...inputStyle, resize: 'vertical', fontFamily: 'inherit' }}
            />
          </label>

          {err && (
            <div role="alert" style={errStyle}>
              <span style={{ marginRight: 6 }}>⚠</span>
              {err}
            </div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="btn btn-primary"
            onClick={() => void submit()}
            disabled={busy || !title.trim() || !activeProductId}
          >
            {busy ? 'Creating…' : 'Create'}
          </button>
        </div>
      </div>
    </>
  );
}

const labelStyle: React.CSSProperties = {
  display: 'grid',
  gap: 5,
};

const labelTextStyle: React.CSSProperties = {
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: 0.2,
  textTransform: 'uppercase',
  color: 'var(--text-secondary)',
};

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 11px',
  fontSize: 13,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const errStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};
