import { useMemo, useState } from 'react';
import { useTaskLive, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import {
  classifyTool,
  extractShellCommand,
  extractFilePath,
  basename,
  TOOL_CATEGORIES,
  CATEGORY_LABELS,
  CATEGORY_GLYPHS,
  CATEGORY_TONES,
  type ToolCategory,
} from '@/views/conversation/toolClassification';

interface Props {
  /**
   * Work-item id whose tool-call stream to render. For workstreams
   * this is the workstream id; for plan-doc work it's the synthetic
   * `plan-doc:{spawn}:{filename}` id (architect / evaluator tool calls
   * surface here too).
   */
  workItemId: string | null;
}

/**
 * 2026-05-16 — Tool tab content for the swarm-control conversation
 * pane. Replaces the inline "list of tool transcript lines" with a
 * structured view that classifies every tool-call into operator-
 * meaningful buckets, surfaces aggregate counts + total time per
 * category, and expands each call into a full args / result card
 * (with shell exit-code, file path, and error-only filter chips).
 *
 * <para>The pane reads the same <c>useTaskLive</c> transcript stream
 * the other special panes use (Build / Worktree), filters to
 * tool-call / tool-result events, then matches every call with its
 * result by walking the timeline (the event log is ordered but the
 * Copilot SDK can interleave concurrent tools — we pair by index
 * within the same iteration, which is correct for the current
 * single-flight runner contract).</para>
 *
 * <para>The data shape is forward-compatible with the Phase 2
 * backend enrichment plan: when <c>toolCallId</c> / <c>durationMs</c>
 * / <c>exitCode</c> appear on payloads, the cards render them; while
 * those fields are absent the pane falls back to wall-clock duration
 * (start.At → result.At) and best-effort exit-code sniff via
 * <see cref="pickExitCode"/>.</para>
 */
export function ToolsPane({ workItemId }: Props) {
  const live = useTaskLive(workItemId);
  const transcript = live?.transcript ?? [];

  const [category, setCategory] = useState<ToolCategory | 'all'>('all');
  const [errorsOnly, setErrorsOnly] = useState(false);

  const pairs = useMemo(() => buildToolPairs(transcript), [transcript]);

  const filteredPairs = useMemo(() => {
    return pairs.filter((p) => {
      if (errorsOnly && p.ok) return false;
      if (category !== 'all' && p.category !== category) return false;
      return true;
    });
  }, [pairs, category, errorsOnly]);

  const summary = useMemo(() => summarize(pairs), [pairs]);

  if (!workItemId) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ TOOLS</div>
        <div className="sub">Select a workstream to see its tool-call activity.</div>
      </div>
    );
  }

  if (pairs.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO TOOL ACTIVITY YET</div>
        <div className="sub">
          Tool calls appear here once the agent starts using <code>read_file</code>,{' '}
          <code>edit</code>, <code>run_in_terminal</code>, etc. Calls are grouped by
          category (file-read / file-write / shell / search / web / mcp) with
          per-call duration, arguments, results, and exit codes.
        </div>
      </div>
    );
  }

  return (
    <div className="tools-pane" style={{ padding: 6, overflow: 'auto' }}>
      <ToolsSummary
        summary={summary}
        activeCategory={category}
        errorsOnly={errorsOnly}
        onCategorySelect={setCategory}
        onErrorsToggle={() => setErrorsOnly((v) => !v)}
      />

      {filteredPairs.length === 0 ? (
        <div
          style={{
            padding: '24px 12px',
            textAlign: 'center',
            fontSize: 11,
            color: 'var(--sc-text-dim, #9ca3af)',
            fontFamily: 'var(--sc-font-mono, monospace)',
          }}
        >
          no calls match the current filter
        </div>
      ) : (
        filteredPairs.map((p) => <ToolCallCard key={p.key} pair={p} />)
      )}
    </div>
  );
}

// ── summary strip ───────────────────────────────────────────────────────

interface CategorySummary {
  count: number;
  errorCount: number;
  totalMs: number;
  /**
   * Per-tool-name breakdown inside this category. Keys are the raw
   * tool names emitted by the agent (e.g. `view`, `powershell`,
   * `report_intent`); values are the call counts. Used by the group
   * → kind disclosure so the operator can see WHICH specific tool
   * accounts for a bucket without expanding every individual card.
   */
  byTool: Record<string, { count: number; errorCount: number }>;
}

