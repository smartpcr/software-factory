import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { AgentDetail, AgentLevelHistoryEntry, Crew, CrewHistoryEntry, CrewPayroll, CrewQueueStats, CrewSalary, DomainExpert, ModelInfo } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useQueueStore, useQueueChannel } from '@/realtime/useQueueStore';
import { useHasRole } from '@/state/useCurrentUserStore';
import { useAgentEditorStore } from '@/state/useAgentEditorStore';
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

type TabKey = 'roster' | 'pair' | 'capabilities' | 'runtime' | 'quota' | 'performance' | 'history';

const TAB_ORDER: Array<{ key: TabKey; label: string }> = [
  { key: 'roster',       label: 'Roster' },
  { key: 'pair',         label: 'Pair' },
  { key: 'capabilities', label: 'Capabilities' },
  { key: 'runtime',      label: 'Runtime' },
  { key: 'quota',        label: 'Quota' },
  { key: 'performance',  label: 'Performance' },
  { key: 'history',      label: 'History' },
];

const ROLE_OPTIONS = ['coder', 'reviewer', 'architect', 'qa', 'lead', 'generator', 'evaluator', 'observer'];

export function CrewsView() {
  const isPO = useHasRole('product-owner');
  const navigate = useNavigate();

  const [crews, setCrews] = useState<Crew[]>([]);
  const [agents, setAgents] = useState<AgentDetail[]>([]);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [salaries, setSalaries] = useState<Record<string, CrewSalary>>({});
  // Phase 17.8 — per-crew queue stats (depth + ETA + strained flag).
  const [queueStats, setQueueStats] = useState<Record<string, CrewQueueStats>>({});
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [tab, setTab] = useState<TabKey>('roster');
  const [error, setError] = useState<string | null>(null);
  const [autoAssignBusy, setAutoAssignBusy] = useState(false);
  const [autoAssignMsg, setAutoAssignMsg] = useState<string | null>(null);
  // Phase 13f — agent edit popup is now mounted globally at AppShell;
  // we just call openAgent(id) from any click site here.
  const openAgent = useAgentEditorStore((s) => s.openAgent);

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
        // Phase 17.8 — fetch queue stats per crew (depth + ETA).
        const queueEntries = await Promise.all(
          crewList.map((c) => forgeApi.getCrewQueueStats(c.id).then((s) => [c.id, s] as const).catch(() => null)),
        );
        if (cancelled) return;
        const qMap: Record<string, CrewQueueStats> = {};
        for (const e of queueEntries) if (e) qMap[e[0]] = e[1];
        setQueueStats(qMap);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one-shot init
  }, []);

  // Phase 17.8 — keep queue stats fresh against the SignalR feed. Whenever
  // a queue.changed event lands the store's rows reference flips, which
  // triggers a per-crew stats refresh.
  useQueueChannel({});
  const queueRowsRef = useQueueStore((s) => s.rows);
  useEffect(() => {
    if (crews.length === 0) return;
    let cancelled = false;
    void (async () => {
      const entries = await Promise.all(
        crews.map((c) => forgeApi.getCrewQueueStats(c.id).then((s) => [c.id, s] as const).catch(() => null)),
      );
      if (cancelled) return;
      const next: Record<string, CrewQueueStats> = {};
      for (const e of entries) if (e) next[e[0]] = e[1];
      setQueueStats(next);
    })();
    return () => { cancelled = true; };
  }, [queueRowsRef, crews]);

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
                {queueStats[c.id] && (queueStats[c.id].depth > 0 || queueStats[c.id].strained) && (
                  <span
                    role="button"
                    tabIndex={0}
                    title={queueStats[c.id].strained
                      ? `Strained · ${queueStats[c.id].depth} queued · ETA ${queueStats[c.id].etaMinutes}m`
                      : `${queueStats[c.id].depth} queued · ETA ${queueStats[c.id].etaMinutes}m`}
                    onClick={(e) => { e.stopPropagation(); navigate('/inbox'); }}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') { e.stopPropagation(); navigate('/inbox'); }
                    }}
                    style={{
                      marginLeft: 6,
                      padding: '2px 8px',
                      borderRadius: 8,
                      fontSize: 10,
                      fontFamily: 'var(--font-mono, monospace)',
                      fontWeight: 600,
                      cursor: 'pointer',
                      border: queueStats[c.id].strained
                        ? '1px solid var(--danger-border)'
                        : '1px solid var(--border)',
                      background: queueStats[c.id].strained ? 'var(--danger-bg)' : 'var(--bg-surface-2)',
                      color: queueStats[c.id].strained ? 'var(--danger)' : 'var(--text-secondary)',
                    }}
                  >
                    Q:{queueStats[c.id].depth} · {queueStats[c.id].etaMinutes}m
                  </span>
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
                    onEditAgent={openAgent}
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
                    onEditAgent={openAgent}
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
                {tab === 'performance' && (
                  <PerformanceTab crew={selected} allAgents={agents} onEditAgent={openAgent} onError={setError} />
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
  // Phase 13f — soul.md editing now lives inside the global AgentEditModal
  // (opened via onEditAgent). No separate soul-only modal needed here.
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
            <button
              onClick={() => onEditAgent(crew.lead!)}
              style={chipClickStyle}
              title="Click to edit agent (name, role, runtime, soul.md)"
            >
              <AgentChip id={crew.lead} agents={allAgents} />
            </button>
            <span style={leadCrownStyle}>★ lead</span>
          </div>
        )}
        {(crew.roster ?? []).length === 0 && !crew.lead && (
          <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No members — add agents from the catalog →</div>
        )}
        {(crew.roster ?? []).map((m) => (
          <div key={m.agentId} style={memberRowStyle(m.agentId === crew.lead)}>
            <button
              onClick={() => onEditAgent(m.agentId)}
              style={chipClickStyle}
              title="Click to edit agent"
            >
              <AgentChip id={m.agentId} agents={allAgents} />
            </button>
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


// ═══════════════════════════ PAIR TAB ═════════════════════════════════════

function PairTab({ crew, allAgents, models, canEdit, onChange, onError, onEditAgent }: {
  crew: Crew;
  allAgents: AgentDetail[];
  models: ModelInfo[];
  canEdit: boolean;
  onChange: (next: Crew) => void;
  onError: (msg: string | null) => void;
  onEditAgent: (agentId: string) => void;
}) {
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
        The team lead assigns each work item to a specific pair (or a solo agent), so this view
        just shows how the crew's roster pairs up. The <strong>generator</strong> writes the spec
        or the diff; the <strong>evaluator</strong> reviews before the WIT tree commits or the PR
        comes off draft. Models below apply crew-wide.
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

      <RosterPairsList
        crew={crew}
        allAgents={allAgents}
        generatorModel={generatorModel}
        evaluatorModel={evaluatorModel}
        onEditAgent={onEditAgent}
      />
    </div>
  );
}

// Auto-pair every roster member two-by-two so the team lead can see how the
// crew lines up. We don't track a "default pair" — work is assigned per-task,
// not per-crew. Strategy:
//   1. Cross-pair generators (eng/arch/lead/coder) with evaluators
//      (rev/qa/reviewer) 1-to-1 while both buckets have someone.
//   2. Drain whichever bucket still has people by pairing them with each
//      other (eng+eng or rev+rev — the second member acts as evaluator).
//   3. Pair "other" roles among themselves the same way.
//   4. If a single agent is left at the end, render them solo.
function buildRosterPairs(crew: Crew, allAgents: AgentDetail[]): Array<{
  generator: AgentDetail | null;
  evaluator: AgentDetail | null;
}> {
  const byId = new Map(allAgents.map((a) => [a.id, a]));
  const memberIds = new Set<string>();
  if (crew.lead) memberIds.add(crew.lead);
  for (const m of crew.roster ?? []) memberIds.add(m.agentId);
  const members = [...memberIds].map((id) => byId.get(id)).filter((a): a is AgentDetail => !!a);

  const isGen = (a: AgentDetail) => /^(eng|arch|lead|generator|coder)$/i.test(a.role);
  const isEval = (a: AgentDetail) => /^(rev|qa|reviewer|evaluator)$/i.test(a.role);

  const out: Array<{ generator: AgentDetail | null; evaluator: AgentDetail | null }> = [];
  const generators = members.filter(isGen);
  const evaluators = members.filter(isEval);
  const others = members.filter((a) => !isGen(a) && !isEval(a));

  while (generators.length > 0 && evaluators.length > 0) {
    out.push({ generator: generators.shift()!, evaluator: evaluators.shift()! });
  }
  while (generators.length >= 2) {
    out.push({ generator: generators.shift()!, evaluator: generators.shift()! });
  }
  while (evaluators.length >= 2) {
    out.push({ generator: evaluators.shift()!, evaluator: evaluators.shift()! });
  }
  while (others.length >= 2) {
    out.push({ generator: others.shift()!, evaluator: others.shift()! });
  }
  const leftover = [...generators, ...evaluators, ...others];
  while (leftover.length >= 2) {
    out.push({ generator: leftover.shift()!, evaluator: leftover.shift()! });
  }
  if (leftover.length === 1) {
    out.push({ generator: leftover[0], evaluator: null });
  }

  return out;
}

function RosterPairsList({ crew, allAgents, generatorModel, evaluatorModel, onEditAgent }: {
  crew: Crew;
  allAgents: AgentDetail[];
  generatorModel: string;
  evaluatorModel: string;
  onEditAgent: (agentId: string) => void;
}) {
  const pairs = useMemo(() => buildRosterPairs(crew, allAgents), [crew, allAgents]);
  const memberCount = (crew.roster?.length ?? 0) + (crew.lead ? 1 : 0);
  const unpaired = pairs.reduce((n, p) => n + (p.generator && p.evaluator ? 0 : 1), 0);

  if (pairs.length === 0) {
    return (
      <div style={{ fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
        No roster — add members in the Roster tab to auto-pair them here.
      </div>
    );
  }

  return (
    <div>
      <SectionHead
        label="Roster pairs"
        right={`${memberCount} member${memberCount === 1 ? '' : 's'} → ${pairs.length} pair${pairs.length === 1 ? '' : 's'}${unpaired ? ` · ${unpaired} solo` : ''}`}
      />
      <div style={{ display: 'grid', gap: 4 }}>
        {pairs.map((p, i) => (
          <RosterPairRow
            key={(p.generator?.id ?? '_') + ':' + (p.evaluator?.id ?? '_') + ':' + i}
            generator={p.generator}
            evaluator={p.evaluator}
            generatorModel={generatorModel}
            evaluatorModel={evaluatorModel}
            onEditAgent={onEditAgent}
          />
        ))}
      </div>
    </div>
  );
}

function RosterPairRow({ generator, evaluator, generatorModel, evaluatorModel, onEditAgent }: {
  generator: AgentDetail | null;
  evaluator: AgentDetail | null;
  generatorModel: string;
  evaluatorModel: string;
  onEditAgent: (agentId: string) => void;
}) {
  const rowStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '1fr 18px 1fr',
    alignItems: 'center',
    gap: 8,
    padding: '8px 10px',
    borderRadius: 6,
    border: '1px solid var(--border)',
    background: 'var(--surface-1, transparent)',
  };
  return (
    <div style={rowStyle}>
      <RosterPairSide agent={generator} model={generatorModel} placeholder="(no generator)" onEditAgent={onEditAgent} />
      <span aria-hidden style={{ color: 'var(--text-tertiary)', textAlign: 'center', fontSize: 12 }}>→</span>
      <RosterPairSide agent={evaluator} model={evaluatorModel} placeholder="(solo)" onEditAgent={onEditAgent} />
    </div>
  );
}

function RosterPairSide({ agent, model, placeholder, onEditAgent }: {
  agent: AgentDetail | null;
  model: string;
  placeholder: string;
  onEditAgent: (agentId: string) => void;
}) {
  if (!agent) {
    return <span style={{ fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>{placeholder}</span>;
  }
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
      <AgentFace
        id={agent.id}
        name={agent.name}
        role={agent.role}
        soulPath={agent.soulPath}
        size={26}
        onClick={() => onEditAgent(agent.id)}
      />
      <button
        type="button"
        onClick={() => onEditAgent(agent.id)}
        style={{ flex: 1, minWidth: 0, background: 'transparent', border: 'none', color: 'inherit', textAlign: 'left', cursor: 'pointer', padding: 0 }}
        title="Edit member"
      >
        <div style={{ fontWeight: 600, fontSize: 12.5, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {agent.name}
          <span style={{ color: 'var(--text-tertiary)', fontWeight: 400, marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10 }}>{agent.id}</span>
        </div>
        <div style={{ fontSize: 10, color: 'var(--text-tertiary)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {agent.role} · {model || 'inherit'}
        </div>
      </button>
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

// ══════════════════════════ CAPABILITIES TAB ══════════════════════════════

function CapabilitiesTab({ crew, canEdit, onChange, onError }: {
  crew: Crew; canEdit: boolean; onChange: (next: Crew) => void; onError: (m: string | null) => void;
}) {
  const [pending, setPending] = useState('');
  const [busy, setBusy] = useState(false);
  const [picker, setPicker] = useState('');
  const [catalogue, setCatalogue] = useState<DomainExpert[]>([]);
  const caps = crew.capabilities ?? [];

  // Load the domain-expert catalogue (built-in team caps + sha256 docs).
  useEffect(() => {
    let cancelled = false;
    forgeApi.listDomainExperts().then((rows) => { if (!cancelled) setCatalogue(rows); })
      .catch((err) => { if (!cancelled) onError(err instanceof Error ? err.message : String(err)); });
    return () => { cancelled = true; };
  }, [onError]);

  // Resolve a content-id to its topic label (falls back to a shortened id).
  const labelFor = (cid: string) => {
    const hit = catalogue.find((e) => e.contentId === cid);
    return hit?.topic ?? shortId(cid);
  };

  // Eligible picker options = catalogue minus the ones already on the crew.
  // Built-in team capabilities that match this crew's team are surfaced
  // first; user-authored sha256 docs follow.
  const eligible = useMemo(() => {
    const have = new Set(caps);
    const teamPrefix = (() => {
      const n = crew.name.toLowerCase();
      if (n.startsWith('arch')) return 'team:architecture:';
      if (n.startsWith('eng'))  return 'team:engineering:';
      if (n.startsWith('rev'))  return 'team:review:';
      if (n.startsWith('qa'))   return 'team:qa:';
      return null;
    })();
    return catalogue
      .filter((e) => !have.has(e.contentId))
      .sort((a, b) => {
        const aTeam = teamPrefix && a.contentId.startsWith(teamPrefix) ? 0 : a.contentId.startsWith('team:') ? 1 : 2;
        const bTeam = teamPrefix && b.contentId.startsWith(teamPrefix) ? 0 : b.contentId.startsWith('team:') ? 1 : 2;
        if (aTeam !== bTeam) return aTeam - bTeam;
        return a.topic.localeCompare(b.topic);
      });
  }, [catalogue, caps, crew.name]);

  async function add(contentId: string) {
    const id = contentId.trim(); if (!id || !canEdit) return;
    setBusy(true); onError(null);
    try { onChange(await forgeApi.addCrewCapability(crew.id, id)); setPending(''); setPicker(''); }
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
    <div style={{ display: 'grid', gap: 12, maxWidth: 720 }}>
      <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
        Capabilities are content-addressed pointers into the
        <code style={{ fontFamily: 'var(--font-mono, monospace)', margin: '0 4px' }}>domain_experts</code>
        index. <strong>Built-in team caps</strong> (<code style={{ fontFamily: 'var(--font-mono, monospace)' }}>team:&hellip;</code>) are seeded
        automatically per the Phase 13 spec; <strong>operator caps</strong> (<code style={{ fontFamily: 'var(--font-mono, monospace)' }}>sha256:&hellip;</code>) point at <code>domain-expert.md</code> files
        committed under a workstream's repo. Both are appended to soul.md so the prompt cache prefix stays content-addressed.
      </div>
      <div>
        <SectionHead label="Active capabilities" right={`${caps.length}`} />
        {caps.length === 0 && (
          <div style={{ padding: 8, fontSize: 11, color: 'var(--text-tertiary)' }}>None — pick from the catalogue below.</div>
        )}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {caps.map((c) => {
            const isTeam = c.startsWith('team:');
            return (
              <span key={c} style={capChipStyle} title={c}>
                {isTeam && <span style={{ fontSize: 8.5, opacity: 0.7, marginRight: 4 }}>team</span>}
                <span style={{ fontFamily: isTeam ? 'inherit' : 'var(--font-mono, monospace)' }}>{labelFor(c)}</span>
                <button onClick={() => void remove(c)} disabled={!canEdit || busy} style={chipRemoveStyle}>×</button>
              </span>
            );
          })}
        </div>
      </div>

      {canEdit && eligible.length > 0 && (
        <div>
          <SectionHead label="Add from catalogue" right={`${eligible.length} available`} />
          <div style={{ display: 'flex', gap: 6 }}>
            <select
              value={picker}
              onChange={(e) => { const v = e.target.value; if (v) void add(v); else setPicker(''); }}
              disabled={busy}
              style={{ ...selectStyle, flex: 1 }}
            >
              <option value="">— pick a capability —</option>
              {eligible.filter((e) => e.contentId.startsWith('team:')).length > 0 && (
                <optgroup label="Built-in team capabilities">
                  {eligible.filter((e) => e.contentId.startsWith('team:')).map((e) => (
                    <option key={e.contentId} value={e.contentId}>
                      {e.topic} — {e.summary.length > 70 ? e.summary.slice(0, 67) + '…' : e.summary}
                    </option>
                  ))}
                </optgroup>
              )}
              {eligible.filter((e) => !e.contentId.startsWith('team:')).length > 0 && (
                <optgroup label="Domain experts (sha256)">
                  {eligible.filter((e) => !e.contentId.startsWith('team:')).map((e) => (
                    <option key={e.contentId} value={e.contentId}>{e.topic} · {shortId(e.contentId)}</option>
                  ))}
                </optgroup>
              )}
            </select>
          </div>
          <div style={hintStyle}>One click adds it. The picker hides capabilities the crew already has.</div>
        </div>
      )}

      {canEdit && (
        <div style={{ display: 'flex', gap: 6 }}>
          <input
            type="text" value={pending}
            onChange={(e) => setPending(e.target.value)}
            placeholder="sha256:abc123… (advanced — paste a content-id directly)"
            style={{ ...searchInputStyle, fontFamily: 'var(--font-mono, monospace)' }}
          />
          <button onClick={() => void add(pending)} disabled={busy || !pending.trim()} style={primaryBtnStyle}>
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

// ═══════════════════════════ PERFORMANCE TAB ═════════════════════════════

const LEVEL_TITLES: Record<number, string> = {
  0: 'Trainee',
  1: 'Engineer I',
  2: 'Engineer II',
  3: 'Engineer III',
  4: 'Senior I',
  5: 'Senior II',
  6: 'Senior III',
  7: 'Staff',
  8: 'Principal',
  9: 'Distinguished',
  10: 'Fellow',
};

const LEAD_MIN_LEVEL = 7;
const HOURLY_BASE = 4;
const HOURLY_STEP = 3;
function hourlyForLevel(level: number): number {
  const clamped = Math.max(0, Math.min(10, level));
  return HOURLY_BASE + clamped * HOURLY_STEP;
}

function PerformanceTab({ crew, allAgents, onEditAgent, onError }: {
  crew: Crew;
  allAgents: AgentDetail[];
  onEditAgent: (id: string) => void;
  onError: (msg: string | null) => void;
}) {
  const [payroll, setPayroll] = useState<CrewPayroll | null>(null);
  const [spend, setSpend] = useState<CrewSalary | null>(null);
  const [now, setNow] = useState<Date>(() => new Date());
  const [history, setHistory] = useState<Record<string, AgentLevelHistoryEntry[]>>({});

  useEffect(() => {
    let cancelled = false;
    onError(null);
    Promise.all([
      forgeApi.getCrewPayroll(crew.id).catch(() => null),
      forgeApi.getCrewSpend(crew.id).catch(() => null),
    ]).then(([p, s]) => {
      if (cancelled) return;
      setPayroll(p);
      setSpend(s);
    });
    return () => { cancelled = true; };
  }, [crew.id, onError]);

  // Tick "now" every 30s so the schedule strip's countdown stays fresh.
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), 30_000);
    return () => window.clearInterval(id);
  }, []);

  const memberIds = useMemo(() => {
    const set = new Set<string>();
    if (crew.lead) set.add(crew.lead);
    for (const m of crew.roster ?? []) set.add(m.agentId);
    return set;
  }, [crew.lead, crew.roster]);

  const members = useMemo(() => allAgents.filter((a) => memberIds.has(a.id)), [allAgents, memberIds]);

  // Load level-history per member lazily on mount.
  useEffect(() => {
    let cancelled = false;
    Promise.all(members.map((m) => forgeApi.getAgentLevelHistory(m.id).catch(() => [] as AgentLevelHistoryEntry[])))
      .then((all) => {
        if (cancelled) return;
        const byId: Record<string, AgentLevelHistoryEntry[]> = {};
        members.forEach((m, i) => { byId[m.id] = all[i] ?? []; });
        setHistory(byId);
      });
    return () => { cancelled = true; };
  }, [members]);

  const tz = (crew as Crew & { timezone?: string }).timezone ?? 'UTC';
  const scheduleEnabled = (crew as Crew & { scheduleEnabled?: boolean }).scheduleEnabled !== false;
  const shift: 'day' | 'night' = crew.shift === 'night' ? 'night' : 'day';
  const onShift = isOnShiftLocal(now, tz, scheduleEnabled, shift);
  const next = nextTransitionLocal(now, tz, shift);
  const bandLabel = shift === 'night' ? 'Mon–Sat 21:00–09:00' : 'Mon–Sat 09:00–21:00';

  return (
    <div style={{ display: 'grid', gap: 16 }}>
      {/* (a) Schedule strip */}
      <div
        style={{
          display: 'flex', alignItems: 'center', gap: 10,
          padding: '8px 12px', borderRadius: 8,
          border: `1px solid ${onShift ? 'var(--success, rgb(34, 197, 94))' : 'var(--border)'}`,
          background: onShift
            ? 'color-mix(in srgb, var(--success, rgb(34, 197, 94)) 12%, transparent)'
            : 'var(--bg-sunken, transparent)',
        }}
      >
        <span style={{
          width: 8, height: 8, borderRadius: 999,
          background: scheduleEnabled
            ? (onShift ? 'var(--success, rgb(34, 197, 94))' : 'var(--text-tertiary)')
            : 'var(--text-tertiary)',
          boxShadow: onShift ? '0 0 0 3px color-mix(in srgb, var(--success, rgb(34, 197, 94)) 30%, transparent)' : 'none',
        }} />
        <div style={{ flex: 1, fontSize: 12.5 }}>
          <strong style={{ color: scheduleEnabled
            ? (onShift ? 'var(--success, rgb(34, 197, 94))' : 'var(--text-secondary)')
            : 'var(--text-tertiary)' }}>
            {!scheduleEnabled ? 'Schedule disabled' : (onShift ? 'On shift' : 'Off shift')}
          </strong>
          <span style={{
            marginLeft: 8,
            padding: '1px 6px',
            borderRadius: 4,
            fontSize: 10,
            fontWeight: 600,
            letterSpacing: 0.4,
            textTransform: 'uppercase',
            color: shift === 'night' ? 'rgb(178, 165, 255)' : 'rgb(255, 196, 90)',
            background: shift === 'night'
              ? 'color-mix(in srgb, rgb(178, 165, 255) 14%, transparent)'
              : 'color-mix(in srgb, rgb(255, 196, 90) 14%, transparent)',
            border: `1px solid ${shift === 'night' ? 'rgb(178, 165, 255)' : 'rgb(255, 196, 90)'}`,
          }}>
            {shift === 'night' ? 'Night' : 'Day'}
          </span>
          <span style={{ color: 'var(--text-tertiary)', marginLeft: 8, fontSize: 11.5 }}>
            {tz} · {scheduleEnabled ? bandLabel : 'always available'}
          </span>
        </div>
        {scheduleEnabled && next && (
          <span style={{ fontSize: 11, color: 'var(--text-tertiary)', fontFamily: 'var(--font-mono, monospace)' }}>
            {onShift ? 'closes' : 'opens'} {next}
          </span>
        )}
      </div>

      {/* Payroll + spend cards (Q14.7 dual surface) */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
        <PayrollCard
          title="Payroll"
          subtitle="HR salary · level × on-shift hours"
          accent="rgb(34, 197, 94)"
          data={payroll}
        />
        <PayrollCard
          title="Spend"
          subtitle="Token cost · agent_usage rollup"
          accent="rgb(20, 110, 245)"
          data={spend}
        />
      </div>

      {/* (b) Roster performance grid */}
      <div>
        <SectionHead label="Roster performance" right={`${members.length} member${members.length === 1 ? '' : 's'} · click avatar to edit`} />
        {members.length === 0 ? (
          <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
            No members — add agents in the Roster tab to see scorecards.
          </div>
        ) : (
          <div style={{ display: 'grid', gap: 4 }}>
            {members.map((m) => (
              <RosterPerformanceRow
                key={m.id}
                agent={m}
                history={history[m.id] ?? []}
                isLead={m.id === crew.lead}
                onEditAgent={onEditAgent}
              />
            ))}
          </div>
        )}
        <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 8 }}>
          Levels (0–10) and salary update via the nightly promotion job. Trailing-20 rolling-avg ≥ 92 promotes; &lt; 75 demotes. Lead requires L{LEAD_MIN_LEVEL}+.
        </div>
      </div>
    </div>
  );
}

function PayrollCard({ title, subtitle, accent, data }: {
  title: string;
  subtitle: string;
  accent: string;
  data: CrewPayroll | CrewSalary | null;
}) {
  const max = data ? Math.max(0.01, ...data.sparkline) : 0.01;
  return (
    <div
      style={{
        padding: '10px 12px',
        borderRadius: 8,
        border: '1px solid var(--border)',
        background: 'var(--bg-sunken, transparent)',
        display: 'grid', gap: 8,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 8 }}>
        <div>
          <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 9.5, color: accent, textTransform: 'uppercase', letterSpacing: 0.12 }}>{title}</div>
          <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>{subtitle}</div>
        </div>
        <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 16, fontWeight: 700, color: accent }}>
          {data ? `$${data.today.toFixed(2)}` : '—'}
          <span style={{ fontSize: 10, fontWeight: 400, color: 'var(--text-tertiary)', marginLeft: 4 }}>today</span>
        </div>
      </div>
      <div style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: 32 }}>
        {(data?.sparkline ?? Array.from({ length: 14 }, () => 0)).map((v, i) => (
          <div
            key={i}
            title={`Day ${-13 + i + 1}: $${v.toFixed(2)}`}
            style={{
              flex: 1,
              height: `${Math.max(2, (v / max) * 32)}px`,
              background: accent,
              opacity: i === 13 ? 1 : 0.55,
              borderRadius: 1.5,
            }}
          />
        ))}
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10.5, color: 'var(--text-tertiary)', fontFamily: 'var(--font-mono, monospace)' }}>
        <span>sprint ${data ? data.sprint.toFixed(2) : '—'}</span>
        <span>month ${data ? data.month.toFixed(2) : '—'}</span>
      </div>
    </div>
  );
}

function RosterPerformanceRow({ agent, history, isLead, onEditAgent }: {
  agent: AgentDetail;
  history: AgentLevelHistoryEntry[];
  isLead: boolean;
  onEditAgent: (id: string) => void;
}) {
  // Level + hourly come off the agent record (server keeps them in sync).
  const lev = (agent as AgentDetail & { level?: number }).level ?? 0;
  const hourly = (agent as AgentDetail & { hourlyUsd?: number }).hourlyUsd ?? hourlyForLevel(lev);
  const last = history[0];
  const lastPromote = history.find((h) => h.reason === 'promote');
  const nextEligible = lastPromote
    ? new Date(new Date(lastPromote.createdAt).getTime() + 30 * 86400_000)
    : null;
  const leadFloorWarn = isLead && lev < LEAD_MIN_LEVEL;

  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'auto 1fr auto auto auto',
        alignItems: 'center',
        gap: 10,
        padding: '8px 10px',
        borderRadius: 6,
        border: leadFloorWarn ? '1px solid var(--warning, rgb(180, 112, 0))' : '1px solid var(--border)',
        background: 'var(--surface-1, transparent)',
      }}
    >
      <AgentFace
        id={agent.id}
        name={agent.name}
        role={agent.role}
        soulPath={agent.soulPath}
        size={28}
        onClick={() => onEditAgent(agent.id)}
      />
      <button
        type="button"
        onClick={() => onEditAgent(agent.id)}
        style={{ minWidth: 0, background: 'transparent', border: 'none', color: 'inherit', textAlign: 'left', cursor: 'pointer', padding: 0 }}
      >
        <div style={{ fontWeight: 600, fontSize: 12.5, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {agent.name}
          <span style={{ color: 'var(--text-tertiary)', fontWeight: 400, marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10 }}>{agent.id}</span>
          {isLead && <span style={{ marginLeft: 6, fontSize: 9.5, fontFamily: 'var(--font-mono, monospace)', color: 'var(--accent)' }}>★ lead</span>}
        </div>
        <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
          {agent.role}
          {last
            ? <> · last {last.reason === 'promote' ? '↑' : last.reason === 'demote' ? '↓' : '·'} {fmtDate(last.createdAt)}</>
            : <> · no level history yet</>}
          {nextEligible && nextEligible > new Date() && (
            <> · next eligible {fmtDate(nextEligible.toISOString())}</>
          )}
        </div>
      </button>
      <span style={levelPillStyle(lev, leadFloorWarn)} title={leadFloorWarn ? `Lead must be L${LEAD_MIN_LEVEL}+` : undefined}>
        L{lev}
        <span style={{ marginLeft: 4, fontWeight: 400, color: 'var(--text-tertiary)' }}>{LEVEL_TITLES[lev] ?? ''}</span>
      </span>
      <span style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 11, color: 'var(--text-secondary)' }}>
        ${hourly.toFixed(0)}/hr
      </span>
      <span style={{ fontSize: 10, color: 'var(--text-tertiary)', fontFamily: 'var(--font-mono, monospace)' }}>
        {history.length === 0 ? '—' : `${history.length} change${history.length === 1 ? '' : 's'}`}
      </span>
    </div>
  );
}

