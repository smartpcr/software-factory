import { useEffect, useMemo, useRef, useState } from 'react';
import { useToastStore } from '@/state/useToastStore';
import { useCurrentUser } from '@/state/useCurrentUserStore';
import { useProductsStore } from '@/state/useProductsStore';
import { typeLabel, type WorkItemType } from './workItemTypes';

interface Props {
  open: boolean;
  onClose: () => void;
}

interface SyncFilters {
  repoId: string;
  areaPath: string;
  workItemTypes: WorkItemType[];
  assignedTo: string;
  fromDate: string;
  toDate: string;
}

const ALL_TYPES: WorkItemType[] = ['epic', 'feature', 'task', 'bug', 'phase', 'stage', 'step'];

// Phase 12.10 — sync dialog. Mockup-only: collects filters and toasts the
// payload. Real wiring is the implementation step:
// POST /api/work-items/sync/{repoId} with this body.
export function SyncDialog({ open, onClose }: Props) {
  const me = useCurrentUser();
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const products = useProductsStore((s) => s.products);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  const ref = useRef<HTMLDivElement | null>(null);
  const showToast = useToastStore((s) => s.show);

  // Repos for the currently-selected product. Loaded lazily by SideBar /
  // ProductsView; if the dialog is opened before that we kick off a fetch.
  const repos = useMemo(
    () => (activeProductId ? reposByProduct[activeProductId] ?? [] : []),
    [activeProductId, reposByProduct],
  );
  const activeProduct = useMemo(
    () => products.find((p) => p.id === activeProductId) ?? null,
    [products, activeProductId],
  );

  useEffect(() => {
    if (open && activeProductId && !reposByProduct[activeProductId]) {
      void loadReposFor(activeProductId);
    }
  }, [open, activeProductId, reposByProduct, loadReposFor]);

  const [filters, setFilters] = useState<SyncFilters>(() => initialFilters(me.email ?? me.displayName));
  const [running, setRunning] = useState(false);

  // Re-default `assignedTo` whenever the dialog opens so the operator's name
  // is fresh after sign-in. Also pin repoId to the active product's first
  // repo when opening (or when the product changes).
  useEffect(() => {
    if (open) {
      setFilters((f) => ({
        ...f,
        assignedTo: f.assignedTo || (me.email ?? me.displayName) || '',
        repoId: repos.some((r) => r.id === f.repoId) ? f.repoId : repos[0]?.id ?? '',
      }));
    }
  }, [open, me.email, me.displayName, repos]);

  // ESC + click-outside dismiss.
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape' && !running) onClose(); }
    function onMouseDown(e: MouseEvent) {
      if (!ref.current) return;
      if (!ref.current.contains(e.target as Node) && !running) onClose();
    }
    window.addEventListener('keydown', onKey);
    window.addEventListener('mousedown', onMouseDown);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('mousedown', onMouseDown);
    };
  }, [open, onClose, running]);

  if (!open) return null;

  function toggleType(t: WorkItemType) {
    setFilters((f) => ({
      ...f,
      workItemTypes: f.workItemTypes.includes(t)
        ? f.workItemTypes.filter((x) => x !== t)
        : [...f.workItemTypes, t],
    }));
  }

  async function runSync() {
    setRunning(true);
    // Real impl posts to /api/work-items/sync/{repoId}; mock = 600 ms delay.
    await new Promise((r) => setTimeout(r, 600));
    const summary = `${filters.repoId} · ${filters.workItemTypes.length || 'all'} types · ${filters.assignedTo || 'anyone'}`;
    showToast(`Sync queued · ${summary}`, 'success');
    setRunning(false);
    onClose();
  }

  return (
    <div className="wit-sync-overlay" role="dialog" aria-modal="true" aria-labelledby="wit-sync-title">
      <div ref={ref} className="wit-sync-dialog">
        <header className="wit-sync-head">
          <div>
            <div className="wit-sync-eyebrow">12.10 · sync</div>
            <h2 id="wit-sync-title">Sync work items from provider</h2>
            <p className="wit-sync-sub">
              Pulls items matching the filters below into the local cache. Real-time
              updates continue via webhook + 60-second polling.
            </p>
          </div>
          <button
            type="button"
            className="wit-sync-close"
            onClick={onClose}
            disabled={running}
            aria-label="Close"
          >
            ×
          </button>
        </header>

        <div className="wit-sync-body">
          <Field
            label="Repository"
            hint={
              repos.length === 0
                ? activeProduct
                  ? `${activeProduct.name} has no repos connected — add one in Products.`
                  : 'No active product selected.'
                : `Sync runs against one repo at a time. Showing repos for ${activeProduct?.name ?? 'active product'}.`
            }
          >
            <select
              value={filters.repoId}
              onChange={(e) => setFilters((f) => ({ ...f, repoId: e.target.value }))}
              disabled={repos.length === 0}
            >
              {repos.length === 0 ? (
                <option value="">— no repos —</option>
              ) : (
                repos.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.displayName} · {r.kind === 'ado' ? 'Azure DevOps' : 'GitHub'}
                  </option>
                ))
              )}
            </select>
          </Field>

          <Field label="Area path" hint="ADO: System.AreaPath. GitHub: ignored.">
            <input
              type="text"
              value={filters.areaPath}
              onChange={(e) => setFilters((f) => ({ ...f, areaPath: e.target.value }))}
              placeholder="e.g. Atlas\Platform\Auth"
            />
          </Field>

          <Field label="Work item types" hint="Empty = all types.">
            <div className="wit-sync-types">
              {ALL_TYPES.map((t) => (
                <label key={t} className={`wit-sync-type-chip${filters.workItemTypes.includes(t) ? ' active' : ''}`}>
                  <input
                    type="checkbox"
                    checked={filters.workItemTypes.includes(t)}
                    onChange={() => toggleType(t)}
                  />
                  {typeLabel(t)}
                </label>
              ))}
            </div>
          </Field>

          <Field label="Assigned to" hint="Defaults to your email. Leave blank for everyone.">
            <input
              type="text"
              value={filters.assignedTo}
              onChange={(e) => setFilters((f) => ({ ...f, assignedTo: e.target.value }))}
              placeholder="someone@your-org.com"
            />
          </Field>

          <div className="wit-sync-row">
            <Field label="Updated from">
              <input
                type="date"
                value={filters.fromDate}
                onChange={(e) => setFilters((f) => ({ ...f, fromDate: e.target.value }))}
              />
            </Field>
            <Field label="Updated to">
              <input
                type="date"
                value={filters.toDate}
                onChange={(e) => setFilters((f) => ({ ...f, toDate: e.target.value }))}
              />
            </Field>
          </div>
        </div>

        <footer className="wit-sync-foot">
          <button
            type="button"
            className="btn btn-secondary"
            onClick={onClose}
            disabled={running}
          >
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => void runSync()}
            disabled={running || !filters.repoId}
            title={!filters.repoId ? 'Pick a repo to sync' : undefined}
          >
            {running ? 'Running…' : 'Run sync'}
          </button>
        </footer>
      </div>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="wit-sync-field">
      <span className="wit-sync-field-label">{label}</span>
      {children}
      {hint && <span className="wit-sync-field-hint">{hint}</span>}
    </label>
  );
}

function initialFilters(meHandle: string | null): SyncFilters {
  const todayIso = new Date().toISOString().slice(0, 10);
  const fromIso = new Date(Date.now() - 1000 * 60 * 60 * 24 * 30).toISOString().slice(0, 10);
  // repoId is set on open from the active product's repo list — see the
  // useEffect above. Seed empty to avoid showing a stale default.
  return {
    repoId: '',
    areaPath: '',
    workItemTypes: [],
    assignedTo: meHandle ?? '',
    fromDate: fromIso,
    toDate: todayIso,
  };
}
