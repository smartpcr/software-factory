import { useMemo } from 'react';
import { useTaskLive, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { isBuildEvent } from '@/views/conversation/filters';

interface Props {
  /**
   * Work-item id whose build / test stream to render. For workstreams
   * this is the workstream id; for plan-doc work it's the synthetic
   * `plan-doc:{spawn}:{filename}` id (which today rarely produces
   * build events but is supported for symmetry with the other panes).
   */
  workItemId: string | null;
}

/**
 * Phase 49 follow-up — Build tab content for the workstream
 * Conversation pane. Aggregates two streams in one chronological
 * timeline so the operator never has to context-switch to figure out
 * "why did this workstream stall":
 *
 *   1. **Forge gate events** — every <c>task.build-gate.passed</c> /
 *      <c>.failed</c> / <c>.skipped</c> and <c>task.test-gate.*</c>
 *      that <c>CrewStoryArtifactStrategy</c> emits at the
 *      build / quality / test gate trip points before opening a PR.
 *      These are the gates that block PR creation when the engineer's
 *      diff doesn't compile or breaks tests.
 *
 *   2. **Agent-issued build commands** — every tool-call / tool-result
 *      whose payload matches a known build/test command pattern
 *      (cargo build, cargo test, dotnet build, dotnet test, npm test,
 *      go test, pytest, etc.). These let the operator see the
 *      engineer agent's own verification attempts mid-iteration, not
 *      just the post-pass gate result.
 *
 * Both streams use the same status card layout: kind + command tag,
 * exit-code + warning-count badges, a duration pill, and an
 * expandable <pre> block for the error summary on failures.
 *
 * Mirrors <see cref="WorktreePane"/>'s "render-special" pattern: the
 * conversation panes branch when <c>filter === 'build'</c> and render
 * this component inline instead of the regular transcript list.
 */
export function BuildPane({ workItemId }: Props) {
  const live = useTaskLive(workItemId);
  const transcript = live?.transcript ?? [];

  const buildEvents = useMemo(() => transcript.filter(isBuildEvent), [transcript]);

  if (!workItemId) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ BUILD</div>
        <div className="sub">Select a workstream to see its build and test gate runs.</div>
      </div>
    );
  }

  if (buildEvents.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO BUILD ACTIVITY YET</div>
        <div className="sub">
          Build, quality, and test gate runs appear here once the engineer agent
          starts producing changes. Each row is one gate or one agent-issued
          {' '}<code>cargo build</code> / <code>cargo test</code> /
          {' '}<code>dotnet build</code> / <code>dotnet test</code> /
          {' '}<code>pytest</code> / etc. Forge refuses to open a PR if any
          post-pass gate is red.
        </div>
      </div>
    );
  }

  return (
    <div className="build-pane" style={{ padding: 6, overflow: 'auto' }}>
      {buildEvents.map((evt) => (
        <BuildEventCard key={`${evt.id}-${evt.kind}`} event={evt} />
      ))}
    </div>
  );
}

interface EventCardProps {
  event: TaskTranscriptEvent;
}

/**
 * Render one build/test event as a status card. Layout:
 * <pre>
 *   ┌─────────────────────────────────────────────────────────────────┐
 *   │ [STATUS] kind · stage · command           ⏱ 4.36s · ⚠ 22       │
 *   │ → optional one-line summary                                      │
 *   │ ┌─────────────────────────────────────────────────────────────┐ │
 *   │ │ error summary (only when failed)                            │ │
 *   │ │ ...                                                          │ │
 *   │ └─────────────────────────────────────────────────────────────┘ │
 *   └─────────────────────────────────────────────────────────────────┘
 * </pre>
 *
 * Status is derived from the event kind:
 *   - <c>*-gate.passed</c> / agent tool-result with exit 0  → green
 *   - <c>*-gate.failed</c> / <c>build-failed.aborted</c> / agent
 *     tool-result with non-zero exit → red
 *   - <c>*-gate.skipped</c> / agent tool-call (in-flight)    → gray
 *   - <c>build-failed.detected</c>                            → amber
 *     (per-iter warning, hasn't aborted yet)
 */
