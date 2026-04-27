import { useEffect, useMemo, useState } from 'react';
import type { AgentDetail, Crew, CrewHistoryEntry, CrewSalary, ModelInfo, SoulPayload } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useHasRole } from '@/state/useCurrentUserStore';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import { AGENT_ALLOCATION, type Team } from '@/agentAllocation';

// Resolve the `Team` (Architect / Engineer / Review / QA) for an agent id.
// Falls back to deriving from role-prefix when the allocation manifest
// doesn't list the agent (e.g. legacy seeds that haven't been re-imported).
function teamOfAgent(id: string, role?: string | null): Team | null {
  const entry = AGENT_ALLOCATION.find((a) => a.id === id);
  if (entry) return entry.team;
  switch ((role ?? '').toLowerCase()) {
    case 'arch':
    case 'product-owner': return 'Architect';
    case 'eng':           return 'Engineer';
    case 'rev':           return 'Review';
    case 'qa':            return 'QA';
    default:              return null;
  }
}

// Map a crew row to its line-of-business team. The seed names crews
// "Engineering" / "Review" / "Architecture" / "QA"; this lookup tolerates
// case + adjective drift.
function teamOfCrew(name: string | undefined | null): Team | null {
  const n = (name ?? '').toLowerCase();
  if (n.startsWith('arch')) return 'Architect';
  if (n.startsWith('eng'))  return 'Engineer';
  if (n.startsWith('rev'))  return 'Review';
  if (n.startsWith('qa'))   return 'QA';
  return null;
}

// Phase 13b — full crew management view.
// Layout:
//   Left rail (260px) : list of crews, selectable + filter by parent.
//   Center            : tabbed detail (Roster / Pair / Capabilities / Runtime / Quota / History).
// The "Roster" tab embeds a Member Catalog (search across all ~100 agents
// with one-click add / remove). "Pair" is the (generator, evaluator) the
// crew uses by default for plan and coding tasks (Q13.4 / 13b).
//
// Mutations are gated on product-scoped Product Owner (Q13.2). The view
// remains visible to anyone signed-in (Phase 13.10) but the controls
// disable when the role check fails.

type TabKey = 'roster' | 'pair' | 'capabilities' | 'runtime' | 'quota' | 'history';

const TAB_ORDER: Array<{ key: TabKey; label: string }> = [
  { key: 'roster',       label: 'Roster' },
  { key: 'pair',         label: 'Pair' },
  { key: 'capabilities', label: 'Capabilities' },
  { key: 'runtime',      label: 'Runtime' },
  { key: 'quota',        label: 'Quota' },
  { key: 'history',      label: 'History' },
];

const ROLE_OPTIONS = ['coder', 'reviewer', 'architect', 'qa', 'lead', 'generator', 'evaluator', 'observer'];

export function CrewsView() {
  const isPO = useHasRole('product-owner');

  const [crews, setCrews] = useState<Crew[]>([]);
  const [agents, setAgents] = useState<AgentDetail[]>([]);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [salaries, setSalaries] = useState<Record<string, CrewSalary>>({});
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [tab, setTab] = useState<TabKey>('roster');
  const [error, setError] = useState<string | null>(null);
  const [autoAssignBusy, setAutoAssignBusy] = useState(false);
  const [autoAssignMsg, setAutoAssignMsg] = useState<string | null>(null);
  // Phase 13b — per-agent edit modal opened from the catalog.
  const [editAgentId, setEditAgentId] = useState<string | null>(null);

  async function runAutoAssign() {
    setAutoAssignBusy(true); setError(null); setAutoAssignMsg(null);
    try {
      const r = await forgeApi.autoAssignAgents();
      setAutoAssignMsg(r.added === 0
        ? `Already balanced — every agent is on a team (${r.inspected} inspected).`
        : `Added ${r.added} of ${r.inspected} agents to their teams.`);
      // Refetch crews so the rosters reflect the reconcile.
      const cs = await forgeApi.listCrews();
      setCrews(cs);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setAutoAssignBusy(false);
    }
  }

  const selected = useMemo(() => crews.find((c) => c.id === selectedId) ?? null, [crews, selectedId]);

  // Initial load — crews, agents, models, salaries (parallel where possible).
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [crewList, agentList, modelList] = await Promise.all([
          forgeApi.listCrews(),
          forgeApi.listAgents().catch(() => [] as AgentDetail[]),
          forgeApi.listModels().catch(() => [] as ModelInfo[]),
        ]);
        if (cancelled) return;
        setCrews(crewList);
        setAgents(agentList);
        setModels(modelList);
        if (crewList.length > 0 && !selectedId) setSelectedId(crewList[0].id);
        // Salaries best-effort; some crews may not have entries.
        const salaryEntries = await Promise.all(
          crewList.map((c) => forgeApi.getCrewSalary(c.id).then((s) => [c.id, s] as const).catch(() => null)),
        );
        if (cancelled) return;
        const salaryMap: Record<string, CrewSalary> = {};
        for (const e of salaryEntries) if (e) salaryMap[e[0]] = e[1];
        setSalaries(salaryMap);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one-shot init
  }, []);

  function applyCrewUpdate(next: Crew) {
    setCrews((cs) => cs.map((c) => c.id === next.id ? next : c));
  }

  return (
    <section className="view active" id="view-crews">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">10</span>&nbsp;Crews</span>
            <span className="sep">/</span>
            <span>{crews.length} crew{crews.length === 1 ? '' : 's'} · {agents.length} agent{agents.length === 1 ? '' : 's'}</span>
            {!isPO && <><span className="sep">/</span><span style={{ color: 'var(--text-tertiary)' }}>read-only — PO required to mutate</span></>}
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Crews
          </h1>
        </div>
        <div className="page-head-right" style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {autoAssignMsg && (
            <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>{autoAssignMsg}</span>
          )}
          <button
            className="btn btn-secondary"
            onClick={() => void runAutoAssign()}
            disabled={autoAssignBusy}
            title="Add every agent without a crew to their team's crew (alternating generator / evaluator)."
          >
            {autoAssignBusy ? 'Reconciling…' : 'Auto-assign unassigned'}
          </button>
        </div>
      </div>

      {error && (
        <div style={{ margin: '0 24px 14px', padding: 10, borderRadius: 'var(--radius-sm)', background: 'var(--danger-bg)', color: 'var(--danger)', fontSize: 11.5 }}>
          {error}
        </div>
      )}

      <div style={layoutStyle}>
        {/* ─── Left rail — crew list ─────────────────────────────────────── */}
        <aside style={crewListStyle}>
          <div style={crewListHeadStyle}>
            <span>All crews</span>
            <span style={{ color: 'var(--text-tertiary)', fontWeight: 400 }}>{crews.length}</span>
          </div>
          {crews.length === 0 && (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)' }}>No crews seeded yet.</div>
          )}
          {crews.map((c) => {
            const sel = c.id === selectedId;
            return (
              <button
                key={c.id}
                onClick={() => setSelectedId(c.id)}
                style={crewListItemStyle(sel, c.depth ?? 0)}
              >
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={crewListNameStyle(sel)}>{c.name}</div>
                  <div style={crewListMetaStyle}>
                    {(c.roster?.length ?? 0)} member{(c.roster?.length ?? 0) === 1 ? '' : 's'}
                    {' · '}
                    <span style={runtimePillStyle(c.backend === 'claude' ? 'claude' : 'copilot')}>{c.backend ?? 'copilot'}</span>
                    {' '}
                    <span style={runtimePillStyle((c.invokeMode ?? 'sdk') === 'cli' ? 'cli' : 'sdk')}>{c.invokeMode ?? 'sdk'}</span>
                  </div>
                </div>
                {salaries[c.id] && (
                  <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10, color: 'var(--text-tertiary)', textAlign: 'right' }}>
                    ${salaries[c.id].today.toFixed(2)}
                  </div>
                )}
              </button>
            );
          })}
        </aside>

        {/* ─── Center — selected crew detail ────────────────────────────── */}
        <main style={detailStyle}>
          {selected ? (
            <>
              <header style={detailHeadStyle}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={detailEyebrowStyle}>
                    {selected.productId ? <span>product · <strong>{selected.productId}</strong></span> : <span>(no product)</span>}
                    {selected.parentCrewId && <span> · child of {selected.parentCrewId}</span>}
                    <span> · depth {selected.depth ?? 0}</span>
                  </div>
                  <div style={detailNameStyle}>{selected.name}</div>
                  <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>{selected.summary || '—'}</div>
                </div>
                <RuntimeBadgeGroup crew={selected} />
              </header>

              <SalaryStrip salary={salaries[selected.id]} budget={selected.dailyBudgetUsd ?? null} backend={selected.backend} />

              <nav style={tabsStyle}>
                {TAB_ORDER.map((t) => (
                  <button key={t.key} style={tabBtnStyle(tab === t.key)} onClick={() => setTab(t.key)}>{t.label}</button>
                ))}
              </nav>

              <div style={tabBodyStyle}>
                {tab === 'roster' && (
                  <RosterTab
                    crew={selected}
                    allAgents={agents}
                    canEdit={isPO}
                    onChange={applyCrewUpdate}
                    onError={setError}
                    onEditAgent={setEditAgentId}
                  />
                )}
                {tab === 'pair' && (
                  <PairTab
                    crew={selected}
                    allAgents={agents}
                    models={models}
                    canEdit={isPO}
                    onChange={applyCrewUpdate}
                    onError={setError}
                  />
                )}
                {tab === 'capabilities' && (
                  <CapabilitiesTab crew={selected} canEdit={isPO} onChange={applyCrewUpdate} onError={setError} />
                )}
                {tab === 'runtime' && (
                  <RuntimeTab crew={selected} models={models} canEdit={isPO} onChange={applyCrewUpdate} onError={setError} />
                )}
                {tab === 'quota' && (
                  <QuotaTab crew={selected} canEdit={isPO} onChange={applyCrewUpdate} onError={setError} />
                )}
                {tab === 'history' && <HistoryTab crewId={selected.id} />}
              </div>
            </>
          ) : (
            <div style={{ padding: 30, textAlign: 'center', color: 'var(--text-tertiary)' }}>
              Pick a crew on the left.
            </div>
          )}
        </main>
      </div>

      {editAgentId && (
        <AgentEditModal
          agentId={editAgentId}
          agents={agents}
          canEdit={isPO}
          onClose={() => setEditAgentId(null)}
          onSaved={(next) => {
            setAgents((cur) => cur.map((a) => a.id === next.id ? next : a));
          }}
          onError={setError}
        />
      )}
    </section>
  );
}

