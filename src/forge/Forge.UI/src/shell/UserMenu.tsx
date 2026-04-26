import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useCurrentUser, useCurrentUserStore } from '@/state/useCurrentUserStore';
import { useToastStore } from '@/state/useToastStore';
import { forgeApi } from '@/api/forgeApi';
import type { CurrentUserProvider } from '@/types';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Anchor element rect — popover positions below + right-aligned to it. */
  anchor: DOMRect | null;
}

// Phase 9.10 — TopBar avatar popover. Lists the signed-in providers,
// surfaces re-authenticate / sign-out for each, and links to /settings.
// Closes on outside click or Esc.
export function UserMenu({ open, onClose, anchor }: Props) {
  const me = useCurrentUser();
  const refresh = useCurrentUserStore((s) => s.refresh);
  const ref = useRef<HTMLDivElement | null>(null);
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const navigate = useNavigate();

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    const onMouseDown = (e: MouseEvent) => {
      if (!ref.current) return;
      if (!ref.current.contains(e.target as Node)) onClose();
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('mousedown', onMouseDown);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('mousedown', onMouseDown);
    };
  }, [open, onClose]);

  if (!open) return null;

  const top = (anchor?.bottom ?? 56) + 6;
  const right = window.innerWidth - (anchor?.right ?? window.innerWidth);

  async function reauth(kind: 'ado' | 'github') {
    setBusy((m) => ({ ...m, [kind]: true }));
    try {
      await forgeApi.signIn(kind);
      await refresh();
      useToastStore.getState().show(`Re-authenticated ${kind}`, 'success');
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Re-auth failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setBusy((m) => ({ ...m, [kind]: false }));
    }
  }

  async function signOut(kind: 'ado' | 'github') {
    setBusy((m) => ({ ...m, [kind]: true }));
    try {
      await forgeApi.signOut(kind);
      await refresh();
      useToastStore.getState().show(`Signed out of ${kind}`, 'info');
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Sign-out failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setBusy((m) => ({ ...m, [kind]: false }));
    }
  }

  return (
    <div
      ref={ref}
      role="menu"
      aria-label="User menu"
      style={{
        position: 'fixed',
        top,
        right,
        zIndex: 80,
        width: 320,
        background: 'var(--bg-elevated)',
        border: '1px solid var(--border-strong)',
        borderRadius: 'var(--radius-lg)',
        boxShadow: 'var(--shadow-lg)',
        padding: 14,
        fontSize: 12.5,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, paddingBottom: 12, borderBottom: '1px solid var(--border)' }}>
        <div
          className="avatar"
          style={
            me.avatarUrl
              ? { margin: 0, backgroundImage: `url(${me.avatarUrl})`, backgroundSize: 'cover' }
              : { margin: 0 }
          }
        >
          {me.avatarUrl ? '' : me.initials}
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div style={{ fontWeight: 600, fontSize: 13, color: 'var(--text-primary)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {me.displayName}
          </div>
          {me.email && (
            <div style={{ fontSize: 11, color: 'var(--text-tertiary)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {me.email}
            </div>
          )}
          <div style={{ fontSize: 10.5, marginTop: 3 }}>
            <RolePill role={me.role} />
          </div>
        </div>
      </div>

      <div style={{ marginTop: 10 }}>
        <ProviderRow
          kind="ado"
          label="Azure DevOps"
          state={me.providers.ado}
          busy={!!busy.ado}
          onReauth={() => void reauth('ado')}
          onSignOut={() => void signOut('ado')}
        />
        <ProviderRow
          kind="github"
          label="GitHub"
          state={me.providers.github}
          busy={!!busy.github}
          onReauth={() => void reauth('github')}
          onSignOut={() => void signOut('github')}
        />
      </div>

      <div style={{ marginTop: 10, paddingTop: 10, borderTop: '1px solid var(--border)', display: 'flex', justifyContent: 'flex-end', gap: 6 }}>
        <button
          className="btn btn-secondary"
          onClick={() => { onClose(); navigate('/settings'); }}
        >
          Settings
        </button>
      </div>
    </div>
  );
}

interface ProviderRowProps {
  kind: 'ado' | 'github';
  label: string;
  state: CurrentUserProvider;
  busy: boolean;
  onReauth: () => void;
  onSignOut: () => void;
}

function ProviderRow({ label, state, busy, onReauth, onSignOut }: ProviderRowProps) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '8px 0' }}>
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)' }}>{label}</div>
        <div style={{ fontSize: 11, color: state.connected ? 'var(--success)' : 'var(--text-tertiary)' }}>
          {state.connected
            ? <>● connected{state.account ? ` · ${state.account}` : ''}</>
            : state.error
              ? <>○ {state.error}</>
              : <>○ not connected</>}
        </div>
      </div>
      <div style={{ display: 'flex', gap: 4 }}>
        <button
          className="btn btn-secondary"
          style={{ padding: '4px 8px', fontSize: 11 }}
          onClick={onReauth}
          disabled={busy}
        >
          {state.connected ? 'Re-auth' : 'Sign in'}
        </button>
        {state.connected && (
          <button
            className="btn btn-ghost"
            style={{ padding: '4px 8px', fontSize: 11 }}
            onClick={onSignOut}
            disabled={busy}
          >
            Sign out
          </button>
        )}
      </div>
    </div>
  );
}

function RolePill({ role }: { role: string }) {
  const label = role === 'product-owner' ? 'Product Owner'
    : role === 'tech-lead' ? 'Tech lead'
    : role;
  const color = role === 'product-owner' ? 'var(--accent)'
    : role === 'tech-lead' ? 'var(--info, var(--accent))'
    : 'var(--text-tertiary)';
  return (
    <span style={{
      display: 'inline-block',
      padding: '1px 7px',
      borderRadius: 999,
      border: `1px solid ${color}`,
      color,
      fontWeight: 600,
      letterSpacing: 0.2,
    }}>
      {label}
    </span>
  );
}
