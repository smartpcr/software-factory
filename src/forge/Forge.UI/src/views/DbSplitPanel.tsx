import { useEffect, useState } from 'react';
import { forgeApi, type DbSplitStatus, type SplitDryRunReport, type SplitApplyResult } from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useToastStore } from '@/state/useToastStore';

function fmtNum(n: number): string {
  return n.toLocaleString();
}

function fmtBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

export function DbSplitPanel(): JSX.Element | null {
  const [status, setStatus] = useState<DbSplitStatus | null>(null);
  const [statusErr, setStatusErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<'dry' | 'apply' | null>(null);
  const [dryRunReport, setDryRunReport] = useState<SplitDryRunReport | null>(null);
  const [applyResult, setApplyResult] = useState<SplitApplyResult | null>(null);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => { void refresh(); }, []);

  async function refresh() {
    try {
      const s = await forgeApi.getDbSplitStatus();
      setStatus(s);
      setStatusErr(null);
    } catch (err) {
      setStatusErr(err instanceof Error ? err.message : String(err));
    }
  }

  async function runDryRun() {
    setBusy('dry');
    setDryRunReport(null);
    setApplyResult(null);
    try {
      const report = await forgeApi.splitDryRun();
      setDryRunReport(report);
      showToast(
        `Dry-run: ${fmtNum(report.totalRows)} total rows — ${fmtNum(report.productRows)} product, ${fmtNum(report.controlRows)} control, ${fmtNum(report.orphanRows)} orphan.`,
        'info',
      );
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Dry-run failed', 'danger');
    } finally {
      setBusy(null);
    }
  }

  async function runApply() {
    const ok = await useConfirmStore.getState().ask({
      title: 'Split database into per-product files?',
      body: [
        'This partitions forge.db into separate per-product SQLite files ({slug}.db) and strips product tables from the control DB.',
        'A backup of forge.db is created first. The operation is idempotent — re-running returns AlreadyApplied.',
        'Forge.Api should be restarted after the split completes.',
      ].join('\n'),
      tone: 'warn',
      confirmLabel: '✓ SPLIT NOW',
      cancelLabel: 'Cancel',
      checklist: [
        'Creates a backup of forge.db before modifying',
        'Copies product-side rows into per-product {slug}.db files',
        'Stamps phase73-split-applied marker in forge_seed_state',
        'Requires Forge.Api restart to activate per-product routing',
      ],
    });
    if (!ok) return;
    setBusy('apply');
    setApplyResult(null);
    try {
      const result = await forgeApi.splitApply();
      setApplyResult(result);
      if (result.success) {
        showToast(`Split complete: ${fmtNum(result.rowsCopied)} rows copied to ${result.productDbs.length} product DB(s). Restart Forge.Api to activate.`, 'success');
        void refresh();
      } else {
        showToast(`Split finished with ${result.errors.length} error(s).`, 'danger');
      }
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Split failed', 'danger');
    } finally {
      setBusy(null);
    }
  }

  // Don't render the panel when status hasn't loaded yet.
  if (!status && !statusErr) return null;

  const dotColor = status?.alreadyApplied
    ? 'var(--success)'
    : status?.canSplit
      ? 'var(--warning, #ffb347)'
      : 'var(--text-tertiary)';

  return (
    <div
      className="ws-card ws-card--static"
      style={{ display: 'grid', gap: 12, '--ws-color': dotColor } as React.CSSProperties}
    >
      <div className="ws-card-head">
        <div className="ws-card-head-left">
          <div className="ws-card-eyebrow">
            <span className="epic-badge">DB</span>
            <span style={{ opacity: 0.4 }}>·</span>
            <span>Multi-product split</span>
          </div>
          <div className="ws-card-name">Per-product database split</div>
        </div>
        <button
          type="button"
          className="btn-secondary"
          onClick={() => void refresh()}
          style={{ fontSize: 11 }}
          title="Re-fetch split status"
        >
          ↻ Refresh
        </button>
      </div>

      {statusErr && (
        <div style={{ padding: 8, background: 'var(--danger-bg)', color: 'var(--danger)', fontSize: 11.5, borderRadius: 'var(--radius-sm)' }}>
          {statusErr}
        </div>
      )}

      {status && (
        <>
          <div className="ws-card-stats">
            <div className="ws-stat">
              <div className="ws-stat-label">Split status</div>
              <div className="ws-stat-val" style={{ color: status.alreadyApplied ? 'var(--success)' : 'var(--text-primary)' }}>
                {status.alreadyApplied ? '✓ Applied' : 'Not split'}
              </div>
            </div>
            <div className="ws-stat">
              <div className="ws-stat-label">Products</div>
              <div className="ws-stat-val mono">{status.productCount}</div>
            </div>
            <div className="ws-stat">
              <div className="ws-stat-label">SplitEnabled flag</div>
              <div className="ws-stat-val" style={{ color: status.splitEnabled ? 'var(--success)' : 'var(--danger)' }}>
                {status.splitEnabled ? 'ON' : 'OFF'}
              </div>
            </div>
            <div className="ws-stat">
              <div className="ws-stat-label">Product tables in control</div>
              <div className="ws-stat-val">{status.controlHasProductTables ? 'Yes' : 'No'}</div>
            </div>
          </div>

          <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
            {status.alreadyApplied ? (
              <>The database has been split into per-product files. Each product's workstreams, work items, and deliverables live in their own <code>{'{slug}'}.db</code> file.</>
            ) : !status.splitEnabled ? (
              <>Split tooling is disabled. Set <code>Forge__Db__SplitEnabled=true</code> in environment or <code>appsettings.json</code> and restart Forge.Api to enable.</>
            ) : status.productCount === 0 ? (
              <>No products exist yet. Create at least one product before splitting.</>
            ) : !status.controlHasProductTables ? (
              <>Product tables have already been removed from the control DB, but the split marker is missing. Use the <strong>split-resume</strong> endpoint to stamp it.</>
            ) : (
              <>Ready to split. Run a <strong>dry-run</strong> first to inspect the partition report, then <strong>apply</strong> to create per-product DB files.</>
            )}
          </div>

          {status.canSplit && (
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <button
                type="button"
                className="btn-secondary"
                disabled={busy !== null}
                onClick={() => void runDryRun()}
                title="Analyze partition without modifying the DB"
              >
                {busy === 'dry' ? '⏳ Analyzing…' : '◇ Dry run'}
              </button>
              <button
                type="button"
                className="btn-primary"
                disabled={busy !== null}
                onClick={() => void runApply()}
                title="Partition forge.db into per-product DB files"
                style={{ borderColor: 'var(--warning, #ffb347)' }}
              >
                {busy === 'apply' ? '⏳ Splitting…' : '✓ Split now'}
              </button>
            </div>
          )}
        </>
      )}

      {dryRunReport && (
        <div
          style={{
            padding: 10,
            background: 'var(--bg-surface-2)',
            borderRadius: 'var(--radius-sm)',
            fontSize: 11.5,
            display: 'grid',
            gap: 6,
          }}
        >
          <div style={{ fontWeight: 600, color: 'var(--text-primary)' }}>
            Dry-run report · {new Date(dryRunReport.generatedAt).toLocaleString()}
          </div>
          <div style={{ fontFamily: 'var(--sc-font-mono, monospace)' }}>
            DB: {fmtBytes(dryRunReport.controlDbSizeBytes)} · {fmtNum(dryRunReport.totalRows)} rows
          </div>
          <div className="ws-card-stats" style={{ margin: '4px 0' }}>
            <div className="ws-stat">
              <div className="ws-stat-label">Control rows</div>
              <div className="ws-stat-val mono">{fmtNum(dryRunReport.controlRows)}</div>
            </div>
            <div className="ws-stat">
              <div className="ws-stat-label">Product rows</div>
              <div className="ws-stat-val mono">{fmtNum(dryRunReport.productRows)}</div>
            </div>
            <div className="ws-stat">
              <div className="ws-stat-label">Orphan rows</div>
              <div className="ws-stat-val mono" style={{ color: dryRunReport.orphanRows > 0 ? 'var(--warning)' : undefined }}>
                {fmtNum(dryRunReport.orphanRows)}
              </div>
            </div>
          </div>

          {dryRunReport.products.length > 0 && (
            <div>
              <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', marginBottom: 4 }}>
                Per-product breakdown
              </div>
              {dryRunReport.products.map((p) => (
                <div key={p.productId} style={{ fontSize: 11, fontFamily: 'var(--sc-font-mono, monospace)', padding: '2px 0' }}>
                  <strong>{p.productSlug}</strong> — {fmtNum(p.totalRows)} rows
                </div>
              ))}
            </div>
          )}

          {dryRunReport.orphans.length > 0 && (
            <div style={{ color: 'var(--warning)', fontSize: 11 }}>
              ⚠ {dryRunReport.orphans.length} orphan bucket(s) found — review before applying.
            </div>
          )}
        </div>
      )}

      {applyResult && (
        <div
          style={{
            padding: 10,
            background: applyResult.success ? 'var(--success-bg, var(--bg-surface-2))' : 'var(--danger-bg)',
            color: applyResult.success ? 'var(--text-primary)' : 'var(--danger)',
            borderRadius: 'var(--radius-sm)',
            fontSize: 11.5,
            fontFamily: 'var(--sc-font-mono, monospace)',
            display: 'grid',
            gap: 4,
          }}
        >
          {applyResult.alreadyApplied ? (
            <div>Split was already applied — no changes made.</div>
          ) : (
            <>
              <div>
                {applyResult.success ? '✓ ' : '✗ '}
                {fmtNum(applyResult.rowsCopied)} rows copied to {applyResult.productDbs.length} product DB(s)
              </div>
              {applyResult.backupPath && (
                <div style={{ color: 'var(--text-tertiary)' }}>Backup: {applyResult.backupPath}</div>
              )}
              {applyResult.errors.length > 0 && (
                <div>
                  {applyResult.errors.map((e, i) => (
                    <div key={i} style={{ color: 'var(--danger)' }}>• {e}</div>
                  ))}
                </div>
              )}
              {applyResult.success && (
                <div style={{ color: 'var(--warning)', fontFamily: 'inherit', fontWeight: 600, marginTop: 4 }}>
                  ⚠ Restart Forge.Api to activate per-product routing.
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}
