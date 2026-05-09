// 2026-05-07 — Settings → Maintenance panel. UI surface for the transcript
// retention janitor: shows live DB size + transcript event counts, lets
// the operator dry-run the prune to preview, then run for real with the
// `vacuum=true` flag to actually reclaim disk space.
//
// The periodic janitor (BackgroundService, 6-hour tick) keeps the table
// from regrowing automatically. This panel is for (a) the one-off "reclaim
// 2.2 GB right now" scenario and (b) future debugging when the operator
// wants to see what the policy would touch before committing.

import { useEffect, useState } from 'react';
import { forgeApi, type DbStats, type PruneStats } from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useToastStore } from '@/state/useToastStore';

function fmtBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function fmtNum(n: number): string {
  return n.toLocaleString();
}

export function MaintenancePanel(): JSX.Element {
  const [stats, setStats] = useState<DbStats | null>(null);
  const [statsErr, setStatsErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<'dry' | 'real' | null>(null);
  const [lastResult, setLastResult] = useState<{ stats: PruneStats; before: number; after: number | null } | null>(null);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => { void refresh(); }, []);

  async function refresh() {
    try {
      const s = await forgeApi.getDbStats();
      setStats(s);
      setStatsErr(null);
    } catch (err) {
      setStatsErr(err instanceof Error ? err.message : String(err));
    }
  }

  async function runDryRun() {
    setBusy('dry');
    setLastResult(null);
    try {
      const before = stats?.fileBytes ?? 0;
      const result = await forgeApi.pruneTranscripts({ dryRun: true });
      setLastResult({ stats: result, before, after: null });
      showToast(`Dry-run: ${fmtNum(result.rowsDeleted)} rows would be deleted across ${result.workItemsPruned} work items.`, 'info');
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Dry-run failed', 'danger');
    } finally {
      setBusy(null);
    }
  }

  async function runReal() {
    const ok = await useConfirmStore.getState().ask({
      title: 'Prune transcript history?',
      body: [
        'This deletes older transcript events per the retention policy and runs SQLite VACUUM to reclaim disk space.',
        'Per-iteration generator and evaluator summaries (in the deliverables table) are preserved untouched — only the noisy task.toolresult / task.toolcall stream is pruned.',
      ].join('\n'),
      tone: 'warn',
      confirmLabel: '✓ PRUNE + VACUUM',
      cancelLabel: 'Cancel',
      checklist: [
        'Closed/Completed work items: keep events for the LAST iteration only',
        'Open work items > 10 iterations: keep events for the LAST 10 iterations',
        'All deliverable summaries (per iteration) are kept',
        'VACUUM rewrites the DB file to reclaim deleted-row space',
        'Briefly stalls writers while VACUUM runs (~10–30 s on a multi-GB DB)',
      ],
    });
    if (!ok) return;
    setBusy('real');
    setLastResult(null);
    try {
      const before = stats?.fileBytes ?? 0;
      const result = await forgeApi.pruneTranscripts({ vacuum: true });
      const fresh = await forgeApi.getDbStats();
      setStats(fresh);
      setLastResult({ stats: result, before, after: fresh.fileBytes });
      const reclaimed = before - fresh.fileBytes;
      const reclaimedStr = reclaimed > 0 ? ` Reclaimed ${fmtBytes(reclaimed)}.` : '';
      showToast(`Pruned ${fmtNum(result.rowsDeleted)} rows across ${result.workItemsPruned} work items.${reclaimedStr}`, 'success');
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Prune failed', 'danger');
    } finally {
      setBusy(null);
    }
  }

  return (
    <div
      className="ws-card ws-card--static"
      style={{ display: 'grid', gap: 12, '--ws-color': 'var(--accent)' } as React.CSSProperties}
    >
      <div className="ws-card-head">
        <div className="ws-card-head-left">
          <div className="ws-card-eyebrow">
            <span className="epic-badge">DB</span>
            <span style={{ opacity: 0.4 }}>·</span>
            <span>Transcript retention &amp; vacuum</span>
          </div>
          <div className="ws-card-name">Database maintenance</div>
        </div>
        <button
          type="button"
          className="btn-secondary"
          onClick={() => void refresh()}
          style={{ fontSize: 11 }}
          title="Re-fetch DB stats"
        >
          ↻ Refresh
        </button>
      </div>

      {statsErr && (
        <div style={{ padding: 8, background: 'var(--danger-bg)', color: 'var(--danger)', fontSize: 11.5, borderRadius: 'var(--radius-sm)' }}>
          {statsErr}
        </div>
      )}

      {stats && (
        <div className="ws-card-stats">
          <div className="ws-stat">
            <div className="ws-stat-label">DB file size</div>
            <div className="ws-stat-val mono">{fmtBytes(stats.fileBytes)}</div>
          </div>
          <div className="ws-stat">
            <div className="ws-stat-label">Transcript rows</div>
            <div className="ws-stat-val mono">{fmtNum(stats.transcriptRows)}</div>
          </div>
          <div className="ws-stat">
            <div className="ws-stat-label">Transcript bytes</div>
            <div className="ws-stat-val mono">{fmtBytes(stats.transcriptPayloadBytes)}</div>
          </div>
          <div className="ws-stat">
            <div className="ws-stat-label">Deliverables</div>
            <div className="ws-stat-val mono">{fmtNum(stats.deliverableRows)}</div>
          </div>
        </div>
      )}

      <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
        Retention policy: <strong>completed</strong> work items keep the last 1 iteration of transcript;
        <strong> open</strong> work items with more than 10 iterations keep the last 10. Per-iteration
        generator/evaluator summaries in the <code>deliverables</code> table are never deleted.
        The periodic janitor runs every 6 hours; use the buttons below to dry-run or trigger an immediate prune + VACUUM.
      </div>

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button
          type="button"
          className="btn-secondary"
          disabled={busy !== null}
          onClick={() => void runDryRun()}
          title="Compute what would be pruned without modifying the DB"
        >
          {busy === 'dry' ? '⏳ Dry-running…' : '◇ Dry run'}
        </button>
        <button
          type="button"
          className="btn-primary"
          disabled={busy !== null}
          onClick={() => void runReal()}
          title="Prune transcripts per policy and VACUUM the DB"
          style={{ borderColor: 'var(--warning, #ffb347)' }}
        >
          {busy === 'real' ? '⏳ Pruning + VACUUM…' : '✓ Prune + VACUUM now'}
        </button>
      </div>

      {lastResult && (
        <div
          style={{
            padding: 10,
            background: 'var(--bg-surface-2)',
            borderRadius: 'var(--radius-sm)',
            fontSize: 11.5,
            fontFamily: 'var(--sc-font-mono, monospace)',
            display: 'grid',
            gap: 4,
          }}
        >
          <div>
            <span style={{ color: 'var(--text-secondary)' }}>{lastResult.stats.dryRun ? 'Dry-run' : 'Prune'} result · </span>
            <strong>{fmtNum(lastResult.stats.rowsDeleted)}</strong> rows
            {lastResult.stats.dryRun ? ' would be ' : ' '}deleted across <strong>{lastResult.stats.workItemsPruned}</strong> /
            {' '}{lastResult.stats.workItemsScanned} work items
            {' · '}<span style={{ color: 'var(--text-tertiary)' }}>{lastResult.stats.elapsedMs} ms</span>
          </div>
          {lastResult.after !== null && (
            <div>
              <span style={{ color: 'var(--text-secondary)' }}>DB size · </span>
              {fmtBytes(lastResult.before)} → <strong>{fmtBytes(lastResult.after)}</strong>
              {' · reclaimed '}
              <strong style={{ color: 'var(--success, #5eead4)' }}>{fmtBytes(Math.max(0, lastResult.before - lastResult.after))}</strong>
              {lastResult.stats.vacuumRan ? ' (VACUUM ran)' : ''}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
