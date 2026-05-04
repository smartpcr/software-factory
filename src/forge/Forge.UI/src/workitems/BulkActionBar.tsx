import { useMemo, useState } from 'react';
import { useWitStore } from './useWitStore';
import { useWorkItemsStore } from './useWorkItemsStore';
import { useToastStore } from '@/state/useToastStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import { WORK_ITEM_STATES, type WorkItemState } from './workItemTypes';
import { DispatchToAiModal } from './DispatchToAiModal';

interface Props {
  count: number;
}

// Phase 12.8 — bulk action bar surfaces when ≥1 item is selected. Each
// action iterates the selected ids and dispatches the right store mutator
// in series. The store is responsible for optimistic + rollback semantics.
export function BulkActionBar({ count }: Props) {
  const selection = useWitStore((s) => s.selection);
  const clearSelection = useWitStore((s) => s.clearSelection);
  const items = useWorkItemsStore((s) => s.items);
  const patch = useWorkItemsStore((s) => s.patch);
  const softDelete = useWorkItemsStore((s) => s.softDelete);
  const showToast = useToastStore((s) => s.show);

  const [stateMenuOpen, setStateMenuOpen] = useState(false);
  const [dispatchOpen, setDispatchOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  // Resolve selected ids → WorkItem objects for the dispatch modal.
  const selectedItems = useMemo(
    () => [...selection].map((id) => items.find((w) => w.id === id)).filter((w): w is NonNullable<typeof w> => !!w),
    [selection, items],
  );

  if (count === 0) return null;

  async function bulkSetState(state: WorkItemState) {
    setStateMenuOpen(false);
    setBusy(true);
    try {
      const ids = [...selection];
      await Promise.all(ids.map((id) => patch(id, { state })));
      showToast(`Moved ${ids.length} item${ids.length === 1 ? '' : 's'} to ${state}`, 'success');
      clearSelection();
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'bulk move failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  async function bulkDelete() {
    const n = selection.size;
    const ok = await useConfirmStore.getState().ask({
      title: `Soft-delete ${n} item${n === 1 ? '' : 's'}?`,
      body: 'Soft-delete moves the items to the "closed" state. They stay in the database and can be reopened from the work-items list.',
      tone: 'warn',
      confirmLabel: '✕ Close items',
      cancelLabel: 'Cancel',
    });
    if (!ok) return;
    setBusy(true);
    try {
      const ids = [...selection];
      await Promise.all(ids.map((id) => softDelete(id)));
      showToast(`Closed ${ids.length} item${ids.length === 1 ? '' : 's'}`, 'info');
      clearSelection();
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'bulk delete failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="wit-bulk-bar" role="region" aria-label="Bulk actions">
      <div className="wit-bulk-count">
        <span className="num">{count}</span>
        <span className="lbl">selected</span>
      </div>
      <div className="wit-bulk-actions">
        <div className="wit-bulk-state-wrap">
          <button
            type="button"
            className="wit-bulk-btn"
            onClick={() => setStateMenuOpen((o) => !o)}
            disabled={busy}
            aria-haspopup="menu"
            aria-expanded={stateMenuOpen}
          >
            Change state ▾
          </button>
          {stateMenuOpen && (
            <div className="wit-bulk-state-menu" role="menu">
              {WORK_ITEM_STATES.map((s) => (
                <button
                  key={s}
                  type="button"
                  role="menuitem"
                  className="wit-bulk-state-menu-item"
                  onClick={() => void bulkSetState(s)}
                  disabled={busy}
                >
                  {s}
                </button>
              ))}
            </div>
          )}
        </div>
        <button
          type="button"
          className="wit-bulk-btn"
          onClick={() => placeholder('move-iter')}
          disabled={busy}
          title="Iteration combo lands with Phase 12.7 follow-up"
        >
          Move iteration
        </button>
        <button
          type="button"
          className="wit-bulk-btn"
          onClick={() => placeholder('reassign')}
          disabled={busy}
          title="Assignee picker lands with Phase 12.7 follow-up"
        >
          Reassign
        </button>
        <button
          type="button"
          className="wit-bulk-btn"
          onClick={() => placeholder('tag')}
          disabled={busy}
          title="Tag editor lands with Phase 12.7 follow-up"
        >
          Tag
        </button>
        <button
          type="button"
          className="wit-bulk-btn"
          onClick={() => setDispatchOpen(true)}
          disabled={busy || selectedItems.length === 0}
          title="Assign to an AI crew — lead delegates and the runner kicks off (Phase 14)"
          style={{ borderColor: 'var(--accent)', color: 'var(--accent)' }}
        >
          ✦ Dispatch to AI
        </button>
        <button
          type="button"
          className="wit-bulk-btn danger"
          onClick={() => void bulkDelete()}
          disabled={busy}
        >
          Delete
        </button>
      </div>
      <button type="button" className="wit-bulk-clear" onClick={clearSelection} disabled={busy}>
        Clear
      </button>
      <DispatchToAiModal
        open={dispatchOpen}
        onClose={() => { setDispatchOpen(false); }}
        items={selectedItems}
      />
    </div>
  );
}

function placeholder(action: string) {
  // Iteration / assignee / tag pickers need richer input than bulk-fire-and-
  // forget; they ship with Phase 12.7 follow-up.
  // eslint-disable-next-line no-console
  console.info(`[wit] bulk ${action} — wired in Phase 12.7 follow-up`);
}
