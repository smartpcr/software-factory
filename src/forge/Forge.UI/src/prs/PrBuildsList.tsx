import { useEffect, useMemo, useState } from 'react';
import { renderRichText } from '@/markdown';
import { automationCategory, classifyBuildEvent, isBuildEvent, summarizePlainText, type AutomationCategory, type BuildEventKind, type CiStatus } from './prBuildEvents';
import type { BuildStatus, PrComment } from '@/types';

// Phase 28.2 — Builds tab on the PR detail panel.
//
// Aggregates the build / CI / pipeline signals on a PR:
//   1. The current `pr.build` status (top card).
//   2. The build-log tail returned by `GET /api/prs/{id}/details`.
//   3. Per-run pipeline + voting events synced as PR comments.
//
// Uses {@link isBuildEvent} (the same classifier the Comments tab
// uses to *exclude* these) so the two tabs are guaranteed to be a
// disjoint partition of the comment list.

interface Props {
  /** Source-of-truth comments for the selected PR — fetched by `PrDetailPanel`. */
  comments: readonly PrComment[];
  /** Last fetch error from the parent's listPrComments call, if any. */
  error: string | null;
  /** Current build status, sourced from the PR row in the store. */
  build: BuildStatus | null;
  /** Last N lines of build output from `GET /api/prs/{id}/details`. */
  buildLogTail: readonly string[];
  /** Which automation category this list is rendering. The Builds tab
   *  passes `'build'`, Changes passes `'change'`, Policy passes
   *  `'policy'`. The list filters events to only its category. */
  category: AutomationCategory;
}

interface RowEvent {
  id: number;
  kind: BuildEventKind;
  name: string;
  body: string;
  at: string;
  vote?: number;
  voter?: string;
  author: string;
  pipelineName?: string;
  ciStatus?: CiStatus;
  /** When > 1, this row represents the latest of N runs/votes that
   *  were collapsed into one entry. Surfaced as a `· N runs` suffix. */
  occurrences?: number;
  /** All bodies merged for display (latest first) when this row is a
   *  collapse of multiple events. */
  combinedBody?: string;
}

/** Filter chip identity. `'all'`, `'votes'`, `'other'`, or a literal pipeline name. */
type BuildFilterId = string;

function voteBadge(vote: number | undefined): { label: string; cls: string } {
  if (vote === undefined) return { label: 'voted', cls: 'pending' };
  if (vote >= 10) return { label: `approved +${vote}`, cls: 'pass' };
  if (vote <= -10) return { label: `rejected ${vote}`, cls: 'fail' };
  if (vote < 0) return { label: `changes ${vote}`, cls: 'fail' };
  return { label: 'pending', cls: 'pending' };
}

function ciStatusBadge(s: CiStatus | undefined): { label: string; cls: string } {
  switch (s) {
    case 'failed':    return { label: 'failed',    cls: 'fail' };
    case 'succeeded': return { label: 'succeeded', cls: 'pass' };
    case 'running':   return { label: 'running',   cls: 'running' };
    case 'queued':    return { label: 'queued',    cls: 'pending' };
    case 'cancelled': return { label: 'cancelled', cls: 'pending' };
    default:          return { label: 'pipeline',  cls: 'pending' };
  }
}

function kindBadge(kind: BuildEventKind): { label: string; cls: string } | null {
  switch (kind) {
    case 'ref':           return { label: 'push',       cls: 'pending' };
    case 'auto-complete': return { label: 'auto',       cls: 'pending' };
    case 'policy':        return { label: 'policy',     cls: 'pending' };
    case 'reviewers':     return { label: 'reviewers',  cls: 'pending' };
    case 'assistant':     return { label: 'assistant',  cls: 'pending' };
    default:              return null;
  }
}

function formatDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const date = d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  const time = d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  return `${date}, ${time}`;
}

function buildStatusCls(status: string): string {
  switch (status) {
    case 'passing': return 'pass';
    case 'failing': return 'fail';
    case 'running': return 'running';
    case 'queued':  return 'pending';
    default:        return 'pending';
  }
}

function toRowEvent(c: PrComment): RowEvent | null {
  if (!isBuildEvent(c)) return null;
  const cls = classifyBuildEvent(c);
  return {
    id: c.id,
    kind: cls.kind,
    name: cls.name || summarizePlainText(c.body, 80) || '(no content)',
    body: c.body ?? '',
    at: c.createdAt,
    vote: cls.vote,
    voter: cls.voter,
    author: c.authorId && c.authorId.length > 0 ? c.authorId : 'system',
    pipelineName: cls.pipelineName,
    ciStatus: cls.ciStatus,
  };
}

/**
 * Group every CI pipeline run + every per-reviewer vote down to a
 * single row showing the latest status. Other event kinds (refs,
 * policy, auto-complete, etc.) pass through unchanged because each
 * is its own discrete event, not a state of an ongoing thing.
 */
