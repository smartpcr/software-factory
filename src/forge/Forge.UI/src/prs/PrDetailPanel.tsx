import { useEffect, useState } from 'react';
import { forgeApi, type PrDetailsResponse } from '@/api/forgeApi';
import { usePrsStore } from '@/state/usePrsStore';

// Phase 4.5 — PR detail panel. Reuses the prototype's .detail-panel
// chrome (CSS already in place from Phase 1). Adds a build-log tail
// and reviewer list specific to PRs.
export function PrDetailPanel() {
  const selectedId = usePrsStore((s) => s.selectedId);
  const pr = usePrsStore((s) => s.prs.find((p) => p.id === s.selectedId)) ?? null;
  const setSelected = usePrsStore((s) => s.setSelected);
  const toggleAutoMerge = usePrsStore((s) => s.toggleAutoMerge);
  const rerun = usePrsStore((s) => s.rerun);
  const cancel = usePrsStore((s) => s.cancel);

  const [details, setDetails] = useState<PrDetailsResponse | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!selectedId) { setDetails(null); return; }
    setLoading(true);
    forgeApi.getPrDetails(selectedId)
      .then((d) => { setDetails(d); })
      .catch(() => { setDetails(null); })
      .finally(() => setLoading(false));
  }, [selectedId]);

  const open = !!selectedId && !!pr;

  return (
    <aside className={`detail-panel${open ? ' open' : ''}`} id="pr-detail-panel">
      {pr && (
        <>
          <div className="detail-head">
            <div className="detail-head-row">
              <div className="detail-eyebrow">
                <span>{pr.id}</span>
                <span style={{ opacity: 0.4 }}>/</span>
                <span>{pr.epic}</span>
                <span style={{ opacity: 0.4 }}>/</span>
                <span>{pr.col}</span>
              </div>
              <button className="detail-close" title="Close" onClick={() => setSelected(null)}>
                <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                  <path d="M3 3 L 11 11 M 11 3 L 3 11" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                </svg>
              </button>
            </div>
            <h2 className="detail-title">{pr.title}</h2>
            <div className="detail-status-row">
              <span className="detail-status-pill">
                <span className="label">Branch</span>
                <span className="mono">{pr.branch}</span>
              </span>
              <span className="detail-status-pill">
                <span className="label">Author</span>
                <span>{pr.author.name}</span>
              </span>
              <span className="detail-status-pill">
                <span className="label">Auto-merge</span>
                <button
                  onClick={() => void toggleAutoMerge(pr.id)}
                  style={{
                    color: pr.autoMerge ? 'var(--info)' : 'var(--text-tertiary)',
                    fontWeight: 600,
                    cursor: 'pointer',
                  }}
                  data-testid="auto-merge-toggle"
                  data-state={pr.autoMerge ? 'on' : 'off'}
                >
                  {pr.autoMerge ? 'on' : 'off'}
                </button>
              </span>
            </div>
          </div>

          <div className="detail-body">
            <div className="detail-meta">
              <div className="detail-meta-cell"><div className="detail-meta-label">+ / −</div><div className="detail-meta-val mono">+{pr.additions} / −{pr.deletions}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Files</div><div className="detail-meta-val mono">{pr.files}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Commits</div><div className="detail-meta-val mono">{pr.commits}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Age</div><div className="detail-meta-val mono">{pr.age}</div></div>
            </div>

            {pr.humanGate && pr.gateReason && (
              <div className="detail-section" style={{ marginTop: 18 }}>
                <div className="detail-section-title">Human gate</div>
                <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>{pr.gateReason}</div>
              </div>
            )}

            <div className="detail-section">
              <div className="detail-section-title">Description</div>
              <div className="detail-desc">
                {loading ? 'Loading…' : details?.description ?? '—'}
              </div>
            </div>

            <div className="detail-section">
              <div className="detail-section-title">
                <span>Reviewers</span>
                <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)' }}>
                  {details?.reviewers.length ?? 0}
                </span>
              </div>
              {details?.reviewers.length ? (
                <ul className="detail-criteria" style={{ marginTop: 6 }}>
                  {details.reviewers.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
              ) : (
                <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
                  No reviewers yet
                </div>
              )}
            </div>

            <div className="detail-section">
              <div className="detail-section-title">Build log</div>
              <pre
                style={{
                  fontFamily: 'var(--font-mono)',
                  fontSize: 11,
                  background: 'var(--bg-sunken)',
                  padding: 10,
                  borderRadius: 'var(--radius-sm)',
                  overflowX: 'auto',
                  margin: 0,
                  color: 'var(--text-secondary)',
                }}
              >
                {(details?.buildLogTail ?? []).join('\n')}
              </pre>
            </div>
          </div>

          <div className="detail-actions">
            <button className="btn btn-secondary" onClick={() => void rerun(pr.id)}>Re-run build</button>
            <button className="btn btn-secondary" onClick={() => void cancel(pr.id)}>Cancel build</button>
            <button
              className="btn btn-ghost"
              onClick={() => void toggleAutoMerge(pr.id)}
            >
              {pr.autoMerge ? 'Disarm auto-merge' : 'Arm auto-merge'}
            </button>
          </div>
        </>
      )}
    </aside>
  );
}