// ═══════════════════════════ ROSTER TAB ═══════════════════════════════════

function RosterTab({ crew, allAgents, canEdit, onChange, onError, onEditAgent }: {
  crew: Crew;
  allAgents: AgentDetail[];
  canEdit: boolean;
  onChange: (next: Crew) => void;
  onError: (msg: string | null) => void;
  onEditAgent: (agentId: string) => void;
}) {
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  // Phase 11.3 — soul.md editor. State holds the agent id currently being
  // edited; the modal owns the contents + save lifecycle.
  const [soulFor, setSoulFor] = useState<string | null>(null);
  // Cross-team move confirmation. Held in state so the modal can render
  // outside the row that triggered it.
  const [pendingMove, setPendingMove] = useState<{
    agentId: string;
    name: string;
    fromTeam: Team;
    toTeam: Team;
    fromCrewName?: string | null;
  } | null>(null);

  const rosterIds = useMemo(() => new Set((crew.roster ?? []).map((m) => m.agentId)), [crew.roster]);
  const targetTeam = useMemo(() => teamOfCrew(crew.name), [crew.name]);
  const filteredAgents = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return allAgents;
    return allAgents.filter((a) =>
      a.id.toLowerCase().includes(q) ||
      a.name.toLowerCase().includes(q) ||
      a.role.toLowerCase().includes(q) ||
      a.capabilities.some((c) => c.toLowerCase().includes(q)),
    );
  }, [allAgents, search]);

  async function performAdd(agentId: string) {
    setBusy(agentId); onError(null);
    try { onChange(await forgeApi.addCrewMember(crew.id, agentId)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(null); }
  }

  async function add(agentId: string) {
    if (!canEdit) return;
    const agent = allAgents.find((x) => x.id === agentId);
    const fromTeam = teamOfAgent(agentId, agent?.role);
    // Same team (or unknown teams) → drop straight in. Different team →
    // raise a confirm before mutating, since this also wipes the agent's
    // previous crew membership server-side (Phase 13b move semantics).
    if (fromTeam && targetTeam && fromTeam !== targetTeam) {
      const fromCrewName = agent?.crewId
        ? null  // resolved by the modal via the crew list lookup; we only
        : null; // know it's `fromTeam` for messaging.
      setPendingMove({
        agentId,
        name: agent?.name ?? agentId,
        fromTeam,
        toTeam: targetTeam,
        fromCrewName,
      });
      return;
    }
    await performAdd(agentId);
  }
  async function remove(agentId: string) {
    if (!canEdit) return;
    setBusy(agentId); onError(null);
    try { onChange(await forgeApi.removeCrewMember(crew.id, agentId)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(null); }
  }
  async function setRole(agentId: string, role: string) {
    if (!canEdit) return;
    setBusy(agentId); onError(null);
    try { onChange(await forgeApi.setCrewMemberRole(crew.id, agentId, role)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(null); }
  }

  return (
    <div style={twoColStyle}>
      {/* Current roster */}
      <div>
        <SectionHead label="Current roster" right={`${(crew.roster?.length ?? 0)} member${(crew.roster?.length ?? 0) === 1 ? '' : 's'}`} />
        {crew.lead && (
          <div style={memberRowStyle(true)}>
            <AgentChip id={crew.lead} agents={allAgents} />
            <span style={leadCrownStyle}>★ lead</span>
            <button
              onClick={() => setSoulFor(crew.lead!)}
              style={soulBtnStyle}
              title="Edit soul.md (system prompt)"
            >✎ soul</button>
          </div>
        )}
        {(crew.roster ?? []).length === 0 && !crew.lead && (
          <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No members — add agents from the catalog →</div>
        )}
        {(crew.roster ?? []).map((m) => (
          <div key={m.agentId} style={memberRowStyle(m.agentId === crew.lead)}>
            <AgentChip id={m.agentId} agents={allAgents} />
            <select
              value={m.role}
              disabled={!canEdit || busy === m.agentId}
              onChange={(e) => void setRole(m.agentId, e.target.value)}
              style={roleSelectStyle}
              title="Per-crew role"
            >
              {ROLE_OPTIONS.map((r) => <option key={r} value={r}>{r}</option>)}
              {!ROLE_OPTIONS.includes(m.role) && <option value={m.role}>{m.role}</option>}
            </select>
            <button
              onClick={() => setSoulFor(m.agentId)}
              style={soulBtnStyle}
              title="Edit soul.md (system prompt)"
            >✎ soul</button>
            <button
              disabled={!canEdit || busy === m.agentId}
              onClick={() => void remove(m.agentId)}
              style={dangerBtnStyle}
              title="Remove from crew"
            >×</button>
          </div>
        ))}
      </div>

      {/* Member catalog */}
      <div>
        <SectionHead label="Member catalog" right={`${filteredAgents.length} of ${allAgents.length}`} />
        <input
          type="text"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="search by name, id, role, capability…"
          style={searchInputStyle}
        />
        <div style={catalogListStyle}>
          {filteredAgents.length === 0 && (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)' }}>No agents match.</div>
          )}
          {filteredAgents.map((a) => {
            const inCrew = rosterIds.has(a.id) || a.id === crew.lead;
            return (
              <div key={a.id} style={catalogRowStyle(inCrew)}>
                <AgentFace id={a.id} name={a.name} role={a.role} size={26} />
                <button
                  onClick={() => onEditAgent(a.id)}
                  style={{ flex: 1, minWidth: 0, background: 'transparent', border: 'none', color: 'inherit', textAlign: 'left', cursor: 'pointer', padding: 0 }}
                  title="Click to edit agent (name, role, soul.md path)"
                >
                  <div style={catalogNameStyle}>
                    {a.name}
                    <span style={agentRolePillStyle}>{a.role}</span>
                    <span style={agentStatePillStyle(a.state)}>{a.state}</span>
                  </div>
                  {a.capabilities.length > 0 && (
                    <div style={catalogCapsStyle} title={a.capabilities.join(', ')}>
                      {a.capabilities.slice(0, 3).join(' · ')}
                      {a.capabilities.length > 3 && <span> +{a.capabilities.length - 3}</span>}
                    </div>
                  )}
                </button>
                <button
                  onClick={() => void (inCrew ? remove(a.id) : add(a.id))}
                  disabled={!canEdit || busy === a.id || a.id === crew.lead}
                  style={catalogBtnStyle(inCrew)}
                  title={a.id === crew.lead ? 'This agent is the crew lead' : inCrew ? 'Remove from crew' : 'Add to crew'}
                >
                  {a.id === crew.lead ? 'lead' : inCrew ? '−' : '+'}
                </button>
              </div>
            );
          })}
        </div>
      </div>

      {pendingMove && (
        <CrossTeamMoveModal
          move={pendingMove}
          onCancel={() => setPendingMove(null)}
          onConfirm={async () => {
            const id = pendingMove.agentId;
            setPendingMove(null);
            await performAdd(id);
          }}
        />
      )}
      {soulFor && (
        <AgentSoulEditor
          agentId={soulFor}
          agents={allAgents}
          canEdit={canEdit}
          onClose={() => setSoulFor(null)}
          onError={onError}
        />
      )}
    </div>
  );
}

// ═══════════════════════════ CROSS-TEAM MOVE CONFIRM ══════════════════════

function CrossTeamMoveModal({ move, onCancel, onConfirm }: {
  move: { agentId: string; name: string; fromTeam: Team; toTeam: Team; fromCrewName?: string | null };
  onCancel: () => void;
  onConfirm: () => void | Promise<void>;
}) {
  // Closes on Esc; click-outside cancels too. Same pattern as SyncDialog so
  // the dismissal feel is consistent.
  useEffect(() => {
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') onCancel(); }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onCancel]);

  return (
    <div
      style={modalOverlayStyle}
      onClick={(e) => { if (e.target === e.currentTarget) onCancel(); }}
      role="dialog"
      aria-modal="true"
      aria-labelledby="cross-team-move-title"
    >
      <div style={modalCardStyle}>
        <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12 }}>
          Confirm cross-team move
        </div>
        <h3 id="cross-team-move-title" style={{ margin: '6px 0 8px', fontSize: 18, fontWeight: 600 }}>
          Move <span style={{ color: 'var(--accent)' }}>{move.name}</span> to {move.toTeam}?
        </h3>
        <p style={{ margin: 0, fontSize: 12.5, lineHeight: 1.5, color: 'var(--text-secondary)' }}>
          {move.name} is currently on the <strong>{move.fromTeam}</strong> team.
          Adding to <strong>{move.toTeam}</strong> will <strong>remove</strong> the
          existing membership — an agent can only belong to one crew at a time.
        </p>
        <p style={{ margin: '8px 0 0', fontSize: 11, color: 'var(--text-tertiary)' }}>
          This is logged as a <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>member-move-out</code> /{' '}
          <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>member-move-in</code> pair in the crew history.
        </p>
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
          <button onClick={onCancel} style={modalBtnSecondaryStyle}>Cancel</button>
          <button onClick={() => void onConfirm()} style={modalBtnPrimaryStyle}>
            Move to {move.toTeam}
          </button>
        </div>
      </div>
    </div>
  );
}

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
  padding: 18,
  color: 'var(--text-primary)',
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

// ═══════════════════════════ AGENT SOUL EDITOR ════════════════════════════

/**
 * Phase 11.3 — soul.md editor modal. Loads the file via
 * <c>GET /api/agents/{id}/soul</c>, lets the operator edit, saves via PUT.
 * The system prompt for every invocation of this agent is the soul.md
 * contents, so changes take effect on the next <c>StartAsync</c> call.
 *
 * Capabilities are parsed from the YAML front-matter <c>capabilities:</c>
 * list and shown read-only at the top — they're authored inline in the
 * markdown source rather than via a separate field, so the prompt cache
 * prefix stays content-addressable (Q13.3).
 */
function AgentSoulEditor({ agentId, agents, canEdit, onClose, onError }: {
  agentId: string;
  agents: AgentDetail[];
  canEdit: boolean;
  onClose: () => void;
  onError: (msg: string | null) => void;
}) {
  const agent = agents.find((a) => a.id === agentId);
  const [soul, setSoul] = useState<SoulPayload | null>(null);
  const [contents, setContents] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);

  // ESC closes; click-outside closes; Cmd/Ctrl-S saves.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
      if ((e.key === 's' || e.key === 'S') && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        if (canEdit && !saving && soul && contents !== soul.contents) void save();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- save closure stable enough for this short-lived modal
  }, [onClose, canEdit, saving, soul, contents]);

  // Initial load.
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    forgeApi.getAgentSoul(agentId)
      .then((p) => {
        if (cancelled) return;
        setSoul(p);
        setContents(p.contents);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        onError(err instanceof Error ? err.message : String(err));
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, [agentId, onError]);

  async function save() {
    if (!canEdit || !soul) return;
    setSaving(true); onError(null);
    try {
      const next = await forgeApi.putAgentSoul(agentId, contents);
      setSoul(next);
      setContents(next.contents);
      setSavedAt(new Date());
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  const dirty = soul !== null && contents !== soul.contents;

  return (
    <div
      style={modalOverlayStyle}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      role="dialog"
      aria-modal="true"
      aria-labelledby="soul-title"
    >
      <div style={soulCardStyle}>
        <header style={soulHeadStyle}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12 }}>
              soul.md · agent system prompt
            </div>
            <h3 id="soul-title" style={{ margin: '4px 0 4px', fontSize: 17, fontWeight: 600 }}>
              {agentId}
              {agent && <span style={{ color: 'var(--text-secondary)', fontWeight: 400 }}> · {agent.name}</span>}
              {!soul?.exists && !loading && (
                <span style={{ marginLeft: 8, fontSize: 10, padding: '2px 6px', borderRadius: 999, background: 'var(--warning-bg, rgba(245, 158, 11, 0.10))', color: 'var(--warning, rgb(180, 112, 0))', fontFamily: 'var(--font-mono, monospace)', textTransform: 'uppercase', letterSpacing: 0.4 }}>
                  new file
                </span>
              )}
            </h3>
            {soul?.path && (
              <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10.5, color: 'var(--text-tertiary)', wordBreak: 'break-all' }}>
                {soul.path}
              </div>
            )}
          </div>
          <button onClick={onClose} style={soulCloseBtnStyle} aria-label="Close">×</button>
        </header>

        {soul && soul.capabilities.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
            <span style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12, alignSelf: 'center', marginRight: 4 }}>
              capabilities ({soul.capabilities.length})
            </span>
            {soul.capabilities.map((c) => (
              <span key={c} style={soulCapChipStyle}>{c}</span>
            ))}
          </div>
        )}

        <div style={{ padding: '8px 0 4px', flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          {loading ? (
            <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-tertiary)', fontSize: 12 }}>Loading…</div>
          ) : (
            <textarea
              value={contents}
              onChange={(e) => setContents(e.target.value)}
              disabled={!canEdit || saving}
              spellCheck={false}
              placeholder={soul?.exists ? '' : '---\ncapabilities:\n  - sha256:…\n---\n\n# Agent system prompt\n\n…'}
              style={soulTextareaStyle}
            />
          )}
        </div>

        <footer style={soulFootStyle}>
          <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
            {!canEdit && <em>read-only — PO required to save</em>}
            {canEdit && savedAt && !dirty && <span>saved · {savedAt.toLocaleTimeString()}</span>}
            {canEdit && dirty && <em style={{ color: 'var(--warning, rgb(180, 112, 0))' }}>unsaved changes</em>}
            {canEdit && !savedAt && !dirty && <span>{contents.length.toLocaleString()} chars</span>}
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
                {saving ? 'Saving…' : 'Save soul.md'}
              </button>
            )}
          </div>
        </footer>
      </div>
    </div>
  );
}

