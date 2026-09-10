import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useModelCatalog, selectableModels } from '@/state/useModelCatalogStore';
import type { Crew, AgentDetail } from '@/types';

interface Props {
  open: boolean;
  onClose: () => void;
  onCreated?: (agent: AgentDetail) => void;
}

// Phase 80 — create a brand-new agent and assign it to a crew. Name + crew are
// required; the agent inherits the crew's backend / model defaults unless an
// override is picked here. Mirrors the create-agent endpoint (POST /api/agents).
export function NewAgentModal({ open, onClose, onCreated }: Props) {
  const [name, setName] = useState('');
  const [crewId, setCrewId] = useState('');
  const [level, setLevel] = useState('1');
  const [backend, setBackend] = useState('');        // '' = inherit crew
  const [invokeMode, setInvokeMode] = useState('');   // '' = inherit crew
  const [generatorModel, setGeneratorModel] = useState(''); // '' = inherit
  const [evaluatorModel, setEvaluatorModel] = useState(''); // '' = inherit
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const [crews, setCrews] = useState<Crew[]>([]);
  const models = useModelCatalog();
  const pickable = selectableModels(models);
  const nameRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!open) return;
    setName(''); setLevel('1'); setBackend(''); setInvokeMode('');
    setGeneratorModel(''); setEvaluatorModel(''); setBusy(false); setErr(null);
    let cancelled = false;
    void forgeApi.listCrews()
      .then((cs) => { if (!cancelled) { setCrews(cs); setCrewId((prev) => prev || cs[0]?.id || ''); } })
      .catch(() => { /* offline */ });
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => { cancelled = true; window.cancelAnimationFrame(t); };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanName = name.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    if (!crewId) { setErr('Pick a crew'); return; }
    if (generatorModel && generatorModel === evaluatorModel) {
      setErr('Generator and evaluator models must differ'); return;
    }
    const lvl = Number.parseInt(level, 10);
    setBusy(true);
    setErr(null);
    try {
      const agent = await forgeApi.createAgent({
        name: cleanName,
        crewId,
        level: Number.isNaN(lvl) ? 1 : Math.max(1, lvl),
        backend: backend || undefined,
        invokeMode: invokeMode || undefined,
        generatorModel: generatorModel || undefined,
        evaluatorModel: evaluatorModel || undefined,
      });
      useToastStore.getState().show(`Agent "${agent.name}" created`, 'success');
      onCreated?.(agent);
      onClose();
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Create failed');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className={`modal-backdrop${open ? ' show' : ''}`} onClick={() => { if (!busy) onClose(); }} aria-hidden={!open} />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-agent-title"
        onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && !busy) { e.preventDefault(); void submit(); } }}
        style={{ width: 520 }}
      >
        <h3 className="modal-title" id="new-agent-title" style={{ margin: '0 0 4px' }}>New agent</h3>
        <p className="modal-text" style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-tertiary)' }}>
          The agent joins the chosen crew and inherits its backend &amp; models unless you override them below.
        </p>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Name</span>
            <input ref={nameRef} value={name} onChange={(e) => setName(e.target.value)} maxLength={60} disabled={busy} style={inputStyle} placeholder="e.g. Mercury" />
          </label>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 120px', gap: 12 }}>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Crew</span>
              <select value={crewId} onChange={(e) => setCrewId(e.target.value)} disabled={busy} style={inputStyle}>
                {crews.length === 0 && <option value="">No crews</option>}
                {crews.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </label>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Level</span>
              <input type="number" min={1} max={10} value={level} onChange={(e) => setLevel(e.target.value)} disabled={busy} style={inputStyle} />
            </label>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Backend</span>
              <select value={backend} onChange={(e) => setBackend(e.target.value)} disabled={busy} style={inputStyle}>
                <option value="">Inherit crew</option>
                <option value="copilot">Copilot</option>
                <option value="claude">Claude</option>
              </select>
            </label>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Invoke mode</span>
              <select value={invokeMode} onChange={(e) => setInvokeMode(e.target.value)} disabled={busy} style={inputStyle}>
                <option value="">Inherit crew</option>
                <option value="sdk">SDK</option>
                <option value="cli">CLI</option>
              </select>
            </label>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Generator model</span>
              <select value={generatorModel} onChange={(e) => setGeneratorModel(e.target.value)} disabled={busy} style={inputStyle}>
                <option value="">Inherit crew</option>
                {pickable.map((m) => <option key={m.id} value={m.id}>{m.displayName}</option>)}
              </select>
            </label>
            <label style={{ display: 'grid', gap: 5 }}>
              <span style={labelStyle}>Evaluator model</span>
              <select value={evaluatorModel} onChange={(e) => setEvaluatorModel(e.target.value)} disabled={busy} style={inputStyle}>
                <option value="">Inherit crew</option>
                {pickable.map((m) => <option key={m.id} value={m.id}>{m.displayName}</option>)}
              </select>
            </label>
          </div>

          {err && (
            <div role="alert" style={errStyle}><span style={{ marginRight: 6 }}>⚠</span>{err}</div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn btn-primary" onClick={() => void submit()} disabled={busy || !name.trim() || !crewId}>
            {busy ? 'Creating…' : 'Create agent'}
          </button>
        </div>
      </div>
    </>
  );
}

const labelStyle: React.CSSProperties = {
  fontSize: 11, fontWeight: 600, letterSpacing: 0.2, textTransform: 'uppercase', color: 'var(--text-secondary)',
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '8px 11px', fontSize: 13, fontFamily: 'inherit',
  color: 'var(--text-primary)', background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)', borderRadius: 'var(--radius-sm)', outline: 'none',
};
const errStyle: React.CSSProperties = {
  fontSize: 12, color: 'var(--danger)', background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))', borderRadius: 'var(--radius-sm)', padding: '7px 10px',
};