interface SummaryShape {
  total: number;
  errors: number;
  totalMs: number;
  byCategory: Record<ToolCategory, CategorySummary>;
}

function summarize(pairs: ToolPair[]): SummaryShape {
  const makeEmpty = (): CategorySummary => ({
    count: 0,
    errorCount: 0,
    totalMs: 0,
    byTool: {},
  });
  const byCategory: Record<ToolCategory, CategorySummary> = {
    'file-read':  makeEmpty(),
    'file-write': makeEmpty(),
    'shell':      makeEmpty(),
    'search':     makeEmpty(),
    'web':        makeEmpty(),
    'mcp':        makeEmpty(),
    'other':      makeEmpty(),
  };
  let total = 0;
  let errors = 0;
  let totalMs = 0;
  for (const p of pairs) {
    total++;
    if (!p.ok) errors++;
    totalMs += p.durationMs ?? 0;
    const row = byCategory[p.category];
    row.count++;
    if (!p.ok) row.errorCount++;
    row.totalMs += p.durationMs ?? 0;
    const kind = p.toolName || '(unknown)';
    const tool = row.byTool[kind] ?? { count: 0, errorCount: 0 };
    tool.count++;
    if (!p.ok) tool.errorCount++;
    row.byTool[kind] = tool;
  }
  return { total, errors, totalMs, byCategory };
}

interface SummaryProps {
  summary: SummaryShape;
  activeCategory: ToolCategory | 'all';
  errorsOnly: boolean;
  onCategorySelect: (c: ToolCategory | 'all') => void;
  onErrorsToggle: () => void;
}

/**
 * Compact two-level disclosure:
 * <list>
 *   <item>Collapsed (default): one line — Σ count · total time · ✗ errors.</item>
 *   <item>Click the leading chevron → reveals group rows (one per category).</item>
 *   <item>Click a group's chevron → reveals its kind breakdown (tool names + counts).</item>
 * </list>
 * Clicking a group row's body (not its chevron) sets/clears it as the
 * pane's category filter. Replaces the previous always-on histogram +
 * chip row, which used ~3× the vertical space.
 */
