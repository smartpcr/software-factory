import { OperatorSessionSettings } from '@/auth/OperatorGate';
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import '../styles/settings.css';
import { ChatIntegrations } from '../swarm/plugins/ChatIntegrations';
import { forgeApi, type AuthStatus, type ProviderHealth } from '@/api/forgeApi';
import { useCurrentUserStore } from '@/state/useCurrentUserStore';
import type { UserSettings } from '@/types';
import { MaintenancePanel } from './MaintenancePanel';
import { DbSplitPanel } from './DbSplitPanel';

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
  health: ProviderHealth | undefined;
  auth?: AuthStatus | undefined;
  authSupported?: boolean;
  settings: UserSettings | null;
  onSaveDefaults: (req: Partial<UserSettings>) => Promise<void>;
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
  kind, label, description, health, auth, authSupported = false,
  settings, onSaveDefaults,
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
                  : kind === 'ado'
                    ? <>Opens the Microsoft account picker in your browser. Forge stores only an encrypted refresh token.</>
                    : <>Delegates to the GitHub CLI (<code style={{ fontFamily: 'var(--font-mono)' }}>gh auth login --web</code>) — no client id, no app registration.</>}
              </div>
              <button
                className="ws-action primary"
                onClick={onSignIn}
                disabled={signingIn}
                title={kind === 'ado' ? 'Opens the Microsoft account picker' : 'Opens a console window running gh auth login'}
              >
                {signingIn ? 'Signing in…' : `Sign in to ${label}`}
              </button>
            </div>
          )}
        </div>
      )}

      {kind === 'ado' && (
        <DefaultsPanel
          kind="ado"
          settings={settings}
          onSave={onSaveDefaults}
        />
      )}
      {kind === 'github' && (
        <DefaultsPanel
          kind="github"
          settings={settings}
          onSave={onSaveDefaults}
        />
      )}

      <div className="ws-card-foot">
        <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
          {authSupported
            ? <>Sign in above (preferred). Defaults flow into every "create" action.</>
            : <>Defaults flow into every create / sync action. Configure provider PAT in env / user-secrets for now.</>}
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

interface DefaultsPanelProps {
  kind: 'ado' | 'github';
  settings: UserSettings | null;
  onSave: (req: Partial<UserSettings>) => Promise<void>;
}