const soulCardStyle: React.CSSProperties = {
  width: 'min(880px, 100%)',
  height: 'min(720px, 90vh)',
  background: 'var(--bg-elevated, #1c1c1c)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-md, 8px)',
  boxShadow: '0 10px 30px rgba(0,0,0,0.45)',
  padding: 18,
  color: 'var(--text-primary)',
  display: 'flex', flexDirection: 'column',
  minHeight: 0,
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
  flex: 1,
  width: '100%', minHeight: 0,
  resize: 'none',
  fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
  fontSize: 12.5, lineHeight: 1.55,
  padding: 10,
  background: 'var(--bg-base, #0c0c0c)',
  color: 'var(--text-primary)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
};
const soulFootStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'space-between',
  paddingTop: 10, borderTop: '1px solid var(--border)',
};

const soulBtnStyle: React.CSSProperties = {
  background: 'transparent',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--text-secondary)',
  cursor: 'pointer',
  fontSize: 10.5,
  padding: '3px 7px',
  fontFamily: 'var(--font-mono, monospace)',
  whiteSpace: 'nowrap',
};

// ═══════════════════════════ AGENT EDIT MODAL ═════════════════════════════

/**
 * Phase 13b — edit a single agent (name / role / soul.md path override).
 * Reuses the same modal chrome as the soul editor; the soul textarea lives
 * in <see cref="AgentSoulEditor"/> as a separate flow because the file is
 * large and the save lifecycle is independent.
 */
