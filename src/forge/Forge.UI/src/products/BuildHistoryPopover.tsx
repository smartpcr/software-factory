import { useCallback, useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import type { BuildValidationRun, ValidateStep } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

interface Props {
  repoId: string;
  repoName: string;
}

/**
 * Phase 29 — icon button + popover showing build validation history.
 * Polls automatically while any run is queued/running. Shows elapsed
 * time and a cancel button for active runs.
 */
export function BuildHistoryPopover({ repoId, repoName }: Props) {
  const [open, setOpen] = useState(false);
  const [runs, setRuns] = useState<BuildValidationRun[]>([]);
  const [loading, setLoading] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());
  const ref = useRef<HTMLDivElement>(null);
  const toast = useToastStore((s) => s.show);

  const refresh = useCallback(() => {
    forgeApi.getValidationRuns(repoId).then(setRuns).catch(() => {});
  }, [repoId]);

  // Load on open.
  useEffect(() => {
    if (!open) return;
    setLoading(true);
    forgeApi.getValidationRuns(repoId).then(setRuns).catch(() => {}).finally(() => setLoading(false));
  }, [open, repoId]);

  // Poll while any run is active.
  const hasActive = runs.some((r) => r.status === 'queued' || r.status === 'running');
  useEffect(() => {
    if (!open || !hasActive) return;
    const iv = setInterval(() => {
      refresh();
      setNow(Date.now());
    }, 3000);
    return () => clearInterval(iv);
  }, [open, hasActive, refresh]);

  // Tick elapsed timer every second when active.
  useEffect(() => {
    if (!open || !hasActive) return;
    const iv = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(iv);
  }, [open, hasActive]);

  // Close on outside click.
  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', onClick);
    return () => document.removeEventListener('mousedown', onClick);
  }, [open]);

  // Close on Escape.
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);

  // Icon pulse when running.
  const iconColor = hasActive ? 'var(--accent)' : 'var(--text-tertiary)';

  return (
    <div ref={ref} style={{ position: 'relative', display: 'inline-flex' }}>
      <button
        type="button"
        className="icon-btn"
        onClick={() => setOpen(!open)}
        title={`Build history for ${repoName}`}
        aria-label={`Build history for ${repoName}`}
        style={{ width: 22, height: 22, color: iconColor, transition: 'color 200ms' }}
      >
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none"
          style={hasActive ? { animation: 'spin 2s linear infinite' } : undefined}>
          <circle cx="8" cy="8" r="6.5" stroke="currentColor" strokeWidth="1.3" />
          <path d="M8 4.5V8l2.5 1.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div style={{
          position: 'absolute', top: '100%', right: 0, marginTop: 6,
          width: 440, maxHeight: 380, overflowY: 'auto',
          background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
          borderRadius: 'var(--radius-sm)', boxShadow: '0 8px 24px rgba(0,0,0,0.4)',
          zIndex: 1000, padding: '10px 12px', fontSize: 11,
        }}>
          {/* Header */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
            <span style={{ fontWeight: 700, fontSize: 11, textTransform: 'uppercase', letterSpacing: 0.5, color: 'var(--text-secondary)' }}>
              Build validation
            </span>
            <span style={{ opacity: 0.5, fontSize: 10 }}>{repoName}</span>
            <button type="button" onClick={refresh} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: 'var(--text-tertiary)', fontSize: 11 }}>↻</button>
          </div>

          {loading && <div style={{ color: 'var(--text-tertiary)', padding: '12px 0', textAlign: 'center' }}>Loading…</div>}

          {!loading && runs.length === 0 && (
            <div style={{ color: 'var(--text-tertiary)', padding: '16px 0', textAlign: 'center' }}>No validation runs yet</div>
          )}

          {!loading && runs.map((run) => (
            <RunRow
              key={run.id}
              run={run}
              repoId={repoId}
              now={now}
              expanded={expandedId === run.id}
              onToggle={() => setExpandedId(expandedId === run.id ? null : run.id)}
              onCancelled={refresh}
              toast={toast}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// ── Run row with elapsed time + cancel ────────────────────────────────

function RunRow({ run, repoId, now, expanded, onToggle, onCancelled, toast }: {
  run: BuildValidationRun;
  repoId: string;
  now: number;
  expanded: boolean;
  onToggle: () => void;
  onCancelled: () => void;
  toast: (msg: string, kind: 'success' | 'info' | 'danger') => void;
}) {
  const isActive = run.status === 'queued' || run.status === 'running';
  const steps: ValidateStep[] = run.stepsJson ? JSON.parse(run.stepsJson) : [];
  const icon = run.status === 'succeeded' ? '✓'
    : run.status === 'failed' ? '✗'
    : run.status === 'cancelled' ? '⊘'
    : '⏳';
  const color = run.status === 'succeeded' ? 'rgb(34,197,94)'
    : run.status === 'failed' ? 'rgb(220,38,38)'
    : run.status === 'cancelled' ? 'var(--text-tertiary)'
    : 'var(--accent)';

  const elapsed = isActive ? formatElapsed(now - new Date(run.startedAt).getTime()) : null;

  const [cancelling, setCancelling] = useState(false);

  async function handleCancel(e: React.MouseEvent) {
    e.stopPropagation();
    setCancelling(true);
    try {
      await forgeApi.cancelValidationRun(repoId, run.id);
      toast('Validation cancelled', 'info');
      onCancelled();
    } catch {
      toast('Failed to cancel', 'danger');
    } finally {
      setCancelling(false);
    }
  }

  return (
    <div style={{ borderTop: '1px solid var(--border-subtle)' }}>
      <button type="button" onClick={onToggle} style={{
        background: expanded ? 'color-mix(in srgb, var(--accent) 6%, transparent)' : 'transparent',
        border: 'none', cursor: 'pointer', color: 'inherit', font: 'inherit',
        padding: '5px 4px', width: '100%', textAlign: 'left',
        display: 'flex', gap: 6, alignItems: 'center', fontSize: 11,
      }}>
        <span style={{ color, fontWeight: 600, fontSize: 13 }}>{icon}</span>
        <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10 }}>
          {new Date(run.startedAt).toLocaleString()}
        </span>

        {/* Active: show elapsed + spinner */}
        {isActive && (
          <span style={{
            color: 'var(--accent)', fontFamily: 'var(--font-mono)', fontSize: 10,
            fontWeight: 600, display: 'flex', alignItems: 'center', gap: 4,
          }}>
            <span style={{ animation: 'spin 1s linear infinite', display: 'inline-block' }}>⏳</span>
            {elapsed}
          </span>
        )}

        {/* Completed: step summary */}
        {!isActive && steps.length > 0 && (
          <span style={{ opacity: 0.5, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {steps.map((s) => `${s.step}:${s.passed ? 'ok' : 'fail'}`).join(' → ')}
          </span>
        )}

        {run.triggeredBy && <span style={{ opacity: 0.4, fontSize: 10, marginLeft: 'auto' }}>by {run.triggeredBy}</span>}

        {/* Cancel button for active runs */}
        {isActive && (
          <button
            type="button"
            onClick={handleCancel}
            disabled={cancelling}
            style={{
              marginLeft: 'auto', padding: '1px 6px', fontSize: 10,
              background: 'rgba(220,38,38,0.12)', color: 'rgb(220,38,38)',
              border: '1px solid rgba(220,38,38,0.3)', borderRadius: 3,
              cursor: 'pointer', fontWeight: 600,
            }}
          >
            {cancelling ? '…' : 'Cancel'}
          </button>
        )}

        {!isActive && <span style={{ opacity: 0.4, fontSize: 9 }}>{expanded ? '▼' : '▶'}</span>}
      </button>

      {/* Expanded step detail — always show for active runs */}
      {expanded && (
        <div style={{ padding: '4px 0 6px 18px' }}>
          {steps.length === 0 && isActive && (
            <div style={{ color: 'var(--text-tertiary)', fontSize: 11, padding: '4px 0' }}>
              ⏳ Preparing pipeline…
            </div>
          )}
          {steps.map((s) => (
            <div key={s.step} style={{ marginBottom: 3 }}>
              <div style={{ display: 'flex', gap: 5, alignItems: 'center', fontSize: 11 }}>
                <span style={{ color: s.passed ? 'rgb(34,197,94)' : 'rgb(220,38,38)' }}>{s.passed ? '✓' : '✗'}</span>
                <span style={{ fontWeight: 600, minWidth: 44 }}>{s.step}</span>
                <code style={{ fontFamily: 'var(--font-mono)', fontSize: 10, opacity: 0.6 }}>{s.command}</code>
                {s.timedOut && <span style={{ color: 'rgb(220,38,38)', fontSize: 9, fontWeight: 600 }}>TIMED OUT</span>}
                {!s.passed && <span style={{ color: 'rgb(220,38,38)', fontSize: 9 }}>exit {s.exitCode}</span>}
              </div>
              {(s.stderr?.trim() || s.stdout?.trim()) && (
                <pre style={{
                  fontFamily: 'var(--font-mono, monospace)', fontSize: 10,
                  background: 'rgba(0,0,0,0.3)',
                  color: s.passed ? 'var(--text-secondary)' : 'rgb(248,113,113)',
                  padding: '5px 7px', borderRadius: 4, marginTop: 2,
                  maxHeight: 140, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
                }}>
                  {s.stderr?.trim() ? `── stderr ──\n${s.stderr.trim()}` : ''}
                  {s.stderr?.trim() && s.stdout?.trim() ? '\n\n' : ''}
                  {s.stdout?.trim() ? `── stdout ──\n${s.stdout.trim()}` : ''}
                </pre>
              )}
            </div>
          ))}
          {isActive && steps.length > 0 && (
            <div style={{ display: 'flex', gap: 5, alignItems: 'center', fontSize: 11, color: 'var(--accent)', marginTop: 2 }}>
              <span style={{ animation: 'spin 1s linear infinite', display: 'inline-block' }}>⏳</span>
              <span>Running next step…</span>
            </div>
          )}
          {run.errorMessage && <div style={{ color: 'rgb(220,38,38)', fontSize: 10, marginTop: 3 }}>Error: {run.errorMessage}</div>}
        </div>
      )}
    </div>
  );
}

function formatElapsed(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const min = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  return `${min}m ${sec}s`;
}
