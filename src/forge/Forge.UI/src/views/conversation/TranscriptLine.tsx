// Phase 28 — shared paged-conversation transcript renderer. Used by the
// LiveExecutionView and DocConversationPane; both consume the same
// TaskTranscriptEvent stream and need identical event formatting.
import { useState } from 'react';
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { GateLogModal } from '@/swarm/GateLogModal';

export function TranscriptLine(props: {
  evt: TaskTranscriptEvent;
  generatorCrewId?: string | null;
  evaluatorCrewId?: string | null;
}): JSX.Element {
  const { evt, generatorCrewId, evaluatorCrewId } = props;
  if (
    evt.kind === 'task.iteration.scored'
    || evt.kind === 'task.iteration.passed'
    || evt.kind === 'task.iteration.rejected'
    || evt.kind === 'task.iteration.iterated'
    || evt.kind === 'task.iteration.stalled'
  ) {
    const payload = evt.payload as { iter?: number; score?: number; verdict?: string; notes?: string };
    const verdict = payload.verdict ?? evt.kind.replace('task.iteration.', '');
    return (
      <div className="live-exec__iter-block">
        <div className="live-exec__iter-rule">
          <span>iter {payload.iter ?? '?'}</span>
          <span className={`live-exec__score-chip ${verdict}`}>
            {payload.score ?? 0} · {verdict}
          </span>
        </div>
        {payload.notes && <div className="live-exec__iter-notes">{payload.notes}</div>}
      </div>
    );
  }
  if (evt.kind === 'task.prompt') {
    return <PromptLine evt={evt} />;
  }
  const side = evt.crewId === generatorCrewId ? 'generator'
             : evt.crewId === evaluatorCrewId ? 'evaluator'
             : null;
  const meta = new Date(evt.at).toLocaleTimeString();
  return (
    <div className="live-exec__line" data-kind={evt.kind} data-side={side ?? ''}>
      <span className="live-exec__line-meta">{meta}</span>
      <span className="live-exec__line-kind">{evt.kind.replace('task.', '')}</span>
      <span className="live-exec__line-text">{renderPayload(evt)}</span>
      <TestCommandChip evt={evt} />
      <LogAttachmentChip evt={evt} />
    </div>
  );
}

/**
 * Phase 59 Stage B — when a gate event payload carries `logUrl` (set by
 * StoryBuildRunner via CrewStoryArtifactStrategy), render an inline
 * `📎 view log` link that opens the full per-attempt stdout+stderr in
 * a new tab. Mirrors the precedent set by the `task.pr-opened`
 * handler in renderPayload, which embeds the PR webUrl as a clickable
 * inline string — same shape, dedicated to gate logs so the operator
 * never has to shell into the worktree to scroll a 50 MB test output.
 *
 * Skipped events (no command ran, nothing was written) do NOT carry
 * `logUrl` server-side; this component returns null in that case so
 * the chip never renders for a no-op gate.
 */
export function LogAttachmentChip(props: { evt: TaskTranscriptEvent }): JSX.Element | null {
  const [open, setOpen] = useState(false);
  const p = props.evt.payload as Record<string, unknown> | null;
  if (!p) return null;
  const logUrl = typeof p.logUrl === 'string' ? p.logUrl : '';
  if (!logUrl) return null;
  const impactedMode = typeof p.impactedMode === 'string' ? p.impactedMode : '';
  const gateStage = typeof p.gateStage === 'string' ? p.gateStage : '';
  const iter = typeof p.iter === 'number' ? p.iter : null;
  const modalTitle = [
    gateStage || 'gate',
    iter != null ? `iter ${iter}` : '',
    'log',
  ].filter(Boolean).join(' · ');
  const baseTooltip = 'View the full gate log (stdout + stderr)';
  const tooltip = impactedMode
    ? `${baseTooltip} · mode: ${impactedMode}`
    : baseTooltip;
  return (
    <>
      <button
        type="button"
        className="live-exec__log-chip"
        data-testid="transcript-log-chip"
        data-mode={impactedMode || undefined}
        title={tooltip}
        onClick={() => setOpen(true)}
        style={{
          marginLeft: 8,
          padding: '0 6px',
          fontSize: 11,
          fontWeight: 600,
          color: 'var(--info, rgb(56,189,248))',
          background: 'transparent',
          border: '1px solid var(--info, rgb(56,189,248))',
          borderRadius: 3,
          textDecoration: 'none',
          whiteSpace: 'nowrap',
          cursor: 'pointer',
          lineHeight: '18px',
        }}
      >
        📎 view log
      </button>
      <GateLogModal
        open={open}
        logUrl={logUrl}
        title={modalTitle}
        onClose={() => setOpen(false)}
      />
    </>
  );
}

/**
 * Phase 59 follow-up (2026-05-19) — render the actual `dotnet test`
 * (or other) command that ran + the narrowing summary. The
 * LogAttachmentChip was silent about WHICH tests ran; operators
 * had to inspect SignalR payloads in DevTools to find out whether
 * the resolver narrowed or fell back to the full suite. After a
 * 481s timeout on the FRP `update` repo (whole-suite ran, 31
 * test projects, 1 actual failure), the operator's first question
 * was "did it actually run any filter?" — this chip answers that
 * at a glance.
 *
 * Shape: `narrowed 2/31` (when the resolver filtered) or
 * `whole suite (31 projects)` (when it fell back) or
 * `verbatim cmd` (when the resolver no-opped). Effective command
 * sits in the tooltip so operators can copy-paste to reproduce
 * locally.
 */
