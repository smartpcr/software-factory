import { useCallback, useEffect, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import type { BuildValidationRun, ValidateStep } from '@/api/forgeApi';
import type { Repo, WorkItemSyncRun } from '@/types';
import { useToastStore } from '@/state/useToastStore';

type Tab = 'build' | 'wi-sync' | 'pr-sync';

interface Props {
  repo: Repo;
}

/**
 * Phase 29 — full-width inline detail panel that slides open below a
 * repo row. Three tabs: Build History, WI Sync, PR Sync. Replaces the
 * cramped popover with a proper layout.
 */
export function RepoDetailPanel({ repo }: Props) {
  const [tab, setTab] = useState<Tab>('build');

  return (
    <div style={{
      background: 'color-mix(in srgb, var(--bg-surface) 60%, transparent)',
      border: '1px solid var(--border-subtle)',
      borderRadius: 'var(--radius-sm)',
      margin: '4px 0 8px',
      overflow: 'hidden',
    }}>
      {/* Tab bar */}
      <div style={{
        display: 'flex', gap: 0,
        borderBottom: '1px solid var(--border-subtle)',
        background: 'color-mix(in srgb, var(--bg-base) 40%, transparent)',
      }}>
        {([
          ['build', '🔨 Build History'],
          ['wi-sync', '📋 WI Sync'],
          ['pr-sync', '🔀 PR Sync'],
        ] as [Tab, string][]).map(([id, label]) => (
          <button
            key={id}
            type="button"
            onClick={() => setTab(id)}
            style={{
              background: 'none', border: 'none', cursor: 'pointer', font: 'inherit',
              padding: '6px 14px', fontSize: 11, fontWeight: 600,
              color: tab === id ? 'var(--accent)' : 'var(--text-tertiary)',
              borderBottom: tab === id ? '2px solid var(--accent)' : '2px solid transparent',
              marginBottom: -1, transition: 'color 100ms',
            }}
          >
            {label}
          </button>
        ))}
      </div>

      {/* Tab content */}
      <div style={{ padding: '8px 12px', maxHeight: 320, overflowY: 'auto' }}>
        {tab === 'build' && <BuildHistoryTab repo={repo} />}
        {tab === 'wi-sync' && <SyncHistoryTab repo={repo} kind="wi" />}
        {tab === 'pr-sync' && <SyncHistoryTab repo={repo} kind="pr" />}
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════
// Build History Tab
// ═══════════════════════════════════════════════════════════════════════

function BuildHistoryTab({ repo }: { repo: Repo }) {
  const [runs, setRuns] = useState<BuildValidationRun[]>([]);
  const [loading, setLoading] = useState(true);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());

  const refresh = useCallback(() => {
    forgeApi.getValidationRuns(repo.id).then(setRuns).catch(() => {});
  }, [repo.id]);

  useEffect(() => {
    setLoading(true);
    forgeApi.getValidationRuns(repo.id).then(setRuns).catch(() => {}).finally(() => setLoading(false));
  }, [repo.id]);

  // Poll while any run is active.
  const hasActive = runs.some((r) => r.status === 'queued' || r.status === 'running');
  useEffect(() => {
    if (!hasActive) return;
    const iv = setInterval(() => { refresh(); setNow(Date.now()); }, 3000);
    return () => clearInterval(iv);
  }, [hasActive, refresh]);

  useEffect(() => {
    if (!hasActive) return;
    const iv = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(iv);
  }, [hasActive]);

  if (loading) return <Placeholder>Loading…</Placeholder>;
  if (runs.length === 0) return <Placeholder>No build validation runs yet. Click ✓ Validate in the Pipeline tab to start one.</Placeholder>;

  return (
    <div style={{ display: 'grid', gap: 1 }}>
      {/* Header row */}
      <div style={{
        display: 'grid', gridTemplateColumns: '20px 1fr 140px 100px 60px',
        gap: 8, padding: '2px 4px', fontSize: 10, fontWeight: 700,
        textTransform: 'uppercase', letterSpacing: 0.5, color: 'var(--text-tertiary)',
        borderBottom: '1px solid var(--border-subtle)',
        alignItems: 'center',
      }}>
        <span />
        <span>Started</span>
        <span>Steps</span>
        <span>By</span>
        <ClearHistoryButton label="Clear" title="Clear completed validation runs" onClick={async () => {
          await forgeApi.clearValidationRuns(repo.id);
          refresh();
        }} />
      </div>

      {runs.map((run) => {
        const open = expandedId === run.id;
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

        return (
          <div key={run.id}>
            <button type="button" onClick={() => setExpandedId(open ? null : run.id)}
              title={`${run.status}${run.errorMessage ? ': ' + run.errorMessage : ''}`}
              style={{
                display: 'grid', gridTemplateColumns: '20px 1fr 140px 100px 60px',
                gap: 8, padding: '4px 4px', width: '100%', textAlign: 'left',
                background: open ? 'color-mix(in srgb, var(--accent) 6%, transparent)' : 'transparent',
                border: 'none', cursor: 'pointer', color: 'inherit', font: 'inherit', fontSize: 11,
                alignItems: 'center',
              }}
            >
              <span style={{ color, fontWeight: 700 }}>{icon}</span>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, display: 'flex', gap: 6, alignItems: 'center' }}>
                {new Date(run.startedAt).toLocaleString()}
                {elapsed && <span style={{ color: 'var(--accent)', fontWeight: 600 }}>{elapsed}</span>}
              </span>
              <span style={{ opacity: 0.6, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 10 }}>
                {run.errorMessage
                  ? run.errorMessage
                  : steps.map((s) => `${s.step}:${s.passed ? 'ok' : 'fail'}`).join(' → ')}
              </span>
              <span style={{ opacity: 0.4, fontSize: 10, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {run.triggeredBy?.split('@')[0] ?? ''}
              </span>
              <span style={{ display: 'flex', gap: 4, justifyContent: 'flex-end' }}>
                {isActive && (
                  <CancelButton repoId={repo.id} runId={run.id} onDone={refresh} />
                )}
                <span style={{ opacity: 0.4, fontSize: 9 }}>{open ? '▼' : '▶'}</span>
              </span>
            </button>

            {open && (
              <div style={{ padding: '4px 0 8px 28px' }}>
                {steps.length === 0 && isActive && (
                  <div style={{ color: 'var(--text-tertiary)', fontSize: 11 }}>⏳ Preparing pipeline…</div>
                )}
                {steps.map((s) => <StepRow key={s.step} step={s} />)}
                {isActive && steps.length > 0 && (
                  <div style={{ display: 'flex', gap: 5, alignItems: 'center', fontSize: 11, color: 'var(--accent)', marginTop: 2 }}>
                    <span style={{ animation: 'spin 1s linear infinite', display: 'inline-block' }}>⏳</span>
                    Running next step…
                  </div>
                )}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════
// Sync History Tab (shared for WI and PR)
// ═══════════════════════════════════════════════════════════════════════

function SyncHistoryTab({ repo, kind }: { repo: Repo; kind: 'wi' | 'pr' }) {
  const [runs, setRuns] = useState<WorkItemSyncRun[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (kind === 'pr') {
      // PR sync does not yet have its own run history table.
      setRuns([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    fetch(`/api/repos/${encodeURIComponent(repo.id)}/sync-work-items/runs?limit=20`)
      .then((r) => r.ok ? r.json() : [])
      .then((data: WorkItemSyncRun[]) => setRuns(Array.isArray(data) ? data : []))
      .catch(() => setRuns([]))
      .finally(() => setLoading(false));
  }, [repo.id, kind]);

  if (loading) return <Placeholder>Loading…</Placeholder>;
  if (runs.length === 0) return <Placeholder>{kind === 'pr' ? 'PR sync history is not yet tracked separately. PR syncs run as part of the background sync sweep.' : 'No work item sync runs yet.'}</Placeholder>;

  return (
    <div style={{ display: 'grid', gap: 1 }}>
      <div style={{
        display: 'grid', gridTemplateColumns: '20px 1fr 80px 60px 60px 60px 50px',
        gap: 8, padding: '2px 4px', fontSize: 10, fontWeight: 700,
        textTransform: 'uppercase', letterSpacing: 0.5, color: 'var(--text-tertiary)',
        borderBottom: '1px solid var(--border-subtle)', alignItems: 'center',
      }}>
        <span />
        <span>Started</span>
        <span>Kind</span>
        <span>Added</span>
        <span>Updated</span>
        <span>Conflicts</span>
        <ClearHistoryButton label="Clear" title="Clear all sync run history" onClick={async () => {
          await forgeApi.clearSyncRuns(repo.id);
          setRuns([]);
        }} />
      </div>
      {runs.map((run) => {
        const ok = run.completedSuccessfully !== false && !run.errorMessage;
        return (
          <div key={run.id} style={{
            display: 'grid', gridTemplateColumns: '20px 1fr 80px 60px 60px 60px 50px',
            gap: 8, padding: '4px 4px', fontSize: 11, alignItems: 'center',
          }}
            title={run.errorMessage ?? undefined}
          >
            <span style={{ color: ok ? 'rgb(34,197,94)' : 'rgb(220,38,38)', fontWeight: 700 }}>
              {ok ? '✓' : '✗'}
            </span>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10 }}>
              {new Date(run.startedAt).toLocaleString()}
            </span>
            <span style={{
              fontSize: 9, fontWeight: 600, padding: '1px 4px', borderRadius: 3,
              background: 'color-mix(in srgb, var(--accent) 10%, transparent)',
              color: 'var(--accent)', textAlign: 'center',
            }}>
              {run.kind}
            </span>
            <span style={{ textAlign: 'center', color: run.itemsAdded > 0 ? 'rgb(34,197,94)' : 'var(--text-tertiary)' }}>
              {run.itemsAdded > 0 ? `+${run.itemsAdded}` : '—'}
            </span>
            <span style={{ textAlign: 'center', color: run.itemsUpdated > 0 ? 'var(--accent)' : 'var(--text-tertiary)' }}>
              {run.itemsUpdated > 0 ? run.itemsUpdated : '—'}
            </span>
            <span style={{ textAlign: 'center', color: run.itemsConflicted > 0 ? 'rgb(220,38,38)' : 'var(--text-tertiary)' }}>
              {run.itemsConflicted > 0 ? run.itemsConflicted : '—'}
            </span>
            <span />
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════
// Shared sub-components
// ═══════════════════════════════════════════════════════════════════════

function StepRow({ step: s }: { step: ValidateStep }) {
  const [open, setOpen] = useState(!s.passed);
  const hasOutput = s.stderr?.trim() || s.stdout?.trim();
  return (
    <div style={{ marginBottom: 3 }}>
      <button type="button" onClick={() => hasOutput && setOpen(!open)} style={{
        display: 'flex', gap: 5, alignItems: 'center', fontSize: 11, width: '100%',
        background: 'none', border: 'none', cursor: hasOutput ? 'pointer' : 'default',
        color: 'inherit', font: 'inherit', padding: '1px 0', textAlign: 'left',
      }}>
        <span style={{ color: s.passed ? 'rgb(34,197,94)' : 'rgb(220,38,38)' }}>{s.passed ? '✓' : '✗'}</span>
        <span style={{ fontWeight: 600, minWidth: 50 }}>{s.step}</span>
        <code style={{ fontFamily: 'var(--font-mono)', fontSize: 10, opacity: 0.6, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.command}</code>
        {s.timedOut && <span style={{ color: 'rgb(220,38,38)', fontSize: 9, fontWeight: 600 }}>TIMED OUT</span>}
        {!s.passed && !s.timedOut && <span style={{ color: 'rgb(220,38,38)', fontSize: 9 }}>exit {s.exitCode}</span>}
        {hasOutput && <span style={{ opacity: 0.4, fontSize: 9 }}>{open ? '▼' : '▶'}</span>}
      </button>
      {open && hasOutput && (
        <div style={{ position: 'relative', marginTop: 2 }}>
          <button
            type="button"
            title="Copy log to clipboard"
            onClick={(e) => {
              e.stopPropagation();
              const text = [
                s.stderr?.trim() ? `── stderr ──\n${s.stderr.trim()}` : '',
                s.stderr?.trim() && s.stdout?.trim() ? '\n\n' : '',
                s.stdout?.trim() ? `── stdout ──\n${s.stdout.trim()}` : '',
              ].join('');
              navigator.clipboard.writeText(text).catch(() => {});
            }}
            style={{
              position: 'absolute', top: 4, right: 4, zIndex: 1,
              background: 'rgba(255,255,255,0.08)', border: '1px solid rgba(255,255,255,0.15)',
              borderRadius: 3, padding: '1px 5px', cursor: 'pointer',
              color: 'var(--text-tertiary)', fontSize: 10,
            }}
          >📋</button>
          <pre style={{
            fontFamily: 'var(--font-mono, monospace)', fontSize: 10,
            background: 'rgba(0,0,0,0.3)',
            color: s.passed ? 'var(--text-secondary)' : 'rgb(248,113,113)',
            padding: '5px 7px', borderRadius: 4,
            maxHeight: 200, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
          }}>
            {s.stderr?.trim() ? `── stderr ──\n${s.stderr.trim()}` : ''}
            {s.stderr?.trim() && s.stdout?.trim() ? '\n\n' : ''}
            {s.stdout?.trim() ? `── stdout ──\n${s.stdout.trim()}` : ''}
          </pre>
        </div>
      )}
    </div>
  );
}

function CancelButton({ repoId, runId, onDone }: { repoId: string; runId: string; onDone: () => void }) {
  const [busy, setBusy] = useState(false);
  const toast = useToastStore((s) => s.show);
  return (
    <button
      type="button"
      title="Cancel this validation run"
      disabled={busy}
      onClick={(e) => {
        e.stopPropagation();
        setBusy(true);
        forgeApi.cancelValidationRun(repoId, runId)
          .then(() => { toast('Cancelled', 'info'); onDone(); })
          .catch(() => toast('Failed to cancel', 'danger'))
          .finally(() => setBusy(false));
      }}
      style={{
        padding: '1px 5px', fontSize: 9, fontWeight: 600,
        background: 'rgba(220,38,38,0.12)', color: 'rgb(220,38,38)',
        border: '1px solid rgba(220,38,38,0.3)', borderRadius: 3,
        cursor: 'pointer',
      }}
    >
      {busy ? '…' : 'Cancel'}
    </button>
  );
}

function Placeholder({ children }: { children: React.ReactNode }) {
  return <div style={{ color: 'var(--text-tertiary)', fontSize: 11, padding: '16px 0', textAlign: 'center' }}>{children}</div>;
}

function ClearHistoryButton({ label, title, onClick }: { label: string; title: string; onClick: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const toast = useToastStore((s) => s.show);
  return (
    <button
      type="button"
      title={title}
      disabled={busy}
      onClick={(e) => {
        e.stopPropagation();
        if (!window.confirm('Clear all history? This cannot be undone.')) return;
        setBusy(true);
        onClick()
          .then(() => toast('History cleared', 'info'))
          .catch(() => toast('Failed to clear', 'danger'))
          .finally(() => setBusy(false));
      }}
      style={{
        padding: '1px 5px', fontSize: 9, fontWeight: 600,
        background: 'rgba(255,255,255,0.06)', color: 'var(--text-tertiary)',
        border: '1px solid rgba(255,255,255,0.1)', borderRadius: 3,
        cursor: 'pointer', justifySelf: 'end',
      }}
    >
      {busy ? '…' : label}
    </button>
  );
}

function formatElapsed(ms: number): string {
  const totalSec = Math.max(0, Math.floor(ms / 1000));
  if (totalSec < 60) return `${totalSec}s`;
  const min = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  return `${min}m ${sec}s`;
}
