import { useEffect, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import type { AgentProviderSettings } from '@/types';

// Settings → Agent providers. Picks which signed-in GitHub account the shared
// Copilot server runs as (host-wide, host admins only). Independent of the
// GitHub repository provider's account, so agents can use a different Copilot
// seat than issues/PRs. Claude's credentials come from host config and are
// shown read-only.

const DEFAULT_CHOICE = '';

export function AgentProvidersPanel() {
  const [data, setData] = useState<AgentProviderSettings | null>(null);
  const [choice, setChoice] = useState<string>(DEFAULT_CHOICE);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<Date | null>(null);

  async function load() {
    try {
      const next = await forgeApi.getAgentProviders();
      setData(next);
      setChoice(next.copilot.selectedAccount ?? DEFAULT_CHOICE);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  useEffect(() => { void load(); }, []);

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const next = await forgeApi.setCopilotAccount(choice === DEFAULT_CHOICE ? null : choice);
      setData(next);
      setChoice(next.copilot.selectedAccount ?? DEFAULT_CHOICE);
      setSavedAt(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  if (!data) {
    return <div style={{ fontSize: 12, color: error ? 'var(--danger)' : 'var(--text-tertiary)' }}>{error ?? 'Loading agent providers…'}</div>;
  }

  const copilot = data.copilot;
  const dirty = choice !== (copilot.selectedAccount ?? DEFAULT_CHOICE);
  const failed = !copilot.server.running && !!copilot.server.lastStartError;
  const statusColor = copilot.server.running ? 'var(--success)' : failed ? 'var(--danger)' : 'var(--text-tertiary)';
  const statusText = copilot.server.running
    ? `running as ${copilot.server.account ?? 'unknown'}`
    : failed ? 'start failed' : 'not started';

  return (
    <div className="workstream-grid">
      <div className="ws-card" data-agent-provider="copilot" style={{ display: 'grid', gap: 10, '--ws-color': statusColor } as React.CSSProperties}>
        <div className="ws-card-head">
          <div className="ws-card-head-left">
            <div className="ws-card-eyebrow">
              <span className="epic-badge">COPILOT</span>
              <span style={{ opacity: 0.4 }}>·</span>
              <span>GitHub.Copilot.SDK · shared server for every Copilot agent</span>
            </div>
            <div className="ws-card-name">GitHub Copilot</div>
          </div>
          <span className="ws-card-status-pill" style={{ color: statusColor, borderColor: statusColor }}>
            <span className="dot" style={{ background: statusColor }} />
            {statusText}
          </span>
        </div>

        {failed && (
          <div role="alert" style={{ padding: 10, borderRadius: 'var(--radius-sm)', background: 'var(--danger-bg)', color: 'var(--danger)', fontSize: 11.5, lineHeight: 1.5 }}>
            Last start failed{copilot.server.lastStartErrorAt ? ` at ${new Date(copilot.server.lastStartErrorAt).toLocaleTimeString()}` : ''}: {copilot.server.lastStartError}
          </div>
        )}

        <fieldset className="ws-card-section" style={{ border: 0, padding: 0, margin: 0, display: 'grid', gap: 6 }}>
          <legend className="ws-card-section-title" style={{ marginBottom: 6 }}>Account</legend>
          <AccountOption
            value={DEFAULT_CHOICE}
            checked={choice === DEFAULT_CHOICE}
            onChange={setChoice}
            title="Same as GitHub provider"
            detail={copilot.defaultAccount
              ? <>currently <span style={{ fontFamily: 'var(--font-mono)' }}>{copilot.defaultAccount}</span> (Repository providers → GitHub → Preferred account, else gh&apos;s active account)</>
              : 'no GitHub account signed in'}
          />
          {copilot.accounts.map((a) => (
            <AccountOption
              key={a.login}
              value={a.login}
              checked={choice === a.login}
              onChange={setChoice}
              disabled={a.state !== 'success'}
              title={<span style={{ fontFamily: 'var(--font-mono)' }}>{a.login}</span>}
              badges={[a.activeInGh ? 'gh active' : null, a.state !== 'success' ? a.state : null]}
              detail={a.scopes ? `scopes: ${a.scopes}` : undefined}
            />
          ))}
          {copilot.accounts.length === 0 && (
            <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)' }}>
              No GitHub accounts found in gh. Run <code>gh auth login</code> on the host for each account you want to offer.
            </div>
          )}
        </fieldset>

        <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', lineHeight: 1.5 }}>
          Applies to every Copilot agent on this host. Saving a different account restarts the shared Copilot server,
          so Copilot turns already running lose their connection. gh&apos;s active account (used by git and the GitHub
          provider) is not changed. To add an account, run <code>gh auth login</code> on the host, then reopen this tab.
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
          <button className="ws-action primary" onClick={() => void save()} disabled={!dirty || saving}>
            {saving ? 'Saving…' : 'Use this account'}
          </button>
          <button className="ws-action" onClick={() => void load()} disabled={saving}>Refresh</button>
          {savedAt && !dirty && (
            <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>Saved · {savedAt.toLocaleTimeString()}</span>
          )}
          {!savedAt && copilot.updatedBy && copilot.updatedAt && (
            <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
              Last changed {new Date(copilot.updatedAt).toLocaleString()} by {copilot.updatedBy}
            </span>
          )}
        </div>
        {error && <div role="alert" style={{ fontSize: 11.5, color: 'var(--danger)' }}>{error}</div>}
      </div>

      <div className="ws-card" data-agent-provider="claude" style={{ display: 'grid', gap: 10 }}>
        <div className="ws-card-head">
          <div className="ws-card-head-left">
            <div className="ws-card-eyebrow">
              <span className="epic-badge">CLAUDE</span>
              <span style={{ opacity: 0.4 }}>·</span>
              <span>Anthropic · per crew or agent runtime override</span>
            </div>
            <div className="ws-card-name">Claude</div>
          </div>
        </div>
        <div className="ws-card-stats">
          <div className="ws-stat">
            <div className="ws-stat-label">SDK mode · API key</div>
            <div className="ws-stat-val">{data.claude.apiKeyConfigured ? 'configured' : 'not set'}</div>
          </div>
          <div className="ws-stat">
            <div className="ws-stat-label">CLI mode · sign-in</div>
            <div className="ws-stat-val mono">{data.claude.cliPath ?? 'claude on PATH'}</div>
          </div>
        </div>
        <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', lineHeight: 1.5 }}>
          SDK mode uses <code>Forge:Agents:AnthropicApiKey</code> from host configuration. CLI mode uses whatever account
          the <code>claude</code> CLI is signed in to on the host (subscription or API key). Choosing a Claude account here
          is not supported yet.
        </div>
      </div>
    </div>
  );
}

function AccountOption({ value, checked, onChange, title, detail, badges = [], disabled = false }: {
  value: string;
  checked: boolean;
  onChange: (value: string) => void;
  title: React.ReactNode;
  detail?: React.ReactNode;
  badges?: (string | null)[];
  disabled?: boolean;
}) {
  return (
    <label
      style={{
        display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '2px 10px', alignItems: 'start',
        padding: '8px 10px', borderRadius: 'var(--radius-sm)',
        border: `1px solid ${checked ? 'var(--accent)' : 'var(--border)'}`,
        background: checked ? 'var(--bg-surface)' : 'transparent',
        opacity: disabled ? 0.55 : 1, cursor: disabled ? 'not-allowed' : 'pointer',
      }}
    >
      <input type="radio" name="copilot-account" value={value} checked={checked} disabled={disabled}
        onChange={() => onChange(value)} style={{ marginTop: 2 }} />
      <span style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', fontSize: 12.5, color: 'var(--text-primary)', fontWeight: 500 }}>
        {title}
        {badges.filter((b): b is string => !!b).map((b) => (
          <span key={b} className="epic-badge" style={{ fontSize: 9 }}>{b}</span>
        ))}
      </span>
      {detail && <span style={{ gridColumn: 2, fontSize: 10.5, color: 'var(--text-tertiary)' }}>{detail}</span>}
    </label>
  );
}