export function TestCommandChip(props: { evt: TaskTranscriptEvent }): JSX.Element | null {
  const p = props.evt.payload as Record<string, unknown> | null;
  if (!p) return null;
  // Only test-gate events carry the impacted-test fields. Stay
  // silent on every other transcript event so we don't add noise.
  const gateStage = typeof p.gateStage === 'string' ? p.gateStage : '';
  if (gateStage !== 'test') return null;

  const effectiveCmd = typeof p.effectiveTestCommand === 'string' ? p.effectiveTestCommand : '';
  const originalCmd = typeof p.testCommand === 'string' ? p.testCommand : '';
  const rewritten = p.impactedRewritten === true;
  const wholeSuite = p.wholeSuiteFallback === true;
  const impactedCount = Array.isArray(p.impactedProjects) ? (p.impactedProjects as unknown[]).length : 0;
  const totalCount = typeof p.allTestProjectCount === 'number' ? p.allTestProjectCount : 0;

  // Nothing useful to show when the resolver didn't run (legacy
  // event shape, pre-Stage-A).
  if (!effectiveCmd && !originalCmd) return null;

  let label: string;
  let color = 'var(--text-secondary)';
  if (rewritten) {
    label = totalCount > 0 ? `narrowed ${impactedCount}/${totalCount}` : `narrowed (${impactedCount} project(s))`;
    color = 'var(--success, rgb(34,197,94))';
  } else if (wholeSuite) {
    label = totalCount > 0 ? `whole suite (${totalCount} projects)` : 'whole suite';
    color = 'var(--warning, rgb(245,158,11))';
  } else if (effectiveCmd === originalCmd) {
    // Resolver no-opped (operator override, unsupported command,
    // etc.). Common case: command already had --filter so the
    // resolver respected the operator's explicit choice.
    label = 'verbatim cmd';
  } else {
    label = 'cmd';
  }

  const cmd = effectiveCmd || originalCmd;
  return (
    <span
      data-testid="transcript-test-cmd-chip"
      title={`Ran: ${cmd}`}
      style={{
        marginLeft: 8,
        padding: '0 6px',
        fontSize: 11,
        fontWeight: 600,
        color,
        background: 'var(--bg-surface-alt, rgba(255,255,255,0.04))',
        border: `1px solid ${color}`,
        borderRadius: 3,
        whiteSpace: 'nowrap',
        fontFamily: 'var(--font-mono, monospace)',
      }}
    >
      {label}
    </span>
  );
}

/**
 * Phase 59 Stage B — predicate used by ConversationPane's "Has logs"
 * filter chip. Returns true when the event's payload carries a
 * `logUrl`, false otherwise. Exported so callers don't repeat the
 * payload-shape coupling.
 */
export function hasLogAttachment(evt: TaskTranscriptEvent): boolean {
  const p = evt.payload as Record<string, unknown> | null;
  return !!p && typeof p.logUrl === 'string' && (p.logUrl as string).length > 0;
}

function PromptLine(props: { evt: TaskTranscriptEvent }): JSX.Element {
  const { evt } = props;
  const p = evt.payload as Record<string, unknown> | null;
  const model = (p?.model as string) ?? '';
  const backend = (p?.backend as string) ?? '';
  const mode = (p?.invokeMode as string) ?? '';
  const meta = new Date(evt.at).toLocaleTimeString();
  return (
    <div className="live-exec__line" data-kind="task.prompt">
      <span className="live-exec__line-meta">{meta}</span>
      <span className="live-exec__line-kind">prompt</span>
      <span className="live-exec__line-text">
        [{backend}/{mode} · {model}] — see Prompts section above
      </span>
    </div>
  );
}

