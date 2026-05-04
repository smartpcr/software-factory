import { useEffect, useMemo, useState } from 'react';
import type { Card, ProductStory } from '@/types';
import { useBoardStore, filterCards } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { EditStoryModal } from './EditStoryModal';

// Story list view for Plan & Design. Shows each product-scoped story
// as an expandable row with its associated cards underneath.
export function StoryList() {
  const activeProductId = useProductsStore((s) => s.activeProductId);

  // Fetch product-scoped stories.
  const [stories, setStories] = useState<ProductStory[]>([]);
  useEffect(() => {
    if (!activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setStories(s); })
      .catch(() => { /* best effort */ });
    return () => { cancelled = true; };
  }, [activeProductId]);

  const cards = useBoardStore((s) => s.cards);
  const setSelected = useBoardStore((s) => s.setSelected);
  const setSelectedStory = useBoardStore((s) => s.setSelectedStory);
  const activeFilter = useBoardStore((s) => s.activeFilter);
  const epicFilter = useBoardStore((s) => s.epicFilter);
  const agentFilter = useBoardStore((s) => s.agentFilter);
  const priorityFilter = useBoardStore((s) => s.priorityFilter);
  const searchTerm = useBoardStore((s) => s.searchTerm);
  const filtered = useMemo(
    () => filterCards(cards, { activeFilter, epicFilter, agentFilter, priorityFilter, searchTerm }),
    [cards, activeFilter, epicFilter, agentFilter, priorityFilter, searchTerm],
  );

  // Group cards by product story.
  const groups = useMemo(() => {
    const map = new Map<string, { story: ProductStory | null; cards: Card[] }>();
    for (const s of stories) {
      map.set(s.id, { story: s, cards: [] });
    }
    const orphans: Card[] = [];
    for (const c of filtered) {
      if (c.epic && map.has(c.epic)) {
        map.get(c.epic)!.cards.push(c);
      } else {
        orphans.push(c);
      }
    }
    const result = [...map.values()];
    if (orphans.length > 0) {
      result.push({ story: null, cards: orphans });
    }
    return result;
  }, [stories, filtered]);

  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  function toggle(id: string) {
    setCollapsed((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  const [editing, setEditing] = useState<ProductStory | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<ProductStory | null>(null);
  const [deleting, setDeleting] = useState(false);

  async function handleDelete() {
    if (!confirmDelete) return;
    setDeleting(true);
    try {
      await forgeApi.deleteProductStory(confirmDelete.productId, confirmDelete.id);
      useToastStore.getState().show(`Story "${confirmDelete.name}" deleted`, 'success');
      setStories((prev) => prev.filter((s) => s.id !== confirmDelete.id));
      setConfirmDelete(null);
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Delete failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div className="story-list">
      <div style={headerStyle}>
        <span style={{ flex: 2 }}>Story / Card</span>
        <span style={{ width: 80, textAlign: 'center' }}>Status</span>
        <span style={{ width: 50, textAlign: 'center' }}>Pts</span>
        <span style={{ width: 70, textAlign: 'center' }}>Priority</span>
        <span style={{ width: 80, textAlign: 'center' }}>Column</span>
        <span style={{ width: 60 }} />
      </div>

      {groups.map(({ story, cards: groupCards }) => {
        const id = story?.id ?? '__none__';
        const isCollapsed = collapsed.has(id);
        const totalPts = groupCards.reduce((s, c) => s + c.pts, 0);
        const storyPts = story?.points ?? 0;

        return (
          <div key={id} style={{ marginBottom: 2 }}>
            {/* Story header row */}
            <div style={storyRowStyle}>
              <div
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  flex: 2,
                  minWidth: 0,
                  gap: 8,
                }}
              >
                <button
                  type="button"
                  onClick={(e) => { e.stopPropagation(); toggle(id); }}
                  style={chevronBtnStyle}
                  title={isCollapsed ? 'Expand' : 'Collapse'}
                  aria-label={isCollapsed ? 'Expand story' : 'Collapse story'}
                >
                  <svg width="10" height="10" viewBox="0 0 10 10" style={{ flexShrink: 0, transition: 'transform 150ms', transform: isCollapsed ? 'rotate(-90deg)' : 'rotate(0deg)' }}>
                    <path d="M2 3 L5 6 L8 3" stroke="currentColor" fill="none" strokeWidth="1.4" strokeLinecap="round" />
                  </svg>
                </button>
                <button
                  type="button"
                  onClick={() => { if (story) setSelectedStory(story.id); }}
                  disabled={!story}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    flex: 1,
                    minWidth: 0,
                    background: 'transparent',
                    border: 'none',
                    color: 'inherit',
                    font: 'inherit',
                    fontWeight: 600,
                    padding: 0,
                    cursor: story ? 'pointer' : 'default',
                    textAlign: 'left',
                    gap: 8,
                  }}
                  title={story ? (story.description || 'Open story detail') : undefined}
                >
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600 }}>
                    {story?.name ?? 'No story'}
                  </span>
                  <span style={countBadge}>{groupCards.length}</span>
                </button>
              </div>
              <span style={{ width: 80, textAlign: 'center', fontSize: 11 }}>
                {story?.status ?? '—'}
              </span>
              <span style={{ width: 50, textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                {storyPts > 0 ? `${totalPts}/${storyPts}` : totalPts}
              </span>
              <span style={{ width: 70 }} />
              <span style={{ width: 80 }} />
              <span style={{ width: 140, textAlign: 'right', display: 'flex', gap: 4, justifyContent: 'flex-end' }}>
                {story && (
                  <>
                    <button
                      type="button"
                      onClick={(e) => { e.stopPropagation(); setEditing(story); }}
                      style={editBtnStyle}
                      title="Edit story"
                    >
                      ✎ Edit
                    </button>
                    <button
                      type="button"
                      onClick={(e) => { e.stopPropagation(); setConfirmDelete(story); }}
                      style={deleteBtnStyle}
                      title="Delete story"
                    >
                      🗑 Delete
                    </button>
                  </>
                )}
              </span>
            </div>

            {/* Card rows */}
            {!isCollapsed && groupCards.map((c) => (
              <CardRow key={c.id} card={c} onClick={() => setSelected(c.id)} />
            ))}

            {/* Empty state */}
            {!isCollapsed && groupCards.length === 0 && (
              <div style={emptyRowStyle}>No cards in this story</div>
            )}
          </div>
        );
      })}

      {groups.length === 0 && (
        <div style={{ padding: 40, textAlign: 'center', color: 'var(--text-tertiary)' }}>
          No stories yet. Click "New story" to create one.
        </div>
      )}

      <EditStoryModal
        open={editing !== null}
        story={editing}
        onClose={() => setEditing(null)}
        onSaved={(updated) => {
          setStories((prev) => prev.map((s) => (s.id === updated.id ? updated : s)));
        }}
      />

      {/* Delete confirmation modal — matches app modal style */}
      <>
        <div
          className={`modal-backdrop${confirmDelete ? ' show' : ''}`}
          onClick={() => !deleting && setConfirmDelete(null)}
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
            Delete <strong>{confirmDelete?.name}</strong>? Cards will remain but become ungrouped.
          </p>
          <div className="modal-actions">
            <button className="btn btn-secondary" onClick={() => setConfirmDelete(null)} disabled={deleting}>Cancel</button>
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
      </>
    </div>
  );
}