function collapseLatestPerCheck(events: readonly RowEvent[]): RowEvent[] {
  const latest = new Map<string, RowEvent>();
  const counts = new Map<string, number>();
  const bodies = new Map<string, string[]>();
  const passthrough: RowEvent[] = [];

  const keyFor = (ev: RowEvent): string | null => {
    if (ev.kind === 'ci-pipeline' && ev.pipelineName) return `ci:${ev.pipelineName}`;
    if (ev.kind === 'vote' && ev.voter) return `vote:${ev.voter}`;
    return null;
  };

  for (const ev of events) {
    const key = keyFor(ev);
    if (!key) {
      passthrough.push(ev);
      continue;
    }
    counts.set(key, (counts.get(key) ?? 0) + 1);
    const list = bodies.get(key);
    if (list) list.push(ev.body); else bodies.set(key, [ev.body]);
    const prev = latest.get(key);
    if (!prev || prev.at.localeCompare(ev.at) < 0) latest.set(key, ev);
  }

  const collapsed: RowEvent[] = [];
  for (const [key, ev] of latest) {
    const occurrences = counts.get(key) ?? 1;
    const all = bodies.get(key) ?? [ev.body];
    // Keep newest body first, then older ones separated by a divider —
    // this is what shows when the row is expanded.
    const ordered = [...all].sort().reverse();
    collapsed.push({
      ...ev,
      occurrences,
      combinedBody: occurrences > 1 ? ordered.join('\n\n---\n\n') : undefined,
    });
  }
  return [...passthrough, ...collapsed];
}

