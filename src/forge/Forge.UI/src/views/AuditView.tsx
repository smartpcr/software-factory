import { useEffect, useState } from 'react';
import { forgeApi, type AuditEntry } from '@/api/forgeApi';

// Phase 6.10 — read-only audit log view.
function relTime(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const ms = Date.now() - t;
  if (ms < 60_000) return `${Math.round(ms / 1000)}s ago`;
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)}m ago`;
  if (ms < 86_400_000) return `${Math.round(ms / 3_600_000)}h ago`;
  return `${Math.round(ms / 86_400_000)}d ago`;
}

function statusColor(status: number): string {
  if (status >= 500) return 'var(--danger)';
  if (status >= 400) return 'var(--warning)';
  if (status >= 300) return 'var(--info)';
  return 'var(--success)';
}

export function AuditView() {
  const [rows, setRows] = useState<AuditEntry[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    forgeApi.listAudit(500)
      .then(setRows)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)));
    const id = window.setInterval(() => {
      forgeApi.listAudit(500).then(setRows).catch(() => { /* keep last good list */ });
    }, 5_000);
    return () => window.clearInterval(id);
  }, []);

  return (
    <section className="view active" id="view-audit">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">06</span>&nbsp;Audit</span>
            <span className="sep">/</span>
            <span>{rows.length} action{rows.length === 1 ? '' : 's'}</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />updates every 5s</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Audit log
          </h1>
        </div>
      </div>

      {error && (
        <div
          style={{
            margin: '0 24px 14px',
            padding: 10,
            borderRadius: 'var(--radius-sm)',
            background: 'var(--danger-bg)',
            color: 'var(--danger)',
            fontSize: 11.5,
          }}
        >
          {error}
        </div>
      )}

      <div style={{ padding: '0 24px 24px', overflow: 'auto' }}>
        <table style={{ width: '100%', fontSize: 11.5, borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ textAlign: 'left', color: 'var(--text-tertiary)' }}>
              <th style={th()}>When</th>
              <th style={th()}>User</th>
              <th style={th()}>Method</th>
              <th style={th()}>Path</th>
              <th style={th()}>Status</th>
              <th style={th()}>Idempotency</th>
              <th style={th()}>Body</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} style={{ borderTop: '1px solid var(--border)' }}>
                <td style={td('mono')}>{relTime(r.at)}</td>
                <td style={td()}>{r.user}</td>
                <td style={td('mono')}>{r.method}</td>
                <td style={td('mono')}>{r.path}</td>
                <td style={{ ...td('mono'), color: statusColor(r.status), fontWeight: 600 }}>{r.status}</td>
                <td style={td('mono')}>{r.idempotencyKey ?? '—'}</td>
                <td style={{ ...td('mono'), color: 'var(--text-tertiary)', maxWidth: 320, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {r.requestBodySnippet ?? '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function th(): React.CSSProperties {
  return { padding: '8px 10px', fontWeight: 500, fontFamily: 'var(--font-body)' };
}
function td(extra?: 'mono'): React.CSSProperties {
  return {
    padding: '8px 10px',
    color: 'var(--text-secondary)',
    fontFamily: extra === 'mono' ? 'var(--font-mono)' : 'inherit',
    fontVariantNumeric: 'tabular-nums',
  };
}
