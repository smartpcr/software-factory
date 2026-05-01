import { useEffect, useMemo, useState } from 'react';
import { usePrsStore, filterPrs } from '@/state/usePrsStore';
import { useForgeHub } from '@/realtime/useForgeHub';
import { PrBoard, type PrBoardLayout } from '@/prs/PrBoard';
import { PrFilterBar } from '@/prs/PrFilterBar';
import { PrDetailPanel } from '@/prs/PrDetailPanel';
import { PrSyncDialog } from '@/prs/PrSyncDialog';
import { Toaster } from '@/board/Toaster';

const LAYOUT_KEY = 'forge.prs.layout';

function readStoredLayout(): PrBoardLayout {
  try {
    const raw = window.localStorage.getItem(LAYOUT_KEY);
    return raw === 'lifecycle' ? 'lifecycle' : 'flow';
  } catch {
    return 'flow';
  }
}

export function PrsView() {
  useForgeHub();

  const prs = usePrsStore((s) => s.prs);
  const load = usePrsStore((s) => s.load);
  const filter = usePrsStore((s) => s.filter);
  const authorFilter = usePrsStore((s) => s.authorFilter);
  const workstreamFilter = usePrsStore((s) => s.workstreamFilter);
  const searchQuery = usePrsStore((s) => s.searchQuery);
  const dateFrom = usePrsStore((s) => s.dateFrom);
  const dateTo = usePrsStore((s) => s.dateTo);
  const setSelected = usePrsStore((s) => s.setSelected);
  const toggleAutoMerge = usePrsStore((s) => s.toggleAutoMerge);
  const [syncOpen, setSyncOpen] = useState(false);
  const [layout, setLayout] = useState<PrBoardLayout>(readStoredLayout);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    try { window.localStorage.setItem(LAYOUT_KEY, layout); } catch { /* ignore */ }
  }, [layout]);

  const filtered = useMemo(
    () => filterPrs(prs, filter, authorFilter, workstreamFilter, searchQuery, dateFrom, dateTo),
    [prs, filter, authorFilter, workstreamFilter, searchQuery, dateFrom, dateTo],
  );

  const openCount = prs.length;
  const needApproval = prs.filter((p) => p.humanGate).length;
  const buildsRunning = prs.filter((p) => p.build?.status === 'running').length;

  return (
    <section className="view active" id="view-prs">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">04</span>&nbsp;Pull Requests</span>
            <span className="sep">/</span>
            <span>{openCount} open · {needApproval} need approval</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{buildsRunning} build{buildsRunning === 1 ? '' : 's'} running</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Pull request flow
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary" onClick={() => setSyncOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M2 6 a 4 4 0 0 1 7 -2.5 M 10 6 a 4 4 0 0 1 -7 2.5 M 9 1.5 V 3.5 H 7 M 3 10.5 V 8.5 H 5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            Sync PRs
          </button>
          <button className="btn btn-secondary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <circle cx="6" cy="6" r="4" stroke="currentColor" strokeWidth="1.3" fill="none" />
              <path d="M4 6 L 5.5 7.5 L 8 4.5" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            Recent merges
          </button>
          <button className="btn btn-primary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <circle cx="3" cy="6" r="1.5" stroke="currentColor" strokeWidth="1.3" />
              <path d="M9 4 V 9 a 1 1 0 0 1 -1 1 H 5" stroke="currentColor" strokeWidth="1.3" fill="none" />
            </svg>
            Open PR
          </button>
        </div>
      </div>

      <PrFilterBar prs={prs} />

      <div className="pr-board-toolbar">
        <div className="pr-layout-toggle" role="tablist" aria-label="Board layout">
          <button
            type="button"
            role="tab"
            aria-selected={layout === 'flow'}
            className={`pr-layout-toggle-btn ${layout === 'flow' ? 'active' : ''}`}
            onClick={() => setLayout('flow')}
            data-testid="pr-layout-flow"
            title="Forge agent flow — Implementation / Critical review / Build & merge"
          >
            Agent flow
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={layout === 'lifecycle'}
            className={`pr-layout-toggle-btn ${layout === 'lifecycle' ? 'active' : ''}`}
            onClick={() => setLayout('lifecycle')}
            data-testid="pr-layout-lifecycle"
            title="ADO PR lifecycle — Draft / In Review / Approved / Merged"
          >
            ADO lifecycle
          </button>
        </div>
      </div>

      <div className="pr-board-wrap">
        {prs.length === 0 ? (
          <div className="empty-state">
            <div className="title">No pull requests yet</div>
            <div className="sub">
              Open one from a workstream (Workstreams → PR), or wait for the configured
              provider to sync existing PRs in.
            </div>
          </div>
        ) : (
          <PrBoard
            prs={filtered}
            layout={layout}
            onOpen={(pr) => setSelected(pr.id)}
            onApprove={(pr) => void toggleAutoMerge(pr.id, true)}
            onRequestChanges={(pr) => setSelected(pr.id)}
          />
        )}
      </div>

      <PrDetailPanel />
      <PrSyncDialog open={syncOpen} onClose={() => setSyncOpen(false)} />
      <Toaster />
    </section>
  );
}