function levelPillStyle(level: number, warn: boolean): React.CSSProperties {
  // Color ramp: 0–3 muted, 4–6 mid, 7–10 accent. Warn overlays warning border.
  const tone = level >= 7 ? 'rgb(34, 197, 94)' : level >= 4 ? 'rgb(20, 110, 245)' : 'var(--text-tertiary)';
  return {
    fontSize: 11,
    padding: '2px 8px',
    borderRadius: 999,
    border: `1px solid ${warn ? 'var(--warning, rgb(180, 112, 0))' : tone}`,
    color: warn ? 'var(--warning, rgb(180, 112, 0))' : tone,
    fontFamily: 'var(--font-mono, monospace)',
    background: 'transparent',
    whiteSpace: 'nowrap',
  };
}

function fmtDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }); }
  catch { return iso; }
}

// ── Schedule helpers (mirror server-side ScheduleGate). ────────────────

function isOnShiftLocal(nowUtc: Date, tz: string, scheduleEnabled: boolean, shift: 'day' | 'night' = 'day'): boolean {
  if (!scheduleEnabled) return true; // bypassed = always available
  const local = inTimezone(nowUtc, tz);
  const dow = local.getDay(); // 0=Sun..6=Sat
  const hour = local.getHours();
  if (shift === 'night') {
    // Night band starts at 21:00 on a work day (Mon–Sat) and runs until
    // 09:00 the next morning. So 00:00–09:00 is on shift iff yesterday
    // was a work day (i.e. today is Mon..Sun, but Sun morning closes
    // out the Sat night block).
    if (hour >= 21 && dow !== 0) return true;
    if (hour < 9 && dow !== 1) return true; // dow !== Mon → yesterday was Mon..Sat
    return false;
  }
  if (dow === 0) return false;
  return hour >= 9 && hour < 21;
}

