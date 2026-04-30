// Phase 25 — PR sync dialog. Collects filters (repo, creator, date range,
// status) and triggers POST /api/prs/sync to pull PRs from ADO or GitHub
// into the local cache. Follows the same modal pattern as SyncDialog.

import { useEffect, useMemo, useRef, useState } from 'react';
import { useToastStore } from '@/state/useToastStore';
import { useCurrentUser } from '@/state/useCurrentUserStore';
import { useProductsStore } from '@/state/useProductsStore';
import { forgeApi } from '@/api/forgeApi';
import { usePrsStore } from '@/state/usePrsStore';

interface Props {
  open: boolean;
  onClose: () => void;
}

interface PrSyncFilters {
  repoId: string;
  createdBy: string;
  fromDate: string;
  toDate: string;
  status: 'active' | 'completed' | 'all';
}

export function PrSyncDialog({ open, onClose }: Props) {
  const me = useCurrentUser();
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  const ref = useRef<HTMLDivElement | null>(null);
  const showToast = useToastStore((s) => s.show);

  const repos = useMemo(
    () => (activeProductId ? reposByProduct[activeProductId] ?? [] : []),
    [activeProductId, reposByProduct],
  );

  useEffect(() => {
    if (open && activeProductId && !reposByProduct[activeProductId]) {
      void loadReposFor(activeProductId);
    }
  }, [open, activeProductId, reposByProduct, loadReposFor]);

  const [filters, setFilters] = useState<PrSyncFilters>(() => ({
    repoId: '',
    createdBy: me.email ?? me.displayName ?? '',
    fromDate: thirtyDaysAgo(),
    toDate: today(),
    status: 'active',
  }));
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const reloadPrs = usePrsStore((s) => s.load);

  // Default repo to the active product's first repo when the dialog opens.
  useEffect(() => {
    if (open) {
      setFilters((f) => ({
        ...f,
        createdBy: f.createdBy || (me.email ?? me.displayName) || '',
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

  const selectedRepo = repos.find((r) => r.id === filters.repoId);
  const providerKind = selectedRepo?.kind ?? 'github';
  const providerLabel = providerKind === 'ado' ? 'ADO' : 'GitHub';

  async function runSync() {
    if (!filters.repoId) {
      setError('Pick a repository first.');
      return;
    }
    setRunning(true);
    setError(null);
    try {
      const result = await forgeApi.syncPrs({
        repoId: filters.repoId,
        provider: providerKind,
        createdBy: filters.createdBy || null,
        status: filters.status === 'all' ? null : filters.status,
        fromDate: filters.fromDate || null,
        toDate: filters.toDate || null,
      });
      const summary = `${result.pulled} pulled · ${result.inserted} new · ${result.updated} updated`;
      showToast(`PRs synced from ${providerLabel} · ${summary}`, 'success');
      void reloadPrs();
      onClose();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'sync failed';
      setError(msg);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="wit-sync-overlay" role="dialog" aria-modal="true" aria-labelledby="pr-sync-title">
      <div ref={ref} className="wit-sync-dialog">
        <header className="wit-sync-head">
          <div>
            <div className="wit-sync-eyebrow">25 · pr sync</div>
            <h2 id="pr-sync-title">Sync pull requests from {providerLabel}</h2>
            <p className="wit-sync-sub">
              Pulls PRs matching the filters below into the local cache from {providerLabel}.
            </p>
          </div>
          <button
            type="button"
            className="wit-sync-close"
            onClick={onClose}
            disabled={running}
            aria-label="Close"
          >×</button>
        </header>

        <div className="wit-sync-body">
          {/* Repo */}
          <div className="wit-sync-field">
            <span className="wit-sync-field-label">Repository</span>
            <select
              value={filters.repoId}
              onChange={(e) => setFilters((f) => ({ ...f, repoId: e.target.value }))}
            >
              <option value="" disabled>Select a repo…</option>
              {repos.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.displayName} ({r.kind})
                </option>
              ))}
            </select>
          </div>

          {/* Created by */}
          <div className="wit-sync-field">
            <span className="wit-sync-field-label">Created by</span>
            <input
              type="text"
              value={filters.createdBy}
              placeholder="Author name or email"
              onChange={(e) => setFilters((f) => ({ ...f, createdBy: e.target.value }))}
            />
          </div>

          {/* Status */}
          <div className="wit-sync-field">
            <span className="wit-sync-field-label">Status</span>
            <select
              value={filters.status}
              onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value as PrSyncFilters['status'] }))}
            >
              <option value="active">Active (open)</option>
              <option value="completed">Completed (merged)</option>
              <option value="all">All</option>
            </select>
          </div>

          {/* Date range */}
          <div className="wit-sync-row">
            <div className="wit-sync-field">
              <span className="wit-sync-field-label">From</span>
              <input
                type="date"
                value={filters.fromDate}
                onChange={(e) => setFilters((f) => ({ ...f, fromDate: e.target.value }))}
              />
            </div>
            <div className="wit-sync-field">
              <span className="wit-sync-field-label">To</span>
              <input
                type="date"
                value={filters.toDate}
                onChange={(e) => setFilters((f) => ({ ...f, toDate: e.target.value }))}
              />
            </div>
          </div>

          {error && (
            <div className="wit-sync-error">{error}</div>
          )}
        </div>

        <footer className="wit-sync-foot">
          {selectedRepo && (
            <span style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
              fontSize: 11,
              color: 'var(--text-tertiary)',
              marginRight: 'auto',
            }}>
              <span style={{
                width: 6, height: 6, borderRadius: 999,
                background: providerKind === 'ado' ? 'var(--info)' : 'var(--success)',
              }} />
              {providerLabel} · {selectedRepo.displayName}
            </span>
          )}
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
          >
            {running ? (
              <>
                <span className="wit-sync-spinner" />
                Syncing…
              </>
            ) : (
              <>
                <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
                  <path d="M2 6 a 4 4 0 0 1 7 -2.5 M 10 6 a 4 4 0 0 1 -7 2.5 M 9 1.5 V 3.5 H 7 M 3 10.5 V 8.5 H 5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
                Sync PRs
              </>
            )}
          </button>
        </footer>
      </div>
    </div>
  );
}

function thirtyDaysAgo(): string {
  const d = new Date();
  d.setDate(d.getDate() - 30);
  return d.toISOString().slice(0, 10);
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
