import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useProductsStore } from '@/state/useProductsStore';

interface Props {
  open: boolean;
  onClose: () => void;
}

// Product-scoped story creation modal. Creates under the active product.
export function NewStoryModal({ open, onClose }: Props) {
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const activeProductId = useProductsStore((s) => s.activeProductId);

  useEffect(() => {
    if (!open) return;
    setName('');
    setBusy(false);
    setErr(null);
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanName = name.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    if (!activeProductId) { setErr('No active product selected'); return; }
    setBusy(true);
    setErr(null);
    try {
      const story = await forgeApi.createProductStory(activeProductId, { name: cleanName });
      useToastStore.getState().show(`Story "${story.name}" created`, 'success');
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Create failed';
      setErr(msg);
    } finally {
      setBusy(false);
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey && !busy) {
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
        aria-labelledby="new-story-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 420 }}
      >
        <h3 className="modal-title" id="new-story-title" style={{ margin: '0 0 12px' }}>
          New story
        </h3>
        <p className="modal-text" style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-tertiary)' }}>
          A story groups related cards on the board. Both plan tasks and coding tasks can be associated with a story.
        </p>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <label style={{ display: 'grid', gap: 5 }}>
            <span style={{ fontSize: 11, fontWeight: 600, letterSpacing: 0.2, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
              Name
            </span>
            <input
              ref={nameRef}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Multi-tenant authentication"
              maxLength={80}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
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
            disabled={busy || !name.trim()}
          >
            {busy ? 'Creating…' : 'Create story'}
          </button>
        </div>
      </div>
    </>
  );
}

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