export function renderPayload(evt: TaskTranscriptEvent): string {
  const p = evt.payload as Record<string, unknown> | null;
  if (!p) return '';
  if (typeof p === 'string') return p;
  if ('delta' in p && typeof p.delta === 'string') return p.delta;
  if ('text'  in p && typeof p.text  === 'string') return p.text;
  if (evt.kind === 'task.delegated') {
    return `→ crew ${(p.generatorCrewId as string) ?? '?'}${p.evaluatorCrewId ? ` ↔ ${p.evaluatorCrewId}` : ''} (lead: ${(p.leadId as string) ?? '?'})`;
  }
  if (evt.kind === 'task.prompt') {
    const model = (p.model as string) ?? '';
    const backend = (p.backend as string) ?? '';
    const mode = (p.invokeMode as string) ?? '';
    return `[${backend}/${mode} · ${model}]`;
  }
  // Both crews — engineer emits `task.tool-call` (hyphenated), architect
  // emits `task.toolcall` (no hyphen). Same payload shape.
  if (evt.kind === 'task.tool-call' || evt.kind === 'task.toolcall') {
    const name = (p.toolName as string) ?? (p.tool as string) ?? '?';
    const payload = typeof p.toolPayload === 'string' ? p.toolPayload : '';
    return payload ? `${name}: ${payload.substring(0, 120)}${payload.length > 120 ? '…' : ''}` : name;
  }
  if (evt.kind === 'task.tool-result' || evt.kind === 'task.toolresult') {
    const name = (p.toolName as string) ?? '';
    const payload = typeof p.toolPayload === 'string' ? p.toolPayload : '';
    const err = typeof p.error === 'string' ? p.error : '';
    if (err) return `✗ ${name || 'tool'}: ${err.substring(0, 120)}`;
    return payload ? `✓ ${name || 'result'}: ${payload.substring(0, 120)}${payload.length > 120 ? '…' : ''}` : `✓ ${name || 'done'}`;
  }
  if (evt.kind === 'task.branch-pushed') {
    const branch = typeof p.branch === 'string' ? p.branch : '';
    const sha = typeof p.headSha === 'string' ? p.headSha : '';
    return branch ? `↑ pushed ${branch}${sha ? ` @ ${sha.substring(0, 8)}` : ''}` : '↑ branch pushed';
  }
  if (evt.kind === 'task.comment-response-pushed') {
    const branch = typeof p.branch === 'string' ? p.branch : '';
    return branch ? `↑ pushed comment-response to ${branch}` : '↑ comment-response pushed';
  }
  if (evt.kind === 'task.budget-warning') {
    return `over budget: ${(p.actual as number) ?? '?'} / ${(p.expected as number) ?? '?'}`;
  }
  if (evt.kind === 'task.completed') return '✓ completed';
  if (evt.kind === 'task.failed') {
    const err = typeof p.error === 'string' ? p.error : '';
    return err ? `✗ ${err.substring(0, 150)}` : '✗ failed';
  }
  if (evt.kind === 'task.delivery-skipped') {
    const reason = typeof p.reason === 'string' ? p.reason : '';
    return reason ? `⊘ no PR — ${reason.substring(0, 150)}` : '⊘ no PR created';
  }
  if (evt.kind === 'task.iteration.resumed') {
    const next = (p.nextIter as number | undefined) ?? '?';
    const prior = (p.priorVerdict as string | undefined) ?? '';
    const score = (p.priorScore as number | undefined);
    const agent = (p.priorAgent as string | undefined) ?? '';
    return `⟲ resumed at iter ${next} (prior: ${agent} → ${prior}${score != null ? ` ${score}` : ''})`;
  }
  if (evt.kind === 'task.auto-resumed') {
    const fromIter = (p.resumeFromIter as number | undefined) ?? '?';
    const verdict = (p.priorVerdict as string | undefined) ?? '';
    const score = (p.priorScore as number | undefined);
    const crew = (p.crewId as string | undefined) ?? '';
    return `⟳ auto-resumed by reconciler — re-queued to ${crew} from iter ${fromIter} (${verdict}${score != null ? ` ${score}` : ''})`;
  }
  if (evt.kind === 'task.pr-opened') {
    const prId = (p.prId as string | number | undefined) ?? '?';
    const branch = (p.branch as string | undefined) ?? '';
    const webUrl = (p.webUrl as string | undefined) ?? '';
    const parts = branch ? `↑ PR #${prId} (${branch})` : `↑ PR #${prId}`;
    return webUrl ? `${parts} — ${webUrl}` : parts;
  }
  if (evt.kind === 'task.pr-failed') {
    const err = typeof p.error === 'string' ? p.error : '';
    return err ? `✗ PR failed: ${err.substring(0, 150)}` : '✗ PR failed';
  }
  if (evt.kind === 'task.delivery-failed') {
    const reason = typeof p.reason === 'string' ? p.reason : '';
    return reason ? `✗ delivery failed: ${reason}` : '✗ delivery failed';
  }
  if (evt.kind === 'task.build-started') {
    const cmd = typeof p.command === 'string' ? p.command : '';
    return cmd ? `🔨 building: ${cmd}` : '🔨 building…';
  }
  if (evt.kind === 'task.build-passed') return '✓ build passed';
  if (evt.kind === 'task.build-failed') {
    const reason = typeof p.reason === 'string' ? p.reason : '';
    const stderr = typeof p.stderr === 'string' ? p.stderr : '';
    const detail = reason || stderr.substring(0, 200) || 'build failed';
    return `✗ ${detail}`;
  }
  if (evt.kind === 'task.test-started') {
    const cmd = typeof p.command === 'string' ? p.command : '';
    return cmd ? `🧪 testing: ${cmd}` : '🧪 testing…';
  }
  if (evt.kind === 'task.test-passed') return '✓ tests passed';
  if (evt.kind === 'task.test-failed') {
    const reason = typeof p.reason === 'string' ? p.reason : '';
    const stderr = typeof p.stderr === 'string' ? p.stderr : '';
    const detail = reason || stderr.substring(0, 200) || 'tests failed';
    return `✗ ${detail}`;
  }
  try { return JSON.stringify(p); } catch { return ''; }
}
