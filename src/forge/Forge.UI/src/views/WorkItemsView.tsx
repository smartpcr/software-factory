import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useWitStore } from '@/workitems/useWitStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import type { WitMode } from '@/workitems/useWitStore';
import { WorkItemFilterBar } from '@/workitems/WorkItemFilterBar';
import { WorkItemBoard } from '@/workitems/WorkItemBoard';
import { ExecBoard } from '@/workitems/ExecBoard';
import { WorkItemTimeline } from '@/workitems/WorkItemTimeline';
import { WorkItemTree } from '@/workitems/WorkItemTree';
import { WorkItemList } from '@/workitems/WorkItemList';
import { WorkItemDetailPanel } from '@/workitems/WorkItemDetailPanel';
import { BulkActionBar } from '@/workitems/BulkActionBar';
import { SyncDialog } from '@/workitems/SyncDialog';
import { NewStoryModal } from '@/board/NewStoryModal';
import { NewWorkItemModal } from '@/workitems/NewWorkItemModal';
import { Toaster } from '@/board/Toaster';
import type { WorkItem } from '@/workitems/workItemTypes';

import '@/styles/work-items.css';

const MODES: Array<{ id: WitMode; idx: string; label: string; sub: string; icon: React.ReactNode }> = [
  {
    id: 'board', idx: '01', label: 'Board', sub: 'state columns',
    icon: (<svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden><rect x="1" y="2" width="3" height="10" rx="0.5" stroke="currentColor" strokeWidth="1.2" /><rect x="5.5" y="2" width="3" height="6" rx="0.5" stroke="currentColor" strokeWidth="1.2" /><rect x="10" y="2" width="3" height="8" rx="0.5" stroke="currentColor" strokeWidth="1.2" /></svg>),
  },
  {
    id: 'exec', idx: '02', label: 'Exec', sub: 'agent status',
    icon: (<svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden><circle cx="7" cy="5" r="2.5" stroke="currentColor" strokeWidth="1.2" /><path d="M3 12c0-2.2 1.8-4 4-4s4 1.8 4 4" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" /><circle cx="10.5" cy="3.5" r="1.5" fill="currentColor" opacity="0.5" /></svg>),
  },
  {
    id: 'timeline', idx: '03', label: 'Timeline', sub: 'iteration spans',
    icon: (<svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden><line x1="1" y1="3" x2="9" y2="3" stroke="currentColor" strokeWidth="1.2" /><line x1="3" y1="7" x2="13" y2="7" stroke="currentColor" strokeWidth="1.2" /><line x1="2" y1="11" x2="8" y2="11" stroke="currentColor" strokeWidth="1.2" /><circle cx="9" cy="3" r="1.1" fill="currentColor" /><circle cx="13" cy="7" r="1.1" fill="currentColor" /><circle cx="8" cy="11" r="1.1" fill="currentColor" /></svg>),
  },
  {
    id: 'tree', idx: '04', label: 'Tree', sub: 'parent → child',
    icon: (<svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden><path d="M3 2 V 12 M 3 5 H 8 M 3 8 H 8 M 3 11 H 6" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" /><circle cx="3" cy="2" r="1" fill="currentColor" /><circle cx="9" cy="5" r="1" fill="currentColor" /><circle cx="9" cy="8" r="1" fill="currentColor" /><circle cx="7" cy="11" r="1" fill="currentColor" /></svg>),
  },
  {
    id: 'list', idx: '05', label: 'List', sub: 'dense table',
    icon: (<svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden><line x1="2" y1="3" x2="12" y2="3" stroke="currentColor" strokeWidth="1.2" /><line x1="2" y1="7" x2="12" y2="7" stroke="currentColor" strokeWidth="1.2" /><line x1="2" y1="11" x2="12" y2="11" stroke="currentColor" strokeWidth="1.2" /></svg>),
  },
];

// Phase 12 — Work Item Tracking. Mockup view: sample-data only, no API.
// Visual register: editorial/manuscript — typeset metadata, monospace
// numerics, single-letter type stamps. Reuses the existing forge tokens.
export function WorkItemsView() {
  const mode = useWitStore((s) => s.mode);
  const setMode = useWitStore((s) => s.setMode);
  const filters = useWitStore((s) => s.filters);
  const focusedId = useWitStore((s) => s.focusedId);
  const selection = useWitStore((s) => s.selection);

  const panelExpanded = useWitStore((s) => s.panelExpanded);
  const [syncOpen, setSyncOpen] = useState(false);
  const [storyModalOpen, setStoryModalOpen] = useState(false);
  const [newItemOpen, setNewItemOpen] = useState(false);

  // Phase 12.6 — pull from the store. Falls back to SAMPLE_WORK_ITEMS when
  // the API is offline (handled inside useWorkItemsStore.load()).
  const items = useWorkItemsStore((s) => s.items);
  const loaded = useWorkItemsStore((s) => s.loaded);
  const load = useWorkItemsStore((s) => s.load);
  useEffect(() => { void load(); }, [load]);

  // Phase 17 follow-up — `?focus=ID` query param auto-opens that work
  // item's detail panel. Used by the Plan & Design "Assign to agent or
  // team" button to deep-link from the legacy board.
  const [searchParams] = useSearchParams();
  const focusParam = searchParams.get('focus');
  useEffect(() => {
    if (!focusParam || !loaded) return;
    if (items.find((i) => i.id === focusParam)) {
      useWitStore.getState().focus(focusParam);
    }
  }, [focusParam, loaded, items]);

  // Phase 12 — keyboard: Esc collapses an expanded panel first, otherwise
  // closes the detail rail. Lets users dismiss the overlay without hunting
  // for the close button.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key !== 'Escape') return;
      const store = useWitStore.getState();
      if (store.panelExpanded) {
        store.togglePanelExpanded();
        return;
      }
      if (store.focusedId) store.focus(null);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
  const visible = useMemo(() => applyFilters(items, filters), [items, filters]);
  const focused = useMemo(
    () => items.find((i) => i.id === focusedId) ?? null,
    [items, focusedId],
  );

  return (
    <section className="view active wit-view" id="view-work-items">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">06</span>&nbsp;Work items</span>
            <span className="sep">/</span>
            <span>{loaded ? `${items.length} item${items.length === 1 ? '' : 's'}` : 'loading…'}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Work registry
          </h1>
        </div>
        <div className="page-head-right">
          <button
            className="btn btn-secondary wit-sync-btn"
            type="button"
            title="Sync work items from ADO / GitHub"
            onClick={() => setSyncOpen(true)}
          >
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M2 6 a 4 4 0 0 1 7 -2.5 M 10 6 a 4 4 0 0 1 -7 2.5 M 9 1.5 V 3.5 H 7 M 3 10.5 V 8.5 H 5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            Sync now
          </button>
          <button className="btn btn-primary" type="button" title="Create a new work item" onClick={() => setNewItemOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New item
          </button>
          <button className="btn btn-secondary" type="button" onClick={() => setStoryModalOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New story
          </button>
        </div>
      </div>

      <ModeToggle mode={mode} onChange={setMode} count={visible.length} />

      <WorkItemFilterBar items={items} visibleItems={visible} />

      <div
        className={[
          'wit-canvas',
          `wit-canvas-${mode}`,
          focused && 'wit-canvas-with-rail',
          focused && panelExpanded && 'wit-canvas-rail-expanded',
        ].filter(Boolean).join(' ')}
      >
        <main className="wit-canvas-main">
          {mode === 'board'    && <WorkItemBoard    items={visible} />}
          {mode === 'exec'     && <ExecBoard        items={visible} onOpen={(id) => useWitStore.getState().focus(id)} />}
          {mode === 'timeline' && <WorkItemTimeline items={visible} />}
          {mode === 'tree'     && <WorkItemTree     items={visible} />}
          {mode === 'list'     && <WorkItemList     items={visible} />}
        </main>
        {focused && <div><WorkItemDetailPanel item={focused} /></div>}
      </div>

      <BulkActionBar count={selection.size} />
      <SyncDialog open={syncOpen} onClose={() => setSyncOpen(false)} />
      <NewStoryModal open={storyModalOpen} onClose={() => setStoryModalOpen(false)} />
      <NewWorkItemModal open={newItemOpen} onClose={() => setNewItemOpen(false)} />
      <Toaster />
    </section>
  );
}

interface ModeToggleProps {
  mode: WitMode;
  onChange: (m: WitMode) => void;
  count: number;
}

function ModeToggle({ mode, onChange, count }: ModeToggleProps) {
  return (
    <nav className="wit-mode-toggle" role="tablist" aria-label="View mode">
      {MODES.map((m, i) => (
        <button
          key={m.id}
          type="button"
          role="tab"
          aria-selected={mode === m.id}
          className={`wit-mode${mode === m.id ? ' active' : ''}`}
          onClick={() => onChange(m.id)}
          title={`${m.label} — ${m.sub}`}
        >
          <span className="wit-mode-idx">{m.idx}</span>
          <span className="wit-mode-icon" aria-hidden>{m.icon}</span>
          <span className="wit-mode-name">{m.label}</span>
          <span className="wit-mode-sub" aria-hidden>{m.sub}</span>
          {mode === m.id && (
            <span className="wit-mode-count" aria-label={`${count} items`}>{count}</span>
          )}
          {i < MODES.length - 1 && <span className="wit-mode-sep" aria-hidden>/</span>}
        </button>
      ))}
    </nav>
  );
}

function applyFilters(items: WorkItem[], f: ReturnType<typeof useWitStore.getState>['filters']): WorkItem[] {
  return items.filter((i) => {
    // Work Items view shows dev-track (coding) items only.
    // Design-track items (phase/stage/step) live on Plan & Design.
    if (i.track === 'design') return false;

    // Chips run first.
    switch (f.chip) {
      case 'mine':     if (i.assignedTo?.id !== f.meId) return false; break;
      case 'active':   if (i.state !== 'active')        return false; break;
      case 'blocked':  if (!i.blocked)                  return false; break;
      case 'watching': if (!i.watching)                 return false; break;
      // 'all' falls through.
    }
    // Multi-select — empty array means "any".
    if (f.type.length         && !f.type.includes(i.type))                 return false;
    if (f.state.length        && !f.state.includes(i.state))               return false;
    if (f.iterationRef.length && !f.iterationRef.includes(i.iterationRef)) return false;
    if (f.repoId.length       && (!i.repoId || !f.repoId.includes(i.repoId))) return false;
    if (f.featureRef.length   && (!i.featureRef || !f.featureRef.includes(i.featureRef))) return false;
    if (f.assignee.length) {
      const wantUnassigned = f.assignee.includes('__unassigned__');
      const id = i.assignedTo?.id ?? null;
      const idMatch = id !== null && f.assignee.includes(id);
      const unassignedMatch = id === null && wantUnassigned;
      if (!idMatch && !unassignedMatch) return false;
    }
    if (f.q.trim()) {
      const q = f.q.toLowerCase();
      const hay = `${i.id} ${i.title} ${i.tags.join(' ')}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
}
