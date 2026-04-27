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
import { NewStoryModal } from '@/board/NewStoryModal';
import { Toaster } from '@/board/Toaster';
import { ConfirmModal } from '@/board/ConfirmModal';
import { Card as BoardCard } from '@/board/Card';
import { SprintPicker } from '@/sprints/SprintPicker';
import { AreaPathFilter } from '@/sprints/AreaPathFilter';
import { SprintManager } from '@/sprints/SprintManager';
import { useProductsStore } from '@/state/useProductsStore';
import { useSprintsStore, selectIterations, selectDefaultArea } from '@/state/useSprintsStore';

const COLUMN_KEYS: ColumnId[] = ['backlog', 'design', 'build', 'review', 'done'];

export function PlanView() {
  useForgeHub();

  const setDragging = useBoardStore((s) => s.setDragging);
  const move = useBoardStore((s) => s.move);
  const setSelected = useBoardStore((s) => s.setSelected);
  const cards = useBoardStore((s) => s.cards);
  const burndown = useMemo(() => computeBurndown(cards), [cards]);

  const [modalOpen, setModalOpen] = useState(false);
  const [storyModalOpen, setStoryModalOpen] = useState(false);
  const [focusedCol, setFocusedCol] = useState<ColumnId | null>(null);

  // Phase 15 — sprint picker + area path filter state. The active product
  // comes from the global TopBar selector; iterations/areas come from the
  // sprints store (API-backed, with SignalR invalidation).
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const ensureSprints = useSprintsStore((s) => s.ensure);
  useEffect(() => {
    if (activeProductId) void ensureSprints(activeProductId);
  }, [activeProductId, ensureSprints]);
  // Pass an empty string when the product hasn't loaded — the selectors
  // tolerate that and return their stable EMPTY constants. Avoid the
  // `activeProductId ? ... : () => …` ternary because the inline
  // arrow function is fresh every render and would force a re-subscribe.
  const productIterations = useSprintsStore(selectIterations(activeProductId ?? ''));
  const defaultArea = useSprintsStore(selectDefaultArea(activeProductId ?? ''));

  const [selectedIterationId, setSelectedIterationId] = useState<string>('');
  const [iterationCleared, setIterationCleared] = useState(false);
  const [areaPath, setAreaPath] = useState<string | null>(null);
  const [sprintManagerOpen, setSprintManagerOpen] = useState(false);
  const [sprintManagerTab, setSprintManagerTab] = useState<'iterations' | 'areas' | 'import' | 'prefixes'>('iterations');

  function selectIteration(id: string) {
    setSelectedIterationId(id);
    setIterationCleared(id === '');
  }

  // Default the selected iteration to the product's bound current sprint
  // once data lands, and reset when the product switches.
  useEffect(() => {
    setSelectedIterationId('');
    setIterationCleared(false);
    setAreaPath(null);
  }, [activeProductId]);
  useEffect(() => {
    if (selectedIterationId || iterationCleared) return;
    const current = productIterations.find((i) => i.state === 'current'
      && i.bindings.some((b) => b.role === 'current'));
    const fallback = current ?? productIterations[0];
    if (fallback) setSelectedIterationId(fallback.id);
  }, [productIterations, selectedIterationId, iterationCleared]);

  const selectedIteration = useMemo(
    () => productIterations.find((i) => i.id === selectedIterationId) ?? productIterations[0],
    [productIterations, selectedIterationId],
  );

  // Default area path follows the product's flagged row until the user
  // overrides it manually (clearing the chip wipes both).
  useEffect(() => {
    if (areaPath !== null) return;
    if (defaultArea) setAreaPath(defaultArea.path);
  }, [defaultArea, areaPath]);

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
          <h1 className="page-title" style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
            <span className="mark" />
            <span>{selectedIteration?.name ?? (iterationCleared ? 'All sprints' : 'Sprint board')}</span>
            <SprintPicker
              productId={activeProductId ?? ''}
              selectedId={selectedIteration?.id ?? ''}
              burndown={burndown}
              onSelect={selectIteration}
              onManage={() => { setSprintManagerTab('iterations'); setSprintManagerOpen(true); }}
            />
            <AreaPathFilter
              productId={activeProductId ?? ''}
              activePath={areaPath}
              onChange={setAreaPath}
              onManage={() => { setSprintManagerTab('areas'); setSprintManagerOpen(true); }}
            />
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
          <button className="btn btn-secondary" onClick={() => setStoryModalOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg>
            New story
          </button>
        </div>
      </div>

      <FilterBar />

      <div className="board-wrap">
        {cards.length === 0 ? (
          <div className="empty-state">
            <div className="title">No cards in this sprint</div>
            <div className="sub">
              Press <kbd>n</kbd> to add a new card, or sync from a provider to import
              your backlog.
            </div>
            <div className="actions">
              <button className="btn btn-primary" onClick={() => setModalOpen(true)}>New card</button>
            </div>
          </div>
        ) : (
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
        )}
      </div>

      <DetailPanel />
      <CardModal open={modalOpen} onClose={() => setModalOpen(false)} />
      <NewStoryModal open={storyModalOpen} onClose={() => setStoryModalOpen(false)} />
      <ConfirmModal />
      <SprintManager
        open={sprintManagerOpen}
        productId={activeProductId ?? ''}
        productName={
          useProductsStore.getState().products.find((p) => p.id === activeProductId)?.name
            ?? activeProductId
            ?? 'Product'
        }
        initialTab={sprintManagerTab}
        onClose={() => setSprintManagerOpen(false)}
      />
      <Toaster />
    </section>
  );
}