function ToolsSummary({
  summary,
  activeCategory,
  errorsOnly,
  onCategorySelect,
  onErrorsToggle,
}: SummaryProps) {
  const [groupsOpen, setGroupsOpen] = useState(false);
  const [openGroup, setOpenGroup] = useState<ToolCategory | null>(null);

  const visibleCategories = TOOL_CATEGORIES.filter(
    (c) => summary.byCategory[c].count > 0,
  );

  return (
    <div
      className="tools-pane__summary"
      style={{
        padding: '6px 10px',
        marginBottom: 6,
        border: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.08))',
        borderRadius: 3,
        background: 'var(--sc-surface, rgba(255, 255, 255, 0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
        fontSize: 11,
      }}
    >
      {/* Header — single compact row. Chevron toggles the group list,
          totals stay visible whether expanded or not. */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          flexWrap: 'wrap',
        }}
      >
        <button
          type="button"
          onClick={() => setGroupsOpen((v) => !v)}
          aria-expanded={groupsOpen}
          aria-label={groupsOpen ? 'hide tool breakdown' : 'show tool breakdown'}
          style={{
            all: 'unset',
            cursor: 'pointer',
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            color: 'var(--sc-text-dim, #9ca3af)',
            fontSize: 10,
            letterSpacing: '0.18em',
            textTransform: 'uppercase',
          }}
        >
          <span style={{ width: 10, textAlign: 'center' }}>
            {groupsOpen ? '⌄' : '›'}
          </span>
          ▌ TOOLS
        </button>
        <span style={{ color: 'var(--sc-text, currentColor)' }}>
          Σ <strong>{summary.total}</strong>
        </span>
        <span
          style={{ color: 'var(--sc-text-dim, #6b7280)', fontSize: 10 }}
          title="aggregate across all iters of this workstream (generator + evaluator combined)"
        >
          lifetime
        </span>
        {summary.totalMs > 0 && (
          <span style={{ color: 'var(--sc-text-dim, #9ca3af)' }}>
            · {formatDuration(summary.totalMs)}
          </span>
        )}
        {summary.errors > 0 && (
          <button
            type="button"
            onClick={onErrorsToggle}
            style={{
              all: 'unset',
              cursor: 'pointer',
              color: 'var(--sc-red, #f87171)',
              fontSize: 11,
              padding: '0 4px',
              border: errorsOnly
                ? '1px solid var(--sc-red, #f87171)'
                : '1px solid transparent',
              borderRadius: 2,
            }}
            title={errorsOnly ? 'show all calls' : 'filter to error calls only'}
          >
            · ✗ <strong>{summary.errors}</strong>
          </button>
        )}
        {activeCategory !== 'all' && (
          <button
            type="button"
            onClick={() => onCategorySelect('all')}
            style={{
              all: 'unset',
              cursor: 'pointer',
              marginLeft: 'auto',
              fontSize: 10,
              color: 'var(--sc-text-dim, #9ca3af)',
              textDecoration: 'underline dotted',
            }}
            title="clear category filter"
          >
            clear filter ({activeCategory})
          </button>
        )}
      </div>

      {/* Level-1 disclosure — group rows. Hidden until the operator
          opens the header chevron, keeping the collapsed state to a
          single line. */}
      {groupsOpen && (
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 1,
            marginTop: 6,
            paddingTop: 4,
            borderTop: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.06))',
          }}
        >
          {visibleCategories.length === 0 && (
            <span style={{ color: 'var(--sc-text-dim, #6b7280)', fontSize: 10 }}>
              no tool calls yet
            </span>
          )}
          {visibleCategories.map((c) => {
            const row = summary.byCategory[c];
            const tone = CATEGORY_TONES[c];
            const active = activeCategory === c;
            const open = openGroup === c;
            const kinds = Object.entries(row.byTool).sort(
              (a, b) => b[1].count - a[1].count,
            );
            return (
              <div key={c}>
                {/* Group row — two interactions: chevron toggles
                    kinds, body toggles category filter. */}
                <div
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '14px 14px 1fr auto auto',
                    alignItems: 'center',
                    gap: 6,
                    padding: '1px 2px',
                    borderRadius: 2,
                    background: active
                      ? 'var(--sc-surface-2, rgba(255, 255, 255, 0.04))'
                      : 'transparent',
                  }}
                >
                  <button
                    type="button"
                    onClick={() => setOpenGroup(open ? null : c)}
                    aria-expanded={open}
                    aria-label={open ? `hide ${c} kinds` : `show ${c} kinds`}
                    style={{
                      all: 'unset',
                      cursor: 'pointer',
                      textAlign: 'center',
                      color: 'var(--sc-text-dim, #6b7280)',
                      fontSize: 11,
                    }}
                  >
                    {open ? '⌄' : '›'}
                  </button>
                  <span
                    style={{
                      textAlign: 'center',
                      fontWeight: 'bold',
                      color: `var(${tone.var.split(',')[0]}, ${tone.fallback})`,
                    }}
                  >
                    {CATEGORY_GLYPHS[c]}
                  </span>
                  <button
                    type="button"
                    onClick={() => onCategorySelect(active ? 'all' : c)}
                    title={active ? 'click to clear filter' : `filter by ${c}`}
                    style={{
                      all: 'unset',
                      cursor: 'pointer',
                      color: active
                        ? 'var(--sc-text, currentColor)'
                        : 'var(--sc-text-dim, #9ca3af)',
                      textAlign: 'left',
                    }}
                  >
                    {CATEGORY_LABELS[c]}
                  </button>
                  <span style={{ textAlign: 'right', color: 'var(--sc-text, currentColor)' }}>
                    <strong>{row.count}</strong>
                    {row.errorCount > 0 && (
                      <span style={{ color: 'var(--sc-red, #f87171)', marginLeft: 4 }}>
                        ✗{row.errorCount}
                      </span>
                    )}
                  </span>
                  <span
                    style={{
                      textAlign: 'right',
                      color: 'var(--sc-text-dim, #9ca3af)',
                      minWidth: 44,
                      fontSize: 10,
                    }}
                  >
                    {row.totalMs > 0 ? formatDuration(row.totalMs) : ''}
                  </span>
                </div>

                {/* Level-2 disclosure — kind breakdown. Inline, single
                    line of `name count` pairs separated by ·. Indents
                    under the glyph column so the visual nesting is
                    obvious without nested borders. */}
                {open && kinds.length > 0 && (
                  <div
                    style={{
                      paddingLeft: 36,
                      paddingTop: 1,
                      paddingBottom: 3,
                      fontSize: 10,
                      lineHeight: 1.5,
                      color: 'var(--sc-text-dim, #9ca3af)',
                      whiteSpace: 'normal',
                    }}
                  >
                    {kinds.map(([name, info], ix) => (
                      <span key={name}>
                        {ix > 0 && <span style={{ opacity: 0.4 }}> · </span>}
                        <span style={{ color: 'var(--sc-text, currentColor)' }}>
                          {name}
                        </span>{' '}
                        <span>{info.count}</span>
                        {info.errorCount > 0 && (
                          <span
                            style={{
                              color: 'var(--sc-red, #f87171)',
                              marginLeft: 2,
                            }}
                          >
                            ✗{info.errorCount}
                          </span>
                        )}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ── tool call card ───────────────────────────────────────────────────────

interface CardProps {
  pair: ToolPair;
}

function ToolCallCard({ pair }: CardProps) {
  const [expanded, setExpanded] = useState(false);
  const tone = CATEGORY_TONES[pair.category];
  const accentColor = `var(${tone.var.split(',')[0]}, ${tone.fallback})`;
  const headline = oneLineSummary(pair);

  return (
    <div
      className="tools-pane__card"
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        padding: '6px 10px',
        marginBottom: 4,
        border: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.08))',
        borderLeft: `3px solid ${pair.ok ? accentColor : 'var(--sc-red, #f87171)'}`,
        borderRadius: 3,
        background: 'var(--sc-surface, rgba(255, 255, 255, 0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
      }}
    >
      {/* Header row — clickable to expand */}
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        style={{
          all: 'unset',
          cursor: 'pointer',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          flexWrap: 'wrap',
          width: '100%',
        }}
      >
        <span
          style={{
            color: 'var(--sc-text-dim, #6b7280)',
            fontSize: 11,
            width: 12,
            textAlign: 'center',
          }}
        >
          {expanded ? '⌄' : '›'}
        </span>
        <span
          style={{
            fontSize: 10,
            letterSpacing: '0.12em',
            textTransform: 'uppercase',
            padding: '1px 5px',
            border: `1px solid ${accentColor}`,
            color: accentColor,
            borderRadius: 2,
          }}
        >
          {CATEGORY_GLYPHS[pair.category]} {CATEGORY_LABELS[pair.category]}
        </span>
        <span style={{ fontSize: 11, color: 'var(--sc-text, currentColor)' }}>
          {pair.toolName || '?'}
        </span>
        {headline && (
          <span
            style={{
              fontSize: 11,
              color: 'var(--sc-text-dim, #9ca3af)',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
              maxWidth: 480,
            }}
            title={headline}
          >
            {headline}
          </span>
        )}
        <span style={{ flex: 1 }} />
        {pair.exitCode != null && pair.exitCode !== 0 && (
          <span
            style={{ fontSize: 10, color: 'var(--sc-red, #f87171)' }}
            title="process exit code"
          >
            exit {pair.exitCode}
          </span>
        )}
        {!pair.ok && (
          <span style={{ fontSize: 10, color: 'var(--sc-red, #f87171)' }}>✗ error</span>
        )}
        {pair.durationMs != null && (
          <span style={{ fontSize: 10, color: 'var(--sc-text-dim, #9ca3af)' }}>
            ⏱ {formatDuration(pair.durationMs)}
          </span>
        )}
        <span style={{ fontSize: 10, color: 'var(--sc-text-dim, #6b7280)' }}>
          {new Date(pair.startedAt).toLocaleTimeString()}
        </span>
      </button>

      {/* Expanded body — args + result. Stays out of the DOM until
          the operator clicks the header so the pane stays light for
          large tool counts (a 1000-call iter is realistic). */}
      {expanded && (
        <div style={{ marginTop: 4, display: 'flex', flexDirection: 'column', gap: 6 }}>
          {pair.argsBlock && (
            <DetailBlock label="ARGS" body={pair.argsBlock} />
          )}
          {pair.cwd && (
            <div style={{ fontSize: 10, color: 'var(--sc-text-dim, #9ca3af)' }}>
              cwd: <code style={{ color: 'var(--sc-text, currentColor)' }}>{pair.cwd}</code>
            </div>
          )}
          {pair.error && (
            <DetailBlock label="ERROR" body={pair.error} tone="red" />
          )}
          {pair.resultBlock && (
            <DetailBlock label="RESULT" body={pair.resultBlock} />
          )}
          {!pair.argsBlock && !pair.resultBlock && !pair.error && (
            <div
              style={{
                fontSize: 10,
                color: 'var(--sc-text-dim, #9ca3af)',
                fontStyle: 'italic',
                paddingLeft: 18,
              }}
            >
              no payload captured for this call
            </div>
          )}
        </div>
      )}
    </div>
  );
}

interface DetailProps {
  label: string;
  body: string;
  tone?: 'red';
}

function DetailBlock({ label, body, tone }: DetailProps) {
  const labelColor = tone === 'red'
    ? 'var(--sc-red, #f87171)'
    : 'var(--sc-text-dim, #9ca3af)';
  return (
    <div>
      <div
        style={{
          fontSize: 9,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: labelColor,
          marginBottom: 2,
        }}
      >
        {label} <span style={{ opacity: 0.5 }}>· {body.split('\n').length} line{body.split('\n').length === 1 ? '' : 's'}</span>
      </div>
      <pre
        style={{
          margin: 0,
          padding: '6px 8px',
          background: 'var(--sc-surface-2, rgba(0, 0, 0, 0.18))',
          border: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.06))',
          borderRadius: 2,
          fontSize: 10,
          lineHeight: 1.4,
          maxHeight: 240,
          overflow: 'auto',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
          color: 'var(--sc-text, currentColor)',
        }}
      >
        {body}
      </pre>
    </div>
  );
}

// ── data shaping ─────────────────────────────────────────────────────────

interface ToolPair {
  key: string;
  toolName: string;
  category: ToolCategory;
  startedAt: string;
  durationMs: number | null;
  ok: boolean;
  exitCode: number | null;
  cwd: string | null;
  argsBlock: string | null;
  resultBlock: string | null;
  error: string | null;
  /** First-line summary used in the collapsed header. */
  filePath: string | null;
}

const TOOL_CALL_KINDS = new Set(['task.tool-call', 'task.toolcall']);
const TOOL_RESULT_KINDS = new Set(['task.tool-result', 'task.toolresult']);

/**
 * Walk the transcript chronologically, pair every tool-call with the
 * next tool-result that matches its tool name. The Copilot SDK is
 * single-flight today (one tool call at a time per session); even
 * when a future SDK upgrade lets concurrent calls overlap, matching
 * by tool name + earliest-unmatched-result is a reasonable fallback
 * until the backend supplies a stable <c>toolCallId</c> on both
 * events. When the backend lands the id, this matcher takes the
 * shortcut and uses it directly.
 */
function buildToolPairs(transcript: TaskTranscriptEvent[]): ToolPair[] {
  // Sort once. The store usually delivers events in id-order already
  // but we re-sort defensively so re-ordering an old event (e.g.
  // catch-up from a missed signalR connection) doesn't break the
  // pairing.
  const events = transcript
    .filter((e) => TOOL_CALL_KINDS.has(e.kind) || TOOL_RESULT_KINDS.has(e.kind))
    .slice()
    .sort((a, b) => a.id - b.id);

  const pairs: ToolPair[] = [];
  const unmatchedByName = new Map<string, TaskTranscriptEvent[]>();
  // Phase 2-ready: when backend stamps a toolCallId, prefer that
  // lookup over the name-based one. Both maps coexist so legacy
  // events still pair.
  const unmatchedById = new Map<string, TaskTranscriptEvent>();

  for (const e of events) {
    const p = (e.payload as Record<string, unknown> | null) ?? {};
    const toolName = pickToolName(p);
    const toolCallId = typeof p.toolCallId === 'string' ? (p.toolCallId as string) : null;

    if (TOOL_CALL_KINDS.has(e.kind)) {
      if (toolCallId) unmatchedById.set(toolCallId, e);
      else {
        const list = unmatchedByName.get(toolName) ?? [];
        list.push(e);
        unmatchedByName.set(toolName, list);
      }
      continue;
    }

    // tool-result — find matching start.
    let startEvent: TaskTranscriptEvent | null = null;
    if (toolCallId && unmatchedById.has(toolCallId)) {
      startEvent = unmatchedById.get(toolCallId)!;
      unmatchedById.delete(toolCallId);
    } else {
      const list = unmatchedByName.get(toolName);
      if (list && list.length > 0) {
        startEvent = list.shift() ?? null;
        if (list.length === 0) unmatchedByName.delete(toolName);
      }
    }

    pairs.push(makePair(startEvent, e));
  }

  // Flush stragglers — calls without a result (cancelled / still in
  // flight when the live store sliced off). They render as
  // "no result yet" which is useful diagnostic info.
  for (const list of unmatchedByName.values()) {
    for (const e of list) pairs.push(makePair(e, null));
  }
  for (const e of unmatchedById.values()) pairs.push(makePair(e, null));

  // Newest first — operators scrolling the live view want to see the
  // most recent call without scrolling to the bottom.
  pairs.sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1));
  return pairs;
}

