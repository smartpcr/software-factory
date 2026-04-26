import { useEffect, useState } from 'react';
import { forgeApi, type AuthStatus, type ProviderHealth } from '@/api/forgeApi';

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
  auth?: AuthStatus | undefined;
  authSupported?: boolean;
  busy: boolean;
  syncing: boolean;
  signingIn: boolean;
  onTest: () => void;
  onSync: () => void;
  onSignIn: () => void;
  onSignOut: () => void;
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

function ProviderRow({
  kind, label, description, configKeys, health, auth, authSupported = false,
  busy, syncing, signingIn, onTest, onSync, onSignIn, onSignOut,
}: ProviderRowProps) {
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

      {authSupported && (
        <div className="ws-card-section">
          <div className="ws-card-section-title">Sign-in</div>
          {auth?.connected ? (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: 12, color: 'var(--text-primary)', fontWeight: 500 }}>
                  Signed in as <span style={{ fontFamily: 'var(--font-mono)' }}>{auth.userName ?? auth.account}</span>
                </div>
                {auth.expiresAt && (
                  <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 2 }}>
                    Token valid until {new Date(auth.expiresAt).toLocaleString()}
                  </div>
                )}
              </div>
              <button className="ws-action" onClick={onSignOut} disabled={signingIn}>
                Sign out
              </button>
            </div>
          ) : (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
              <div style={{ minWidth: 0, fontSize: 11.5, color: 'var(--text-secondary)' }}>
                {auth?.error
                  ? <>Last attempt: <strong>{auth.error}</strong></>
                  : <>Sign in opens a Microsoft account picker (WAM / browser popup). Forge stores only an encrypted refresh token.</>}
              </div>
              <button
                className="ws-action primary"
                onClick={onSignIn}
                disabled={signingIn}
                title="Opens the Microsoft account picker"
              >
                {signingIn ? 'Signing in…' : `Sign in to ${label}`}
              </button>
            </div>
          )}
        </div>
      )}

      <div className="ws-card-section">
        <div className="ws-card-section-title">
          {authSupported ? 'Optional configuration' : 'Configuration keys'}
        </div>
        <ul style={{ margin: 0, paddingLeft: 18, fontSize: 11.5, color: 'var(--text-secondary)' }}>
          {configKeys.map((k) => (
            <li key={k} style={{ fontFamily: 'var(--font-mono)' }}>{k}</li>
          ))}
        </ul>
      </div>

      <div className="ws-card-foot">
        <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
          {authSupported
            ? <>Sign in above (preferred), or set the keys for a headless / CI flow.</>
            : <>Set the keys above in user-secrets / env, then click <strong>Test connection</strong>.</>}
        </div>
        <div className="ws-card-actions">
          <button className="ws-action" onClick={onTest} disabled={busy || syncing}>
            {busy ? 'Testing…' : 'Test connection'}
          </button>
          <button
            className="ws-action primary"
            onClick={onSync}
            disabled={busy || syncing || !health?.ok}
            title={
              !health?.ok
                ? 'Connect the provider first'
                : 'Pull work items + PRs now (bypasses the 60s background poll)'
            }
          >
            {syncing ? 'Syncing…' : 'Sync now'}
          </button>
        </div>
      </div>
    </div>
  );
}

interface ProviderEntry {
  kind: 'ado' | 'github';
  label: string;
  description: string;
  configKeys: string[];
  authSupported: boolean;
}

const PROVIDERS: ProviderEntry[] = [
  {
    kind: 'ado',
    label: 'Azure DevOps',
    description: 'Work items + repos + pipelines',
    authSupported: true,
    configKeys: [
      'Forge:Ado:OrgUrl       e.g. https://dev.azure.com/contoso',
      'Forge:Ado:Project',
      'Forge:Ado:Repository',
      'Forge:Ado:Pat               (optional — only needed for headless / CI runs)',
      'Forge:Ado:WebhookSecret  (X-Forge-Hook-Secret)',
    ],
  },
  {
    kind: 'github',
    label: 'GitHub',
    description: 'Issues + pull requests + Actions',
    // Phase 9.3 — GitHub interactive sign-in lands next; PAT for now.
    authSupported: false,
    configKeys: [
      'Forge:GitHub:Repo       e.g. microsoft/forge',
      'Forge:GitHub:Pat',
      'Forge:GitHub:WebhookSecret  (X-Hub-Signature-256)',
    ],
  },
];