function CardRow({ card, onClick }: { card: Card; onClick: () => void }) {
  const priorityColor = card.priority === 'high' ? 'var(--danger, #ef4444)'
    : card.priority === 'low' ? 'var(--text-tertiary)' : 'var(--text-secondary)';

  return (
    <button type="button" onClick={onClick} style={cardRowStyle}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 2, minWidth: 0, paddingLeft: 24 }}>
        <span style={{ fontSize: 10, fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', flexShrink: 0 }}>
          {card.id}
        </span>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {card.title}
        </span>
        {card.tags.filter((t) => t.startsWith('task:')).map((t) => (
          <span key={t} style={tagStyle}>{t.replace('task:', '')}</span>
        ))}
      </span>
      <span style={{ width: 80, textAlign: 'center' }}>
        <span style={{ ...typePill, background: card.type === 'bug' ? 'rgba(239,68,68,0.15)' : card.type === 'task' ? 'rgba(59,130,246,0.15)' : 'rgba(99,102,241,0.12)' }}>
          {card.type}
        </span>
      </span>
      <span style={{ width: 50, textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
        {card.pts}
      </span>
      <span style={{ width: 70, textAlign: 'center', fontSize: 11, color: priorityColor }}>
        {card.priority}
      </span>
      <span style={{ width: 80, textAlign: 'center' }}>
        <span style={{ ...colPill, borderLeftColor: colColor(card.col) }}>{card.col}</span>
      </span>
    </button>
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

// ---- styles ----

const headerStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  padding: '8px 12px',
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: 0.3,
  textTransform: 'uppercase',
  color: 'var(--text-tertiary)',
  borderBottom: '1px solid var(--border)',
};

const storyRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  width: '100%',
  padding: '8px 12px',
  background: 'var(--bg-sunken)',
  border: 'none',
  borderBottom: '1px solid var(--border)',
  color: 'var(--text-primary)',
  font: 'inherit',
  fontSize: 13,
  fontWeight: 600,
  textAlign: 'left',
};

const chevronBtnStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  width: 18,
  height: 18,
  flexShrink: 0,
  background: 'transparent',
  border: 'none',
  color: 'inherit',
  padding: 0,
  cursor: 'pointer',
  borderRadius: 'var(--radius-sm)',
};

const editBtnStyle: React.CSSProperties = {
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: 0.3,
  textTransform: 'uppercase',
  color: 'var(--text-secondary)',
  background: 'transparent',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  padding: '3px 8px',
  cursor: 'pointer',
};

const deleteBtnStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1,
  color: 'var(--text-tertiary)',
  background: 'transparent',
  border: '1px solid transparent',
  borderRadius: 'var(--radius-sm)',
  padding: '3px 5px',
  cursor: 'pointer',
};

const cardRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  width: '100%',
  padding: '6px 12px',
  background: 'transparent',
  border: 'none',
  borderBottom: '1px solid var(--border)',
  color: 'var(--text-primary)',
  font: 'inherit',
  fontSize: 12,
  textAlign: 'left',
  cursor: 'pointer',
  transition: 'background 80ms ease',
};

const emptyRowStyle: React.CSSProperties = {
  padding: '8px 12px 8px 36px',
  fontSize: 11,
  color: 'var(--text-tertiary)',
  fontStyle: 'italic',
  borderBottom: '1px solid var(--border)',
};

const countBadge: React.CSSProperties = {
  fontSize: 10,
  fontFamily: 'var(--font-mono)',
  color: 'var(--text-tertiary)',
  background: 'var(--bg-surface)',
  borderRadius: 'var(--radius-sm)',
  padding: '1px 5px',
  flexShrink: 0,
};

const tagStyle: React.CSSProperties = {
  fontSize: 9,
  fontFamily: 'var(--font-mono)',
  textTransform: 'uppercase',
  letterSpacing: 0.3,
  padding: '1px 5px',
  borderRadius: 'var(--radius-sm)',
  background: 'rgba(99,102,241,0.12)',
  color: 'var(--accent)',
  flexShrink: 0,
};

const typePill: React.CSSProperties = {
  fontSize: 10,
  padding: '2px 6px',
  borderRadius: 'var(--radius-sm)',
};

const colPill: React.CSSProperties = {
  fontSize: 10,
  padding: '2px 6px',
  borderLeft: '2px solid',
  fontFamily: 'var(--font-mono)',
};