export function PrBuildsList({ comments, error, build, buildLogTail, category }: Props) {
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set());
  const [logExpanded, setLogExpanded] = useState(false);
  const [filter, setFilter] = useState<BuildFilterId>('all');

  const events = useMemo<RowEvent[]>(() => {
    const list: RowEvent[] = [];
    for (const c of comments) {
      const ev = toRowEvent(c);
      if (!ev) continue;
      if (automationCategory(ev.kind) !== category) continue;
      list.push(ev);
    }
    return collapseLatestPerCheck(list).sort((a, b) => b.at.localeCompare(a.at));
  }, [comments, category]);

  // Reset the filter chip when the category changes — `'AzSCI_E2E_PR_Bot'`
  // is meaningless on the Policy tab.
  useEffect(() => { setFilter('all'); }, [category]);

  // Build category-specific filter chips. Build tab shows
  // pipeline-name chips + Votes + Other; Policy tab shows
  // kind-based chips (Policy / Auto-complete / Reviewers); Change
  // tab is single-kind so it just gets the implicit "All".
  interface Chip { id: string; label: string; count: number; kind?: string }
  const chips = useMemo<Chip[]>(() => {
    const list: Chip[] = [{ id: 'all', label: 'All', count: events.length }];
    if (category === 'build') {
      const byPipeline = new Map<string, number>();
      let votes = 0;
      let other = 0;
      for (const ev of events) {
        if (ev.pipelineName) byPipeline.set(ev.pipelineName, (byPipeline.get(ev.pipelineName) ?? 0) + 1);
        else if (ev.kind === 'vote') votes++;
        else other++;
      }
      for (const [name, count] of byPipeline) list.push({ id: name, label: name, count, kind: 'pipeline' });
      if (votes > 0) list.push({ id: 'votes', label: 'Votes', count: votes, kind: 'votes' });
      if (other > 0) list.push({ id: 'other', label: 'Other', count: other });
    } else if (category === 'policy') {
      const byKind = new Map<BuildEventKind, number>();
      for (const ev of events) byKind.set(ev.kind, (byKind.get(ev.kind) ?? 0) + 1);
      const policyCount = byKind.get('policy') ?? 0;
      const autoCount = byKind.get('auto-complete') ?? 0;
      const revCount = byKind.get('reviewers') ?? 0;
      if (policyCount > 0) list.push({ id: 'kind:policy', label: 'Policy', count: policyCount });
      if (autoCount > 0) list.push({ id: 'kind:auto-complete', label: 'Auto-complete', count: autoCount });
      if (revCount > 0) list.push({ id: 'kind:reviewers', label: 'Reviewers', count: revCount });
    }
    return list;
  }, [events, category]);

  const filtered = useMemo(() => {
    if (filter === 'all') return events;
    if (filter === 'votes') return events.filter((ev) => ev.kind === 'vote');
    if (filter === 'other') return events.filter((ev) => !ev.pipelineName && ev.kind !== 'vote');
    if (filter.startsWith('kind:')) {
      const k = filter.slice(5) as BuildEventKind;
      return events.filter((ev) => ev.kind === k);
    }
    return events.filter((ev) => ev.pipelineName === filter);
  }, [events, filter]);

  const toggle = (id: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const showLogSection = buildLogTail.length > 0;

  return (
    <div className="pr-builds" data-testid="pr-builds-list">
      {build && (
        <div className={`pr-build-current pr-build-status-${buildStatusCls(build.status)}`}>
          <div className="pr-build-current-row">
            <span className={`pr-build-status-pill pr-build-status-${buildStatusCls(build.status)}`}>
              {build.status}
            </span>
            <span className="pr-build-current-step">{build.step}</span>
            <span className="pr-build-current-percent">{build.percent}%</span>
          </div>
          {build.failingTest && (
            <div className="pr-build-current-fail">FAIL · {build.failingTest}</div>
          )}
        </div>
      )}

      {showLogSection && (
        <div className="pr-build-log-section">
          <button
            type="button"
            className="pr-build-log-toggle"
            onClick={() => setLogExpanded((v) => !v)}
            aria-expanded={logExpanded}
          >
            <span className="caret" aria-hidden style={{ transform: logExpanded ? 'rotate(90deg)' : '' }}>›</span>
            Build log · {buildLogTail.length} lines
          </button>
          {logExpanded && (
            <pre className="pr-build-log">{buildLogTail.join('\n')}</pre>
          )}
        </div>
      )}

      {error && <div className="pr-comments-error">Error: {error}</div>}

      {events.length === 0 && !error && (
        <div className="pr-comments-empty">
          {category === 'change' ? 'No branch updates yet'
            : category === 'policy' ? 'No policy events yet'
            : 'No build events yet'}
        </div>
      )}

      {events.length > 0 && chips.length > 1 && (
        <div className="pr-comments-filters">
          {chips.map((chip) => (
            <BuildFilterChip
              key={chip.id}
              label={chip.label}
              count={chip.count}
              active={filter === chip.id}
              onClick={() => setFilter(chip.id)}
              kind={chip.kind}
            />
          ))}
        </div>
      )}

      {events.length > 0 && filtered.length === 0 && (
        <div className="pr-comments-empty">No events match this filter</div>
      )}

      {filtered.length > 0 && (
        <ul className="pr-build-events">
          {filtered.map((ev) => {
            const isOpen = expanded.has(ev.id);
            const ci = ev.kind === 'ci-pipeline' ? ciStatusBadge(ev.ciStatus) : null;
            const vote = ev.kind === 'vote' ? voteBadge(ev.vote) : null;
            const kBadge = ci || vote ? null : kindBadge(ev.kind);
            const occurrences = ev.occurrences ?? 1;
            const repeatLabel = occurrences > 1
              ? (ev.kind === 'ci-pipeline' ? `${occurrences} runs` : ev.kind === 'vote' ? `${occurrences} votes` : `${occurrences}×`)
              : null;
            const expandedBody = ev.combinedBody ?? ev.body;
            return (
              <li key={ev.id} className={`pr-build-row ${isOpen ? 'is-expanded' : ''}`}>
                <button
                  type="button"
                  className="pr-build-row-summary"
                  onClick={() => toggle(ev.id)}
                  aria-expanded={isOpen}
                >
                  <span className="caret" aria-hidden style={{ transform: isOpen ? 'rotate(90deg)' : '' }}>›</span>
                  <span className="pr-build-row-name">{ev.name}</span>
                  {repeatLabel && <span className="pr-build-row-repeat">{repeatLabel}</span>}
                  <span className="pr-build-row-time">{formatDateTime(ev.at)}</span>
                  {ci && (
                    <span className={`pr-build-status-pill pr-build-status-${ci.cls}`}>
                      {ci.label}
                    </span>
                  )}
                  {vote && (
                    <span className={`pr-build-status-pill pr-build-status-${vote.cls}`}>
                      {vote.label}
                    </span>
                  )}
                  {kBadge && (
                    <span className={`pr-build-status-pill pr-build-status-${kBadge.cls}`}>
                      {kBadge.label}
                    </span>
                  )}
                </button>
                {isOpen && (
                  <div className="pr-build-row-detail">
                    <div className="pr-build-row-meta">
                      <span className="pr-build-row-time">{formatDateTime(ev.at)}</span>
                      <span className="pr-build-row-author">{ev.author}</span>
                      {repeatLabel && <span className="pr-build-row-author">· {repeatLabel}</span>}
                    </div>
                    <div
                      className="pr-build-row-body detail-desc-md"
                      dangerouslySetInnerHTML={{ __html: renderRichText(expandedBody) }}
                    />
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function BuildFilterChip({
  label, count, active, onClick, kind,
}: { label: string; count: number; active: boolean; onClick: () => void; kind?: string }) {
  // Reuses the comments-tab chip styling; the parent wraps it in
  // `.pr-comments-filters` so the row sticks below the tab strip.
  const cls = ['pr-comments-filter'];
  if (active) cls.push('is-selected');
  if (kind) cls.push(`chip-${kind}`);
  return (
    <button className={cls.join(' ')} type="button" onClick={onClick} aria-pressed={active} title={label}>
      <span className="label">{label}</span>
      <span className="count">{count}</span>
    </button>
  );
}