function AgentEditModal({ agentId, agents, canEdit, onClose, onSaved, onError }: {
  agentId: string;
  agents: AgentDetail[];
  canEdit: boolean;
  onClose: () => void;
  onSaved: (next: AgentDetail) => void;
  onError: (msg: string | null) => void;
}) {
  const original = agents.find((a) => a.id === agentId);
  const [name, setName] = useState(original?.name ?? '');
  const [role, setRole] = useState(original?.role ?? '');
  const [soulPath, setSoulPath] = useState(original?.soulPath ?? '');
  const [saving, setSaving] = useState(false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') onClose(); }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  if (!original) {
    return (
      <div style={modalOverlayStyle} onClick={onClose} role="dialog" aria-modal="true">
        <div style={modalCardStyle}>
          <p style={{ margin: 0, fontSize: 12.5 }}>Agent {agentId} not found.</p>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 12 }}>
            <button onClick={onClose} style={modalBtnSecondaryStyle}>Close</button>
          </div>
        </div>
      </div>
    );
  }

  const dirty = name !== original.name || role !== original.role || (soulPath ?? '') !== (original.soulPath ?? '');

  async function save() {
    if (!canEdit || !dirty) return;
    setSaving(true); onError(null);
    try {
      const next = await forgeApi.patchAgent(agentId, { name, role, soulPath });
      onSaved(next);
      setSavedAt(new Date());
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div
      style={modalOverlayStyle}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      role="dialog"
      aria-modal="true"
      aria-labelledby="agent-edit-title"
    >
      <div style={{ ...modalCardStyle, width: 'min(520px, 100%)' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
          <AgentFace id={original.id} name={original.name} role={original.role} size={36} />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12 }}>
              agent · {agentId}
            </div>
            <h3 id="agent-edit-title" style={{ margin: 0, fontSize: 17, fontWeight: 600 }}>
              {name || agentId}
              <span style={agentStatePillStyle(original.state)}>{original.state}</span>
            </h3>
          </div>
        </div>

        <div style={{ display: 'grid', gap: 10 }}>
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
            <div style={hintStyle}>Drives auto-assign team mapping (arch → Architecture, eng → Engineering, etc.).</div>
          </div>
          <div>
            <SectionHead label="soul.md path override" />
            <input
              type="text" value={soulPath} onChange={(e) => setSoulPath(e.target.value)}
              disabled={!canEdit || saving}
              placeholder="empty = default %LOCALAPPDATA%\Forge\agents\{id}\soul.md"
              style={{ ...searchInputStyle, fontFamily: 'var(--font-mono, monospace)' }}
            />
            <div style={hintStyle}>Set to override where soul.md lives on disk; empty falls back to the default location.</div>
          </div>
          <div style={{ display: 'flex', gap: 6, fontSize: 11, color: 'var(--text-tertiary)' }}>
            <span>capabilities · {original.capabilities.length}</span>
            {original.crewId && <><span>·</span><span>crew · {original.crewId}</span></>}
          </div>
        </div>

        <footer style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, marginTop: 16, paddingTop: 12, borderTop: '1px solid var(--border)' }}>
          <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
            {!canEdit && <em>read-only — PO required</em>}
            {canEdit && savedAt && !dirty && <span>saved · {savedAt.toLocaleTimeString()}</span>}
            {canEdit && dirty && <em style={{ color: 'var(--warning, rgb(180, 112, 0))' }}>unsaved changes</em>}
          </span>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={onClose} style={modalBtnSecondaryStyle}>Close</button>
            {canEdit && (
              <button
                onClick={() => void save()}
                disabled={saving || !dirty}
                style={{ ...modalBtnPrimaryStyle, opacity: saving || !dirty ? 0.55 : 1, cursor: saving || !dirty ? 'not-allowed' : 'pointer' }}
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

// ═══════════════════════════ PAIR TAB ═════════════════════════════════════

function PairTab({ crew, allAgents, models, canEdit, onChange, onError }: {
  crew: Crew;
  allAgents: AgentDetail[];
  models: ModelInfo[];
  canEdit: boolean;
  onChange: (next: Crew) => void;
  onError: (msg: string | null) => void;
}) {
  const candidates = useMemo(() => {
    const ids = new Set<string>();
    if (crew.lead) ids.add(crew.lead);
    for (const m of crew.roster ?? []) ids.add(m.agentId);
    return allAgents.filter((a) => ids.has(a.id));
  }, [allAgents, crew.lead, crew.roster]);

  // Filter the model registry to entries that match the crew's backend
  // vendor — Copilot crews see Copilot variants (dotted ids like
  // claude-opus-4.7), Claude crews see Anthropic ids (claude-opus-4-7).
  // The 'forge' vendor (local-stub) is always offered for tests.
  const backendVendors = useMemo(() => {
    const b = (crew.backend ?? 'copilot').toLowerCase();
    return b === 'claude' ? ['anthropic', 'forge'] : ['copilot', 'forge'];
  }, [crew.backend]);
  const eligibleModels = useMemo(
    () => models.filter((m) => backendVendors.includes(m.vendor) && m.status === 'live'),
    [models, backendVendors],
  );

  async function patch(p: Partial<Crew>) {
    onError(null);
    try { onChange(await forgeApi.patchCrew(crew.id, p)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
  }

  const generatorModel = crew.model;
  const evaluatorModel = crew.evaluatorModel ?? '';
  const sameModel = !!evaluatorModel && evaluatorModel === generatorModel;

  return (
    <div style={{ display: 'grid', gap: 14, maxWidth: 720 }}>
      <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
        Every plan / coding task this crew runs uses these two agents by default.
        The <strong>generator</strong> writes the spec or the diff; the <strong>evaluator</strong>
        reviews before the WIT tree commits or the PR comes off draft. Any specific task
        can override the pair via <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>POST /api/crews/{'{id}'}/assign</code>.
      </div>

      <SectionHead label="Agents" />
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
        <PairPicker
          label="Generator"
          hint="Authors the spec / writes the code. Required — falls back to the lead when unset."
          value={crew.defaultGeneratorAgentId ?? ''}
          candidates={candidates}
          canEdit={canEdit}
          excludes={crew.defaultEvaluatorAgentId ?? null}
          onChange={(v) => void patch({ defaultGeneratorAgentId: v })}
          crew={crew}
        />
        <PairPicker
          label="Evaluator"
          hint="Reviews the diff / plan. Empty = single-agent mode (no pair-review pass)."
          value={crew.defaultEvaluatorAgentId ?? ''}
          candidates={candidates}
          canEdit={canEdit}
          excludes={crew.defaultGeneratorAgentId ?? null}
          onChange={(v) => void patch({ defaultEvaluatorAgentId: v })}
          crew={crew}
        />
      </div>

      <SectionHead label="Models" right={`${backendVendors.join(' / ')} — diff required`} />
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
        <ModelPicker
          label="Generator model"
          value={generatorModel}
          excludes={evaluatorModel || null}
          options={eligibleModels}
          canEdit={canEdit}
          onChange={(v) => void patch({ model: v })}
          hint={`Wire id sent to ${crew.backend ?? 'copilot'} runner.`}
        />
        <ModelPicker
          label="Evaluator model"
          value={evaluatorModel}
          excludes={generatorModel || null}
          options={eligibleModels}
          canEdit={canEdit}
          allowEmpty
          onChange={(v) => void patch({ evaluatorModel: v })}
          hint="Must differ from the generator. Empty = inherit generator (warns)."
        />
      </div>
      {sameModel && (
        <div style={warningBoxStyle}>
          <strong>Pair models match.</strong> The diff-model rule (Q13b) says generator and evaluator must
          run on different models so the review pass catches things the generator missed. The server will
          refuse to PATCH evaluatorModel to the same value as model.
        </div>
      )}
      {evaluatorModel === '' && (
        <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
          No evaluator model pinned. Auto-assign on next start picks one from the registry that
          differs from the generator (e.g. opus → sonnet for cost diversity).
        </div>
      )}

      <div style={pairSummaryStyle}>
        <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 11, lineHeight: 1.6 }}>
          <div>
            <strong>{(crew.defaultGeneratorAgentId ?? crew.lead) ?? '(no generator)'}</strong>
            {' '}on{' '}
            <em>{generatorModel}</em>
          </div>
          <div style={{ marginLeft: 14, color: 'var(--text-tertiary)' }}>↓</div>
          <div>
            <strong>{crew.defaultEvaluatorAgentId ?? '(none — solo)'}</strong>
            {evaluatorModel
              ? <> on <em>{evaluatorModel}</em></>
              : <> (no evaluator)</>}
          </div>
        </div>
      </div>
    </div>
  );
}

function ModelPicker({ label, value, excludes, options, canEdit, allowEmpty, onChange, hint }: {
  label: string;
  value: string;
  excludes: string | null;
  options: ModelInfo[];
  canEdit: boolean;
  allowEmpty?: boolean;
  onChange: (next: string) => void;
  hint: string;
}) {
  return (
    <div>
      <SectionHead label={label} />
      <select
        value={value}
        disabled={!canEdit}
        onChange={(e) => onChange(e.target.value)}
        style={{ ...selectStyle, width: '100%' }}
      >
        {allowEmpty && <option value="">— inherit generator —</option>}
        {!options.some((m) => m.id === value) && value !== '' && (
          <option value={value}>{value} (not in registry)</option>
        )}
        {options.map((m) => (
          <option key={m.id} value={m.id} disabled={m.id === excludes}>
            {m.displayName} · {m.id} · ${m.inputPricePerMTok.toFixed(2)}/in ${m.outputPricePerMTok.toFixed(2)}/out
            {m.id === excludes ? ' — already in pair' : ''}
          </option>
        ))}
      </select>
      <div style={hintStyle}>{hint}</div>
    </div>
  );
}

function PairPicker({ label, hint, value, candidates, canEdit, excludes, onChange, crew }: {
  label: string;
  hint: string;
  value: string;
  candidates: AgentDetail[];
  canEdit: boolean;
  excludes: string | null;
  onChange: (next: string) => void;
  crew: Crew;
}) {
  return (
    <div>
      <SectionHead label={label} />
      <select
        value={value}
        disabled={!canEdit}
        onChange={(e) => onChange(e.target.value)}
        style={{ ...selectStyle, width: '100%' }}
      >
        <option value="">— {label === 'Generator' ? `inherit lead (${crew.lead ?? 'none'})` : 'no evaluator (solo runs)'} —</option>
        {candidates.map((a) => (
          <option key={a.id} value={a.id} disabled={a.id === excludes}>
            {a.id} · {a.name} ({a.role})
            {a.id === excludes ? ' — already in pair' : ''}
          </option>
        ))}
      </select>
      <div style={hintStyle}>{hint}</div>
    </div>
  );
}

// ══════════════════════════ CAPABILITIES TAB ══════════════════════════════

function CapabilitiesTab({ crew, canEdit, onChange, onError }: {
  crew: Crew; canEdit: boolean; onChange: (next: Crew) => void; onError: (m: string | null) => void;
}) {
  const [pending, setPending] = useState('');
  const [busy, setBusy] = useState(false);
  const caps = crew.capabilities ?? [];

  async function add() {
    const id = pending.trim(); if (!id || !canEdit) return;
    setBusy(true); onError(null);
    try { onChange(await forgeApi.addCrewCapability(crew.id, id)); setPending(''); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }
  async function remove(cid: string) {
    if (!canEdit) return;
    setBusy(true); onError(null);
    try { onChange(await forgeApi.removeCrewCapability(crew.id, cid)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  return (
    <div style={{ display: 'grid', gap: 12, maxWidth: 640 }}>
      <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
        Capabilities are content-addressed pointers (sha256:…) into the
        <code style={{ fontFamily: 'var(--font-mono, monospace)', margin: '0 4px' }}>domain_experts</code>
        index. They're appended to this crew's soul.md as YAML front-matter so
        every invocation runs against the same prompt-cache prefix.
      </div>
      <div>
        <SectionHead label="Active capabilities" right={`${caps.length}`} />
        {caps.length === 0 && (
          <div style={{ padding: 8, fontSize: 11, color: 'var(--text-tertiary)' }}>None — add a content-id below.</div>
        )}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {caps.map((c) => (
            <span key={c} style={capChipStyle}>
              <span style={{ fontFamily: 'var(--font-mono, monospace)' }}>{shortId(c)}</span>
              <button onClick={() => void remove(c)} disabled={!canEdit || busy} style={chipRemoveStyle}>×</button>
            </span>
          ))}
        </div>
      </div>
      {canEdit && (
        <div style={{ display: 'flex', gap: 6 }}>
          <input
            type="text" value={pending}
            onChange={(e) => setPending(e.target.value)}
            placeholder="sha256:abc123…"
            style={{ ...searchInputStyle, fontFamily: 'var(--font-mono, monospace)' }}
          />
          <button onClick={() => void add()} disabled={busy || !pending.trim()} style={primaryBtnStyle}>
            {busy ? '…' : 'Add'}
          </button>
        </div>
      )}
    </div>
  );
}

// ═══════════════════════════ RUNTIME TAB ══════════════════════════════════

function RuntimeTab({ crew, models, canEdit, onChange, onError }: {
  crew: Crew; models: ModelInfo[]; canEdit: boolean; onChange: (next: Crew) => void; onError: (m: string | null) => void;
}) {
  async function patch(p: Partial<Crew>) {
    onError(null);
    try { onChange(await forgeApi.patchCrew(crew.id, p)); }
    catch (err) { onError(err instanceof Error ? err.message : String(err)); }
  }

  return (
    <div style={{ display: 'grid', gap: 14, maxWidth: 640 }}>
      <div>
        <SectionHead label="Model" />
        <select
          value={crew.model}
          disabled={!canEdit}
          onChange={(e) => void patch({ model: e.target.value })}
          style={{ ...selectStyle, width: '100%' }}
        >
          {!models.some((m) => m.id === crew.model) && <option value={crew.model}>{crew.model} (not in registry)</option>}
          {models.map((m) => (
            <option key={m.id} value={m.id}>
              {m.displayName} · {m.id} · ${m.inputPricePerMTok.toFixed(2)}/in ${m.outputPricePerMTok.toFixed(2)}/out
              {m.status !== 'live' ? ` (${m.status})` : ''}
            </option>
          ))}
        </select>
        <div style={hintStyle}>
          Switching models writes a <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>crew_history(kind=model)</code> row and
          forces a new prompt cache. Pricing carries over from the registry.
        </div>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
        <div>
          <SectionHead label="Backend" />
          <select
            value={crew.backend ?? 'copilot'}
            disabled={!canEdit}
            onChange={(e) => void patch({ backend: e.target.value })}
            style={{ ...selectStyle, width: '100%' }}
          >
            <option value="copilot">Copilot · GitHub.Copilot.SDK</option>
            <option value="claude">Claude · Anthropic /v1/messages</option>
          </select>
        </div>
        <div>
          <SectionHead label="Invoke mode" />
          <select
            value={crew.invokeMode ?? 'sdk'}
            disabled={!canEdit}
            onChange={(e) => void patch({ invokeMode: e.target.value })}
            style={{ ...selectStyle, width: '100%' }}
          >
            <option value="sdk">SDK · streaming events + tools</option>
            <option value="cli">CLI · stdin/stdout (text-only, no tools)</option>
          </select>
        </div>
      </div>
      {(crew.invokeMode ?? 'sdk') === 'cli' && (
        <div style={warningBoxStyle}>
          <strong>tools: text-only.</strong> CLI mode cannot emit tool calls.
          Plan and coding pipelines refuse to spawn against this crew (HTTP 400 · <code>crew.tools_unavailable_in_cli</code>).
          Switch to SDK to enable them.
        </div>
      )}
      {crew.backend === 'claude' && (crew.invokeMode ?? 'sdk') === 'sdk' && (
        <div style={warningBoxStyle}>
          <strong>tools: text-only (until Phase 13.4a).</strong> The Claude SDK path doesn't yet declare tools to <code>/v1/messages</code>,
          so plan / coding pipelines emit text but can't call <code>create_work_item</code> / <code>git_commit</code>.
          Use Copilot · SDK for the full surface for now.
        </div>
      )}
    </div>
  );
}

// ═══════════════════════════ QUOTA TAB ════════════════════════════════════

function QuotaTab({ crew, canEdit, onChange, onError }: {
  crew: Crew; canEdit: boolean; onChange: (next: Crew) => void; onError: (m: string | null) => void;
}) {
  const [maxConcurrent, setMaxConcurrent] = useState(crew.maxConcurrent ?? 2);
  const [weeklyCap, setWeeklyCap] = useState(crew.weeklyCap ?? 20);
  const [dailyBudget, setDailyBudget] = useState<string>(crew.dailyBudgetUsd?.toString() ?? '');
  const [maxTaskBudget, setMaxTaskBudget] = useState<string>(crew.maxTaskBudgetUsd?.toString() ?? '');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setMaxConcurrent(crew.maxConcurrent ?? 2);
    setWeeklyCap(crew.weeklyCap ?? 20);
    setDailyBudget(crew.dailyBudgetUsd?.toString() ?? '');
    setMaxTaskBudget(crew.maxTaskBudgetUsd?.toString() ?? '');
  }, [crew.id, crew.maxConcurrent, crew.weeklyCap, crew.dailyBudgetUsd, crew.maxTaskBudgetUsd]);

  async function save() {
    if (!canEdit) return;
    setBusy(true); onError(null);
    try {
      onChange(await forgeApi.patchCrew(crew.id, {
        maxConcurrent,
        weeklyCap,
        dailyBudgetUsd: dailyBudget === '' ? null : Number(dailyBudget),
        maxTaskBudgetUsd: maxTaskBudget === '' ? null : Number(maxTaskBudget),
      }));
    } catch (err) { onError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  const dirty =
    maxConcurrent !== (crew.maxConcurrent ?? 2)
    || weeklyCap !== (crew.weeklyCap ?? 20)
    || (dailyBudget === '' ? null : Number(dailyBudget)) !== (crew.dailyBudgetUsd ?? null)
    || (maxTaskBudget === '' ? null : Number(maxTaskBudget)) !== (crew.maxTaskBudgetUsd ?? null);

  return (
    <div style={{ display: 'grid', gap: 14, maxWidth: 480 }}>
      <SliderField
        label="Max concurrent tasks"
        value={maxConcurrent}
        min={1} max={20}
        disabled={!canEdit}
        onChange={setMaxConcurrent}
        hint="How many tasks the crew can claim at once across members. 409 + agent.crew_concurrency_full beyond this."
      />
      <SliderField
        label="Weekly task cap"
        value={weeklyCap}
        min={1} max={200}
        disabled={!canEdit}
        onChange={setWeeklyCap}
        hint="Hard ceiling on tasks completed per ISO week. 429 + agent.crew_weekly_cap_hit when full."
      />
      <NumberField
        label="Daily budget (USD)"
        value={dailyBudget}
        placeholder="e.g. 7.50 — empty = no cap"
        disabled={!canEdit}
        onChange={setDailyBudget}
        hint="Soft cap on the crew's total spend per day. Refuses new invocations beyond this with 402."
      />
      <NumberField
        label="Max per-task budget (USD)"
        value={maxTaskBudget}
        placeholder="e.g. 1.00 — empty = no per-task cap"
        disabled={!canEdit}
        onChange={setMaxTaskBudget}
        hint="Hard cap on a single invocation. The runner refuses to start beyond this."
      />
      {canEdit && (
        <div>
          <button onClick={() => void save()} disabled={!dirty || busy} style={{ ...primaryBtnStyle, opacity: !dirty || busy ? 0.5 : 1 }}>
            {busy ? 'Saving…' : 'Save quotas'}
          </button>
        </div>
      )}
    </div>
  );
}

// ═══════════════════════════ HISTORY TAB ══════════════════════════════════

function HistoryTab({ crewId }: { crewId: string }) {
  const [rows, setRows] = useState<CrewHistoryEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    forgeApi.listCrewHistory(crewId).then((rs) => { if (!cancelled) setRows(rs); }).catch((err) => { if (!cancelled) setError(err instanceof Error ? err.message : String(err)); });
    return () => { cancelled = true; };
  }, [crewId]);

  if (error) return <div style={{ fontSize: 11.5, color: 'var(--danger)' }}>{error}</div>;
  if (rows === null) return <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>Loading…</div>;
  if (rows.length === 0) return <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>No history yet.</div>;

  return (
    <table style={{ width: '100%', fontSize: 11.5, borderCollapse: 'collapse' }}>
      <thead>
        <tr style={{ textAlign: 'left', color: 'var(--text-tertiary)' }}>
          <th style={th()}>When</th><th style={th()}>Kind</th><th style={th()}>From</th><th style={th()}>To</th><th style={th()}>By</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.id} style={{ borderTop: '1px solid var(--border)' }}>
            <td style={td('mono')}>{relTime(r.at)}</td>
            <td style={td()}>{r.kind}</td>
            <td style={td('mono')}>{r.fromValue ?? '—'}</td>
            <td style={td('mono')}>{r.toValue ?? '—'}</td>
            <td style={td()}>{r.byUserId ?? '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ═══════════════════════════ SHARED ═══════════════════════════════════════

function RuntimeBadgeGroup({ crew }: { crew: Crew }) {
  const backend = (crew.backend ?? 'copilot').toLowerCase();
  const mode = (crew.invokeMode ?? 'sdk').toLowerCase();
  const isCli = mode === 'cli';
  const claudeSdkGap = backend === 'claude' && mode === 'sdk';
  return (
    <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', alignItems: 'center', justifyContent: 'flex-end' }}>
      <span style={runtimePillStyle('model')}>{crew.model}</span>
      <span style={runtimePillStyle(backend === 'claude' ? 'claude' : 'copilot')}>{backend}</span>
      <span style={runtimePillStyle(isCli ? 'cli' : 'sdk')}>{mode}</span>
      {(isCli || claudeSdkGap) && (
        <span
          style={runtimePillStyle('warn')}
          title={isCli
            ? 'CLI mode is text-only — plan / coding tasks require SDK mode.'
            : 'Anthropic tools array not yet declared in the Claude SDK path; tools effectively text-only until Phase 13.4a.'}
        >
          tools: text-only
        </span>
      )}
    </div>
  );
}

function SalaryStrip({ salary, budget, backend }: { salary: CrewSalary | undefined; budget: number | null; backend?: string }) {
  if (!salary) return <div style={{ fontSize: 11, color: 'var(--text-tertiary)', padding: '0 16px 12px' }}>—</div>;
  const overBudget = budget != null && salary.today >= budget;
  const isCopilot = (backend ?? 'copilot').toLowerCase() === 'copilot';
  return (
    <div style={{ padding: '0 4px 12px', display: 'grid', gap: 6 }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr) 1fr', gap: 8, fontFamily: 'var(--font-mono, monospace)' }}>
        <SalaryFig label="today"  value={salary.today}  highlight={overBudget} budget={budget} />
        <SalaryFig label="sprint" value={salary.sprint} />
        <SalaryFig label="month"  value={salary.month}  />
        <Sparkline values={salary.sparkline} />
      </div>
      {isCopilot && (
        <div style={{ fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.4 }}>
          est. — Copilot is subscription-billed; figures derive from the registry price.
        </div>
      )}
    </div>
  );
}

function SalaryFig({ label, value, highlight, budget }: { label: string; value: number; highlight?: boolean; budget?: number | null }) {
  const color = highlight ? 'var(--danger, #ef4444)' : 'var(--text-primary)';
  return (
    <div>
      <div style={{ fontSize: 13, fontWeight: 600, color }}>${value.toFixed(2)}</div>
      <div style={{ fontSize: 10, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.4 }}>
        {label}
        {label === 'today' && budget != null && <span style={{ marginLeft: 4, opacity: 0.7 }}>/ ${budget.toFixed(2)}</span>}
      </div>
    </div>
  );
}

function Sparkline({ values }: { values: number[] }) {
  if (values.length === 0) return null;
  const max = Math.max(...values, 0.0001);
  const w = 100, h = 28;
  const stepX = values.length > 1 ? w / (values.length - 1) : w;
  const points = values.map((v, i) => `${(i * stepX).toFixed(2)},${(h - (v / max) * (h - 2) - 1).toFixed(2)}`).join(' ');
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} aria-label="14-day spend sparkline">
      <polyline fill="none" stroke="var(--accent)" strokeWidth="1.25" strokeLinejoin="round" strokeLinecap="round" points={points} />
    </svg>
  );
}

function AgentChip({ id, agents }: { id: string; agents: AgentDetail[] }) {
  const a = agents.find((x) => x.id === id);
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0 }}>
      <AgentFace id={id} name={a?.name} role={a?.role} size={28} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontWeight: 600, fontSize: 12.5 }}>
          {a?.name ?? id}
          {a?.name && <span style={{ color: 'var(--text-tertiary)', fontWeight: 400, marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10 }}>{id}</span>}
        </div>
        {a && (
          <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
            {a.role} · {a.state} · {a.capabilities.length} cap{a.capabilities.length === 1 ? '' : 's'}
          </div>
        )}
      </div>
    </div>
  );
}

