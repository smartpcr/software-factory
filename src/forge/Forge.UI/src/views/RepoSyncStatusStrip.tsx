// -----------------------------------------------------------------------
// Phase 23.8 — RepoSyncStatusStrip. Inline status dot + "Last sync: Xm
// ago" text rendered next to a repo's row on the /products grid. Click
// opens a drawer with the last 20 sync runs (per Phase 23.7 GET
// /api/repos/{id}/sync-work-items/runs).
// -----------------------------------------------------------------------

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { Repo, WorkItemSyncRun } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { syncKindDisplay } from '@/products/syncKindDisplay';

const STATUS_COLOR: Record<NonNullable<Repo['lastSyncStatus']>, string> = {
  idle: 'rgb(34, 197, 94)',     // green
  running: 'rgb(20, 110, 245)', // blue
  failed: 'rgb(220, 38, 38)',   // red
};

const STATUS_LABEL: Record<NonNullable<Repo['lastSyncStatus']>, string> = {
  idle: 'Synced',
  running: 'Syncing…',
  failed: 'Sync failed',
};

/**
 * Phase 23.8 — pure helper. Resolves the dot colour for a repo's sync
 * status. Exported so the vitest spec can lock the contract without a DOM.
 */
export function statusDotColor(status: Repo['lastSyncStatus'] | undefined): string {
  if (!status) return 'var(--text-tertiary)';
  return STATUS_COLOR[status];
}

/**
 * Phase 23.8 — pure helper. "14m ago" / "2h ago" / "—" for the strip's
 * caption text.
 */
export function relativeTime(iso: string | null | undefined, nowMs?: number): string {
  if (!iso) return '—';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '—';
  const ms = (nowMs ?? Date.now()) - t;
  if (ms < 60_000) return 'just now';
  const m = Math.floor(ms / 60_000);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  return `${d}d ago`;
}

interface Props {
  repo: Repo;
  onSyncNow?: (repoId: string) => void | Promise<void>;
}

export function RepoSyncStatusStrip({ repo, onSyncNow }: Props) {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const status = repo.lastSyncStatus ?? 'idle';
  const color = statusDotColor(status);
  const label = STATUS_LABEL[status];

  const captionParts: string[] = [];
  captionParts.push(label);
  if (status !== 'failed') captionParts.push(relativeTime(repo.lastSyncAt));
  if (status === 'failed' && repo.lastSyncError) {
    captionParts.push(repo.lastSyncError.slice(0, 40));
  }

  return (
    <>
      <button
        type="button"
        data-testid={`repo-sync-strip-${repo.id}`}
        data-status={status}
        onClick={() => setDrawerOpen(true)}
        title={`Sync status — ${captionParts.join(' · ')}`}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 6,
          background: 'transparent',
          border: 'none',
          color: 'inherit',
          font: 'inherit',
          cursor: 'pointer',
          padding: '0 4px',
        }}
      >
        <span
          aria-hidden
          style={{
            width: 8,
            height: 8,
            borderRadius: 999,
            background: color,
            boxShadow: status === 'running' ? `0 0 0 3px color-mix(in srgb, ${color} 30%, transparent)` : 'none',
          }}
        />
        <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
          {captionParts.join(' · ')}
        </span>
      </button>
      {drawerOpen && (
        <RepoSyncDrawer
          repo={repo}
          onClose={() => setDrawerOpen(false)}
          onSyncNow={onSyncNow}
        />
      )}
    </>
  );
}