function makePair(
  start: TaskTranscriptEvent | null,
  end: TaskTranscriptEvent | null,
): ToolPair {
  const startP = (start?.payload as Record<string, unknown> | null) ?? {};
  const endP = (end?.payload as Record<string, unknown> | null) ?? {};
  const toolName = pickToolName(startP) || pickToolName(endP);
  const mcpServer = pickString(startP, ['mcpServerName', 'McpServerName']);
  const category = classifyTool(toolName, mcpServer);

  const startedAt = start?.at ?? end?.at ?? new Date().toISOString();
  const durationMs = computeDuration(start, end);
  const argsObj = pickArgs(startP);
  const resultObj = pickResult(endP);

  const exitCode = pickExitCode(endP, resultObj);
  const cwd = pickString(resultObj, ['cwd', 'Cwd']);
  const errorMessage = pickString(endP, ['error']) ?? pickErrorMessage(endP);
  const ok = end ? errorMessage == null && (exitCode == null || exitCode === 0) : true;

  return {
    key: `${start?.id ?? 'no-start'}-${end?.id ?? 'no-end'}`,
    toolName,
    category,
    startedAt,
    durationMs,
    ok,
    exitCode,
    cwd,
    argsBlock: formatArgsBlock(category, argsObj),
    resultBlock: formatResultBlock(resultObj, endP),
    error: errorMessage,
    filePath: extractFilePath(argsObj),
  };
}