function BuildEventCard({ event }: EventCardProps) {
  const p = (event.payload as Record<string, unknown> | null) ?? {};
  const classification = classify(event, p);

  const cmd = (p.buildCommand as string)
    ?? (p.testCommand as string)
    ?? (p.qualityCommand as string)
    ?? extractAgentCommand(p);
  const stage = (p.stage as string) ?? null;
  const exitCode = typeof p.exitCode === 'number' ? (p.exitCode as number) : null;
  const errorCount = typeof p.errorCount === 'number' ? (p.errorCount as number) : null;
  const warningCount = typeof p.warningCount === 'number' ? (p.warningCount as number) : null;
  const duration = typeof p.durationSec === 'number' ? (p.durationSec as number) : null;
  const errorSummary = (p.errorSummary as string) ?? null;
  const reason = (p.reason as string) ?? null;

  // Friendly label for the kind. Shortens the full event kind to what
  // operators actually scan for: BUILD, BUILD (quality), TEST, etc.
  const label = labelOf(event, stage);

  // Color tokens — match the swarm-control theme variables so the card
  // looks right in both light and dark themes. Falls back to literal
  // hex codes for any environment that doesn't define the variables.
  const tone = classification.tone;
  const toneStyles: React.CSSProperties = {
    'green': { borderColor: 'var(--sc-green, #34d399)', color: 'var(--sc-green, #34d399)' },
    'red': { borderColor: 'var(--sc-red, #f87171)', color: 'var(--sc-red, #f87171)' },
    'amber': { borderColor: 'var(--sc-amber, #fbbf24)', color: 'var(--sc-amber, #fbbf24)' },
    'gray': { borderColor: 'var(--sc-text-dim, #6b7280)', color: 'var(--sc-text-dim, #6b7280)' },
  }[tone];

  return (
    <div
      className="build-pane__card"
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        padding: '8px 10px',
        marginBottom: 6,
        border: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.08))',
        borderLeft: `3px solid ${toneStyles?.borderColor ?? 'transparent'}`,
        borderRadius: 3,
        background: 'var(--sc-surface, rgba(255, 255, 255, 0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span
          className="build-pane__status"
          style={{
            fontSize: 10,
            letterSpacing: '0.18em',
            textTransform: 'uppercase',
            padding: '2px 6px',
            border: `1px solid ${toneStyles?.borderColor ?? 'transparent'}`,
            color: toneStyles?.color,
            borderRadius: 2,
          }}
        >
          {classification.statusText}
        </span>
        <span style={{ fontSize: 11, color: 'var(--sc-text, currentColor)' }}>{label}</span>
        {cmd && (
          <code
            style={{
              fontSize: 11,
              color: 'var(--sc-text-dim, #9ca3af)',
              background: 'var(--sc-surface-2, rgba(255, 255, 255, 0.04))',
              padding: '1px 5px',
              borderRadius: 2,
            }}
            title={cmd}
          >
            {truncate(cmd, 80)}
          </code>
        )}
        <span style={{ flex: 1 }} />
        {duration != null && (
          <span style={{ fontSize: 10, color: 'var(--sc-text-dim, #9ca3af)' }}>
            ⏱ {duration.toFixed(2)}s
          </span>
        )}
        {exitCode != null && exitCode !== 0 && (
          <span
            style={{ fontSize: 10, color: 'var(--sc-red, #f87171)' }}
            title="process exit code"
          >
            exit {exitCode}
          </span>
        )}
        {errorCount != null && errorCount > 0 && (
          <span style={{ fontSize: 10, color: 'var(--sc-red, #f87171)' }}>
            ✗ {errorCount}
          </span>
        )}
        {warningCount != null && warningCount > 0 && (
          <span style={{ fontSize: 10, color: 'var(--sc-amber, #fbbf24)' }}>
            ⚠ {warningCount}
          </span>
        )}
        <span style={{ fontSize: 10, color: 'var(--sc-text-dim, #9ca3af)' }}>
          {new Date(event.at).toLocaleTimeString()}
        </span>
      </div>

      {reason && (
        <div style={{ fontSize: 11, color: 'var(--sc-text-dim, #9ca3af)', paddingLeft: 2 }}>
          {reason}
        </div>
      )}

      {errorSummary && (
        <details style={{ marginTop: 2 }}>
          <summary
            style={{
              cursor: 'pointer',
              fontSize: 10,
              color: 'var(--sc-text-dim, #9ca3af)',
              letterSpacing: '0.06em',
            }}
          >
            error summary ({errorSummary.split('\n').length} line{errorSummary.split('\n').length === 1 ? '' : 's'})
          </summary>
          <pre
            style={{
              marginTop: 6,
              padding: '6px 8px',
              background: 'var(--sc-surface-2, rgba(0, 0, 0, 0.18))',
              border: '1px solid var(--sc-hairline, rgba(255, 255, 255, 0.06))',
              borderRadius: 2,
              fontSize: 10,
              lineHeight: 1.4,
              maxHeight: 320,
              overflow: 'auto',
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              color: 'var(--sc-text, currentColor)',
            }}
          >
            {errorSummary}
          </pre>
        </details>
      )}
    </div>
  );
}

