import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import type { DiscoverableRepo, RepoKind } from '@/types';

interface Props {
  productId: string;
  open: boolean;
  onClose: () => void;
  onAttached: () => void;
}

// Phase 8.5 / 8.7 — modern, two-step add-repo modal.
// Step 1: pick a discoverable repo from ADO or GitHub.
// Step 2: confirm clone path + display name (auto-derived, editable).
// No `window.prompt` calls — everything is inline with the established
// .modal design tokens.
export function AddRepoModal({ productId, open, onClose, onAttached }: Props) {
  const [kind, setKind] = useState<RepoKind>('ado');
  const [discovered, setDiscovered] = useState<DiscoverableRepo[]>([]);
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState('');
  const [chosen, setChosen] = useState<DiscoverableRepo | null>(null);

  // Step-2 form state.
  const [repoId, setRepoId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [cloneRoot, setCloneRoot] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const cloneInputRef = useRef<HTMLInputElement | null>(null);

  // Reset on open / kind switch.
  useEffect(() => {
    if (!open) return;
    setChosen(null);
    setSearch('');
    setErr(null);
  }, [open, kind]);

  // Re-fetch the discoverable list when the kind tab changes.
  useEffect(() => {
    if (!open) return;
    setLoading(true);
    forgeApi
      .discoverProviderRepos(kind)
      .then(setDiscovered)
      .catch(() => setDiscovered([]))
      .finally(() => setLoading(false));
  }, [open, kind]);

  // When the user picks a repo, prime step-2 with sensible defaults.
  useEffect(() => {
    if (!chosen) return;
    const id = slugifyRepo(chosen.displayName);
    setRepoId(id);
    setDisplayName(chosen.displayName);
    setCloneRoot(`C:\\forge\\${id}`);
    // Focus the clone-path input once step 2 is rendered.
    const t = window.requestAnimationFrame(() => cloneInputRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [chosen]);

  // Esc closes / steps back.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (chosen) setChosen(null);
      else onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, chosen, onClose]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return discovered;
    return discovered.filter(
      (d) => d.displayName.toLowerCase().includes(q) || d.remoteUrl.toLowerCase().includes(q),
    );
  }, [discovered, search]);

  async function attach() {
    if (!chosen) return;
    if (!repoId.trim()) { setErr('Repo id is required'); return; }
    if (!cloneRoot.trim()) { setErr('Clone path is required'); return; }
    setBusy(true);
    setErr(null);
    try {
      await forgeApi.attachRepo(productId, {
        id: repoId.trim(),
        kind: chosen.kind,
        remoteUrl: chosen.remoteUrl,
        displayName: displayName.trim() || chosen.displayName,
        cloneRoot: cloneRoot.trim(),
        defaultBranch: chosen.defaultBranch,
        adoOrg: chosen.adoOrg ?? undefined,
        adoProject: chosen.adoProject ?? undefined,
        ghOwner: chosen.ghOwner ?? undefined,
        ghRepo: chosen.ghRepo ?? undefined,
      });
      useToastStore.getState().show(`${displayName || chosen.displayName} attached`, 'success');
      onAttached();
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Attach failed';
      setErr(humanize(msg));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={onClose}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="add-repo-title"
        style={{ width: 580, maxHeight: '80vh', display: 'flex', flexDirection: 'column' }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="modal-icon" style={{
          background: 'var(--accent-soft, var(--bg-surface-2))',
          borderColor: 'var(--accent)',
          color: 'var(--accent)',
        }}>
          <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
            <path d="M4 3h7l3 3v9H4V3Z" stroke="currentColor" strokeWidth="1.4" fill="none" />
            <path d="M11 3v3h3" stroke="currentColor" strokeWidth="1.4" fill="none" />
            <path d="M6 9h6M6 12h4" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </svg>
        </div>
        <h3 className="modal-title" id="add-repo-title">
          {chosen ? 'Confirm repository' : 'Attach repository'}
        </h3>
        <p className="modal-text">
          {chosen
            ? <>Attaching <strong>{chosen.displayName}</strong> to this product. Choose where Forge should clone it locally.</>
            : 'Pick a repo Forge can see from your configured account.'}
        </p>

        {!chosen && (
          <>
            <KindToggle kind={kind} onChange={setKind} />

            <div style={{ position: 'relative', marginBottom: 10 }}>
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={`Search ${kind === 'ado' ? 'ADO' : 'GitHub'} repos…`}
                style={{ ...inputStyle, paddingLeft: 30 }}
              />
              <svg
                width="13" height="13" viewBox="0 0 12 12" fill="none"
                aria-hidden
                style={{
                  position: 'absolute', left: 10, top: '50%',
                  transform: 'translateY(-50%)',
                  color: 'var(--text-tertiary)',
                }}
              >
                <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
                <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
              </svg>
            </div>

            <div style={listWrap}>
              {loading && <Empty title="Loading repos…" />}
              {!loading && filtered.length === 0 && (
                <Empty
                  title="No repos discovered"
                  sub={
                    kind === 'ado'
                      ? 'Configure Forge:Ado:OrgUrl + Project, or sign in (Phase 9).'
                      : 'Configure Forge:GitHub:Pat, or sign in (Phase 9).'
                  }
                />
              )}
              {!loading && filtered.map((d) => (
                <button
                  key={`${d.kind}:${d.remoteUrl}`}
                  className="repo-pick-row"
                  onClick={() => setChosen(d)}
                  style={pickRowStyle}
                >
                  <span style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, flex: 1 }}>
                    <span aria-hidden style={kindBadge(d.kind)}>
                      {d.kind === 'ado' ? 'ADO' : 'GH'}
                    </span>
                    <span style={{ minWidth: 0 }}>
                      <span style={pickRowName}>{d.displayName}</span>
                      <span style={pickRowUrl}>{d.remoteUrl}</span>
                    </span>
                  </span>
                  <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden style={{ color: 'var(--text-tertiary)' }}>
                    <path d="M5 3l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </button>
              ))}
            </div>
          </>
        )}

        {chosen && (
          <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
            <FieldGroup
              label="Repo id"
              hint="Used in API paths and as the worktree subdir name."
            >
              <input
                value={repoId}
                onChange={(e) => setRepoId(e.target.value)}
                spellCheck={false}
                disabled={busy}
                style={{ ...inputStyle, fontFamily: 'var(--font-mono, monospace)' }}
              />
            </FieldGroup>
            <FieldGroup label="Display name">
              <input
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                disabled={busy}
                style={inputStyle}
              />
            </FieldGroup>
            <FieldGroup
              label="Clone path"
              hint="Forge clones into this folder on first use. Worktrees go under {path}/.worktree/."
            >
              <input
                ref={cloneInputRef}
                value={cloneRoot}
                onChange={(e) => setCloneRoot(e.target.value)}
                spellCheck={false}
                disabled={busy}
                style={{ ...inputStyle, fontFamily: 'var(--font-mono, monospace)' }}
              />
            </FieldGroup>
            <FieldGroup label="Remote URL">
              <div
                style={{
                  ...inputStyle,
                  color: 'var(--text-secondary)',
                  background: 'var(--bg-surface-2, var(--bg-surface))',
                  fontFamily: 'var(--font-mono, monospace)',
                  fontSize: 12,
                  cursor: 'default',
                  userSelect: 'all',
                }}
              >
                {chosen.remoteUrl}
              </div>
            </FieldGroup>
            {err && (
              <div role="alert" style={errStyle}>
                <span style={{ marginRight: 6 }}>⚠</span>{err}
              </div>
            )}
          </div>
        )}

        <div className="modal-actions">
          {chosen ? (
            <>
              <button className="btn btn-secondary" onClick={() => setChosen(null)} disabled={busy}>
                ← Back
              </button>
              <button
                className="btn btn-primary"
                onClick={() => void attach()}
                disabled={busy || !repoId.trim() || !cloneRoot.trim()}
              >
                {busy ? 'Attaching…' : 'Attach repo'}
              </button>
            </>
          ) : (
            <button className="btn btn-secondary" onClick={onClose}>
              Cancel
            </button>
          )}
        </div>
      </div>
    </>
  );
}