function pickToolName(p: Record<string, unknown>): string {
  return pickString(p, ['toolName', 'tool', 'name']) ?? '';
}

function pickString(obj: unknown, keys: string[]): string | null {
  if (obj == null || typeof obj !== 'object' || Array.isArray(obj)) return null;
  const o = obj as Record<string, unknown>;
  for (const k of keys) {
    if (typeof o[k] === 'string') return o[k] as string;
  }
  return null;
}

function pickArgs(p: Record<string, unknown>): unknown {
  // Backend serializes ToolExecutionStart.Arguments as JSON into
  // <c>toolPayload</c>. We try the parsed shape first (Phase 2
  // backend will provide an object directly under "arguments"),
  // then fall back to parsing the JSON string.
  if (p.arguments != null && typeof p.arguments === 'object') return p.arguments;
  if (typeof p.toolPayload === 'string') {
    try { return JSON.parse(p.toolPayload as string); } catch { return p.toolPayload; }
  }
  return p.toolPayload ?? null;
}

function pickResult(p: Record<string, unknown>): unknown {
  // Same shape preference as args — prefer parsed object, fall back
  // to the serialized string.
  if (p.result != null && typeof p.result === 'object') return p.result;
  if (typeof p.toolPayload === 'string') {
    try { return JSON.parse(p.toolPayload as string); } catch { return p.toolPayload; }
  }
  return p.toolPayload ?? null;
}

