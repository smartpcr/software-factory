import { useEffect, useMemo, useRef, useState } from 'react';
import type { ProductStory } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useToastStore } from '@/state/useToastStore';
import { forgeApi } from '@/api/forgeApi';

interface Props {
  storyId: string;
}

type EditableField = 'name' | 'description' | 'points' | 'status';

export function StoryDetailPanel({ storyId }: Props) {
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const cards = useBoardStore((s) => s.cards);
  const setSelectedStory = useBoardStore((s) => s.setSelectedStory);
  const setSelected = useBoardStore((s) => s.setSelected);

  const [story, setStory] = useState<ProductStory | null>(null);
  const [loading, setLoading] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [savingField, setSavingField] = useState<EditableField | null>(null);

  useEffect(() => {
    if (!activeProductId || !storyId) { setStory(null); return; }
    let cancelled = false;
    setLoading(true);
    forgeApi.listProductStories(activeProductId)
      .then((stories) => {
        if (cancelled) return;
        setStory(stories.find((s) => s.id === storyId) ?? null);
      })
      .catch(() => { if (!cancelled) setStory(null); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [activeProductId, storyId]);

  const storyCards = useMemo(
    () => cards.filter((c) => c.epic === storyId),
    [cards, storyId],
  );
  const totalPts = useMemo(() => storyCards.reduce((s, c) => s + c.pts, 0), [storyCards]);
  const doneCards = useMemo(() => storyCards.filter((c) => c.col === 'done'), [storyCards]);

  async function persist(field: EditableField, patch: Partial<Pick<ProductStory, 'name' | 'description' | 'points' | 'status'>>) {
    if (!story) return;
    setSavingField(field);
    try {
      const updated = await forgeApi.updateProductStory(story.productId, story.id, {
        name: patch.name ?? story.name,
        description: patch.description ?? story.description,
        points: patch.points ?? story.points,
        status: patch.status ?? story.status,
      });
      setStory(updated);
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Save failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setSavingField(null);
    }
  }

  async function handleDelete() {
    if (!story) return;
    setDeleting(true);
    try {
      await forgeApi.deleteProductStory(story.productId, story.id);
      useToastStore.getState().show(`Story "${story.name}" deleted`, 'success');
      setConfirmDelete(false);
      setSelectedStory(null);
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Delete failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setDeleting(false);
    }
  }

  return (
    <aside className="detail-panel open" id="story-detail-panel">
      <div className="detail-head">
        <div className="detail-head-row">
          <div className="detail-eyebrow">
            <span>{storyId}</span>
            <span style={{ opacity: 0.4 }}>/</span>
            <span>Story</span>
          </div>
          <button
            className="detail-close"
            title="Close"
            onClick={() => setSelectedStory(null)}
          >
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M3 3 L 11 11 M 11 3 L 3 11" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
          </button>
        </div>

        {story ? (
          <InlineTitle
            value={story.name}
            saving={savingField === 'name'}
            onCommit={(v) => {
              const trimmed = v.trim();
              if (!trimmed || trimmed === story.name) return;
              void persist('name', { name: trimmed });
            }}
          />
        ) : (
          <h2 className="detail-title">{loading ? 'Loading…' : 'Story not found'}</h2>
        )}

        <div className="detail-status-row">
          <span className="detail-status-pill">
            <span className="label">Cards</span>
            <span>{storyCards.length}</span>
          </span>
          <span className="detail-status-pill">
            <span className="label">Done</span>
            <span>{doneCards.length}/{storyCards.length}</span>
          </span>
          <span className="detail-status-pill">
            <span className="label">Points</span>
            <span>{totalPts}{story && story.points > 0 ? ` / ${story.points}` : ''}</span>
          </span>
        </div>
      </div>

      <div className="detail-body">
        <div className="detail-section">
          <div className="detail-meta" style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <div className="detail-meta-cell">
              <div className="detail-meta-label">Status</div>
              <div className="detail-meta-val">
                <InlineInput
                  value={story?.status ?? ''}
                  placeholder="planning"
                  disabled={!story}
                  saving={savingField === 'status'}
                  onCommit={(v) => {
                    const trimmed = v.trim() || 'planning';
                    if (!story || trimmed === story.status) return;
                    void persist('status', { status: trimmed });
                  }}
                />
              </div>
            </div>
            <div className="detail-meta-cell">
              <div className="detail-meta-label">Points</div>
              <div className="detail-meta-val">
                <InlineInput
                  type="number"
                  value={String(story?.points ?? 0)}
                  disabled={!story}
                  saving={savingField === 'points'}
                  onCommit={(v) => {
                    const n = Number.parseInt(v, 10);
                    if (Number.isNaN(n) || n < 0) {
                      useToastStore.getState().show('Points must be a non-negative integer', 'danger');
                      return;
                    }
                    if (!story || n === story.points) return;
                    void persist('points', { points: n });
                  }}
                />
              </div>
            </div>
          </div>
        </div>

        <div className="detail-section">
          <div className="detail-section-title">
            Description
            <span style={{ fontSize: 9.5, color: 'var(--text-tertiary)', marginLeft: 8, fontWeight: 400, textTransform: 'none', letterSpacing: 0 }}>
              Used as the architect agent's user prompt.
            </span>
          </div>
          <InlineTextarea
            value={story?.description ?? ''}
            disabled={!story}
            saving={savingField === 'description'}
            placeholder="Describe the goal, constraints, and any context the architect should know."
            onCommit={(v) => {
              const next = v;
              if (!story || next === story.description) return;
              void persist('description', { description: next });
            }}
          />
        </div>

        <div className="detail-section">
          <div className="detail-section-title">Cards in this story</div>
          {storyCards.length === 0 ? (
            <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
              No cards yet.
            </div>
          ) : (
            <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: 4 }}>
              {storyCards.map((c) => (
                <li key={c.id}>
                  <button
                    type="button"
                    onClick={() => setSelected(c.id)}
                    style={cardRowStyle}
                    title="Open card"
                  >
                    <span style={cardIdStyle}>{c.id}</span>
                    <span style={cardTitleStyle}>{c.title}</span>
                    <span style={{ ...colPill, borderLeftColor: colColor(c.col) }}>{c.col}</span>
                    <span style={ptsStyle}>{c.pts}pt</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <div className="detail-actions">
        <button
          className="btn btn-secondary"
          onClick={() => setConfirmDelete(true)}
          disabled={!story}
        >
          Delete story
        </button>
      </div>

      <div
        className={`modal-backdrop${confirmDelete ? ' show' : ''}`}
        onClick={() => !deleting && setConfirmDelete(false)}
      />
      <div className={`modal${confirmDelete ? ' show' : ''}`} role="dialog" aria-modal="true">
        <div className="modal-icon" style={{ color: 'var(--danger, #ef4444)' }}>
          <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
            <path d="M9 2 L 16 14 H 2 Z" stroke="currentColor" strokeWidth="1.5" fill="none" strokeLinejoin="round" />
            <line x1="9" y1="7" x2="9" y2="10.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            <circle cx="9" cy="12.5" r="0.8" fill="currentColor" />
          </svg>
        </div>
        <h3 className="modal-title">Delete story</h3>
        <p className="modal-text">
          Delete <strong>{story?.name}</strong>? Cards will remain but become ungrouped.
        </p>
        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={() => setConfirmDelete(false)} disabled={deleting}>Cancel</button>
          <button
            className="btn"
            onClick={() => void handleDelete()}
            disabled={deleting}
            style={{ background: 'var(--danger, #ef4444)', color: '#fff', border: 'none' }}
          >
            {deleting ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>
    </aside>
  );
}

function InlineTitle({ value, saving, onCommit }: { value: string; saving: boolean; onCommit: (v: string) => void }) {
  const [draft, setDraft] = useState(value);
  const [editing, setEditing] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => { setDraft(value); }, [value]);
  useEffect(() => { if (editing) inputRef.current?.focus(); }, [editing]);

  if (!editing) {
    return (
      <h2
        className="detail-title"
        onClick={() => setEditing(true)}
        title="Click to edit"
        style={{ cursor: 'text' }}
      >
        {value}
        {saving && <span style={savingStyle}> · saving…</span>}
      </h2>
    );
  }
  return (
    <input
      ref={inputRef}
      className="detail-title"
      value={draft}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => { setEditing(false); onCommit(draft); }}
      onKeyDown={(e) => {
        if (e.key === 'Enter') { e.preventDefault(); (e.target as HTMLInputElement).blur(); }
        if (e.key === 'Escape') { setDraft(value); setEditing(false); }
      }}
      style={{
        width: '100%',
        padding: '2px 6px',
        margin: '-3px -7px 9px',
        background: 'var(--bg-surface)',
        border: '1px solid var(--accent)',
        borderRadius: 'var(--radius-sm)',
        color: 'var(--text-primary)',
        fontFamily: 'var(--font-display)',
        fontSize: 18,
        fontWeight: 600,
        lineHeight: 1.25,
        outline: 'none',
      }}
    />
  );
}

function InlineInput({
  value, type = 'text', placeholder, disabled, saving, onCommit,
}: {
  value: string; type?: 'text' | 'number'; placeholder?: string; disabled?: boolean; saving: boolean;
  onCommit: (v: string) => void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => { setDraft(value); }, [value]);
  return (
    <div style={{ position: 'relative' }}>
      <input
        type={type}
        value={draft}
        placeholder={placeholder}
        disabled={disabled}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => onCommit(draft)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') { e.preventDefault(); (e.target as HTMLInputElement).blur(); }
          if (e.key === 'Escape') { setDraft(value); (e.target as HTMLInputElement).blur(); }
        }}
        style={inlineInputStyle}
      />
      {saving && <span style={savingStyle}>saving…</span>}
    </div>
  );
}

function InlineTextarea({
  value, disabled, saving, placeholder, onCommit,
}: {
  value: string; disabled?: boolean; saving: boolean; placeholder?: string;
  onCommit: (v: string) => void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => { setDraft(value); }, [value]);
  return (
    <div style={{ position: 'relative' }}>
      <textarea
        value={draft}
        placeholder={placeholder}
        disabled={disabled}
        rows={6}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => onCommit(draft)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); (e.target as HTMLTextAreaElement).blur(); }
          if (e.key === 'Escape') { setDraft(value); (e.target as HTMLTextAreaElement).blur(); }
        }}
        style={{ ...inlineInputStyle, resize: 'vertical', lineHeight: 1.45, fontFamily: 'inherit' }}
      />
      {saving && <span style={savingStyle}>saving…</span>}
    </div>
  );
}

