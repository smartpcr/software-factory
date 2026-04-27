import { useEffect, useMemo, useRef, useState } from 'react';
import { useStateStore } from '@/state/useStateStore';
import { sampleState } from '@/sampleData';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

interface Props {
  open: boolean;
  /** Current epic id (may be empty / unknown). */
  currentEpic: string;
  onSelect: (epicId: string) => void;
  onClose: () => void;
}

// Popup for assigning or creating a story. Used from the "No story"
// swimlane header and from the card detail panel.
export function StoryPicker({ open, currentEpic, onSelect, onClose }: Props) {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const reloadState = useStateStore((s) => s.load);

  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return epics;
    return epics.filter((e) =>
      e.id.toLowerCase().includes(q) || e.name.toLowerCase().includes(q),
    );
  }, [epics, query]);

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

  async function createAndSelect() {
    const name = query.trim();
    if (!name) return;
    setBusy(true);
    try {
      const epic = await forgeApi.createEpic({ name });
      useToastStore.getState().show(`Story "${epic.name}" created`, 'success');
      void reloadState();
      onSelect(epic.id);
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
        {/* "No story" option */}
        <button
          type="button"
          style={rowStyle(!currentEpic)}
          onClick={() => { onSelect(''); onClose(); }}
        >
          <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No story</span>
        </button>
        {filtered.map((e) => (
          <button
            key={e.id}
            type="button"
            style={rowStyle(currentEpic === e.id)}
            onClick={() => { onSelect(e.id); onClose(); }}
          >
            <span style={{ fontWeight: 600, fontSize: 11, color: 'var(--text-secondary)' }}>{e.id}</span>
            <span style={{ marginLeft: 6, fontSize: 12 }}>{e.name}</span>
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