function KindToggle({ kind, onChange }: { kind: RepoKind; onChange: (k: RepoKind) => void }) {
  return (
    <div
      role="tablist"
      aria-label="Provider"
      style={{
        display: 'inline-flex',
        padding: 3,
        borderRadius: 'var(--radius-md)',
        background: 'var(--bg-surface)',
        border: '1px solid var(--border)',
        marginBottom: 12,
      }}
    >
      {(['ado', 'github'] as RepoKind[]).map((k) => {
        const active = k === kind;
        return (
          <button
            key={k}
            role="tab"
            aria-selected={active}
            onClick={() => onChange(k)}
            style={{
              padding: '5px 14px',
              fontSize: 12,
              fontWeight: 600,
              letterSpacing: 0.2,
              borderRadius: 'var(--radius-sm)',
              background: active ? 'var(--bg-elevated)' : 'transparent',
              color: active ? 'var(--text-primary)' : 'var(--text-secondary)',
              border: 'none',
              cursor: 'pointer',
              boxShadow: active ? 'var(--shadow-sm, 0 1px 2px rgba(0,0,0,0.08))' : 'none',
              transition: 'all 120ms ease',
            }}
          >
            {k === 'ado' ? 'Azure DevOps' : 'GitHub'}
          </button>
        );
      })}
    </div>
  );
}