function colColor(col: string): string {
  switch (col) {
    case 'backlog': return 'var(--text-tertiary)';
    case 'design':  return '#a78bfa';
    case 'build':   return '#3b82f6';
    case 'review':  return '#f59e0b';
    case 'done':    return '#22c55e';
    default:        return 'var(--text-tertiary)';
  }
}

const inlineInputStyle: React.CSSProperties = {
  width: '100%',
  padding: '6px 8px',
  fontSize: 12,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
};

const savingStyle: React.CSSProperties = {
  position: 'absolute',
  top: 6,
  right: 8,
  fontSize: 9,
  fontFamily: 'var(--font-mono)',
  color: 'var(--text-tertiary)',
  pointerEvents: 'none',
};

const cardRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  width: '100%',
  padding: '8px 10px',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--text-primary)',
  font: 'inherit',
  fontSize: 12,
  textAlign: 'left',
  cursor: 'pointer',
};

const cardIdStyle: React.CSSProperties = {
  fontFamily: 'var(--font-mono)',
  fontSize: 10,
  color: 'var(--text-tertiary)',
  flexShrink: 0,
};

const cardTitleStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const colPill: React.CSSProperties = {
  fontSize: 10,
  padding: '2px 6px',
  borderLeft: '2px solid',
  fontFamily: 'var(--font-mono)',
  flexShrink: 0,
};

const ptsStyle: React.CSSProperties = {
  fontFamily: 'var(--font-mono)',
  fontSize: 10,
  color: 'var(--text-tertiary)',
  flexShrink: 0,
};
