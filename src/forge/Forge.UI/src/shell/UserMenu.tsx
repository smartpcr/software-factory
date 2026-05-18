import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useCurrentUser, useCurrentUserStore } from '@/state/useCurrentUserStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useToastStore } from '@/state/useToastStore';
import { forgeApi } from '@/api/forgeApi';
import type { CurrentUserProvider } from '@/types';
import { ALL_AVATARS, useOperatorAvatarStore } from '@/state/useOperatorAvatarStore';

const ROLE_OPTIONS: Array<{ value: string; label: string }> = [
  { value: 'product-owner', label: 'Product owner' },
  { value: 'tech-lead',     label: 'Tech lead' },
  { value: 'arch',          label: 'Architect' },
  { value: 'eng',           label: 'Engineer' },
  { value: 'rev',           label: 'Reviewer' },
  { value: 'qa',            label: 'QA' },
  { value: 'human',         label: 'Human (default)' },
];

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
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const ref = useRef<HTMLDivElement | null>(null);
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [roleSaving, setRoleSaving] = useState(false);
  const [showAvatarPicker, setShowAvatarPicker] = useState(false);
  const operatorFilename = useOperatorAvatarStore((s) => s.filename);
  const operatorUrl = useOperatorAvatarStore((s) => s.url);
  const setOperatorAvatar = useOperatorAvatarStore((s) => s.setFilename);
  const displayAvatarUrl = operatorUrl || me.avatarUrl;
  const navigate = useNavigate();

  const activeProduct = useMemo(
    () => products.find((p) => p.id === activeProductId) ?? null,
    [products, activeProductId],
  );

  async function changeRole(role: string) {
    if (role === me.role) return;
    setRoleSaving(true);
    try {
      await forgeApi.setMyRole(role, activeProductId);
      await refresh();
      const scope = activeProduct ? activeProduct.name : 'all products';
      useToastStore.getState().show(`Role on ${scope}: ${role}`, 'success');
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Role change failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setRoleSaving(false);
    }
  }

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

  // Auto-flip vertical + horizontal so the popover stays in the viewport.
  // Approximate menu size so the math works before we have a real rect.
  const MENU_W = 320;
  const MENU_H = 280;
  const r = anchor;
  const placeAbove = r ? (window.innerHeight - r.bottom < MENU_H + 12) : false;
  const placeLeftAligned = r ? (r.right < MENU_W) : false;
  const top = placeAbove
    ? Math.max(8, (r?.top ?? 8) - MENU_H - 6)
    : (r?.bottom ?? 56) + 6;
  const right = placeLeftAligned
    ? Math.max(8, window.innerWidth - (r?.left ?? 0) - MENU_W)
    : window.innerWidth - (r?.right ?? window.innerWidth);

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
          style={{
            margin: 0,
            cursor: 'pointer',
            ...(displayAvatarUrl
              ? { backgroundImage: `url(${displayAvatarUrl})`, backgroundSize: 'cover' }
              : {}),
          }}
          title="Click to change avatar"
          onClick={() => setShowAvatarPicker((v) => !v)}
        >
          {displayAvatarUrl ? '' : me.initials}
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

      {showAvatarPicker && (
        <div style={{ padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
          <div style={{ fontFamily: 'var(--font-mono)', fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.1, marginBottom: 6 }}>
            Choose avatar
          </div>
          <div style={{ maxHeight: 150, overflowY: 'auto', display: 'flex', flexWrap: 'wrap', gap: 4 }}>
            {ALL_AVATARS.map((a) => (
              <img
                key={a.filename}
                src={a.url}
                alt={a.displayName}
                title={`${a.displayName} · ${a.role}`}
                onClick={() => {
                  setOperatorAvatar(a.filename);
                  setShowAvatarPicker(false);
                  void forgeApi.setMyAvatar(a.filename).then(() => refresh());
                }}
                style={{
                  width: 32, height: 32, borderRadius: 999, objectFit: 'cover', cursor: 'pointer',
                  border: operatorFilename === a.filename ? '2px solid var(--accent)' : '1px solid var(--border)',
                  opacity: operatorFilename === a.filename ? 1 : 0.7,
                }}
              />
            ))}
          </div>
        </div>
      )}

      {me.id !== '' && (
        <div style={{ marginTop: 12, paddingTop: 12, borderTop: '1px solid var(--border)' }}>
          <div style={{
            fontFamily: 'var(--font-mono)',
            fontSize: 9.5,
            color: 'var(--text-tertiary)',
            textTransform: 'uppercase',
            letterSpacing: 0.1,
            marginBottom: 6,
          }}>
            My role on {activeProduct ? <strong style={{ color: 'var(--text-secondary)' }}>{activeProduct.name}</strong> : 'all products'}
          </div>
          <select
            value={me.role}
            onChange={(e) => void changeRole(e.target.value)}
            disabled={roleSaving}
            style={{
              width: '100%',
              padding: '7px 8px',
              fontSize: 12.5,
              fontFamily: 'inherit',
              color: 'var(--text-primary)',
              background: 'var(--bg-surface)',
              border: '1px solid var(--border-strong)',
              borderRadius: 'var(--radius-sm, 6px)',
              outline: 'none',
              cursor: roleSaving ? 'wait' : 'pointer',
            }}
          >
            {ROLE_OPTIONS.map((r) => (
              <option key={r.value} value={r.value}>{r.label}</option>
            ))}
          </select>
          <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 4, lineHeight: 1.4 }}>
            {activeProduct
              ? <>Role override applies only on <strong>{activeProduct.name}</strong>. Switch products to set a different role elsewhere.</>
              : <>Sets your default role across products. Per-product overrides take precedence when set.</>}
          </div>
        </div>
      )}

      {!me.providers.ado.connected && !me.providers.github.connected && (
        <div
          role="note"
          style={{
            marginTop: 10,
            padding: '8px 10px',
            borderRadius: 'var(--radius-sm, 6px)',
            border: '1px dashed var(--border-strong)',
            background: 'var(--bg-elevated, transparent)',
            color: 'var(--text-secondary)',
            fontSize: 11.5,
            lineHeight: 1.45,
          }}
        >
          Sign in to a provider to start using Forge. <strong>Azure DevOps</strong> uses
          your Windows account (WAM); <strong>GitHub</strong> uses the <code>gh</code> CLI.
          Both can be set up below.
        </div>
      )}

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