function nextTransitionLocal(nowUtc: Date, tz: string, shift: 'day' | 'night' = 'day'): string | null {
  try {
    const local = inTimezone(nowUtc, tz);
    const onShift = isOnShiftLocal(nowUtc, tz, true, shift);
    const target = new Date(local);
    if (shift === 'night') {
      if (onShift) {
        // Closing transition: next 09:00. Today if hour < 9, else tomorrow.
        if (local.getHours() >= 21) target.setDate(target.getDate() + 1);
        target.setHours(9, 0, 0, 0);
      } else {
        // Opening transition: next work-day 21:00.
        if (local.getDay() === 0 || local.getHours() >= 21) target.setDate(target.getDate() + 1);
        while (target.getDay() === 0) target.setDate(target.getDate() + 1);
        target.setHours(21, 0, 0, 0);
      }
    } else if (onShift) {
      target.setHours(21, 0, 0, 0);
    } else {
      // Day shift, off — walk to next Mon–Sat 09:00.
      if (local.getHours() >= 21) target.setDate(target.getDate() + 1);
      while (target.getDay() === 0) target.setDate(target.getDate() + 1);
      target.setHours(9, 0, 0, 0);
    }
    const sameDay = target.toDateString() === local.toDateString();
    return sameDay
      ? target.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
      : target.toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
  } catch {
    return null;
  }
}

