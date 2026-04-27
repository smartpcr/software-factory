import { useEffect, useMemo, useState } from 'react';
import type { AgentDetail, Crew, ModelInfo, SoulPayload } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useHasRole } from '@/state/useCurrentUserStore';
import { useAgentEditorStore } from '@/state/useAgentEditorStore';
import { avatarFor } from '@/agentAvatar';

/**
 * Phase 13f — global agent-edit popup.
 *
 * Rendered once at AppShell level; visibility is driven by
 * <c>useAgentEditorStore.agentId</c>. Any view can call
 * <c>openAgent(id)</c> to surface it (Roster, Pair tab, Overview crew
 * rail, Plan-view detail panel, work-item assignee chip, &c.). The
 * modal fetches its own data (agent / crews / models / soul.md) on
 * open so callers don't have to thread reference data around.
 *
 * Human agents (role = "human") get a slimmer view — they only
 * participate in human-gate work and inbound triage, so AI runtime
 * overrides (backend / mode / generator-model / evaluator-model) +
 * soul.md don't apply to them.
 */
export function GlobalAgentEditor() {
  const agentId = useAgentEditorStore((s) => s.agentId);
  const close = useAgentEditorStore((s) => s.close);
  if (!agentId) return null;
  return <AgentEditModal agentId={agentId} onClose={close} />;
}

