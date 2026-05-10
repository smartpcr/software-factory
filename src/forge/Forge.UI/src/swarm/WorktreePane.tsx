import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive } from '@/realtime/useTaskLiveStore';

type CommitsResponse = Awaited<ReturnType<typeof forgeApi.getWorkItemCommits>>;

interface Props {
  /**
   * Work-item id whose worktree to render. For workstreams this is the
   * workstream id (the engineer crew runs the gen↔eval loop with the
   * workstream id as workItemId). For plan-doc work this is the synthetic
   * `plan-doc:{spawn}:{filename}` id.
   */
  workItemId: string | null;
}

/**
 * Worktree tab content — file changes per commit + uncommitted dirty list.
 *
 * Data sources:
 *  - <c>useTaskLive</c> → most recent <c>task.worktree-tick</c> snapshot
 *    (worktree path, ahead/behind, dirty file paths).
 *  - <c>GET /api/work-items/{id}/commits</c> → authoritative `git log` per
 *    commit with full added/modified/deleted/renamed file lists. Re-fetched
 *    whenever a new HEAD sha lands (transcript-tick coalesces multiple
 *    rapid commits).
 *
 * Mirrors the rendering used by <c>LiveExecutionView</c>'s worktree tab so
 * both surfaces stay visually consistent.
 */