/** Avatar bubble — falls back to a role-colored mono initial when no portrait. */
function AgentFace({ id, name, role, size = 22 }: { id: string; name?: string | null; role?: string | null; size?: number }) {
  const url = avatarFor(id, name);
  const tip = agentTooltip(id, name, role);
  if (url) {
    return (
      <img
        src={url}
        alt={name || id}
        title={tip}
        className={`agent-chip-avatar ${role ?? ''}`}
        style={{ width: size, height: size }}
      />
    );
  }
  return (
    <span className={`agent-chip ${role ?? ''}`} title={tip} style={{ width: size, height: size, fontSize: size * 0.42 }}>
      {id}
    </span>
  );
}

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

function SliderField({ label, value, min, max, disabled, onChange, hint }: {
  label: string; value: number; min: number; max: number; disabled: boolean; onChange: (n: number) => void; hint: string;
}) {
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between' }}>
        <SectionHead label={label} />
        <span style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 13, fontWeight: 600 }}>{value}</span>
      </div>
      <input
        type="range" min={min} max={max} value={value} disabled={disabled}
        onChange={(e) => onChange(Number(e.target.value))}
        style={{ width: '100%' }}
      />
      <div style={hintStyle}>{hint}</div>
    </div>
  );
}

function NumberField({ label, value, placeholder, disabled, onChange, hint }: {
  label: string; value: string; placeholder: string; disabled: boolean; onChange: (s: string) => void; hint: string;
}) {
  return (
    <div>
      <SectionHead label={label} />
      <input
        type="text" value={value} disabled={disabled} placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        style={{ ...searchInputStyle, fontFamily: 'var(--font-mono, monospace)' }}
      />
      <div style={hintStyle}>{hint}</div>
    </div>
  );
}