function AgentEditModal({ agentId, onClose }: {
  agentId: string;
  onClose: () => void;
}) {
  const canEdit = useHasRole('product-owner');

  const [original, setOriginal] = useState<AgentDetail | null>(null);
  const [crews, setCrews] = useState<Crew[]>([]);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [role, setRole] = useState('');
  const [soulPath, setSoulPath] = useState('');
  const [backend, setBackend] = useState('');
  const [invokeMode, setInvokeMode] = useState('');
  const [generatorModel, setGeneratorModel] = useState('');
  const [evaluatorModel, setEvaluatorModel] = useState('');
  const [soul, setSoul] = useState<SoulPayload | null>(null);
  const [soulText, setSoulText] = useState('');
  const [soulLoading, setSoulLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const [showPathField, setShowPathField] = useState(false);

  // Initial load — fetch agent, crews, models, soul in parallel.
  useEffect(() => {
    let cancelled = false;
    setError(null);
    setSoulLoading(true);
    void (async () => {
      try {
        const [agent, crewList, modelList] = await Promise.all([
          forgeApi.getAgent(agentId),
          forgeApi.listCrews().catch(() => [] as Crew[]),
          forgeApi.listModels().catch(() => [] as ModelInfo[]),
        ]);
        if (cancelled) return;
        setOriginal(agent);
        setCrews(crewList);
        setModels(modelList);
        setName(agent.name);
        setRole(agent.role);
        setSoulPath(agent.soulPath ?? '');
        setBackend(agent.backend ?? '');
        setInvokeMode(agent.invokeMode ?? '');
        setGeneratorModel(agent.generatorModel ?? '');
        setEvaluatorModel(agent.evaluatorModel ?? '');
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      }
    })();
    // soul.md fetched separately so we can show "loading" on the textarea
    // independently of the metadata.
    void (async () => {
      try {
        const p = await forgeApi.getAgentSoul(agentId);
        if (cancelled) return;
        setSoul(p);
        setSoulText(p.contents);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setSoulLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [agentId]);

  const isHuman = (role || original?.role || '').toLowerCase() === 'human';
  const homeCrew = crews.find((c) => c.id === original?.crewId) ?? null;
  const effectiveBackend = (backend || homeCrew?.backend || 'copilot').toLowerCase();
  const eligibleModels = useMemo(
    () => models.filter((m) => {
      if (m.status !== 'live') return false;
      if (effectiveBackend === 'claude') return m.vendor === 'anthropic' || m.vendor === 'forge';
      return m.vendor === 'copilot' || m.vendor === 'forge';
    }),
    [models, effectiveBackend],
  );

  const metaDirty = !!original && (
    name !== original.name ||
    role !== original.role ||
    (soulPath ?? '') !== (original.soulPath ?? '') ||
    (backend ?? '') !== (original.backend ?? '') ||
    (invokeMode ?? '') !== (original.invokeMode ?? '') ||
    (generatorModel ?? '') !== (original.generatorModel ?? '') ||
    (evaluatorModel ?? '') !== (original.evaluatorModel ?? '')
  );
  const soulDirty = !isHuman && soul !== null && soulText !== soul.contents;
  const dirty = metaDirty || soulDirty;

  const pinnedGenerator = generatorModel || (homeCrew?.model ?? '');
  const pinnedEvaluator = evaluatorModel || (homeCrew?.evaluatorModel ?? '');
  const samePairModel = !isHuman
    && !!pinnedGenerator
    && pinnedGenerator === pinnedEvaluator
    && (!!generatorModel || !!evaluatorModel);

  async function save() {
    if (!canEdit || !dirty || !original) return;
    setSaving(true); setError(null);
    try {
      if (soulDirty) {
        const nextSoul = await forgeApi.putAgentSoul(agentId, soulText);
        setSoul(nextSoul);
        setSoulText(nextSoul.contents);
      }
      if (metaDirty) {
        // Humans don't get the AI overrides — clear them server-side too,
        // so a former AI agent re-roled to human doesn't leak old pins.
        const overridePayload = isHuman
          ? { backend: '', invokeMode: '', generatorModel: '', evaluatorModel: '' }
          : { backend, invokeMode, generatorModel, evaluatorModel };
        const next = await forgeApi.patchAgent(agentId, {
          name, role, soulPath, ...overridePayload,
        });
        setOriginal(next);
      }
      setSavedAt(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  // ESC closes; ⌘/Ctrl-S saves.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
      if ((e.key === 's' || e.key === 'S') && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        if (canEdit && !saving && dirty) void save();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- modal lifetime is short
  }, [onClose, canEdit, saving, dirty]);

  if (!original) {
    return (
      <div style={modalOverlayStyle} onClick={onClose} role="dialog" aria-modal="true">
        <div style={modalCardStyle}>
          <p style={{ margin: 0, fontSize: 12.5 }}>
            {error ?? `Loading agent ${agentId}…`}
          </p>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 12 }}>
            <button onClick={onClose} style={modalBtnSecondaryStyle}>Close</button>
          </div>
        </div>
      </div>
    );
  }

  const capabilities = soul?.capabilities ?? original.capabilities;
  const avatar = avatarFor(original.id, original.name);

  return (
    <div
      style={modalOverlayStyle}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      role="dialog"
      aria-modal="true"
      aria-labelledby="agent-edit-title"
    >
      <div style={isHuman ? humanCardStyle : soulCardStyle}>
        <header style={soulHeadStyle}>
          <AvatarThumb id={original.id} name={original.name} role={original.role} url={avatar} size={40} />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={eyebrowStyle}>
              {isHuman ? 'human · ' : 'agent · '}{agentId}
              {isHuman && (
                <span style={humanPillStyle}>human-gate participant</span>
              )}
              {!isHuman && !soul?.exists && !soulLoading && (
                <span style={{ marginLeft: 8, padding: '1px 6px', borderRadius: 999, background: 'var(--warning-bg, rgba(245, 158, 11, 0.10))', color: 'var(--warning, rgb(180, 112, 0))', textTransform: 'uppercase', letterSpacing: 0.4 }}>
                  new soul
                </span>
              )}
            </div>
            <h3 id="agent-edit-title" style={{ margin: '4px 0 2px', fontSize: 17, fontWeight: 600 }}>
              {name || agentId}
              <span style={agentStatePillStyle(original.state)}>{original.state}</span>
            </h3>
            {!isHuman && soul?.path && (
              <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10.5, color: 'var(--text-tertiary)', wordBreak: 'break-all' }}>
                {soul.path}
              </div>
            )}
            {original.crewId && (
              <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
                crew · <strong style={{ color: 'var(--text-secondary)' }}>{homeCrew?.name ?? original.crewId}</strong>
              </div>
            )}
          </div>
          <button onClick={onClose} style={soulCloseBtnStyle} aria-label="Close">×</button>
        </header>

        {error && (
          <div style={{ padding: 8, fontSize: 11.5, color: 'var(--danger)', background: 'var(--danger-bg)', borderRadius: 4, margin: '8px 0' }}>
            {error}
          </div>
        )}

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, padding: '10px 0', borderBottom: '1px solid var(--border)' }}>
          <div>
            <SectionHead label="Name" />
            <input
              type="text" value={name} onChange={(e) => setName(e.target.value)}
              disabled={!canEdit || saving}
              style={searchInputStyle}
            />
          </div>
          <div>
            <SectionHead label="Role" />
            <select
              value={role} disabled={!canEdit || saving}
              onChange={(e) => setRole(e.target.value)}
              style={{ ...selectStyle, width: '100%' }}
            >
              {['arch', 'eng', 'rev', 'qa', 'human', 'product-owner', 'tech-lead'].map((r) => (
                <option key={r} value={r}>{r}</option>
              ))}
              {!['arch', 'eng', 'rev', 'qa', 'human', 'product-owner', 'tech-lead'].includes(role) && (
                <option value={role}>{role}</option>
              )}
            </select>
          </div>
        </div>

        {isHuman ? (
          // ── Human view ──────────────────────────────────────────────
          <div style={{ padding: '12px 0', display: 'grid', gap: 8 }}>
            <div style={humanInfoBoxStyle}>
              <strong>Human participant.</strong> Humans don't run AI invocations,
              so backend / mode / model overrides and <code>soul.md</code> are not
              applicable. Humans participate by:
              <ul style={{ margin: '6px 0 0 18px', padding: 0, lineHeight: 1.55 }}>
                <li><strong>Human-gate work</strong> — approving PRs that touch security paths or large diffs (Phase 13.6).</li>
                <li><strong>Inbound triage</strong> — routing work items synced from ADO / GitHub to a team.</li>
                <li><strong>Lead delegation</strong> — when a human is a crew's lead, they assign tasks down to crew members.</li>
              </ul>
            </div>
            <div style={hintStyle}>
              The full triage queue UI lands in a follow-up; today this section explains the role and
              shows the human's crew membership for reference.
            </div>
          </div>
        ) : (
          // ── AI agent view: runtime overrides + soul.md ───────────────
          <>
            <div style={{ padding: '10px 0', borderBottom: '1px solid var(--border)', display: 'grid', gap: 10 }}>
              <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between' }}>
                <SectionHead label="Runtime overrides" />
                <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                  empty = inherit{homeCrew ? ` from ${homeCrew.name}` : ''}
                </span>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                <div>
                  <SectionHead label="Agent type (backend)" />
                  <select
                    value={backend} disabled={!canEdit || saving}
                    onChange={(e) => setBackend(e.target.value)}
                    style={{ ...selectStyle, width: '100%' }}
                  >
                    <option value="">— inherit ({homeCrew?.backend ?? 'copilot'}) —</option>
                    <option value="copilot">Copilot · GitHub.Copilot.SDK</option>
                    <option value="claude">Claude · Anthropic /v1/messages</option>
                  </select>
                </div>
                <div>
                  <SectionHead label="Invoke mode" />
                  <select
                    value={invokeMode} disabled={!canEdit || saving}
                    onChange={(e) => setInvokeMode(e.target.value)}
                    style={{ ...selectStyle, width: '100%' }}
                  >
                    <option value="">— inherit ({homeCrew?.invokeMode ?? 'sdk'}) —</option>
                    <option value="sdk">SDK · streaming events + tools</option>
                    <option value="cli">CLI · stdin/stdout (text-only)</option>
                  </select>
                </div>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                <div>
                  <SectionHead label="Generator model" />
                  <select
                    value={generatorModel} disabled={!canEdit || saving}
                    onChange={(e) => setGeneratorModel(e.target.value)}
                    style={{ ...selectStyle, width: '100%' }}
                  >
                    <option value="">— inherit ({homeCrew?.model ?? 'unset'}) —</option>
                    {eligibleModels.map((m) => (
                      <option key={m.id} value={m.id} disabled={m.id === evaluatorModel && evaluatorModel !== ''}>
                        {m.displayName} · {m.id}
                      </option>
                    ))}
                    {generatorModel && !eligibleModels.some((m) => m.id === generatorModel) && (
                      <option value={generatorModel}>{generatorModel} (not in registry)</option>
                    )}
                  </select>
                </div>
                <div>
                  <SectionHead label="Evaluator model" />
                  <select
                    value={evaluatorModel} disabled={!canEdit || saving}
                    onChange={(e) => setEvaluatorModel(e.target.value)}
                    style={{ ...selectStyle, width: '100%' }}
                  >
                    <option value="">— inherit ({homeCrew?.evaluatorModel ?? 'unset'}) —</option>
                    {eligibleModels.map((m) => (
                      <option key={m.id} value={m.id} disabled={m.id === generatorModel && generatorModel !== ''}>
                        {m.displayName} · {m.id}
                      </option>
                    ))}
                    {evaluatorModel && !eligibleModels.some((m) => m.id === evaluatorModel) && (
                      <option value={evaluatorModel}>{evaluatorModel} (not in registry)</option>
                    )}
                  </select>
                </div>
              </div>
              {samePairModel && (
                <div style={warningBoxStyle}>
                  <strong>Pair models match.</strong> The diff-model rule says generator and evaluator must
                  run on different models; the server will refuse the PATCH.
                </div>
              )}
            </div>

            {capabilities.length > 0 && (
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
                <span style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12, alignSelf: 'center', marginRight: 4 }}>
                  capabilities ({capabilities.length})
                </span>
                {capabilities.map((c) => (
                  <span key={c} style={soulCapChipStyle}>{c}</span>
                ))}
              </div>
            )}

            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '8px 0 4px' }}>
              <SectionHead label="soul.md (markdown)" right={`${soulText.length.toLocaleString()} chars`} />
              <button
                type="button"
                onClick={() => setShowPathField((v) => !v)}
                style={soulBtnStyle}
                title="Override the on-disk path for soul.md"
              >
                {showPathField ? '− path override' : '+ path override'}
              </button>
            </div>
            {showPathField && (
              <div style={{ paddingBottom: 8 }}>
                <input
                  type="text" value={soulPath} onChange={(e) => setSoulPath(e.target.value)}
                  disabled={!canEdit || saving}
                  placeholder="empty = default %LOCALAPPDATA%\Forge\agents\{id}\soul.md"
                  style={{ ...searchInputStyle, fontFamily: 'var(--font-mono, monospace)', width: '100%' }}
                />
                <div style={hintStyle}>Set to override where soul.md lives on disk; empty = default.</div>
              </div>
            )}
            <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
              {soulLoading ? (
                <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-tertiary)', fontSize: 12 }}>Loading soul.md…</div>
              ) : (
                <textarea
                  value={soulText}
                  onChange={(e) => setSoulText(e.target.value)}
                  disabled={!canEdit || saving}
                  spellCheck={false}
                  placeholder={soul?.exists ? '' : '---\ncapabilities:\n  - sha256:…\n---\n\n# Agent system prompt\n\n…'}
                  style={soulTextareaStyle}
                />
              )}
            </div>
          </>
        )}

        <footer style={soulFootStyle}>
          <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
            {!canEdit && <em>read-only — PO required to save</em>}
            {canEdit && savedAt && !dirty && <span>saved · {savedAt.toLocaleTimeString()}</span>}
            {canEdit && dirty && (
              <em style={{ color: 'var(--warning, rgb(180, 112, 0))' }}>
                unsaved · {[metaDirty && 'metadata', soulDirty && 'soul.md'].filter(Boolean).join(' + ')}
              </em>
            )}
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={onClose} style={modalBtnSecondaryStyle}>Close</button>
            {canEdit && (
              <button
                onClick={() => void save()}
                disabled={saving || !dirty}
                style={{ ...modalBtnPrimaryStyle, opacity: saving || !dirty ? 0.55 : 1, cursor: saving || !dirty ? 'not-allowed' : 'pointer' }}
                title="⌘/Ctrl-S"
              >
                {saving ? 'Saving…' : 'Save'}
              </button>
            )}
          </div>
        </footer>
      </div>
    </div>
  );
}