function DefaultsPanel({ kind, settings, onSave }: DefaultsPanelProps) {
  const [orgUrl, setOrgUrl] = useState(settings?.defaultAdoOrgUrl ?? '');
  const [project, setProject] = useState(settings?.defaultAdoProject ?? '');
  const [owner, setOwner] = useState(settings?.defaultGitHubOwner ?? '');
  const [preferredAdo, setPreferredAdo] = useState(settings?.preferredAdoAccount ?? '');
  const [preferredGh, setPreferredGh] = useState(settings?.preferredGitHubAccount ?? '');
  const [saving, setSaving] = useState(false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);

  // Re-prime the inputs when /api/me/settings finally loads.
  useEffect(() => {
    if (!settings) return;
    if (kind === 'ado') {
      setOrgUrl(settings.defaultAdoOrgUrl ?? '');
      setProject(settings.defaultAdoProject ?? '');
      setPreferredAdo(settings.preferredAdoAccount ?? '');
    } else {
      setOwner(settings.defaultGitHubOwner ?? '');
      setPreferredGh(settings.preferredGitHubAccount ?? '');
    }
  }, [kind, settings]);

  const dirty = kind === 'ado'
    ? orgUrl !== (settings?.defaultAdoOrgUrl ?? '') ||
      project !== (settings?.defaultAdoProject ?? '') ||
      preferredAdo !== (settings?.preferredAdoAccount ?? '')
    : owner !== (settings?.defaultGitHubOwner ?? '') ||
      preferredGh !== (settings?.preferredGitHubAccount ?? '');

  async function save() {
    setSaving(true);
    try {
      const patch: Partial<UserSettings> = kind === 'ado'
        ? { defaultAdoOrgUrl: orgUrl, defaultAdoProject: project, preferredAdoAccount: preferredAdo }
        : { defaultGitHubOwner: owner, preferredGitHubAccount: preferredGh };
      await onSave(patch);
      setSavedAt(new Date());
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="ws-card-section" style={{ display: 'grid', gap: 8 }}>
      <div className="ws-card-section-title">Default {kind === 'ado' ? 'ADO' : 'GitHub'} target</div>
      {kind === 'ado' ? (
        <>
          <FieldLabel label="Org URL" hint="e.g. https://msazure.visualstudio.com or https://dev.azure.com/contoso">
            <input
              value={orgUrl}
              onChange={(e) => setOrgUrl(e.target.value)}
              placeholder="https://dev.azure.com/contoso"
              spellCheck={false}
              style={inputStyle}
            />
          </FieldLabel>
          <FieldLabel label="Project" hint="Project name within the org.">
            <input
              value={project}
              onChange={(e) => setProject(e.target.value)}
              placeholder="One"
              spellCheck={false}
              style={inputStyle}
            />
          </FieldLabel>
          <FieldLabel label="Preferred account" hint="Microsoft account email used for sign-in. Empty = first cached account.">
            <input
              value={preferredAdo}
              onChange={(e) => setPreferredAdo(e.target.value)}
              placeholder="xiaodoli@microsoft.com"
              spellCheck={false}
              style={inputStyle}
            />
          </FieldLabel>
        </>
      ) : (
        <>
          <FieldLabel label="Owner / org" hint="GitHub username or org for default scope.">
            <input
              value={owner}
              onChange={(e) => setOwner(e.target.value)}
              placeholder="microsoft"
              spellCheck={false}
              style={inputStyle}
            />
          </FieldLabel>
          <FieldLabel label="Preferred account" hint="GitHub login (gh username). Empty = whichever account gh reports as active.">
            <input
              value={preferredGh}
              onChange={(e) => setPreferredGh(e.target.value)}
              placeholder="xiaodoli"
              spellCheck={false}
              style={inputStyle}
            />
          </FieldLabel>
        </>
      )}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <button
          className="ws-action primary"
          onClick={() => void save()}
          disabled={!dirty || saving}
        >
          {saving ? 'Saving…' : 'Save default'}
        </button>
        {savedAt && !dirty && (
          <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
            Saved · {savedAt.toLocaleTimeString()}
          </span>
        )}
      </div>
    </div>
  );
}

function FieldLabel({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4 }}>
      <span style={{ fontSize: 11, fontWeight: 600, letterSpacing: 0.2, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>{label}</span>
      {children}
      {hint && <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>{hint}</span>}
    </label>
  );
}

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '7px 10px',
  fontSize: 12.5,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
};

interface ProviderEntry {
  kind: 'ado' | 'github';
  label: string;
  description: string;
  authSupported: boolean;
}

const PROVIDERS: ProviderEntry[] = [
  { kind: 'ado',    label: 'Azure DevOps', description: 'Work items + repos + pipelines',  authSupported: true },
  { kind: 'github', label: 'GitHub',       description: 'Issues + pull requests + Actions', authSupported: true },
];

const SETTINGS_TABS = [{ id: 'chat', label: 'Chat' }, { id: 'repositories', label: 'Repository providers' }, { id: 'general', label: 'General' }] as const;

function SettingsSection({ id, title, description, children, initiallyOpen = false }: {
  id: string; title: string; description: string; children: ReactNode; initiallyOpen?: boolean;
}) {
  return <details className="settings-section" id={id} open={initiallyOpen}>
    <summary id={`${id}-heading`}>
      <svg className="settings-section-chevron" viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true"><path d="m6 3 5 5-5 5" /></svg>
      <h2>{title}<span className="settings-section-description">{description}</span></h2>
    </summary>
    <div className="settings-section-content" role="region" aria-labelledby={`${id}-heading`}>{children}</div>
  </details>;
}

