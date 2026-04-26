import { useWitStore } from './useWitStore';

interface Props {
  count: number;
}

// Phase 12.8 — bulk action bar surfaces when ≥1 item is selected. Sample-only:
// the actions toast and clear the selection. Real CRUD lands with Phase 12.3.
export function BulkActionBar({ count }: Props) {
  const clearSelection = useWitStore((s) => s.clearSelection);
  if (count === 0) return null;

  return (
    <div className="wit-bulk-bar" role="region" aria-label="Bulk actions">
      <div className="wit-bulk-count">
        <span className="num">{count}</span>
        <span className="lbl">selected</span>
      </div>
      <div className="wit-bulk-actions">
        <button type="button" className="wit-bulk-btn" onClick={() => placeholder('move-state')}>
          Change state
        </button>
        <button type="button" className="wit-bulk-btn" onClick={() => placeholder('move-iter')}>
          Move iteration
        </button>
        <button type="button" className="wit-bulk-btn" onClick={() => placeholder('reassign')}>
          Reassign
        </button>
        <button type="button" className="wit-bulk-btn" onClick={() => placeholder('tag')}>
          Tag
        </button>
        <button type="button" className="wit-bulk-btn danger" onClick={() => placeholder('delete')}>
          Delete
        </button>
      </div>
      <button type="button" className="wit-bulk-clear" onClick={clearSelection}>
        Clear
      </button>
    </div>
  );
}

function placeholder(action: string) {
  // Hooked up in Phase 12.3 once the bulk endpoint lands. Mockup-only.
  // eslint-disable-next-line no-console
  console.info(`[wit] bulk ${action} — wired in Phase 12.3`);
}