// ═══════════════════════════ STYLES ═══════════════════════════════════════

const layoutStyle: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '260px 1fr',
  gap: 18,
  padding: '0 24px 24px',
  alignItems: 'start',
};
const crewListStyle: React.CSSProperties = {
  background: 'var(--bg-surface)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-lg)',
  padding: 8,
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  position: 'sticky',
  top: 16,
  maxHeight: 'calc(100vh - 120px)',
  overflowY: 'auto',
};
const crewListHeadStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between',
  fontSize: 10.5, fontFamily: 'var(--font-mono, monospace)',
  color: 'var(--text-tertiary)', padding: '4px 8px',
  textTransform: 'uppercase', letterSpacing: 0.12, fontWeight: 600,
};
function crewListItemStyle(sel: boolean, depth: number): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 8,
    padding: '8px 10px', paddingLeft: 10 + depth * 12,
    background: sel ? 'var(--accent-bg, var(--bg-sunken))' : 'transparent',
    border: sel ? '1px solid var(--accent)' : '1px solid transparent',
    borderRadius: 'var(--radius-md)',
    cursor: 'pointer',
    textAlign: 'left',
    width: '100%',
    color: 'inherit',
  };
}
const crewListNameStyle = (sel: boolean): React.CSSProperties => ({
  fontWeight: sel ? 600 : 500, fontSize: 12.5,
  color: sel ? 'var(--accent)' : 'var(--text-primary)',
  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
});
const crewListMetaStyle: React.CSSProperties = {
  fontSize: 10, color: 'var(--text-tertiary)',
  display: 'flex', flexWrap: 'wrap', gap: 4, alignItems: 'center', marginTop: 2,
};