function RepoSyncDrawer({
  repo,
  onClose,
  onSyncNow,
}: {
  repo: Repo;
  onClose: () => void;
  onSyncNow?: (repoId: string) => void | Promise<void>;
}) {
  const [runs, setRuns] = useState<WorkItemSyncRun[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    forgeApi.listSyncRuns(repo.id, { limit: 20, signal: ac.signal })
      .then((rs) => setRuns(rs))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return;
        if (e instanceof Error && e.name === 'AbortError') return;
        setError(e instanceof Error ? e.message : String(e));
      });
    return () => { ac.abort(); };
  }, [repo.id]);

  // Esc-to-close + body-scroll lock while open.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
    };
  }, [onClose]);

  // Portal to <body> so the panel escapes any transformed/filtered ancestor
  // (the bug that caused the drawer to anchor to the product card instead of
  // the viewport edge — `position:fixed` is contained by transformed
  // ancestors per CSS spec).
  return createPortal(
    <>
      <div
        aria-hidden
        onClick={onClose}
        style={{
          position: 'fixed',
          inset: 0,
          background: 'rgba(0, 0, 0, 0.35)',
          zIndex: 9000,
        }}
      />
      <div
        role="dialog"
        aria-modal="true"
        data-testid={`repo-sync-drawer-${repo.id}`}
        onClick={(e) => e.stopPropagation()}
        style={{
          position: 'fixed',
          top: 0,
          right: 0,
          bottom: 0,
          width: 480,
          background: 'var(--bg-elevated, #1a1a1a)',
          color: 'var(--text-primary)',
          borderLeft: '1px solid var(--border, #333)',
          boxShadow: '-8px 0 24px rgba(0,0,0,0.4)',
          zIndex: 9001,
          display: 'flex',
          flexDirection: 'column',
        }}
      >
      <header style={{ padding: 12, borderBottom: '1px solid var(--border)', display: 'flex', alignItems: 'center', gap: 8 }}>
        <span
          aria-hidden
          style={{ width: 8, height: 8, borderRadius: 999, background: statusDotColor(repo.lastSyncStatus) }}
        />
        <strong style={{ flex: 1 }}>{repo.displayName} · sync history</strong>
        <button type="button" onClick={onClose} aria-label="Close" style={{ background: 'transparent', border: 'none', cursor: 'pointer', fontSize: 16 }}>×</button>
      </header>

      {repo.lastSyncStatus === 'failed' && repo.lastSyncError && (
        <div style={{ padding: 12, background: 'color-mix(in srgb, rgb(220, 38, 38) 8%, transparent)', borderBottom: '1px solid var(--border)', fontSize: 11.5 }}>
          <strong style={{ color: 'rgb(220, 38, 38)' }}>Last error:</strong> {repo.lastSyncError}
        </div>
      )}

      <div style={{ padding: 8, borderBottom: '1px solid var(--border)' }}>
        <button
          type="button"
          className="btn btn-secondary"
          onClick={() => onSyncNow?.(repo.id)}
          data-testid="sync-now-button"
        >
          Sync now
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto' }}>
        {error && <div style={{ padding: 12, color: 'rgb(220, 38, 38)' }}>Failed to load: {error}</div>}
        {runs === null && !error && <div style={{ padding: 12, color: 'var(--text-tertiary)' }}>Loading…</div>}
        {runs && runs.length === 0 && <div style={{ padding: 12, color: 'var(--text-tertiary)' }}>No sync runs yet.</div>}
        {runs && runs.length > 0 && (
          <table style={{ width: '100%', fontSize: 11.5, borderCollapse: 'collapse' }}>
            <thead>
              <tr style={{ textAlign: 'left', color: 'var(--text-tertiary)' }}>
                <th style={{ padding: '6px 8px' }}>Started</th>
                <th style={{ padding: '6px 8px' }}>Kind</th>
                <th style={{ padding: '6px 8px' }}>Δ items</th>
                <th style={{ padding: '6px 8px' }}>Result</th>
              </tr>
            </thead>
            <tbody>
              {runs.map((r) => (
                <>
                  <tr key={r.id} style={{ borderTop: '1px solid var(--border)' }}>
                    <td style={{ padding: '6px 8px' }}>{relativeTime(r.startedAt)}</td>
                    <td style={{ padding: '6px 8px' }}>{syncKindDisplay(r.kind).label}</td>
                    <td style={{ padding: '6px 8px' }}>+{r.itemsAdded}/~{r.itemsUpdated}{r.itemsConflicted > 0 ? `/!${r.itemsConflicted}` : ''}</td>
                    <td style={{ padding: '6px 8px', color: r.completedSuccessfully ? 'rgb(34, 197, 94)' : 'rgb(220, 38, 38)' }}>
                      {r.completedSuccessfully ? 'ok' : 'failed'}
                    </td>
                  </tr>
                  {!r.completedSuccessfully && r.errorMessage && (
                    <tr key={`${r.id}-err`}>
                      <td colSpan={4} style={{ padding: '4px 8px 8px', fontSize: 10.5, color: 'rgb(220, 38, 38)', whiteSpace: 'pre-wrap' }}>
                        {r.errorMessage}
                      </td>
                    </tr>
                  )}
                </>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
    </>,
    document.body,
  );
}
