import { useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { renderRichText } from '@/markdown';
import { avatarFor, agentInfo } from '@/agentAvatar';
import { isAgentReview, isBuildEvent, summarizePlainText } from './prBuildEvents';
import type { PrComment, PrCommentState } from '@/types';

// Phase 28.2 redesign — threaded conversation list. The list shows
// collapsed thread rows by default (avatar / author / brief title /
// state / age / reply count). Clicking a row expands it inline to
// reveal the full markdown body and any replies. System events (ref
// updated, build voted, auto-complete set, CI bot pipeline posts) are
// filtered out via {@link isBuildEvent} — those live in the Builds
// tab so the comments view stays focused on actual review remarks.
//
// Comment data is owned by `PrDetailPanel` (so the verdict badge +
// the Builds tab can share a single fetch); this component is a pure
// view that asks the parent to refresh on resolve / reopen.

interface Props {
  prId: string;
  /** Source-of-truth comments for the selected PR — fetched by `PrDetailPanel`. */
  comments: readonly PrComment[];
  /** Last fetch error, if any. */
  error: string | null;
  /** Re-fetch the parent's comment list (called after resolve / reopen). */
  onRefresh: () => Promise<void> | void;
  /** When true, the caller is a human reviewer; agents see disabled action buttons. */
  callerIsHuman?: boolean;
}

type FilterId = 'all' | 'attention' | 'active' | 'closed';

const STATE_PILL: Record<PrCommentState, { cls: string; label: string }> = {
  active:     { cls: 'state-active',   label: 'active' },
  resolved:   { cls: 'state-resolved', label: 'resolved' },
  'wont-fix': { cls: 'state-wontfix',  label: "won't fix" },
  closed:     { cls: 'state-closed',   label: 'closed' },
  pending:    { cls: 'state-pending',  label: 'pending' },
};

function authorInitials(authorId: string | null | undefined): string {
  if (!authorId) return '??';
  const parts = authorId.split(/[.\s@_-]+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return authorId.slice(0, 2).toUpperCase();
}

function relativeAge(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '—';
  const sec = Math.max(0, (Date.now() - t) / 1000);
  if (sec < 60) return `${Math.floor(sec)}s`;
  if (sec < 3600) return `${Math.floor(sec / 60)}m`;
  if (sec < 86400) return `${Math.floor(sec / 3600)}h`;
  return `${Math.floor(sec / 86400)}d`;
}

/** Convenience wrapper around the shared `summarizePlainText`. */
function summarizeBody(body: string | null | undefined, max = 110): string {
  return summarizePlainText(body, max) || '(empty)';
}

/**
 * Resolve a friendly author name + bot flag + avatar URL for a comment row.
 * ADO PR Assistant remarks land with `authorKind: 'system'` and an empty
 * UPN, so the raw author would render as "unknown" — substitute the
 * canonical name and treat them as agent rows in the UI.
 *
 * Phase 49 follow-up — agent rows now resolve through
 * <see cref="agentInfo"/> so the displayed name is the FULL allocation
 * name (e.g. "Ling Zhu" rather than "LZ") and through
 * <see cref="avatarFor"/> so we can render the bundled character
 * portrait instead of a two-letter initial chip.
 */
function effectiveAuthor(c: PrComment): { name: string; role: string | null; isBot: boolean; avatarUrl: string | null } {
  if (isAgentReview(c)) {
    return { name: 'PR Assistant', role: null, isBot: true, avatarUrl: null };
  }
  if (c.authorKind === 'agent') {
    const info = agentInfo(c.authorId);
    const avatarUrl = avatarFor(c.authorId, info?.name);
    return {
      name: info?.name ?? c.authorId ?? 'agent',
      role: info?.role ?? null,
      isBot: true,
      avatarUrl,
    };
  }
  return {
    name: c.authorId && c.authorId.length > 0 ? c.authorId : 'unknown',
    role: null,
    isBot: false,
    avatarUrl: null,
  };
}

export function PrCommentsList({ prId, comments, error: parentError, onRefresh, callerIsHuman = true }: Props) {
  const [busyId, setBusyId] = useState<number | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [filter, setFilter] = useState<FilterId>('all');
  const [expandedThreads, setExpandedThreads] = useState<Set<string>>(() => new Set());

  const error = actionError ?? parentError;

  // Pipeline / vote / ref-update / policy events live in the Builds
  // tab. The classifier sniffs the body too so CI bot comments that
  // ADO posts as `authorKind: 'human'` still get routed correctly.
  const reviewComments = useMemo(
    () => comments.filter((c) => !isBuildEvent(c)),
    [comments],
  );

  const counts = useMemo(() => {
    let attention = 0; let active = 0; let closed = 0;
    for (const c of reviewComments) {
      if (c.requiresAttention) attention++;
      if (c.state === 'active') active++;
      if (c.state === 'resolved' || c.state === 'closed' || c.state === 'wont-fix') closed++;
    }
    return { all: reviewComments.length, attention, active, closed };
  }, [reviewComments]);

  const threads = useMemo(() => {
    const byThread = new Map<string, PrComment[]>();
    for (const c of reviewComments) {
      // Phase 49 follow-up — defensive grouping key. Empty threadId
      // (legacy data from before ProviderStoryProviderOps minted unique
      // ids) used to collapse every comment on a PR into one mega-thread.
      // Fall back to `parentCommentId ?? id` so each top-level comment
      // gets its own group and replies fold into their parent's group
      // (a reply has parentCommentId set; the parent has it null, so
      // both end up with the same legacy- key on the parent's id).
      const groupKey = c.threadId && c.threadId.length > 0
        ? c.threadId
        : `legacy-${c.parentCommentId ?? c.id}`;
      const arr = byThread.get(groupKey) ?? [];
      arr.push(c);
      byThread.set(groupKey, arr);
    }
    const list = Array.from(byThread.entries()).map(([threadId, items]) => {
      const sorted = [...items].sort((a, b) => {
        if (!a.parentCommentId && b.parentCommentId) return -1;
        if (a.parentCommentId && !b.parentCommentId) return 1;
        return a.createdAt.localeCompare(b.createdAt);
      });
      const parent = sorted[0];
      const replies = sorted.slice(1);
      const threadAttention = sorted.some((c) => c.requiresAttention);
      const threadState: PrCommentState = parent?.state ?? 'active';
      const lastActivityAt = sorted.reduce(
        (latest, c) => (c.updatedAt.localeCompare(latest) > 0 ? c.updatedAt : latest),
        parent?.updatedAt ?? '',
      );
      return { threadId, parent, replies, threadAttention, threadState, lastActivityAt };
    });
    return list.sort((a, b) => {
      // Attention first, then most-recent-activity.
      if (a.threadAttention !== b.threadAttention) return a.threadAttention ? -1 : 1;
      return b.lastActivityAt.localeCompare(a.lastActivityAt);
    });
  }, [reviewComments]);

  const filtered = useMemo(() => threads.filter((t) => {
    if (!t.parent) return false;
    if (filter === 'attention') return t.threadAttention;
    if (filter === 'active') return t.threadState === 'active';
    if (filter === 'closed') {
      return t.threadState === 'resolved' || t.threadState === 'closed' || t.threadState === 'wont-fix';
    }
    return true;
  }), [threads, filter]);

  if (error) {
    return <div className="pr-comments-error">Error: {error}</div>;
  }

  if (reviewComments.length === 0) {
    const buildEventCount = comments.length;
    return (
      <div className="pr-comments-empty">
        No review comments yet
        {buildEventCount > 0 && (
          <span className="pr-comments-empty-hint">
            {' '}— {buildEventCount} build / system event{buildEventCount === 1 ? '' : 's'} in the Builds tab
          </span>
        )}
      </div>
    );
  }

  const toggleThread = (id: string) => {
    setExpandedThreads((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const onResolve = async (c: PrComment) => {
    setBusyId(c.id);
    setActionError(null);
    try { await forgeApi.resolvePrComment(prId, c.id); await onRefresh(); }
    catch (e: unknown) { setActionError(e instanceof Error ? e.message : 'resolve failed'); }
    finally { setBusyId(null); }
  };

  const onReopen = async (c: PrComment) => {
    setBusyId(c.id);
    setActionError(null);
    try { await forgeApi.reopenPrComment(prId, c.id); await onRefresh(); }
    catch (e: unknown) { setActionError(e instanceof Error ? e.message : 'reopen failed'); }
    finally { setBusyId(null); }
  };

  const onReact = async (c: PrComment, reaction: 'thumbsup' | 'thumbsdown') => {
    setBusyId(c.id);
    setActionError(null);
    try { await forgeApi.reactToPrComment(prId, c.id, reaction); await onRefresh(); }
    catch (e: unknown) { setActionError(e instanceof Error ? e.message : 'react failed'); }
    finally { setBusyId(null); }
  };

  return (
    <div className="pr-comments" data-testid="pr-comments-list">
      <div className="pr-comments-filters">
        <FilterChip label="All"            count={counts.all}       active={filter === 'all'}       onClick={() => setFilter('all')} />
        <FilterChip label="Needs response" count={counts.attention} active={filter === 'attention'} onClick={() => setFilter('attention')} kind="attention" />
        <FilterChip label="Active"         count={counts.active}    active={filter === 'active'}    onClick={() => setFilter('active')} kind="active" />
        <FilterChip label="Closed"         count={counts.closed}    active={filter === 'closed'}    onClick={() => setFilter('closed')} kind="closed" />
      </div>

      <div className="pr-comments-list">
        {filtered.length === 0 && (
          <div className="pr-comments-empty">No comments match this filter</div>
        )}
        {filtered.map((t) => t.parent && (
          <CommentThread
            key={t.threadId}
            parent={t.parent}
            replies={t.replies}
            expanded={expandedThreads.has(t.threadId)}
            onToggle={() => toggleThread(t.threadId)}
            callerIsHuman={callerIsHuman}
            busyId={busyId}
            onResolve={onResolve}
            onReopen={onReopen}
            onReact={onReact}
            prId={prId}
            onRefresh={onRefresh}
          />
        ))}
      </div>
    </div>
  );
}

function FilterChip({
  label, count, active, onClick, kind,
}: { label: string; count: number; active: boolean; onClick: () => void; kind?: string }) {
  // Use `is-selected` for the selected state and `chip-{kind}` for the
  // type tint — the original `.active` class collided with the kind
  // name "active" so the Active chip rendered as if always selected.
  const cls = ['pr-comments-filter'];
  if (active) cls.push('is-selected');
  if (kind) cls.push(`chip-${kind}`);
  return (
    <button
      className={cls.join(' ')}
      onClick={onClick}
      type="button"
      aria-pressed={active}
    >
      <span className="label">{label}</span>
      <span className="count">{count}</span>
    </button>
  );
}

interface ThreadProps {
  parent: PrComment;
  replies: PrComment[];
  expanded: boolean;
  onToggle: () => void;
  callerIsHuman: boolean;
  busyId: number | null;
  onResolve: (c: PrComment) => void;
  onReopen: (c: PrComment) => void;
  onReact: (c: PrComment, reaction: 'thumbsup' | 'thumbsdown') => void;
  prId: string;
  onRefresh: () => Promise<void> | void;
}

function CommentThread({
  parent, replies, expanded, onToggle,
  callerIsHuman, busyId, onResolve, onReopen, onReact,
  prId, onRefresh,
}: ThreadProps) {
  const [replyOpen, setReplyOpen] = useState(false);
  const [replyText, setReplyText] = useState('');
  const [replying, setReplying] = useState(false);

  const submitReply = async () => {
    if (!replyText.trim()) return;
    setReplying(true);
    try {
      await forgeApi.createPrComment(prId, { body: replyText, parentCommentId: parent.id });
      setReplyText('');
      setReplyOpen(false);
      await onRefresh();
    } catch {
      // Error is shown via parent error state on next refresh
    } finally {
      setReplying(false);
    }
  };

  const cls = [
    'pr-comment-thread',
    expanded ? 'is-expanded' : 'is-collapsed',
    parent.requiresAttention ? 'is-attention' : '',
  ].filter(Boolean).join(' ');

  return (
    <div className={cls} data-thread-id={parent.threadId}>
      <ThreadSummaryRow
        parent={parent}
        replyCount={replies.length}
        expanded={expanded}
        onClick={onToggle}
      />
      {expanded && (
        <div className="pr-comment-thread-body">
          <CommentBody
            c={parent}
            isReply={false}
            callerIsHuman={callerIsHuman}
            busyId={busyId}
            onResolve={onResolve}
            onReopen={onReopen}
            onReact={onReact}
            onReply={() => setReplyOpen(true)}
          />
          {replies.map((r) => (
            <CommentBody
              key={r.id}
              c={r}
              isReply={true}
              callerIsHuman={callerIsHuman}
              busyId={busyId}
              onResolve={onResolve}
              onReopen={onReopen}
              onReact={onReact}
            />
          ))}
          {replyOpen && (
            <div className="pr-comment-reply-composer">
              <textarea
                value={replyText}
                onChange={(e) => setReplyText(e.target.value)}
                rows={2}
                placeholder="Write a reply…"
                autoFocus
                className="pr-comment-reply-input"
              />
              <div className="pr-comment-reply-actions">
                <button
                  className="btn btn-primary btn-sm"
                  disabled={!replyText.trim() || replying}
                  onClick={() => void submitReply()}
                >
                  {replying ? 'Sending…' : 'Reply'}
                </button>
                <button
                  className="btn btn-ghost btn-sm"
                  onClick={() => { setReplyOpen(false); setReplyText(''); }}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
          {!replyOpen && parent.state === 'active' && (
            <button
              className="btn btn-ghost btn-sm pr-comment-reply-btn"
              onClick={() => setReplyOpen(true)}
            >
              Reply
            </button>
          )}
        </div>
      )}
    </div>
  );
}

interface SummaryProps {
  parent: PrComment;
  replyCount: number;
  expanded: boolean;
  onClick: () => void;
}

function ThreadSummaryRow({ parent, replyCount, expanded, onClick }: SummaryProps) {
  const pill = STATE_PILL[parent.state];
  const title = useMemo(() => summarizeBody(parent.body), [parent.body]);
  const author = effectiveAuthor(parent);

  return (
    <button
      type="button"
      className={`pr-comment-summary${author.isBot ? ' is-bot' : ''}`}
      onClick={onClick}
      aria-expanded={expanded}
      data-testid="pr-comment-thread-row"
    >
      <span className="pr-comment-summary-caret" aria-hidden>
        <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
          <path
            d="M3 2 L 7 5 L 3 8"
            stroke="currentColor"
            strokeWidth="1.4"
            fill="none"
            strokeLinecap="round"
            strokeLinejoin="round"
            style={{ transform: expanded ? 'rotate(90deg)' : '', transformOrigin: '5px 5px', transition: 'transform 120ms' }}
          />
        </svg>
      </span>
      <span
        className={`pr-comment-avatar${author.avatarUrl ? ' has-image' : ''}`}
        title={author.role ? `${author.name} · ${author.role}` : author.name}
      >
        {author.avatarUrl ? (
          <img src={author.avatarUrl} alt={author.name} />
        ) : author.isBot ? (
          '🤖'
        ) : (
          <span>{authorInitials(parent.authorId)}</span>
        )}
      </span>
      <span className="pr-comment-summary-main">
        <span className="pr-comment-summary-meta">
          <span className="author">{author.name}</span>
          <span className={`state-pill ${pill.cls}`}>{pill.label}</span>
          {parent.requiresAttention && (
            <span className="attention-pill" data-testid="needs-response-pill">needs response</span>
          )}
          {replyCount > 0 && (
            <span className="pr-comment-reply-count" title={`${replyCount} ${replyCount === 1 ? 'reply' : 'replies'}`}>
              {replyCount} {replyCount === 1 ? 'reply' : 'replies'}
            </span>
          )}
          <span className="age">{relativeAge(parent.createdAt)}</span>
        </span>
        <span className="pr-comment-summary-title">{title}</span>
        {parent.filePath && (
          <span className="pr-comment-summary-file" title={`${parent.filePath}${parent.line != null ? `:${parent.line}` : ''}`}>
            {parent.filePath.split(/[\\/]/).pop()}{parent.line != null ? `:${parent.line}` : ''}
          </span>
        )}
      </span>
    </button>
  );
}

interface BodyProps {
  c: PrComment;
  isReply: boolean;
  callerIsHuman: boolean;
  busyId: number | null;
  onResolve: (c: PrComment) => void;
  onReopen: (c: PrComment) => void;
  onReply?: () => void;
  onReact: (c: PrComment, reaction: 'thumbsup' | 'thumbsdown') => void;
}

function CommentBody({ c, isReply, callerIsHuman, busyId, onResolve, onReopen, onReply: _onReply, onReact }: BodyProps) {
  const html = useMemo(() => renderRichText(c.body ?? ''), [c.body]);
  const author = effectiveAuthor(c);
  return (
    <div className={`pr-comment-row ${isReply ? 'is-reply' : ''}${author.isBot ? ' is-bot' : ''}`}>
      <div
        className={`pr-comment-avatar${author.avatarUrl ? ' has-image' : ''}`}
        title={author.role ? `${author.name} · ${author.role}` : author.name}
      >
        {author.avatarUrl ? (
          <img src={author.avatarUrl} alt={author.name} />
        ) : author.isBot ? (
          '🤖'
        ) : (
          <span>{authorInitials(c.authorId)}</span>
        )}
      </div>
      <div className="pr-comment-main">
        <div className="pr-comment-meta">
          <span className="author">{author.name}</span>
          {c.linkedCommitSha && (
            <span className="commit" title={c.linkedCommitSha}>↳ {c.linkedCommitSha.substring(0, 8)}</span>
          )}
          <span className="age">{relativeAge(c.createdAt)}</span>
        </div>
        <div
          className="pr-comment-body detail-desc-md"
          dangerouslySetInnerHTML={{ __html: html }}
        />
        <div className="pr-comment-actions">
          <button
            className={`btn btn-ghost btn-sm pr-react-btn${c.reaction === 'thumbsup' ? ' is-active' : ''}`}
            title="Thumbs up"
            onClick={() => onReact(c, 'thumbsup')}
            disabled={busyId === c.id}
          >
            👍{c.reaction === 'thumbsup' ? ' 1' : ''}
          </button>
          <button
            className={`btn btn-ghost btn-sm pr-react-btn${c.reaction === 'thumbsdown' ? ' is-active' : ''}`}
            title="Thumbs down"
            onClick={() => onReact(c, 'thumbsdown')}
            disabled={busyId === c.id}
          >
            👎{c.reaction === 'thumbsdown' ? ' 1' : ''}
          </button>
          {!isReply && (
            <>
              {c.state === 'active' ? (
                <button
                  className="btn btn-ghost btn-sm"
                  disabled={!callerIsHuman || busyId === c.id}
                  title={!callerIsHuman ? 'Only reviewers can resolve comments' : 'Resolve thread'}
                  onClick={() => onResolve(c)}
                >
                  Resolve
                </button>
              ) : (
                <button
                  className="btn btn-ghost btn-sm"
                  disabled={!callerIsHuman || busyId === c.id}
                  title={!callerIsHuman ? 'Only reviewers can reopen comments' : 'Reopen thread'}
                  onClick={() => onReopen(c)}
                >
                  Reopen
                </button>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
