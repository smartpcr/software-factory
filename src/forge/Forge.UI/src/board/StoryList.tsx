import { useEffect, useMemo, useState } from 'react';
import type { Card, ProductStory } from '@/types';
import { useBoardStore, filterCards } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { forgeApi } from '@/api/forgeApi';

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

  return (
    <div className="story-list">
      <div style={headerStyle}>
        <span style={{ flex: 2 }}>Story / Card</span>
        <span style={{ width: 80, textAlign: 'center' }}>Status</span>
        <span style={{ width: 50, textAlign: 'center' }}>Pts</span>
        <span style={{ width: 70, textAlign: 'center' }}>Priority</span>
        <span style={{ width: 80, textAlign: 'center' }}>Column</span>
      </div>

      {groups.map(({ story, cards: groupCards }) => {
        const id = story?.id ?? '__none__';
        const isCollapsed = collapsed.has(id);
        const totalPts = groupCards.reduce((s, c) => s + c.pts, 0);

        return (
          <div key={id} style={{ marginBottom: 2 }}>
            {/* Story header row */}
            <button
              type="button"
              onClick={() => toggle(id)}
              style={storyRowStyle}
            >
              <span style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 2, minWidth: 0 }}>
                <svg width="10" height="10" viewBox="0 0 10 10" style={{ flexShrink: 0, transition: 'transform 150ms', transform: isCollapsed ? 'rotate(-90deg)' : 'rotate(0deg)' }}>
                  <path d="M2 3 L5 6 L8 3" stroke="currentColor" fill="none" strokeWidth="1.4" strokeLinecap="round" />
                </svg>
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600 }}>
                  {story?.name ?? 'No story'}
                </span>
                <span style={countBadge}>{groupCards.length}</span>
              </span>
              <span style={{ width: 80, textAlign: 'center', fontSize: 11 }}>
                {story?.status ?? '—'}
              </span>
              <span style={{ width: 50, textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                {totalPts}
              </span>
              <span style={{ width: 70 }} />
              <span style={{ width: 80 }} />
            </button>

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
