import { useEffect, useState } from 'react';
import { useStateStore } from '@/state/useStateStore';
import { useWorkstreamsStore } from '@/state/useWorkstreamsStore';
import { sampleState } from '@/sampleData';

interface Props {
  open: boolean;
  onClose: () => void;
}

export function NewWorkstreamModal({ open, onClose }: Props) {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const create = useWorkstreamsStore((s) => s.create);

  const [name, setName] = useState('');
  const [base, setBase] = useState('main');
  const [epic, setEpic] = useState(epics[0]?.id ?? '');
  const [worktreePath, setWorktreePath] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setName('');
    setBase('main');
    setEpic(epics[0]?.id ?? '');
    setWorktreePath('');
    setErr(null);
  }, [open, epics]);

  async function submit() {
    if (!name) { setErr('Name is required'); return; }
    setBusy(true);
    setErr(null);
    const id = `ws-${name.replace(/[^a-z0-9-]+/gi, '-').toLowerCase()}`;
    const branch = `ws/${name.replace(/\s+/g, '-').toLowerCase()}`;
    const result = await create({
      id,
      name,
      branch,
      base,
      epic,
      worktreePath: worktreePath || null,
    });
    setBusy(false);
    if (result.ok) onClose();
    else setErr(result.detail ?? 'Create failed');
  }

  return (
    <>
      <div className={`modal-backdrop${open ? ' show' : ''}`} onClick={onClose} />
      <div className={`modal${open ? ' show' : ''}`} role="dialog" aria-modal="true">
        <h3 className="modal-title">New workstream</h3>
        <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
          <Field label="Name">
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="oidc-rotation" autoFocus />
          </Field>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <Field label="Epic">
              <select value={epic} onChange={(e) => setEpic(e.target.value)}>
                {epics.map((ep) => <option key={ep.id} value={ep.id}>{ep.id}</option>)}
              </select>
            </Field>
            <Field label="Base branch">
              <input value={base} onChange={(e) => setBase(e.target.value)} placeholder="main" />
            </Field>
            <Field label="Worktree path (optional)">
              <input
                value={worktreePath}
                onChange={(e) => setWorktreePath(e.target.value)}
                placeholder="C:\forge\atlas\wt\oidc-rotation"
                style={{ gridColumn: '1 / span 2' }}
              />
            </Field>
          </div>
          {err && (
            <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>{err}</div>
          )}
        </div>
        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={busy || !name}>
            {busy ? 'Spawning…' : 'Spawn'}
          </button>
        </div>
      </div>
    </>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4, fontSize: 11, color: 'var(--text-secondary)' }}>
      <span>{label}</span>
      <span
        style={{
          display: 'block',
          padding: '6px 10px',
          borderRadius: 'var(--radius-sm)',
          border: '1px solid var(--border-strong)',
          background: 'var(--bg-surface)',
        }}
      >
        {children}
      </span>
    </label>
  );
}
