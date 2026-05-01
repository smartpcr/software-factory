import { useCallback, useEffect, useMemo, useState } from 'react';
import { forgeApi, type PrDetailsResponse } from '@/api/forgeApi';
import { usePrsStore } from '@/state/usePrsStore';
import { usePrLogStore } from '@/state/usePrLogStore';
import { renderMarkdown } from '@/markdown';
import { PrCommentsList } from './PrCommentsList';
import { PrBuildsList } from './PrBuildsList';
import { PrApprovalBadge } from './PrApprovalBadge';
import { deriveVerdict } from './prVerdict';
import { automationCategory, classifyBuildEvent, isBuildEvent } from './prBuildEvents';
import type { PrComment } from '@/types';

type DetailTab = 'comments' | 'builds' | 'changes' | 'policy';

const EMPTY_LOG: readonly string[] = [];

function formatPrDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  // Date + 12-hr time so users can see "Apr 30, 12:01 PM" — matches what
  // the ADO web UI shows on the PR overview.
  const date = d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  const time = d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  return `${date}, ${time}`;
}

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
  const [maximized, setMaximized] = useState(false);
  const [activeTab, setActiveTab] = useState<DetailTab>('comments');
  const [comments, setComments] = useState<PrComment[]>([]);
  const [commentsError, setCommentsError] = useState<string | null>(null);

  // Reset tab + composer when switching PRs so a stale composer draft
  // never bleeds into a new PR view.
  useEffect(() => {
    setActiveTab('comments');
    setComposer('');
    setComments([]);
    setCommentsError(null);
  }, [selectedId]);

  const refreshComments = useCallback(async () => {
    if (!selectedId) return;
    try {
      const resp = await forgeApi.listPrComments(selectedId, { take: 200 });
      setComments(resp.items);
      setCommentsError(null);
    } catch (e: unknown) {
      setCommentsError(e instanceof Error ? e.message : 'Failed to load comments');
    }
  }, [selectedId]);

  useEffect(() => { void refreshComments(); }, [refreshComments]);

  const verdict = useMemo(() => deriveVerdict(comments), [comments]);

  const tabCounts = useMemo(() => {
    let buildCount = 0;
    let changeCount = 0;
    let policyCount = 0;
    let reviewCount = 0;
    let reviewActive = 0;
    let reviewAttention = 0;
    for (const c of comments) {
      if (isBuildEvent(c)) {
        const cls = classifyBuildEvent(c);
        const cat = automationCategory(cls.kind);
        if (cat === 'build') buildCount++;
        else if (cat === 'change') changeCount++;
        else policyCount++;
        continue;
      }
      reviewCount++;
      if (c.state === 'active') reviewActive++;
      if (c.requiresAttention) reviewAttention++;
    }
    return { reviewCount, buildCount, changeCount, policyCount, reviewActive, reviewAttention };
  }, [comments]);

  useEffect(() => {
    if (!selectedId) setMaximized(false);
  }, [selectedId]);

  useEffect(() => {
    if (!maximized) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMaximized(false); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [maximized]);

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
    <aside className={`detail-panel${open ? ' open' : ''}${maximized ? ' maximized' : ''}`} id="pr-detail-panel">
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
                {pr.webUrl && (
                  <a
                    href={pr.webUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    style={{ color: 'var(--accent)', fontSize: 10, marginLeft: 4, display: 'inline-flex', alignItems: 'center', gap: 2 }}
                    title="Open in ADO"
                  >
                    <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
                      <path d="M3.5 1.5H1.5V8.5H8.5V6.5M5.5 1.5H8.5V4.5M8.5 1.5L4.5 5.5" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" />
                    </svg>
                    Open
                  </a>
                )}
              </div>
              <div className="detail-head-actions">
                <button
                  className="detail-fullscreen"
                  title={maximized ? 'Restore (Esc)' : 'Maximize'}
                  aria-label={maximized ? 'Restore panel size' : 'Maximize panel'}
                  data-testid="pr-detail-fullscreen"
                  onClick={() => setMaximized((v) => !v)}
                >
                  {maximized ? (
                    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden>
                      <path d="M5 1 V 5 H 1 M 9 1 V 5 H 13 M 1 9 H 5 V 13 M 13 9 H 9 V 13" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  ) : (
                    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden>
                      <path d="M1 5 V 1 H 5 M 13 5 V 1 H 9 M 1 9 V 13 H 5 M 13 9 V 13 H 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  )}
                </button>
                <button className="detail-close" title="Close" onClick={() => setSelected(null)}>
                  <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                    <path d="M3 3 L 11 11 M 11 3 L 3 11" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                  </svg>
                </button>
              </div>
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
              {pr.workItemId && (
                <span className="detail-status-pill">
                  <span className="label">Work Item</span>
                  <span className="mono">{pr.workItemId}</span>
                </span>
              )}
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
              <PrApprovalBadge verdict={verdict} showPending />
            </div>
          </div>

          <div className="detail-body">
            <div className="detail-meta">
              <div className="detail-meta-cell"><div className="detail-meta-label">Files</div><div className="detail-meta-val mono">{pr.files}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Commits</div><div className="detail-meta-val mono">{pr.commits}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Updates</div><div className="detail-meta-val mono">{pr.updates ?? 0}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Age</div><div className="detail-meta-val mono">{pr.age}</div></div>
              <div className="detail-meta-cell">
                <div className="detail-meta-label">Created</div>
                <div className="detail-meta-val mono" title={pr.createdAt ?? ''}>{formatPrDateTime(pr.createdAt)}</div>
              </div>
              <div className="detail-meta-cell">
                <div className="detail-meta-label">Updated</div>
                <div className="detail-meta-val mono" title={pr.updatedAt ?? ''}>{formatPrDateTime(pr.updatedAt)}</div>
              </div>
            </div>

            {pr.humanGate && pr.gateReason && (
              <div className="detail-section" style={{ marginTop: 18 }}>
                <div className="detail-section-title">Human gate</div>
                <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>{pr.gateReason}</div>
              </div>
            )}

            <div className="detail-section">
              <div className="detail-section-title">Description</div>
              <PrDescription
                loading={loading}
                source={details?.description || pr.description || pr.title}
              />
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

            <div className="detail-tabs" role="tablist">
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'comments'}
                className={`detail-tab ${activeTab === 'comments' ? 'active' : ''}`}
                onClick={() => setActiveTab('comments')}
                data-testid="pr-tab-comments"
              >
                <span>Comments</span>
                {tabCounts.reviewCount > 0 && (
                  <span className="detail-tab-count">{tabCounts.reviewCount}</span>
                )}
                {tabCounts.reviewAttention > 0 && (
                  <span className="detail-tab-count-attention" title={`${tabCounts.reviewAttention} need response`}>
                    {tabCounts.reviewAttention}!
                  </span>
                )}
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'builds'}
                className={`detail-tab ${activeTab === 'builds' ? 'active' : ''}`}
                onClick={() => setActiveTab('builds')}
                data-testid="pr-tab-builds"
              >
                <span>Builds</span>
                {tabCounts.buildCount > 0 && (
                  <span className="detail-tab-count">{tabCounts.buildCount}</span>
                )}
                {pr.build && (
                  <span className={`detail-tab-build-status pr-build-status-${pr.build.status === 'passing' ? 'pass' : pr.build.status === 'failing' ? 'fail' : pr.build.status === 'running' ? 'running' : 'pending'}`}>
                    {pr.build.status}
                  </span>
                )}
                {liveLog.length > 0 && (
                  <span className="detail-tab-count" title="live log lines">
                    live · {liveLog.length}
                  </span>
                )}
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'changes'}
                className={`detail-tab ${activeTab === 'changes' ? 'active' : ''}`}
                onClick={() => setActiveTab('changes')}
                data-testid="pr-tab-changes"
              >
                <span>Changes</span>
                {tabCounts.changeCount > 0 && (
                  <span className="detail-tab-count">{tabCounts.changeCount}</span>
                )}
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'policy'}
                className={`detail-tab ${activeTab === 'policy' ? 'active' : ''}`}
                onClick={() => setActiveTab('policy')}
                data-testid="pr-tab-policy"
              >
                <span>Policy</span>
                {tabCounts.policyCount > 0 && (
                  <span className="detail-tab-count">{tabCounts.policyCount}</span>
                )}
              </button>
            </div>

            {activeTab === 'comments' && (
              <div className="detail-tab-panel" role="tabpanel">
                <PrCommentsList
                  prId={pr.id}
                  comments={comments}
                  error={commentsError}
                  onRefresh={refreshComments}
                />

                <div className="detail-section detail-composer">
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
            )}

            {activeTab === 'builds' && (
              <div className="detail-tab-panel" role="tabpanel">
                <PrBuildsList
                  comments={comments}
                  error={commentsError}
                  build={pr.build ?? null}
                  buildLogTail={[...(details?.buildLogTail ?? []), ...liveLog]}
                  category="build"
                />
              </div>
            )}

            {activeTab === 'changes' && (
              <div className="detail-tab-panel" role="tabpanel">
                <PrBuildsList
                  comments={comments}
                  error={commentsError}
                  build={null}
                  buildLogTail={[]}
                  category="change"
                />
              </div>
            )}

            {activeTab === 'policy' && (
              <div className="detail-tab-panel" role="tabpanel">
                <PrBuildsList
                  comments={comments}
                  error={commentsError}
                  build={null}
                  buildLogTail={[]}
                  category="policy"
                />
              </div>
            )}
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

/**
 * Renders the PR description as markdown (headings, bullets, bold, code,
 * links). Falls back to a plain pre-wrap pane when the source is empty
 * or while loading. Memoised so we only re-parse when `source` changes.
 */
function PrDescription({ loading, source }: { loading: boolean; source: string }) {
  const html = useMemo(() => renderMarkdown(source ?? ''), [source]);
  if (loading) return <div className="detail-desc detail-desc-md">Loading…</div>;
  if (!source?.trim()) return <div className="detail-desc detail-desc-md muted">— no description</div>;
  return <div className="detail-desc detail-desc-md" dangerouslySetInnerHTML={{ __html: html }} />;
}
