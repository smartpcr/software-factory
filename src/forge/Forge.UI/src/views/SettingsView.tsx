import { useEffect, useState } from 'react';
import { forgeApi, type ProviderHealth } from '@/api/forgeApi';

// Phase 5.4 + 5.11 — provider configuration + health.
// Phase-5 server reads its options from configuration (user-secrets / env).
// This page therefore renders the LIVE health of each registered provider,
// surfaces a "Test connection" button, and links to the docs that describe
// where to set Forge:Ado:* / Forge:GitHub:* secrets. A POST endpoint that
// writes config from the UI is intentionally a Phase-R follow-up so we
// don't accidentally store PATs in SQLite without an encryption story.

interface ProviderRowProps {
  kind: string;
  label: string;
  description: string;
  configKeys: string[];
  health: ProviderHealth | undefined;
  busy: boolean;
  onTest: () => void;
}

function relTime(iso: string | null): string {
  if (!iso) return 'never';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const diffMs = Date.now() - t;
  if (diffMs < 60_000) return `${Math.round(diffMs / 1000)}s ago`;
  if (diffMs < 3_600_000) return `${Math.round(diffMs / 60_000)}m ago`;
  if (diffMs < 86_400_000) return `${Math.round(diffMs / 3_600_000)}h ago`;
  return `${Math.round(diffMs / 86_400_000)}d ago`;
}

function ProviderRow({ kind, label, description, configKeys, health, busy, onTest }: ProviderRowProps) {
  const ok = health?.ok ?? false;
  const dotColor = ok ? 'var(--success)' : 'var(--danger)';
  return (
    <div
      className="ws-card"
      data-provider={kind}
      style={{ display: 'grid', gap: 10, '--ws-color': dotColor } as React.CSSProperties}
    >
      <div className="ws-card-head">
        <div className="ws-card-head-left">
          <div className="ws-card-eyebrow">
            <span className="epic-badge">{kind.toUpperCase()}</span>
            <span style={{ opacity: 0.4 }}>·</span>
            <span>{description}</span>
          </div>
          <div className="ws-card-name">{label}</div>
        </div>
        <span className="ws-card-status-pill" style={{ color: dotColor, borderColor: dotColor }}>
          <span className="dot" style={{ background: dotColor }} />
          {ok ? 'connected' : 'not connected'}
        </span>
      </div>

      <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>
        {health?.message ?? 'no health probe yet'}
      </div>

      <div className="ws-card-stats">
        <div className="ws-stat">
          <div className="ws-stat-label">Last sync</div>
          <div className="ws-stat-val mono">{relTime(health?.lastSync ?? null)}</div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Errors (since last sync)</div>
          <div className={`ws-stat-val ${health && health.lastErrorCount > 0 ? 'conflicts' : ''}`}>
            {health?.lastErrorCount ?? 0}
          </div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Rate-limit budget</div>
          <div className="ws-stat-val mono">
            {health?.rateLimitRemaining != null
              ? `${health.rateLimitRemaining}${health.rateLimitLimit ? ' / ' + health.rateLimitLimit : ''}`
              : '—'}
          </div>
        </div>
      </div>

      <div className="ws-card-section">
        <div className="ws-card-section-title">Configuration keys</div>
        <ul style={{ margin: 0, paddingLeft: 18, fontSize: 11.5, color: 'var(--text-secondary)' }}>
          {configKeys.map((k) => (
            <li key={k} style={{ fontFamily: 'var(--font-mono)' }}>{k}</li>
          ))}
        </ul>
      </div>

      <div className="ws-card-foot">
        <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
          Set the keys above in user-secrets / env, then click <strong>Test connection</strong>.
        </div>
        <div className="ws-card-actions">
          <button className="ws-action primary" onClick={onTest} disabled={busy}>
            {busy ? 'Testing…' : 'Test connection'}
          </button>
        </div>
      </div>
    </div>
  );
}

const PROVIDERS: Array<{ kind: string; label: string; description: string; configKeys: string[] }> = [
  {
    kind: 'ado',
    label: 'Azure DevOps',
    description: 'Work items + repos + pipelines',
    configKeys: [
      'Forge:Ado:OrgUrl       e.g. https://dev.azure.com/contoso',
      'Forge:Ado:Project',
      'Forge:Ado:Repository',
      'Forge:Ado:Pat',
      'Forge:Ado:WebhookSecret  (X-Forge-Hook-Secret)',
    ],
  },
  {
    kind: 'github',
    label: 'GitHub',
    description: 'Issues + pull requests + Actions',
    configKeys: [
      'Forge:GitHub:Repo       e.g. microsoft/forge',
      'Forge:GitHub:Pat',
      'Forge:GitHub:WebhookSecret  (X-Hub-Signature-256)',
    ],
  },
];

export function SettingsView() {
  const [byKind, setByKind] = useState<Record<string, ProviderHealth>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    void load();
  }, []);

  async function load() {
    try {
      const list = await forgeApi.listProviders();
      const map: Record<string, ProviderHealth> = {};
      for (const h of list) map[h.kind] = h;
      setByKind(map);
      setLoadError(null);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : String(err));
    }
  }

  async function test(kind: string) {
    setBusy((b) => ({ ...b, [kind]: true }));
    try {
      const h = await forgeApi.testProvider(kind);
      setByKind((map) => ({ ...map, [kind]: h }));
    } catch (err) {
      setByKind((map) => ({
        ...map,
        [kind]: {
          kind,
          ok: false,
          message: err instanceof Error ? err.message : String(err),
          lastSync: null,
          lastErrorCount: 1,
          rateLimitRemaining: null,
          rateLimitLimit: null,
        },
      }));
    } finally {
      setBusy((b) => ({ ...b, [kind]: false }));
    }
  }

  return (
    <section className="view active" id="view-settings">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">05</span>&nbsp;Settings</span>
            <span className="sep">/</span>
            <span>providers</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Providers &amp; sync
          </h1>
        </div>
      </div>

      {loadError && (
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
          {loadError}
        </div>
      )}

      <div
        className="workstream-grid-wrap"
        style={{ paddingTop: 0 }}
      >
        <div className="workstream-grid">
          {PROVIDERS.map((p) => (
            <ProviderRow
              key={p.kind}
              kind={p.kind}
              label={p.label}
              description={p.description}
              configKeys={p.configKeys}
              health={byKind[p.kind]}
              busy={!!busy[p.kind]}
              onTest={() => void test(p.kind)}
            />
          ))}
        </div>
      </div>
    </section>
  );
}