// ── Small helpers + styles ────────────────────────────────────────────────

function SectionHead({ label, right }: { label: string; right?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', marginBottom: 6 }}>
      <span style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12 }}>
        {label}
      </span>
      {right && <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>{right}</span>}
    </div>
  );
}

function AvatarThumb({ id, name, role, url, size }: { id: string; name: string; role: string; url: string | null; size: number }) {
  if (url) {
    return (
      <img
        src={url}
        alt={name || id}
        title={`${id} · ${name}`}
        style={{ width: size, height: size, borderRadius: 999, objectFit: 'cover', border: '1px solid var(--border-strong)' }}
      />
    );
  }
  // Fallback: text chip with role-tinted border.
  return (
    <span
      title={`${id} · ${name}`}
      style={{
        width: size, height: size, borderRadius: 999, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: 'var(--font-mono, monospace)', fontSize: Math.round(size * 0.36), fontWeight: 700,
        background: 'var(--bg-sunken)', color: 'var(--text-secondary)',
        border: `1px solid ${roleTint(role)}`,
      }}
    >
      {id}
    </span>
  );
}

function roleTint(role: string): string {
  switch ((role || '').toLowerCase()) {
    case 'arch':           return 'rgba(168, 85, 247, 0.5)';
    case 'eng':            return 'rgba(20, 110, 245, 0.5)';
    case 'rev':            return 'rgba(34, 197, 94, 0.5)';
    case 'qa':             return 'rgba(245, 158, 11, 0.5)';
    case 'human':          return 'rgba(220, 38, 38, 0.5)';
    case 'product-owner':  return 'rgba(245, 158, 11, 0.7)';
    case 'tech-lead':      return 'rgba(20, 110, 245, 0.7)';
    default:               return 'var(--border-strong)';
  }
}