const detailStyle: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 10,
  background: 'var(--bg-surface)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-lg)',
  padding: 18,
  minWidth: 0,
};
const detailHeadStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'flex-start', gap: 12, paddingBottom: 12, borderBottom: '1px solid var(--border)',
};
const detailEyebrowStyle: React.CSSProperties = {
  fontSize: 10.5, fontFamily: 'var(--font-mono, monospace)', color: 'var(--text-tertiary)',
  textTransform: 'uppercase', letterSpacing: 0.1, marginBottom: 4,
};
const detailNameStyle: React.CSSProperties = {
  fontFamily: 'var(--font-display, sans-serif)', fontSize: 22, fontWeight: 600, letterSpacing: -0.02,
};

const tabsStyle: React.CSSProperties = {
  display: 'flex', gap: 2, borderBottom: '1px solid var(--border)', marginTop: 4,
};
function tabBtnStyle(active: boolean): React.CSSProperties {
  return {
    padding: '8px 12px', background: 'transparent', border: 'none', cursor: 'pointer',
    color: active ? 'var(--accent)' : 'var(--text-secondary)',
    fontSize: 12, fontWeight: active ? 600 : 500,
    borderBottom: active ? '2px solid var(--accent)' : '2px solid transparent',
    marginBottom: -1,
  };
}
const tabBodyStyle: React.CSSProperties = { paddingTop: 14 };

