import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import type { ProductStory } from '@/types';

interface Props {
  open: boolean;
  story: ProductStory | null;
  onClose: () => void;
  onSaved?: (story: ProductStory) => void;
}

// Edit name / description / points / status of an existing product story.
// Description is the architect agent's user prompt; keeping it editable
// lets the operator refine instructions before the agent picks the story up.
export function EditStoryModal({ open, story, onClose, onSaved }: Props) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [points, setPoints] = useState<string>('0');
  const [status, setStatus] = useState('planning');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!open || !story) return;
    setName(story.name);
    setDescription(story.description ?? '');
    setPoints(String(story.points ?? 0));
    setStatus(story.status ?? 'planning');
    setBusy(false);
    setErr(null);
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open, story]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    if (!story) return;
    const cleanName = name.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    const ptsNum = Number.parseInt(points, 10);
    if (Number.isNaN(ptsNum) || ptsNum < 0) { setErr('Points must be a non-negative integer'); return; }
    setBusy(true);
    setErr(null);
    try {
      const updated = await forgeApi.updateProductStory(story.productId, story.id, {
        name: cleanName,
        description: description.trim(),
        points: ptsNum,
        status: status.trim() || 'planning',
      });
      useToastStore.getState().show(`Story "${updated.name}" updated`, 'success');
      onSaved?.(updated);
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Update failed';
      setErr(msg);
    } finally {
      setBusy(false);
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && !busy) {
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
        aria-labelledby="edit-story-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 520 }}
      >
        <h3 className="modal-title" id="edit-story-title" style={{ margin: '0 0 4px' }}>
          Edit story
        </h3>
        <div style={{ fontSize: 11, color: 'var(--text-tertiary)', fontFamily: 'var(--font-mono)', marginBottom: 12 }}>
          {story?.id}
        </div>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Name</span>
            <input
              ref={nameRef}
              value={name}
              onChange={(e) => setName(e.target.value)}
              maxLength={120}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
            />
          </label>

          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Description</span>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Describe the goal, constraints, and any context the architect should know."
              rows={6}
              disabled={busy}
              style={textareaStyle}
            />
            <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
              Used as the architect agent's user prompt.
            </span>
          </label>

          <div style={{ display: 'grid', gridTemplateColumns: '120px 1fr', gap: 12 }}>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Points</span>
              <input
                type="number"
                min={0}
                step={1}
                value={points}
                onChange={(e) => setPoints(e.target.value)}
                disabled={busy}
                style={inputStyle}
              />
            </label>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Status</span>
              <input
                value={status}
                onChange={(e) => setStatus(e.target.value)}
                placeholder="planning"
                disabled={busy}
                style={inputStyle}
              />
            </label>
          </div>

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
            disabled={busy || !story || !name.trim()}
          >
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </>
  );
}

const labelStyle: React.CSSProperties = {
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

const textareaStyle: React.CSSProperties = {
  ...inputStyle,
  resize: 'vertical',
  fontFamily: 'inherit',
  lineHeight: 1.45,
};

const errStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};