function agentStatePillStyle(state: string): React.CSSProperties {
  const palette: Record<string, [string, string]> = {
    idle:    ['rgba(120, 120, 120, 0.10)', 'rgb(110, 110, 110)'],
    working: ['rgba(34, 197, 94, 0.12)',   'rgb(22, 163, 74)'],
    blocked: ['rgba(239, 68, 68, 0.10)',   'rgb(220, 38, 38)'],
    paused:  ['rgba(245, 158, 11, 0.12)',  'rgb(180, 112, 0)'],
  };
  const [bg, fg] = palette[(state || '').toLowerCase()] ?? palette.idle;
  return {
    marginLeft: 8, fontSize: 9, padding: '1px 5px', borderRadius: 999,
    background: bg, color: fg, fontFamily: 'var(--font-mono, monospace)',
    textTransform: 'uppercase', letterSpacing: 0.4, verticalAlign: 'middle',
  };
}

const eyebrowStyle: React.CSSProperties = {
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 10, color: 'var(--text-tertiary)',
  textTransform: 'uppercase', letterSpacing: 0.12,
  display: 'flex', alignItems: 'center', gap: 6,
};
const humanPillStyle: React.CSSProperties = {
  marginLeft: 4, padding: '1px 6px', borderRadius: 999,
  background: 'rgba(220, 38, 38, 0.10)', color: 'rgb(180, 50, 50)',
  fontFamily: 'var(--font-mono, monospace)', textTransform: 'uppercase', letterSpacing: 0.4,
};
const humanInfoBoxStyle: React.CSSProperties = {
  fontSize: 12, lineHeight: 1.5,
  padding: '10px 12px', borderRadius: 'var(--radius-sm)',
  background: 'rgba(220, 38, 38, 0.06)', color: 'var(--text-primary)',
  border: '1px solid rgba(220, 38, 38, 0.20)',
};

const modalOverlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, zIndex: 100,
  background: 'rgba(0, 0, 0, 0.55)',
  display: 'flex', alignItems: 'center', justifyContent: 'center',
  padding: 16,
};
const modalCardStyle: React.CSSProperties = {
  width: 'min(440px, 100%)',
  background: 'var(--bg-elevated, #1c1c1c)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-md, 8px)',
  boxShadow: '0 10px 30px rgba(0,0,0,0.45)',
  padding: 18, color: 'var(--text-primary)',
};
const modalBtnSecondaryStyle: React.CSSProperties = {
  background: 'transparent', color: 'var(--text-primary)',
  border: '1px solid var(--border-strong)', borderRadius: 'var(--radius-sm)',
  padding: '6px 12px', fontSize: 12, cursor: 'pointer',
};
const modalBtnPrimaryStyle: React.CSSProperties = {
  background: 'var(--accent, #f59e0b)', color: 'var(--text-on-accent, #111)',
  border: 'none', borderRadius: 'var(--radius-sm)',
  padding: '6px 14px', fontSize: 12, cursor: 'pointer', fontWeight: 600,
};

const soulCardStyle: React.CSSProperties = {
  width: 'min(720px, 100%)',
  height: 'min(680px, 92vh)',
  background: 'var(--bg-elevated, #1c1c1c)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-md, 8px)',
  boxShadow: '0 10px 30px rgba(0,0,0,0.45)',
  padding: 18, color: 'var(--text-primary)',
  display: 'flex', flexDirection: 'column', minHeight: 0,
};
const humanCardStyle: React.CSSProperties = {
  width: 'min(560px, 100%)',
  background: 'var(--bg-elevated, #1c1c1c)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-md, 8px)',
  boxShadow: '0 10px 30px rgba(0,0,0,0.45)',
  padding: 18, color: 'var(--text-primary)',
  display: 'flex', flexDirection: 'column',
};
const soulHeadStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'flex-start', gap: 10,
  paddingBottom: 8, borderBottom: '1px solid var(--border)',
};
const soulCloseBtnStyle: React.CSSProperties = {
  background: 'transparent', border: 'none', color: 'var(--text-tertiary)',
  cursor: 'pointer', fontSize: 22, lineHeight: 1, padding: '0 4px',
};
const soulCapChipStyle: React.CSSProperties = {
  fontSize: 10, padding: '2px 6px', borderRadius: 999,
  background: 'var(--accent-bg, rgba(245, 158, 11, 0.10))',
  color: 'var(--accent, rgb(180, 112, 0))',
  border: '1px solid var(--border-accent, rgba(245, 158, 11, 0.32))',
  fontFamily: 'var(--font-mono, monospace)',
};
const soulTextareaStyle: React.CSSProperties = {
  flex: 1, width: '100%', minHeight: 0, resize: 'none',
  fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
  fontSize: 12.5, lineHeight: 1.55, padding: 10,
  background: 'var(--bg-base, #0c0c0c)', color: 'var(--text-primary)',
  border: '1px solid var(--border-strong)', borderRadius: 'var(--radius-sm)',
  outline: 'none',
};
const soulFootStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'space-between',
  paddingTop: 10, borderTop: '1px solid var(--border)',
};
const soulBtnStyle: React.CSSProperties = {
  background: 'transparent', border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)', color: 'var(--text-secondary)',
  cursor: 'pointer', fontSize: 10.5, padding: '3px 7px',
  fontFamily: 'var(--font-mono, monospace)', whiteSpace: 'nowrap',
};

const searchInputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', fontSize: 12,
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)', outline: 'none',
};
const selectStyle: React.CSSProperties = {
  fontSize: 12, padding: '7px 10px', borderRadius: 'var(--radius-sm)',
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
};
const hintStyle: React.CSSProperties = {
  fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 4, lineHeight: 1.4,
};
const warningBoxStyle: React.CSSProperties = {
  fontSize: 11.5, padding: '8px 10px',
  background: 'var(--warning-bg, rgba(245, 158, 11, 0.08))',
  color: 'var(--warning, rgb(180, 112, 0))',
  border: '1px solid var(--warning-border, rgba(245, 158, 11, 0.32))',
  borderRadius: 'var(--radius-sm)', lineHeight: 1.5,
};