export function WorktreePane({ workItemId }: Props) {
  const live = useTaskLive(workItemId);
  const snap = live?.worktree ?? null;
  const transcript = live?.transcript ?? [];

  const [data, setData] = useState<CommitsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Refetch on a new HEAD sha rather than every tick so the API isn't
  // hammered while the agent is mid-edit. The git-log endpoint is the
  // source of truth for per-commit file lists; the live tick is just the
  // dirty/ahead summary between commits.
  const latestTickSha = useMemo(() => {
    for (let i = transcript.length - 1; i >= 0; i--) {
      const e = transcript[i];
      if (e.kind !== 'task.worktree-tick') continue;
      const head = (e.payload as { head?: { sha?: string } } | null)?.head;
      if (head?.sha) return head.sha;
    }
    return null;
  }, [transcript]);

  useEffect(() => {
    if (!workItemId) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    forgeApi.getWorkItemCommits(workItemId)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [workItemId, latestTickSha]);

  if (!workItemId) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ WORKTREE</div>
        <div className="sub">Select a workstream to see its branch + commits.</div>
      </div>
    );
  }

  // First-load spinner. The /commits endpoint shells out to `git log` against
  // the worktree directory, which can take several seconds when the operator
  // first opens the tab (cold disk + slow `git fetch origin`). Show a busy
  // indicator instead of leaving the pane blank — otherwise the tab looks
  // broken / stuck. Once `data` lands we drop into the regular render below.
  if (loading && !snap && !data) {
    return <WorktreeBusySkeleton />;
  }

  if (!snap && !data && !error) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO WORKTREE ACTIVITY YET</div>
        <div className="sub">
          File changes appear here as the agent commits to the workstream branch.
          The plan-task / coding-task feeds populate this view in real time.
        </div>
      </div>
    );
  }

  const commits = data?.commits ?? [];
  const uncommitted = data?.uncommitted ?? { added: [] as string[], modified: [] as string[], deleted: [] as string[] };
  // Phase 49 follow-up — branch totals (committed + uncommitted vs
  // origin/baseBranch) are the right scope for the +N ~N -N pills.
  // `uncommitted` alone underreports the moment the agent commits.
  const branchTotals = data?.branchTotals ?? null;
  const hasDirty = uncommitted.added.length + uncommitted.modified.length + uncommitted.deleted.length > 0;

  // Worktree name = leaf directory of the path (matches the agent's branch
  // suffix under `.worktree/`). Falls back to the workstream id when the
  // path hasn't been resolved yet.
  const wtPath = data?.worktreePath ?? snap?.worktreePath ?? null;
  const wtLeaf = wtPath
    ? (wtPath.split(/[\\/]/).filter(Boolean).pop() ?? wtPath)
    : (snap?.workstreamId || workItemId);

  // Pills — prefer the API's authoritative ahead/behind + branch-total
  // file counts (committed + uncommitted vs origin/baseBranch); fall back
  // to the uncommitted-only counts when branchTotals isn't supplied (older
  // server build), then to the live snap when no API response loaded yet.
  const aheadCount = data?.ahead ?? snap?.ahead ?? 0;
  const behindCount = data?.behind ?? snap?.behind ?? 0;
  const addedCount = branchTotals
    ? branchTotals.added.length
    : (data ? uncommitted.added.length : (snap?.added ?? 0));
  const modifiedCount = branchTotals
    ? branchTotals.modified.length
    : (data ? uncommitted.modified.length : (snap?.modified ?? 0));
  const deletedCount = branchTotals
    ? branchTotals.deleted.length
    : (data ? uncommitted.deleted.length : (snap?.deleted ?? 0));
  const branch = data?.branch ?? null;

  return (
    <div className="swarm-worktree" style={{ padding: 4, overflow: 'auto' }}>
      {(wtLeaf || wtPath) && (
        <div className="live-exec__wt-name" title={wtPath ?? undefined}>
          {wtLeaf && <div className="live-exec__wt-name__leaf">{wtLeaf}</div>}
          {wtPath && <div className="live-exec__wt-name__path">{wtPath}</div>}
          {branch && (
            <div className="live-exec__wt-name__path" style={{ marginTop: 2 }}>
              ⎇ {branch}
            </div>
          )}
        </div>
      )}

      <div className="live-exec__wt-summary">
        <span className="live-exec__wt-pill added">+{addedCount}</span>
        <span className="live-exec__wt-pill modified">~{modifiedCount}</span>
        <span className="live-exec__wt-pill deleted">−{deletedCount}</span>
        {aheadCount  > 0 && <span className="live-exec__wt-pill ahead">↑{aheadCount}</span>}
        {behindCount > 0 && <span className="live-exec__wt-pill ahead">↓{behindCount}</span>}
        {commits.length > 0 && (
          <span className="live-exec__wt-pill" style={{ borderColor: 'var(--sc-border-2)', color: 'var(--sc-text-dim)' }}>
            {commits.length} commit{commits.length === 1 ? '' : 's'}
          </span>
        )}
        {loading && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 10, color: 'var(--sc-text-dim)' }}>
            <Spinner size={10} />
            <span>refreshing…</span>
          </span>
        )}
      </div>

      {error && (
        <div style={{
          fontSize: 11,
          color: 'var(--sc-red, rgb(220, 38, 38))',
          background: 'rgba(255, 107, 128, 0.06)',
          border: '1px solid var(--sc-red-d, rgb(220, 38, 38))',
          padding: '6px 10px',
          borderRadius: 4,
          margin: '6px 0',
          fontFamily: 'var(--sc-font-mono, monospace)',
        }}>
          ⚠ commits load failed: {error}
        </div>
      )}

      {commits.length > 0 && (
        <div className="live-exec__commits">
          {commits.map((c) => (
            <div key={c.sha} className="live-exec__commit-group">
              <div className="live-exec__wt-head">
                <div className="live-exec__wt-sha">{c.sha.slice(0, 8)}</div>
                <div className="live-exec__wt-msg">{c.message}</div>
                <div className="live-exec__wt-author">
                  {c.author} · {c.at ? new Date(c.at).toLocaleString() : ''}
                </div>
              </div>
              <div className="live-exec__file-list">
                {c.added.map((p) => <div key={`a${p}`} className="live-exec__file-row added">{p}</div>)}
                {c.modified.map((p) => <div key={`m${p}`} className="live-exec__file-row modified">{p}</div>)}
                {c.deleted.map((p) => <div key={`d${p}`} className="live-exec__file-row deleted">{p}</div>)}
                {c.other.map((o) => (
                  <div
                    key={`o${o.path}`}
                    className="live-exec__file-row modified"
                    title={o.oldPath ? `${o.status} from ${o.oldPath}` : o.status}
                  >
                    {o.status} {o.path}
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}

      {commits.length === 0 && !loading && data && (
        <div style={{ fontSize: 11, color: 'var(--sc-text-dim)', padding: '8px 0', fontFamily: 'var(--sc-font-mono, monospace)' }}>
          No commits yet on <code>{branch ?? '—'}</code>. The agent hasn't committed anything to this branch.
        </div>
      )}

      {commits.length === 0 && !data && snap?.headSha && (
        <div className="live-exec__wt-head">
          <div className="live-exec__wt-sha">{snap.headSha.slice(0, 8)}</div>
          <div className="live-exec__wt-msg">{snap.headMessage ?? ''}</div>
          <div className="live-exec__wt-author">
            {snap.headAuthor ?? ''} · {snap.headAt ? new Date(snap.headAt).toLocaleString() : ''}
          </div>
        </div>
      )}

      {hasDirty && (
        <div className="live-exec__commit-group" style={{ opacity: 0.78, marginTop: 12 }}>
          <div className="live-exec__wt-msg" style={{ fontStyle: 'italic', fontSize: 11 }}>
            Uncommitted changes (mid-iteration)
          </div>
          <div className="live-exec__file-list">
            {uncommitted.added.map((p)    => <div key={`ua${p}`} className="live-exec__file-row added">{p}</div>)}
            {uncommitted.modified.map((p) => <div key={`um${p}`} className="live-exec__file-row modified">{p}</div>)}
            {uncommitted.deleted.map((p)  => <div key={`ud${p}`} className="live-exec__file-row deleted">{p}</div>)}
          </div>
        </div>
      )}

      {snap?.truncated && (
        <div style={{ fontSize: 11, color: 'var(--sc-text-dim)', marginTop: 6, fontFamily: 'var(--sc-font-mono, monospace)' }}>
          list truncated at 200 paths.
        </div>
      )}
    </div>
  );
}

/**
 * Spinner glyph — two concentric arcs rotating at different speeds. Pure
 * SVG + CSS so it works on any theme without JS animation. Sized via the
 * `size` prop (px); text colour follows currentColor.
 */
function Spinner({ size = 14 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      style={{ animation: 'forge-spin 0.8s linear infinite', flexShrink: 0 }}
      aria-hidden
    >
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="2" strokeOpacity="0.18" />
      <path
        d="M 12 3 A 9 9 0 0 1 21 12"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * First-load skeleton for the worktree pane. The `/commits` endpoint can
 * take several seconds (cold disk, `git fetch origin` over the network),
 * so we show a busy indicator with placeholder shimmer rows instead of
 * leaving the pane blank — otherwise the operator wonders if the tab is
 * broken. Replaced by real content as soon as the API responds.
 */
function WorktreeBusySkeleton() {
  return (
    <div style={{ padding: 16 }}>
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        color: 'var(--sc-amber)',
        fontFamily: 'var(--sc-font-display, monospace)',
        fontSize: 10,
        letterSpacing: '0.22em',
        textTransform: 'uppercase',
        marginBottom: 14,
      }}>
        <Spinner size={14} />
        Loading worktree
        <span style={{ color: 'var(--sc-text-dim)', letterSpacing: '0.04em', textTransform: 'none', fontFamily: 'var(--sc-font-mono, monospace)', fontSize: 11 }}>
          fetching git log + tracking refs…
        </span>
      </div>

      {/* Placeholder rows — three commit-card shapes pulsing so the operator
       * sees structure, not just a void. */}
      {[0, 1, 2].map((i) => (
        <div
          key={i}
          style={{
            border: '1px solid var(--sc-hairline)',
            borderRadius: 4,
            padding: '10px 12px',
            marginBottom: 8,
            background: 'var(--sc-surface)',
            animation: `forge-skeleton-pulse 1.4s ease-in-out infinite`,
            animationDelay: `${i * 0.16}s`,
          }}
        >
          <div style={{
            height: 11,
            width: '38%',
            background: 'var(--sc-border)',
            borderRadius: 2,
            marginBottom: 8,
          }} />
          <div style={{
            height: 9,
            width: '72%',
            background: 'var(--sc-border)',
            borderRadius: 2,
            marginBottom: 6,
          }} />
          <div style={{
            height: 8,
            width: '50%',
            background: 'var(--sc-border)',
            borderRadius: 2,
          }} />
        </div>
      ))}
    </div>
  );
}