function pickExitCode(eventPayload: Record<string, unknown>, resultObj: unknown): number | null {
  if (typeof eventPayload.exitCode === 'number') return eventPayload.exitCode as number;
  if (resultObj == null || typeof resultObj !== 'object' || Array.isArray(resultObj)) return null;
  const r = resultObj as Record<string, unknown>;
  if (typeof r.exitCode === 'number') return r.exitCode as number;
  if (typeof r.ExitCode === 'number') return r.ExitCode as number;
  if (typeof r.exit_code === 'number') return r.exit_code as number;
  // SDK ToolExecutionCompleteContentTerminal nests exit code inside
  // Contents[]. The backend currently serializes Result directly so
  // this branch will fire only after Phase 2 enrichment lands; it's
  // harmless on legacy events.
  if (Array.isArray(r.Contents)) {
    for (const c of r.Contents) {
      if (c && typeof c === 'object' && 'ExitCode' in c) {
        const ec = (c as Record<string, unknown>).ExitCode;
        if (typeof ec === 'number') return ec;
      }
    }
  }
  return null;
}

function pickErrorMessage(p: Record<string, unknown>): string | null {
  const errObj = p.error;
  if (typeof errObj === 'string') return errObj;
  if (errObj && typeof errObj === 'object') {
    const e = errObj as Record<string, unknown>;
    if (typeof e.message === 'string') return e.message as string;
    if (typeof e.Message === 'string') return e.Message as string;
  }
  return null;
}

