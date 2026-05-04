import { useEffect, useMemo, useState } from 'react';
import { DndContext, DragOverlay, PointerSensor, useSensor, useSensors, type DragEndEvent, type DragStartEvent } from '@dnd-kit/core';
import { useHotkeys } from 'react-hotkeys-hook';
import type { ColumnId } from '@/types';
import { useBoardStore, computeBurndown } from '@/state/useBoardStore';
import { useToastStore } from '@/state/useToastStore';
import { useForgeHub } from '@/realtime/useForgeHub';
import { Board } from '@/board/Board';
import { FilterBar } from '@/board/FilterBar';
import { WorkItemDetailPanel } from '@/workitems/WorkItemDetailPanel';
import { DetailPanel } from '@/board/DetailPanel';
import { StoryDetailPanel } from '@/board/StoryDetailPanel';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { useWitStore } from '@/workitems/useWitStore';
import { ExecBoard } from '@/workitems/ExecBoard';
import { CardModal } from '@/board/CardModal';
import { NewStoryModal } from '@/board/NewStoryModal';
import { StoryList } from '@/board/StoryList';
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
  const selectedId = useBoardStore((s) => s.selectedId);
  const selectedStoryId = useBoardStore((s) => s.selectedStoryId);
  const setSelectedStory = useBoardStore((s) => s.setSelectedStory);
  const cards = useBoardStore((s) => s.cards);
  const burndown = useMemo(() => computeBurndown(cards), [cards]);

  // Load work items so the detail panel can resolve card→WI.
  const wiLoad = useWorkItemsStore((s) => s.load);
  const wiItems = useWorkItemsStore((s) => s.items);
  useEffect(() => { void wiLoad(); }, [wiLoad]);
  const selectedWorkItem = useMemo(
    () => selectedId ? wiItems.find((w) => w.id === selectedId) ?? null : null,
    [selectedId, wiItems],
  );
  const selectedCard = useMemo(
    () => selectedId ? cards.find((c) => c.id === selectedId) ?? null : null,
    [selectedId, cards],
  );

  // Sync board selection ↔ WI store focus so the detail panel's × button
  // (which calls useWitStore.focus(null)) also clears the board selection.
  const witFocusedId = useWitStore((s) => s.focusedId);
  useEffect(() => {
    if (selectedId && selectedId !== witFocusedId) {
      useWitStore.getState().focus(selectedId);
    }
  }, [selectedId, witFocusedId]);
  useEffect(() => {
    // Mirror panel close (× button) to the board. selectedId is read via
    // getState — keeping it out of the deps prevents this effect from
    // firing on every card click, which would race with the effect above
    // (witFocusedId is still the stale pre-click null in this closure)
    // and undo the freshly-set selectedId, requiring a second click.
    if (witFocusedId === null && useBoardStore.getState().selectedId) {
      setSelected(null);
    }
  }, [witFocusedId, setSelected]);

  const [modalOpen, setModalOpen] = useState(false);
  const [storyModalOpen, setStoryModalOpen] = useState(false);

  const [focusedCol, setFocusedCol] = useState<ColumnId | null>(null);
  const [viewMode, setViewMode] = useState<'board' | 'stories' | 'exec'>('board');

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
    () => selectedIterationId
      ? (productIterations.find((i) => i.id === selectedIterationId) ?? null)
      : null,
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
  useHotkeys('escape', () => { setSelected(null); setSelectedStory(null); setModalOpen(false); });
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
          <button
            className={`btn ${viewMode === 'board' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setViewMode('board')}
          >
            <svg width="12" height="12" viewBox="0 0 14 14" fill="none"><rect x="1" y="2" width="3" height="10" rx="0.5" stroke="currentColor" strokeWidth="1.2" /><rect x="5.5" y="2" width="3" height="6" rx="0.5" stroke="currentColor" strokeWidth="1.2" /><rect x="10" y="2" width="3" height="8" rx="0.5" stroke="currentColor" strokeWidth="1.2" /></svg>
            Board
          </button>
          <button
            className={`btn ${viewMode === 'exec' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setViewMode('exec')}
          >
            <svg width="12" height="12" viewBox="0 0 14 14" fill="none"><circle cx="7" cy="5" r="2.5" stroke="currentColor" strokeWidth="1.2" /><path d="M3 12c0-2.2 1.8-4 4-4s4 1.8 4 4" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" /><circle cx="10.5" cy="3.5" r="1.5" fill="currentColor" opacity="0.5" /></svg>
            Exec
          </button>
          <button
            className={`btn ${viewMode === 'stories' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setViewMode('stories')}
          >
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M2 3 H 10 M 2 6 H 10 M 2 9 H 10" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>
            Stories
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

      <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <div className="board-wrap" style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
          {cards.length === 0 && viewMode !== 'exec' ? (
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
          ) : viewMode === 'exec' ? (
            <ExecBoard
              items={wiItems}
              onOpen={(id) => setSelected(id)}
            />
          ) : viewMode === 'stories' ? (
            <StoryList />
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

        {selectedStoryId ? (
          <div>
            <StoryDetailPanel storyId={selectedStoryId} />
          </div>
        ) : selectedWorkItem ? (
          <div style={{ width: 420, flexShrink: 0, borderLeft: '1px solid var(--border)', overflow: 'auto' }}>
            <WorkItemDetailPanel item={selectedWorkItem} />
          </div>
        ) : (selectedId && selectedCard) ? (
          <div>
            <DetailPanel />
          </div>
        ) : null}
      </div>
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
