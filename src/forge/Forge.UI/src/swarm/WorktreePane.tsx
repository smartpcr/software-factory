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

  if (!snap && !data && !loading && !error) {
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
  const hasDirty = uncommitted.added.length + uncommitted.modified.length + uncommitted.deleted.length > 0;

  // Worktree name = leaf directory of the path (matches the agent's branch
  // suffix under `.worktree/`). Falls back to the workstream id when the
  // path hasn't been resolved yet.
  const wtPath = data?.worktreePath ?? snap?.worktreePath ?? null;
  const wtLeaf = wtPath
    ? (wtPath.split(/[\\/]/).filter(Boolean).pop() ?? wtPath)
    : (snap?.workstreamId || workItemId);

  // Pills — prefer the API's authoritative ahead/behind + commit-derived
  // file counts; fall back to the live snap when the API hasn't loaded.
  const aheadCount = data?.ahead ?? snap?.ahead ?? 0;
  const behindCount = data?.behind ?? snap?.behind ?? 0;
  const addedCount = data ? uncommitted.added.length : (snap?.added ?? 0);
  const modifiedCount = data ? uncommitted.modified.length : (snap?.modified ?? 0);
  const deletedCount = data ? uncommitted.deleted.length : (snap?.deleted ?? 0);
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
        {loading && <span style={{ fontSize: 10, color: 'var(--sc-text-dim)' }}>loading commits…</span>}
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