export function SettingsView() {
  const [byKind, setByKind] = useState<Record<string, ProviderHealth>>({});
  const [authByKind, setAuthByKind] = useState<Record<string, AuthStatus>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [syncing, setSyncing] = useState<Record<string, boolean>>({});
  const [signingIn, setSigningIn] = useState<Record<string, boolean>>({});
  const [syncMsg, setSyncMsg] = useState<string | null>(null);
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
    // Auth status is independent — fetch in parallel for every provider that supports it.
    for (const p of PROVIDERS.filter((x) => x.authSupported)) {
      try {
        const status = await forgeApi.authStatus(p.kind);
        setAuthByKind((m) => ({ ...m, [p.kind]: status }));
      } catch {
        // Endpoint not available yet (older API) — keep undefined and the row hides the auth section.
      }
    }
  }

  async function signIn(kind: 'ado' | 'github') {
    setSigningIn((m) => ({ ...m, [kind]: true }));
    setSyncMsg(null);
    try {
      const status = await forgeApi.signIn(kind);
      setAuthByKind((m) => ({ ...m, [kind]: status }));
      setSyncMsg(`Signed in to ${kind} as ${status.userName ?? status.account ?? 'unknown'}`);
      // Refresh provider health so connected/last-sync update.
      await test(kind);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setSyncMsg(`Sign-in failed: ${msg}`);
      setAuthByKind((m) => ({
        ...m,
        [kind]: { kind, connected: false, account: null, userName: null, expiresAt: null, error: msg },
      }));
    } finally {
      setSigningIn((m) => ({ ...m, [kind]: false }));
    }
  }

  async function signOut(kind: 'ado' | 'github') {
    setSigningIn((m) => ({ ...m, [kind]: true }));
    try {
      await forgeApi.signOut(kind);
      setAuthByKind((m) => ({
        ...m,
        [kind]: { kind, connected: false, account: null, userName: null, expiresAt: null, error: null },
      }));
      setSyncMsg(`Signed out of ${kind}`);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setSyncMsg(`Sign-out failed: ${msg}`);
    } finally {
      setSigningIn((m) => ({ ...m, [kind]: false }));
    }
  }

  async function sync(kind: string) {
    setSyncing((m) => ({ ...m, [kind]: true }));
    setSyncMsg(null);
    try {
      const h = await forgeApi.syncProviderNow(kind);
      setByKind((map) => ({ ...map, [kind]: h }));
      setSyncMsg(`Synced ${kind} · ${new Date().toLocaleTimeString()}`);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setSyncMsg(`Sync failed: ${msg}`);
    } finally {
      setSyncing((m) => ({ ...m, [kind]: false }));
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

      {Object.values(byKind).every((h) => !h?.ok) && Object.keys(byKind).length > 0 && (
        <div
          style={{
            margin: '0 24px 14px',
            padding: 12,
            borderRadius: 'var(--radius-sm)',
            background: 'var(--warning-bg)',
            color: 'var(--warning)',
            fontSize: 12,
            lineHeight: 1.5,
          }}
        >
          No provider is connected yet. Set the configuration keys below in user-secrets
          (dev) or environment variables (prod), then click <strong>Test connection</strong>.
          Until a provider is configured the board, PR view and timeline run on cached / sample data.
        </div>
      )}

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

      {syncMsg && (
        <div
          style={{
            margin: '0 24px 14px',
            padding: 10,
            borderRadius: 'var(--radius-sm)',
            background: syncMsg.startsWith('Sync failed') ? 'var(--danger-bg)' : 'var(--success-bg, var(--bg-surface-2))',
            color: syncMsg.startsWith('Sync failed') ? 'var(--danger)' : 'var(--text-primary)',
            fontSize: 11.5,
          }}
        >
          {syncMsg}
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
              authSupported={p.authSupported}
              health={byKind[p.kind]}
              auth={authByKind[p.kind]}
              busy={!!busy[p.kind]}
              syncing={!!syncing[p.kind]}
              signingIn={!!signingIn[p.kind]}
              onTest={() => void test(p.kind)}
              onSync={() => void sync(p.kind)}
              onSignIn={() => void signIn(p.kind)}
              onSignOut={() => void signOut(p.kind)}
            />
          ))}
        </div>
      </div>
    </section>
  );
}