function FieldGroup({
  label, hint, children,
}: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 5 }}>
      <span style={{
        fontSize: 11, fontWeight: 600, letterSpacing: 0.2, textTransform: 'uppercase',
        color: 'var(--text-secondary)',
      }}>{label}</span>
      {children}
      {hint && (
        <span style={{ fontSize: 11, color: 'var(--text-tertiary)', lineHeight: 1.4 }}>{hint}</span>
      )}
    </label>
  );
}

function Empty({ title, sub }: { title: string; sub?: string }) {
  return (
    <div style={{
      padding: '28px 12px',
      textAlign: 'center',
      color: 'var(--text-tertiary)',
      fontSize: 12.5,
    }}>
      <div style={{ fontWeight: 600, color: 'var(--text-secondary)' }}>{title}</div>
      {sub && <div style={{ marginTop: 4 }}>{sub}</div>}
    </div>
  );
}

const listWrap: React.CSSProperties = {
  flex: 1,
  minHeight: 200,
  maxHeight: 320,
  overflowY: 'auto',
  marginBottom: 18,
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-md)',
  background: 'var(--bg-surface)',
};

const pickRowStyle: React.CSSProperties = {
  width: '100%',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  padding: '10px 12px',
  background: 'transparent',
  border: 'none',
  borderBottom: '1px solid var(--border)',
  cursor: 'pointer',
  textAlign: 'left',
  transition: 'background 120ms ease',
};

const pickRowName: React.CSSProperties = {
  display: 'block',
  fontSize: 13,
  fontWeight: 500,
  color: 'var(--text-primary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const pickRowUrl: React.CSSProperties = {
  display: 'block',
  fontSize: 11,
  color: 'var(--text-tertiary)',
  fontFamily: 'var(--font-mono, monospace)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
  marginTop: 1,
};

function kindBadge(k: RepoKind): React.CSSProperties {
  const base: React.CSSProperties = {
    display: 'inline-block',
    minWidth: 32,
    padding: '2px 7px',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: 0.4,
    textAlign: 'center',
    borderRadius: 999,
    border: '1px solid var(--border-strong)',
  };
  if (k === 'ado') {
    return { ...base, color: 'var(--info, #0078d4)', background: 'var(--info-bg, rgba(0,120,212,0.08))' };
  }
  return { ...base, color: 'var(--text-secondary)', background: 'var(--bg-surface-2, var(--bg-surface))' };
}

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 11px',
  fontSize: 13,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const errStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};

function slugifyRepo(s: string): string {
  return s
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

function humanize(msg: string): string {
  const m = msg.match(/^.*?:\s*\d+\s*(.*)$/);
  return (m?.[1] ?? msg).trim();
}
