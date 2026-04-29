import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import type { PrComment, PrCommentState } from '@/types';

// Phase 24.10 — comment list rendered inside the PR detail panel.
// Groups comments by ThreadId; renders the parent first then the
// indented replies. Resolve / Reopen buttons are disabled for agents
// (Q24.5 — only humans can close threads).

interface Props {
  prId: string;
  /** When true, the caller is a human reviewer; agents see disabled action buttons. */
  callerIsHuman?: boolean;
}

const STATE_PILL_STYLE: Record<PrCommentState, { bg: string; fg: string; label: string }> = {
  active: { bg: 'var(--danger-bg)', fg: 'var(--danger)', label: 'active' },
  resolved: { bg: 'var(--success-bg)', fg: 'var(--success)', label: 'resolved' },
  'wont-fix': { bg: 'var(--bg-sunken)', fg: 'var(--text-tertiary)', label: "won't fix" },
  closed: { bg: 'var(--bg-sunken)', fg: 'var(--text-tertiary)', label: 'closed' },
  pending: { bg: 'var(--info-bg)', fg: 'var(--info)', label: 'pending' },
};

export function PrCommentsList({ prId, callerIsHuman = true }: Props) {
  const [comments, setComments] = useState<PrComment[]>([]);
  const [busyId, setBusyId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = async () => {
    try {
      const resp = await forgeApi.listPrComments(prId, { take: 200 });
      setComments(resp.items);
      setError(null);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Failed to load comments');
    }
  };

  useEffect(() => { void refresh(); }, [prId]);

  const threads = useMemo(() => {
    const byThread = new Map<string, PrComment[]>();
    for (const c of comments) {
      const arr = byThread.get(c.threadId) ?? [];
      arr.push(c);
      byThread.set(c.threadId, arr);
    }
    return Array.from(byThread.entries()).map(([threadId, items]) => ({
      threadId,
      // sort: parents first, then by createdAt
      items: [...items].sort((a, b) => {
        if (!a.parentCommentId && b.parentCommentId) return -1;
        if (a.parentCommentId && !b.parentCommentId) return 1;
        return a.createdAt.localeCompare(b.createdAt);
      }),
    }));
  }, [comments]);

  if (error) {
    return <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>Error: {error}</div>;
  }

  if (comments.length === 0) {
    return <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No comments</div>;
  }

  const onResolve = async (c: PrComment) => {
    setBusyId(c.id);
    try { await forgeApi.resolvePrComment(prId, c.id); await refresh(); }
    catch (e: unknown) { setError(e instanceof Error ? e.message : 'resolve failed'); }
    finally { setBusyId(null); }
  };

  const onReopen = async (c: PrComment) => {
    setBusyId(c.id);
    try { await forgeApi.reopenPrComment(prId, c.id); await refresh(); }
    catch (e: unknown) { setError(e instanceof Error ? e.message : 'reopen failed'); }
    finally { setBusyId(null); }
  };

  return (
    <div data-testid="pr-comments-list">
      {threads.map((t) => (
        <div key={t.threadId} className="pr-comment-thread" style={{ marginBottom: 12 }}>
          {t.items.map((c) => {
            const isReply = !!c.parentCommentId;
            const pill = STATE_PILL_STYLE[c.state];
            return (
              <div
                key={c.id}
                style={{
                  marginLeft: isReply ? 18 : 0,
                  borderLeft: isReply ? '2px solid var(--border-subtle)' : 'none',
                  paddingLeft: isReply ? 8 : 0,
                  marginTop: 6,
                }}
              >
                <div style={{ display: 'flex', gap: 6, alignItems: 'center', marginBottom: 4, flexWrap: 'wrap' }}>
                  <span
                    style={{
                      background: pill.bg, color: pill.fg, padding: '1px 6px',
                      borderRadius: 'var(--radius-sm)', fontSize: 10, fontWeight: 600,
                      textTransform: 'uppercase', letterSpacing: 0.4,
                    }}
                  >
                    {pill.label}
                  </span>
                  <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
                    {c.authorKind === 'agent' ? '🤖' : c.authorKind === 'system' ? '⚙' : '👤'} {c.authorId ?? 'unknown'}
                  </span>
                  {c.filePath && (
                    <span style={{ fontSize: 10, fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)' }}>
                      {c.filePath}{c.line != null ? `:${c.line}` : ''}
                    </span>
                  )}
                  {c.requiresAttention && (
                    <span
                      data-testid="needs-response-pill"
                      style={{
                        background: 'color-mix(in srgb, rgb(234, 88, 12) 14%, transparent)',
                        color: 'rgb(234, 88, 12)',
                        border: '1px solid rgb(234, 88, 12)',
                        padding: '0 6px', borderRadius: 'var(--radius-sm)',
                        fontSize: 10, fontWeight: 600, textTransform: 'uppercase',
                      }}
                    >
                      needs response
                    </span>
                  )}
                  {c.linkedCommitSha && (
                    <span style={{ fontSize: 10, color: 'var(--info)', fontFamily: 'var(--font-mono)' }}>
                      ↳ {c.linkedCommitSha.substring(0, 8)}
                    </span>
                  )}
                </div>
                <div style={{ fontSize: 11.5, color: 'var(--text-primary)', whiteSpace: 'pre-wrap' }}>
                  {c.body}
                </div>
                {!isReply && (
                  <div style={{ marginTop: 4, display: 'flex', gap: 6 }}>
                    {c.state === 'active' ? (
                      <button
                        className="btn btn-ghost"
                        disabled={!callerIsHuman || busyId === c.id}
                        title={!callerIsHuman ? 'Only reviewers can resolve comments' : 'Resolve thread'}
                        onClick={() => void onResolve(c)}
                      >
                        Resolve
                      </button>
                    ) : (
                      <button
                        className="btn btn-ghost"
                        disabled={!callerIsHuman || busyId === c.id}
                        title={!callerIsHuman ? 'Only reviewers can reopen comments' : 'Reopen thread'}
                        onClick={() => void onReopen(c)}
                      >
                        Reopen
                      </button>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}
