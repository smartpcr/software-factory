import { useDroppable } from '@dnd-kit/core';
import { useEffect, useState } from 'react';
import type { Card as CardModel, ColumnId, Epic } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { checkDragPolicy } from './policy';
import { Card } from './Card';

interface Props {
  epic: Epic;
  col: ColumnId;
  cards: CardModel[];
}

// Each (epic × column) cell on the board. Drop targets carry data-epic +
// data-col so the drop policy can inspect the boundary the same way the
// prototype's onDragEnter does.
export function SwimlaneCell({ epic, col, cards }: Props) {
  const draggingId = useBoardStore((s) => s.draggingId);
  const allCards = useBoardStore((s) => s.cards);

  const { isOver, setNodeRef } = useDroppable({
    id: `cell:${epic.id}:${col}`,
    data: { epic: epic.id, col },
  });

  const [dropCls, setDropCls] = useState('');
  const [warning, setWarning] = useState<string | null>(null);

  useEffect(() => {
    if (!isOver || !draggingId) { setDropCls(''); setWarning(null); return; }
    const card = allCards.find((c) => c.id === draggingId);
    if (!card) { setDropCls(''); setWarning(null); return; }
    if (card.epic !== epic.id) { setDropCls('drop-forbidden'); setWarning(null); return; }
    const policy = checkDragPolicy(card.col, col, card);
    if (!policy.allow) { setDropCls('drop-forbidden'); setWarning(null); return; }
    if (policy.confirm || policy.warn) {
      setDropCls('drop-warning');
      setWarning(policy.warn ?? 'Confirmation required');
      return;
    }
    if (card.col !== col) { setDropCls('drop-allowed'); setWarning(null); return; }
    setDropCls('');
    setWarning(null);
  }, [isOver, draggingId, allCards, col, epic.id]);

  return (
    <div
      ref={setNodeRef}
      className={`swimlane-cell ${dropCls}`.trim()}
      data-epic={epic.id}
      data-col={col}
      data-warning={warning ?? undefined}
    >
      {cards.length === 0 ? (
        <div className="cell-empty">·</div>
      ) : (
        cards.map((card) => <Card key={card.id} card={card} />)
      )}
    </div>
  );
}