export function SettingsView() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = SETTINGS_TABS.find(tab => tab.id === searchParams.get('tab'))?.id ?? 'chat';
  const scrollArea = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { scrollArea.current?.scrollTo(0, 0); }, [activeTab]);
  function selectTab(tab: string) {
    setSearchParams(params => { params.set('tab', tab); return params; }, { replace: true });
  }
  function closeSettings() {
    if (window.history.state?.idx > 0) navigate(-1);
    else navigate('/workstreams', { replace: true });
  }
  const [byKind, setByKind] = useState<Record<string, ProviderHealth>>({});
  const [authByKind, setAuthByKind] = useState<Record<string, AuthStatus>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [syncing, setSyncing] = useState<Record<string, boolean>>({});
  const [signingIn, setSigningIn] = useState<Record<string, boolean>>({});
  const [syncMsg, setSyncMsg] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [settings, setSettings] = useState<UserSettings | null>(null);

  useEffect(() => {
    void load();
    void loadSettings();
  }, []);

  async function loadSettings() {
    try {
      const s = await forgeApi.getMySettings();
      setSettings(s);
    } catch {
      // ignore — defaults panel renders empty fields
    }
  }

  async function saveDefaults(patch: Partial<UserSettings>) {
    const next = await forgeApi.patchMySettings(patch);
    setSettings(next);
    // Probe ADO immediately when org/project changed so the connection light updates.
    if ('defaultAdoOrgUrl' in patch || 'defaultAdoProject' in patch) {
      await test('ado');
    }
  }

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
      // Phase 9.7 — propagate identity change to TopBar / SideBar without page reload.
      void useCurrentUserStore.getState().refresh();
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
      void useCurrentUserStore.getState().refresh();
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
    <section className="view active settings-view" id="view-settings" aria-labelledby="settings-title">
      <header className="settings-page-header">
        <div className="settings-heading-row">
          <div><div className="page-eyebrow">WORKSPACE / PREFERENCES</div><h1 className="page-title" id="settings-title">Settings</h1></div>
          <button type="button" className="btn btn-secondary settings-close" aria-label="Close settings" onClick={closeSettings}>
            <svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true"><path d="m4 4 8 8M12 4l-8 8" /></svg>Close
          </button>
        </div>
        <div className="settings-tabs" role="tablist" aria-label="Settings categories">
          {SETTINGS_TABS.map((tab, index) => <button key={tab.id} id={`settings-tab-${tab.id}`} type="button" role="tab" aria-selected={activeTab === tab.id}
            aria-controls={`settings-panel-${tab.id}`} tabIndex={activeTab === tab.id ? 0 : -1} onClick={() => selectTab(tab.id)}
            onKeyDown={event => {
              const next = event.key === 'ArrowRight' ? (index + 1) % SETTINGS_TABS.length : event.key === 'ArrowLeft' ? (index + SETTINGS_TABS.length - 1) % SETTINGS_TABS.length
                : event.key === 'Home' ? 0 : event.key === 'End' ? SETTINGS_TABS.length - 1 : null;
              if (next === null) return;
              event.preventDefault(); selectTab(SETTINGS_TABS[next].id);
              event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role=tab]')[next]?.focus();
            }}>{tab.label}</button>)}
        </div>
      </header>
      <div ref={scrollArea} className="settings-scroll" role="region" aria-label="Settings sections" tabIndex={0}>
        <div role="tabpanel" id="settings-panel-chat" aria-labelledby="settings-tab-chat" hidden={activeTab !== 'chat'}>
          <SettingsSection id="settings-chat" title="Chat integrations" description="Connections, account linking and host alerts." initiallyOpen>
            <ChatIntegrations />
          </SettingsSection>
        </div>
        <div role="tabpanel" id="settings-panel-repositories" aria-labelledby="settings-tab-repositories" hidden={activeTab !== 'repositories'}>
          <SettingsSection id="settings-providers" title="Providers & sync" description="Connect source control and choose synchronization defaults." initiallyOpen>
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

            <div className="workstream-grid">
              {PROVIDERS.map((p) => (
                <ProviderRow
                  key={p.kind}
                  kind={p.kind}
                  label={p.label}
                  description={p.description}
                  authSupported={p.authSupported}
                  health={byKind[p.kind]}
                  auth={authByKind[p.kind]}
                  settings={settings}
                  onSaveDefaults={saveDefaults}
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
          </SettingsSection>
        </div>
        <div role="tabpanel" id="settings-panel-general" aria-labelledby="settings-tab-general" hidden={activeTab !== 'general'}>
          <SettingsSection id="settings-session" title="Browser session" description="Manage this browser’s pairing with your Forge operator.">
            <OperatorSessionSettings />
          </SettingsSection>
          <SettingsSection id="settings-maintenance" title="Maintenance" description="Database storage, transcript retention and database separation.">
            <div className="workstream-grid">
              <MaintenancePanel />
              <DbSplitPanel />
            </div>
          </SettingsSection>
        </div>
      </div>
    </section>
  );
}