const twoColStyle: React.CSSProperties = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 18, alignItems: 'start' };

function memberRowStyle(isLead: boolean): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 8,
    padding: '8px 10px',
    background: isLead ? 'var(--accent-bg, var(--bg-sunken))' : 'var(--bg-sunken)',
    border: '1px solid var(--border)',
    borderRadius: 'var(--radius-sm)',
    marginBottom: 4,
  };
}
const leadCrownStyle: React.CSSProperties = {
  fontSize: 10, fontFamily: 'var(--font-mono, monospace)',
  color: 'var(--accent)', whiteSpace: 'nowrap',
};
const roleSelectStyle: React.CSSProperties = {
  fontSize: 11, padding: '3px 6px', borderRadius: 'var(--radius-sm)',
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
};
const dangerBtnStyle: React.CSSProperties = {
  background: 'transparent', border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
  color: 'var(--text-tertiary)', cursor: 'pointer',
  width: 24, height: 24, fontSize: 14, lineHeight: 1, padding: 0,
};

const searchInputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', fontSize: 12,
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)', outline: 'none', marginBottom: 6,
};
const catalogListStyle: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 2,
  maxHeight: 480, overflowY: 'auto',
  border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
  padding: 4, background: 'var(--bg-base, var(--bg-sunken))',
};
function catalogRowStyle(inCrew: boolean): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 8,
    padding: '6px 8px',
    background: inCrew ? 'var(--accent-bg, transparent)' : 'transparent',
    borderRadius: 'var(--radius-sm)',
  };
}
const catalogNameStyle: React.CSSProperties = {
  fontSize: 12, display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap',
};
const catalogCapsStyle: React.CSSProperties = {
  fontSize: 10, color: 'var(--text-tertiary)', marginTop: 2,
  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
};
function catalogBtnStyle(inCrew: boolean): React.CSSProperties {
  return {
    width: 24, height: 24, fontSize: 14, lineHeight: 1, padding: 0,
    background: inCrew ? 'var(--accent)' : 'var(--bg-surface)',
    color: inCrew ? 'var(--text-on-accent, white)' : 'var(--text-primary)',
    border: '1px solid ' + (inCrew ? 'var(--accent)' : 'var(--border-strong)'),
    borderRadius: 'var(--radius-sm)', cursor: 'pointer', flexShrink: 0,
  };
}

const agentRolePillStyle: React.CSSProperties = {
  fontSize: 9, padding: '1px 5px', borderRadius: 999,
  background: 'var(--bg-sunken)', color: 'var(--text-secondary)',
  border: '1px solid var(--border-strong)', fontFamily: 'var(--font-mono, monospace)',
  textTransform: 'uppercase', letterSpacing: 0.4,
};
function agentStatePillStyle(state: string): React.CSSProperties {
  const palette: Record<string, [string, string]> = {
    idle:    ['rgba(120, 120, 120, 0.10)', 'rgb(110, 110, 110)'],
    working: ['rgba(34, 197, 94, 0.12)',   'rgb(22, 163, 74)'],
    blocked: ['rgba(239, 68, 68, 0.10)',   'rgb(220, 38, 38)'],
    paused:  ['rgba(245, 158, 11, 0.12)',  'rgb(180, 112, 0)'],
  };
  const [bg, fg] = palette[state.toLowerCase()] ?? palette.idle;
  return {
    fontSize: 9, padding: '1px 5px', borderRadius: 999,
    background: bg, color: fg, fontFamily: 'var(--font-mono, monospace)',
    textTransform: 'uppercase', letterSpacing: 0.4,
  };
}

const capChipStyle: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 4,
  fontSize: 10.5, padding: '2px 4px 2px 7px',
  background: 'var(--accent-bg, rgba(245, 158, 11, 0.10))',
  color: 'var(--accent, rgb(180, 112, 0))',
  border: '1px solid var(--border-accent, rgba(245, 158, 11, 0.32))',
  borderRadius: 999,
};
const chipRemoveStyle: React.CSSProperties = {
  background: 'transparent', border: 'none', color: 'inherit',
  cursor: 'pointer', fontSize: 12, lineHeight: 1, padding: '0 2px', opacity: 0.7,
};

const selectStyle: React.CSSProperties = {
  fontSize: 12, padding: '7px 10px', borderRadius: 'var(--radius-sm)',
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
};
const primaryBtnStyle: React.CSSProperties = {
  padding: '7px 14px', fontSize: 12, fontWeight: 600,
  background: 'var(--accent)', color: 'var(--text-on-accent, white)',
  border: '1px solid var(--accent)', borderRadius: 'var(--radius-sm)',
  cursor: 'pointer',
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
const pairSummaryStyle: React.CSSProperties = {
  padding: 10, background: 'var(--bg-sunken)',
  border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
};

function runtimePillStyle(kind: 'model' | 'copilot' | 'claude' | 'sdk' | 'cli' | 'warn'): React.CSSProperties {
  const palette: Record<typeof kind, { bg: string; fg: string; border: string }> = {
    model:   { bg: 'var(--bg-sunken, #f5f3ee)',   fg: 'var(--text-secondary)', border: 'var(--border-strong, #d6d3cc)' },
    copilot: { bg: 'rgba(20, 110, 245, 0.12)',     fg: 'rgb(20, 110, 245)',      border: 'rgba(20, 110, 245, 0.32)' },
    claude:  { bg: 'rgba(168, 85, 247, 0.12)',     fg: 'rgb(168, 85, 247)',      border: 'rgba(168, 85, 247, 0.32)' },
    sdk:     { bg: 'rgba(34, 197, 94, 0.10)',      fg: 'rgb(22, 163, 74)',       border: 'rgba(34, 197, 94, 0.32)' },
    cli:     { bg: 'rgba(245, 158, 11, 0.10)',     fg: 'rgb(180, 112, 0)',       border: 'rgba(245, 158, 11, 0.32)' },
    warn:    { bg: 'rgba(245, 158, 11, 0.16)',     fg: 'rgb(180, 112, 0)',       border: 'rgba(245, 158, 11, 0.42)' },
  };
  const p = palette[kind];
  return {
    fontSize: 10, fontFamily: 'var(--font-mono, monospace)',
    padding: '1px 6px', borderRadius: 999,
    background: p.bg, color: p.fg, border: `1px solid ${p.border}`,
    fontWeight: 600, letterSpacing: 0.3, whiteSpace: 'nowrap',
  };
}

function shortId(cid: string): string {
  if (cid.length <= 18) return cid;
  return `${cid.slice(0, 11)}…${cid.slice(-4)}`;
}

function relTime(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const ms = Date.now() - t;
  if (ms < 60_000) return `${Math.round(ms / 1000)}s ago`;
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)}m ago`;
  if (ms < 86_400_000) return `${Math.round(ms / 3_600_000)}h ago`;
  return `${Math.round(ms / 86_400_000)}d ago`;
}

function th(): React.CSSProperties { return { padding: '8px 10px', fontWeight: 500, fontFamily: 'var(--font-body, inherit)' }; }
function td(extra?: 'mono'): React.CSSProperties {
  return {
    padding: '8px 10px', color: 'var(--text-secondary)',
    fontFamily: extra === 'mono' ? 'var(--font-mono, monospace)' : 'inherit',
    fontVariantNumeric: 'tabular-nums',
  };
}