function computeDuration(start: TaskTranscriptEvent | null, end: TaskTranscriptEvent | null): number | null {
  // Prefer backend-supplied durationMs (Phase 2 enrichment). Fall
  // back to wall-clock between start.At and result.At — accurate
  // when both events are present in the transcript, which is the
  // common case.
  const endP = (end?.payload as Record<string, unknown> | null) ?? {};
  if (typeof endP.durationMs === 'number') return endP.durationMs as number;
  if (!start || !end) return null;
  try {
    const s = new Date(start.at).getTime();
    const e = new Date(end.at).getTime();
    if (!isFinite(s) || !isFinite(e)) return null;
    return Math.max(0, e - s);
  } catch {
    return null;
  }
}

function formatArgsBlock(category: ToolCategory, args: unknown): string | null {
  if (args == null) return null;
  // Shell command — pull out the command line so the operator
  // doesn't have to read JSON to see what ran.
  if (category === 'shell') {
    const cmd = extractShellCommand(args);
    if (cmd) return `$ ${cmd}`;
  }
  if (typeof args === 'string') return args.length > 0 ? args : null;
  try {
    return JSON.stringify(args, null, 2);
  } catch {
    return String(args);
  }
}

function formatResultBlock(result: unknown, eventPayload: Record<string, unknown>): string | null {
  // Prefer a typed shape (Phase 2 backend exposes
  // <c>Result.Content</c> string) — falls through to JSON dump
  // when nothing matches.
  if (result == null) {
    // Event-level fallback for legacy <c>task.tool-result</c> shape:
    // the result text was stamped directly onto the event payload's
    // <c>text</c> field.
    const text = pickString(eventPayload, ['text']);
    return text && text.length > 0 ? text : null;
  }
  if (typeof result === 'string') return result.length > 0 ? result : null;
  if (typeof result !== 'object') return String(result);
  const r = result as Record<string, unknown>;
  if (typeof r.Content === 'string' && r.Content.length > 0) return r.Content as string;
  if (typeof r.content === 'string' && r.content.length > 0) return r.content as string;
  if (typeof r.text === 'string' && r.text.length > 0) return r.text as string;
  if (typeof r.Text === 'string' && r.Text.length > 0) return r.Text as string;
  // Generic dump.
  try {
    return JSON.stringify(result, null, 2);
  } catch {
    return String(result);
  }
}

function oneLineSummary(pair: ToolPair): string | null {
  if (pair.category === 'shell') {
    // Show the command itself in the header.
    const args = pair.argsBlock;
    if (!args) return null;
    return args.startsWith('$ ') ? args.substring(2) : args;
  }
  if (pair.filePath) {
    // Show shortened path. Full path is in the args block when
    // expanded.
    const bn = basename(pair.filePath);
    return pair.filePath.length > 64 ? `…/${bn}` : pair.filePath;
  }
  return null;
}

// ── formatting helpers ──────────────────────────────────────────────────

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(2)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m${s.toString().padStart(2, '0')}s`;
}
