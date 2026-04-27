import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { RepoIcon } from '@/products/RepoIcon';
import type { Repo } from '@/types';

interface Props {
  open: boolean;
  repo: Repo;
  onClose: () => void;
  onSaved: (updated: Repo) => void;
}

// Edit repo modal — lets the operator change the local clone path, default
// branch, and display name. Saving PATCHes the repo, then asks the API to
// ensure the repo is cloned at cloneRoot. If the directory is missing the
// server runs `git clone` automatically; if it already contains a matching
// checkout the call is a no-op.
export function EditRepoModal({ open, repo, onClose, onSaved }: Props) {
  const [displayName, setDisplayName] = useState(repo.displayName);
  const [cloneRoot, setCloneRoot] = useState(repo.cloneRoot);
  const [defaultBranch, setDefaultBranch] = useState(repo.defaultBranch);
  const [busy, setBusy] = useState(false);
  const [phase, setPhase] = useState<'idle' | 'saving' | 'cloning'>('idle');
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const toast = useToastStore((s) => s.show);

  useEffect(() => {
    if (!open) return;
    setDisplayName(repo.displayName);
    setCloneRoot(repo.cloneRoot);
    setDefaultBranch(repo.defaultBranch);
    setBusy(false);
    setPhase('idle');
    setErr(null);
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open, repo]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !busy) onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose, busy]);

  async function submit() {
    const cleanName = displayName.trim();
    const cleanPath = cloneRoot.trim();
    const cleanBranch = defaultBranch.trim() || 'main';
    if (!cleanName) { setErr('Display name is required'); return; }
    if (!cleanPath) { setErr('Local repo path is required'); return; }

    setBusy(true);
    setErr(null);
    try {
      setPhase('saving');
      const patch: { displayName?: string; cloneRoot?: string; defaultBranch?: string } = {};
      if (cleanName !== repo.displayName) patch.displayName = cleanName;
      if (cleanPath !== repo.cloneRoot) patch.cloneRoot = cleanPath;
      if (cleanBranch !== repo.defaultBranch) patch.defaultBranch = cleanBranch;

      const updated = Object.keys(patch).length > 0
        ? await forgeApi.patchRepo(repo.id, patch)
        : repo;
      onSaved(updated);

      setPhase('cloning');
      const result = await forgeApi.cloneRepo(repo.id);
      if (result.outcome === 'cloned') {
        toast(`Cloned ${cleanName} to ${cleanPath}`, 'success');
      } else {
        toast(`Saved "${cleanName}" — local repo already present`, 'success');
      }
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Save failed';
      setErr(msg);
    } finally {
      setBusy(false);
      setPhase('idle');
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey && !busy) {
      e.preventDefault();
      void submit();
    }
  }

  const buttonLabel = phase === 'saving' ? 'Saving…' : phase === 'cloning' ? 'Cloning…' : 'Save changes';

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={() => { if (!busy) onClose(); }}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="edit-repo-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 520 }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 14 }}>
          <RepoIcon kind={repo.kind} size={32} />
          <div style={{ minWidth: 0, flex: 1 }}>
            <h3 className="modal-title" id="edit-repo-title" style={{ margin: 0 }}>
              Edit repo
            </h3>
            <p className="modal-text" style={{ margin: '4px 0 0' }}>
              Change the local path and the repo will be cloned automatically when missing.
            </p>
          </div>
        </div>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <FieldGroup label="Remote URL" hint="Read-only. Detach and re-attach to change the remote.">
            <input
              value={repo.remoteUrl}
              disabled
              style={{
                ...inputStyle,
                fontFamily: 'var(--font-mono, monospace)',
                fontSize: 12,
                opacity: 0.6,
                cursor: 'not-allowed',
              }}
            />
          </FieldGroup>

          <FieldGroup label="Display name">
            <input
              ref={nameRef}
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              maxLength={120}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
            />
          </FieldGroup>

          <FieldGroup
            label="Local repo path"
            hint="Absolute path on this machine. If the folder is missing or empty it will be cloned now."
          >
            <input
              value={cloneRoot}
              onChange={(e) => setCloneRoot(e.target.value)}
              placeholder="C:\\src\\my-repo"
              spellCheck={false}
              disabled={busy}
              style={{ ...inputStyle, fontFamily: 'var(--font-mono, monospace)', fontSize: 12 }}
            />
          </FieldGroup>

          <FieldGroup label="Default branch">
            <input
              value={defaultBranch}
              onChange={(e) => setDefaultBranch(e.target.value)}
              placeholder="main"
              maxLength={120}
              spellCheck={false}
              disabled={busy}
              style={{ ...inputStyle, fontFamily: 'var(--font-mono, monospace)', fontSize: 12 }}
            />
          </FieldGroup>

          {phase === 'cloning' && (
            <div role="status" style={infoStyle}>
              Cloning {repo.remoteUrl} into {cloneRoot.trim()} — this may take a minute.
            </div>
          )}

          {err && (
            <div role="alert" style={errStyle}>
              <span style={{ marginRight: 6 }}>⚠</span>
              {err}
            </div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            className="btn btn-primary"
            onClick={() => void submit()}
            disabled={busy || !displayName.trim() || !cloneRoot.trim()}
          >
            {buttonLabel}
          </button>
        </div>
      </div>
    </>
  );
}

function FieldGroup({
  label, hint, children,
}: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 5 }}>
      <span
        style={{
          fontSize: 11,
          fontWeight: 600,
          letterSpacing: 0.2,
          textTransform: 'uppercase',
          color: 'var(--text-secondary)',
        }}
      >
        {label}
      </span>
      {children}
      {hint && (
        <span style={{ fontSize: 11, color: 'var(--text-tertiary)', lineHeight: 1.4 }}>
          {hint}
        </span>
      )}
    </label>
  );
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

const infoStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--text-secondary)',
  background: 'var(--bg-surface-alt, rgba(255, 255, 255, 0.04))',
  border: '1px solid var(--border-subtle)',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};