/**
 * Phase 49 follow-up — derive (tone, statusText) from the event kind
 * + payload. Tone drives the card color (green / red / amber / gray);
 * statusText is the short label rendered inside the colored pill.
 */
function classify(
  event: TaskTranscriptEvent,
  p: Record<string, unknown>,
): { tone: 'green' | 'red' | 'amber' | 'gray'; statusText: string } {
  switch (event.kind) {
    case 'task.build-gate.passed':
    case 'task.test-gate.passed':
      return { tone: 'green', statusText: 'pass' };
    case 'task.build-gate.failed':
    case 'task.test-gate.failed':
    case 'task.build-failed.aborted':
      return { tone: 'red', statusText: 'fail' };
    case 'task.build-failed.detected':
      return { tone: 'amber', statusText: 'warn' };
    case 'task.build-gate.skipped':
    case 'task.test-gate.skipped':
      return { tone: 'gray', statusText: 'skip' };
    case 'task.tool-call':
    case 'task.toolcall':
      return { tone: 'gray', statusText: 'run' };
    case 'task.tool-result':
    case 'task.toolresult': {
      // Agent-issued build/test result. The tool framework stores the
      // exit code under different keys depending on backend; check a
      // few common places.
      const exit = pickExitCode(p);
      if (exit == null) return { tone: 'gray', statusText: 'done' };
      return exit === 0
        ? { tone: 'green', statusText: 'pass' }
        : { tone: 'red', statusText: 'fail' };
    }
    default:
      return { tone: 'gray', statusText: 'info' };
  }
}

/**
 * Phase 49 follow-up — pull an exit code out of an agent tool-result
 * payload. SDK and CLI backends vary in how they expose the code:
 *   - SDK (claude-code TS): payload.toolPayload.exit_code
 *   - CLI (claude-code Python): payload.text contains "exit 1" lines
 *   - Generic: payload.exitCode (set by some wrapper layers)
 */
function pickExitCode(p: Record<string, unknown>): number | null {
  if (typeof p.exitCode === 'number') return p.exitCode as number;
  const tp = p.toolPayload;
  if (tp && typeof tp === 'object' && !Array.isArray(tp)) {
    const inner = tp as Record<string, unknown>;
    if (typeof inner.exit_code === 'number') return inner.exit_code as number;
    if (typeof inner.exitCode === 'number') return inner.exitCode as number;
  }
  const text = typeof p.text === 'string' ? (p.text as string) : '';
  const m = /exit\s+(\d+)/i.exec(text);
  if (m) return parseInt(m[1], 10);
  return null;
}

/**
 * Phase 49 follow-up — short human label for an event kind. We don't
 * just dump the raw `task.test-gate.passed` string because the
 * operator's eye is mostly scanning for "is this build, test, or
 * quality" — keep the noise low.
 */
function labelOf(event: TaskTranscriptEvent, stage: string | null): string {
  switch (event.kind) {
    case 'task.build-gate.passed':
    case 'task.build-gate.failed':
    case 'task.build-gate.skipped': {
      if (stage === 'post-pass') return 'build gate';
      if (stage === 'post-pass-quality') return 'quality gate';
      return 'build gate';
    }
    case 'task.build-failed.detected':
      return 'per-iter build (warning)';
    case 'task.build-failed.aborted':
      return 'per-iter build (aborted)';
    case 'task.test-gate.passed':
    case 'task.test-gate.failed':
    case 'task.test-gate.skipped':
      return 'test gate';
    case 'task.tool-call':
    case 'task.toolcall':
      return 'agent → build';
    case 'task.tool-result':
    case 'task.toolresult':
      return 'agent ← result';
    default:
      return event.kind.replace(/^task\./, '');
  }
}

/**
 * Phase 49 follow-up — pull the command an agent invoked out of its
 * tool-call / tool-result payload. The format varies by backend:
 *   - Some shells stringify the args; we use `text` as the canonical
 *     source.
 *   - SDK style nests it under `toolPayload.command`.
 */
function extractAgentCommand(p: Record<string, unknown>): string | null {
  const text = typeof p.text === 'string' ? (p.text as string) : '';
  if (text) {
    // For tool-call events the text usually IS the command. Take the
    // first line so multi-line stdout from tool-result doesn't dominate.
    const firstLine = text.split('\n')[0].trim();
    if (firstLine) return firstLine;
  }
  const tp = p.toolPayload;
  if (tp && typeof tp === 'object' && !Array.isArray(tp)) {
    const inner = tp as Record<string, unknown>;
    if (typeof inner.command === 'string') return inner.command as string;
    if (typeof inner.cmd === 'string') return inner.cmd as string;
  }
  return null;
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s;
  return s.slice(0, max - 1) + '…';
}
