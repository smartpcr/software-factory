import { useEffect, useMemo, useState } from 'react';
import { forgeApi, type PrDetailsResponse } from '@/api/forgeApi';
import { usePrsStore } from '@/state/usePrsStore';
import { usePrLogStore } from '@/state/usePrLogStore';
import { PrCommentsList } from './PrCommentsList';

const EMPTY_LOG: readonly string[] = [];

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
  const approve = usePrsStore((s) => s.approve);
  const requestChanges = usePrsStore((s) => s.requestChanges);
  const comment = usePrsStore((s) => s.comment);
  const merge = usePrsStore((s) => s.merge);

  // Selector MUST return a stable reference — otherwise Zustand's
  // useSyncExternalStore re-renders forever (the "fresh []" trap).
  const allLogs = usePrLogStore((s) => s.lines);
  const clearLog = usePrLogStore((s) => s.clear);
  const liveLog = useMemo<readonly string[]>(
    () => (selectedId ? allLogs[selectedId] ?? EMPTY_LOG : EMPTY_LOG),
    [selectedId, allLogs],
  );

  const [details, setDetails] = useState<PrDetailsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [composer, setComposer] = useState('');

  useEffect(() => {
    if (!selectedId) { setDetails(null); return; }
    setLoading(true);
    forgeApi.getPrDetails(selectedId)
      .then((d) => { setDetails(d); })
      .catch(() => { setDetails(null); })
      .finally(() => setLoading(false));
  }, [selectedId]);

  useEffect(() => {
    if (selectedId) clearLog(selectedId);
  }, [selectedId, clearLog]);

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
                {loading ? 'Loading…' : details?.description || pr.title}
              </div>
            </div>

            {(details?.reviewers?.length ?? 0) > 0 && (
              <div className="detail-section">
                <div className="detail-section-title">
                  <span>Reviewers</span>
                  <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)' }}>
                    {details!.reviewers.length}
                  </span>
                </div>
                <ul className="detail-criteria" style={{ marginTop: 6 }}>
                  {details!.reviewers.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
              </div>
            )}

            {(details?.buildLogTail?.length ?? 0) > 0 || liveLog.length > 0 ? (
              <div className="detail-section">
                <div className="detail-section-title">
                  <span>Build log</span>
                  {liveLog.length > 0 && (
                    <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--success)', fontSize: 10 }}>
                      live · {liveLog.length}
                    </span>
                  )}
                </div>
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
                  {[...(details?.buildLogTail ?? []), ...liveLog].join('\n')}
                </pre>
              </div>
            ) : null}

            {(pr.openCommentCount ?? 0) > 0 && (
              <div className="detail-section">
                <div className="detail-section-title">
                  <span>Comments</span>
                  <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)' }}>
                    {pr.openCommentCount} open
                    {(pr.attentionCommentCount ?? 0) > 0 && ` · ${pr.attentionCommentCount} need response`}
                  </span>
                </div>
                <PrCommentsList prId={pr.id} />
              </div>
            )}

            <div className="detail-section">
              <div className="detail-section-title">Add a comment</div>
              <textarea
                value={composer}
                onChange={(e) => setComposer(e.target.value)}
                rows={3}
                placeholder="Comment, or describe requested changes…"
                style={{
                  width: '100%',
                  padding: 8,
                  fontFamily: 'inherit',
                  fontSize: 11.5,
                  background: 'var(--bg-surface)',
                  color: 'var(--text-primary)',
                  border: '1px solid var(--border-strong)',
                  borderRadius: 'var(--radius-sm)',
                  resize: 'vertical',
                }}
              />
              <div style={{ display: 'flex', gap: 6, marginTop: 6, flexWrap: 'wrap' }}>
                <button
                  className="btn btn-secondary"
                  disabled={!composer.trim()}
                  onClick={() => { void comment(pr.id, composer); setComposer(''); }}
                >
                  Comment
                </button>
                <button
                  className="btn btn-secondary"
                  disabled={!composer.trim()}
                  onClick={() => { void requestChanges(pr.id, composer); setComposer(''); }}
                >
                  Request changes
                </button>
              </div>
            </div>
          </div>

          <div className="detail-actions">
            <button
              className="btn btn-primary"
              style={{ background: 'var(--success)' }}
              onClick={() => void approve(pr.id)}
              data-testid="pr-approve"
            >
              Approve
            </button>
            <button className="btn btn-secondary" onClick={() => void merge(pr.id)} data-testid="pr-merge">
              Merge
            </button>
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
