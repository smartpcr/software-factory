import { useEffect, useMemo, useState } from 'react';
import { DndContext, DragOverlay, PointerSensor, useSensor, useSensors, type DragEndEvent, type DragStartEvent } from '@dnd-kit/core';
import { useHotkeys } from 'react-hotkeys-hook';
import type { ColumnId } from '@/types';
import { useBoardStore, computeBurndown } from '@/state/useBoardStore';
import { useToastStore } from '@/state/useToastStore';
import { useForgeHub } from '@/realtime/useForgeHub';
import { Board } from '@/board/Board';
import { FilterBar } from '@/board/FilterBar';
import { DetailPanel } from '@/board/DetailPanel';
import { CardModal } from '@/board/CardModal';
import { Toaster } from '@/board/Toaster';
import { ConfirmModal } from '@/board/ConfirmModal';
import { Card as BoardCard } from '@/board/Card';

const COLUMN_KEYS: ColumnId[] = ['backlog', 'design', 'build', 'review', 'done'];

export function PlanView() {
  useForgeHub();

  const setDragging = useBoardStore((s) => s.setDragging);
  const move = useBoardStore((s) => s.move);
  const setSelected = useBoardStore((s) => s.setSelected);
  const cards = useBoardStore((s) => s.cards);
  const burndown = useMemo(() => computeBurndown(cards), [cards]);

  const [modalOpen, setModalOpen] = useState(false);
  const [focusedCol, setFocusedCol] = useState<ColumnId | null>(null);

  // dnd-kit pointer sensor with a small activation distance so clicks on
  // cards still open the detail panel.
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  const draggingId = useBoardStore((s) => s.draggingId);
  const draggingCard = useMemo(
    () => (draggingId ? cards.find((c) => c.id === draggingId) ?? null : null),
    [draggingId, cards],
  );

  function onDragStart(e: DragStartEvent) {
    setDragging(String(e.active.id));
  }
  async function onDragEnd(e: DragEndEvent) {
    setDragging(null);
    const cardId = String(e.active.id);
    const overData = e.over?.data?.current as { epic?: string; col?: ColumnId } | undefined;
    if (!overData?.col) return;
    const fromEpic = (e.active.data.current as { fromEpic?: string } | undefined)?.fromEpic;
    if (fromEpic && overData.epic && fromEpic !== overData.epic) {
      useToastStore.getState().show('Cards stay in their epic', 'danger');
      return;
    }
    await move(cardId, overData.col);
  }

  useHotkeys('n', (e) => { e.preventDefault(); setModalOpen(true); });
  useHotkeys('escape', () => { setSelected(null); setModalOpen(false); });
  useHotkeys('/', (e) => {
    e.preventDefault();
    const search = document.querySelector<HTMLInputElement>('.filter-bar .filter-search input');
    search?.focus();
  });
  useHotkeys('1', () => setFocusedCol(COLUMN_KEYS[0]));
  useHotkeys('2', () => setFocusedCol(COLUMN_KEYS[1]));
  useHotkeys('3', () => setFocusedCol(COLUMN_KEYS[2]));
  useHotkeys('4', () => setFocusedCol(COLUMN_KEYS[3]));
  useHotkeys('5', () => setFocusedCol(COLUMN_KEYS[4]));

  useEffect(() => {
    if (!focusedCol) return;
    const head = document.querySelector<HTMLElement>(`.col-marker.${focusedCol}`)?.closest<HTMLElement>('.col-head');
    head?.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'start' });
  }, [focusedCol]);

  return (
    <section className="view active" id="view-plan">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">01</span>&nbsp;Plan & Design</span>
            <span className="sep">/</span>
            <span>day 6 of 10</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />6 agents working</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Sprint 14 board
            <span className="sprint-pill">
              <span className="pulse" />
              <span>{burndown.done} / {burndown.total} pts</span>
              <span className="sprint-burndown">
                <span className="sprint-burndown-bar">
                  <span className="sprint-burndown-fill" style={{ width: `${burndown.pct}%` }} />
                </span>
              </span>
            </span>
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M2 3 H 10 M 2 6 H 10 M 2 9 H 10" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>
            List
          </button>
          <button className="btn btn-secondary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><rect x="1.5" y="2" width="9" height="2" stroke="currentColor" strokeWidth="1.3" /><rect x="3" y="6" width="6" height="2" stroke="currentColor" strokeWidth="1.3" /><rect x="2" y="9" width="5" height="1.5" stroke="currentColor" strokeWidth="1.3" /></svg>
            Timeline
          </button>
          <button className="btn btn-primary" onClick={() => setModalOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg>
            New item
          </button>
        </div>
      </div>

      <FilterBar />

      <div className="board-wrap">
        <DndContext sensors={sensors} onDragStart={onDragStart} onDragEnd={onDragEnd}>
          <Board />
          <DragOverlay dropAnimation={null}>
            {draggingCard ? (
              <div className="drag-overlay">
                <BoardCard card={draggingCard} />
              </div>
            ) : null}
          </DragOverlay>
        </DndContext>
      </div>

      <DetailPanel />
      <CardModal open={modalOpen} onClose={() => setModalOpen(false)} />
      <ConfirmModal />
      <Toaster />
    </section>
  );
}