function inTimezone(d: Date, tz: string): Date {
  // Use Intl to format in the target TZ then re-parse — good enough for
  // hour-resolution display math.
  try {
    const fmt = new Intl.DateTimeFormat('en-US', {
      timeZone: tz, hour12: false,
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit',
    }).formatToParts(d);
    const get = (t: string) => Number(fmt.find((p) => p.type === t)?.value ?? '0');
    return new Date(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute'), get('second'));
  } catch {
    return d;
  }
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
function AgentFace({ id, name, role, soulPath, size = 22, onClick }: {
  id: string;
  name?: string | null;
  role?: string | null;
  soulPath?: string | null;
  size?: number;
  onClick?: () => void;
}) {
  const url = avatarFor(id, name);
  const tip = agentTooltip(id, name, role, soulPath);
  const interactive = !!onClick;
  const interactiveStyle: React.CSSProperties = interactive
    ? { cursor: 'pointer' }
    : {};
  if (url) {
    return (
      <img
        src={url}
        alt={name || id}
        title={tip}
        onClick={onClick}
        className={`agent-chip-avatar ${role ?? ''}`}
        style={{ width: size, height: size, ...interactiveStyle }}
      />
    );
  }
  return (
    <span
      className={`agent-chip ${role ?? ''}`}
      title={tip}
      onClick={onClick}
      style={{ width: size, height: size, fontSize: size * 0.42, ...interactiveStyle }}
    >
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
// Phase 13f — wraps an inline AgentChip / agent name so the row click opens
// the global agent-edit popup. Reset button defaults so it visually looks
// like the wrapped content.
const chipClickStyle: React.CSSProperties = {
  flex: 1, minWidth: 0, background: 'transparent', border: 'none',
  color: 'inherit', textAlign: 'left', cursor: 'pointer',
  padding: 0, font: 'inherit',
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
