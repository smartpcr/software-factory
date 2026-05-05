import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useProductsStore } from '@/state/useProductsStore';
import type { ProductStory } from '@/types';

interface Props {
  open: boolean;
  /** Current story id (may be empty). */
  currentStory: string;
  /** Override product id; defaults to the active product. */
  productId?: string;
  onSelect: (storyId: string) => void;
  onClose: () => void;
}

// Product-scoped story picker. Fetches stories from
// GET /api/products/{id}/stories, with inline create.
export function StoryPicker({ open, currentStory, productId: propProductId, onSelect, onClose }: Props) {
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const productId = propProductId ?? activeProductId ?? '';

  const [stories, setStories] = useState<ProductStory[]>([]);
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);

  // Load stories when opened.
  useEffect(() => {
    if (!open || !productId) return;
    let cancelled = false;
    forgeApi.listProductStories(productId)
      .then((s) => { if (!cancelled) setStories(s); })
      .catch(() => { /* best effort */ });
    return () => { cancelled = true; };
  }, [open, productId]);

  useEffect(() => {
    if (!open) return;
    setQuery('');
    setBusy(false);
    const t = window.requestAnimationFrame(() => inputRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') onClose(); }
    function onClick(e: MouseEvent) {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) onClose();
    }
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onClick);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onClick);
    };
  }, [open, onClose]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return stories;
    return stories.filter((s) =>
      s.id.toLowerCase().includes(q) || s.name.toLowerCase().includes(q),
    );
  }, [stories, query]);

  async function createAndSelect() {
    const name = query.trim();
    if (!name || !productId) return;
    setBusy(true);
    try {
      const result = await forgeApi.createProductStory(productId, { name });
      useToastStore.getState().show(`Story "${result.story.name}" created`, 'success');
      onSelect(result.story.id);
      onClose();
    } catch (e) {
      useToastStore.getState().show(
        e instanceof Error ? e.message : 'Create failed',
        'danger',
      );
    } finally {
      setBusy(false);
    }
  }

  if (!open) return null;

  return (
    <div ref={wrapRef} style={popStyle}>
      <div style={{ padding: '8px 10px 6px', borderBottom: '1px solid var(--border)' }}>
        <input
          ref={inputRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search or create a story…"
          disabled={busy}
          style={searchStyle}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && filtered.length === 0 && query.trim()) {
              e.preventDefault();
              void createAndSelect();
            }
          }}
        />
      </div>
      <div style={{ maxHeight: 200, overflowY: 'auto' }}>
        <button
          type="button"
          style={rowStyle(!currentStory)}
          onClick={() => { onSelect(''); onClose(); }}
        >
          <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No story</span>
        </button>
        {filtered.map((s) => (
          <button
            key={s.id}
            type="button"
            style={rowStyle(currentStory === s.id)}
            onClick={() => { onSelect(s.id); onClose(); }}
          >
            <span style={{ fontSize: 12 }}>{s.name}</span>
            <span style={{ marginLeft: 'auto', fontSize: 10, color: 'var(--text-tertiary)' }}>{s.status}</span>
          </button>
        ))}
        {filtered.length === 0 && query.trim() && (
          <button
            type="button"
            style={{ ...rowStyle(false), color: 'var(--accent)', fontWeight: 600 }}
            onClick={() => void createAndSelect()}
            disabled={busy}
          >
            + Create "{query.trim()}"
          </button>
        )}
      </div>
    </div>
  );
}

const popStyle: React.CSSProperties = {
  position: 'absolute',
  top: '100%',
  left: 0,
  zIndex: 900,
  width: 280,
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  boxShadow: '0 6px 20px rgba(0,0,0,0.35)',
  marginTop: 4,
};

const searchStyle: React.CSSProperties = {
  width: '100%',
  padding: '6px 8px',
  fontSize: 12,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-base)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
};

function rowStyle(active: boolean): React.CSSProperties {
  return {
    display: 'flex',
    alignItems: 'center',
    width: '100%',
    padding: '6px 10px',
    background: active ? 'var(--accent-bg, rgba(99,102,241,0.12))' : 'transparent',
    border: 'none',
    color: 'var(--text-primary)',
    font: 'inherit',
    fontSize: 12,
    textAlign: 'left',
    cursor: 'pointer',
  };
}
