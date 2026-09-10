import { operatorFetch } from '@/api/operatorSession';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { forgeApi } from '@/api/forgeApi';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import type { AgentDetail, Crew, OverviewAgent, OverviewActivity, Role } from '@/types';
import { useStateStore } from '@/state/useStateStore';

// Map AgentDetail (from /api/agents) → OverviewAgent for the card grid.
function agentToOverview(a: AgentDetail): OverviewAgent & { workItemCount: number } {
  const s = (a.state ?? 'idle').toLowerCase();
  const status: OverviewAgent['status'] =
    s === 'working' || s === 'busy' ? 'working' :
    s === 'queued' || s === 'blocked' || s === 'paused' ? 'queued' : 'idle';
  return {
    id: a.id,
    name: a.name,
    role: (a.role ?? 'eng') as Role,
    status,
    task: s === 'idle' ? '—' : a.state ?? '—',
    capabilities: a.capabilities?.slice(0, 4),
    workItemCount: a.workItemCount ?? 0,
  };
}

type AgentWithCount = OverviewAgent & { workItemCount: number };

const ROLE_LABEL: Record<Role, string> = {
  arch:            'Architects',
  eng:             'Engineers',
  rev:             'Reviewers',
  qa:              'QA / Verifiers',
  human:           'Humans',
  'tech-lead':     'Tech leads',
  'product-owner': 'Product owners',
};
const ROLE_ORDER: Role[] = ['product-owner', 'tech-lead', 'arch', 'eng', 'rev', 'qa', 'human'];

type StatusFilter = 'all' | 'working' | 'queued' | 'idle';
type RoleFilter = 'all' | Role;

const STATUS_CHIPS: Array<{ key: StatusFilter; label: string; pulse?: boolean }> = [
  { key: 'all',     label: 'All' },
  { key: 'working', label: 'Working', pulse: true },
  { key: 'queued',  label: 'Queued' },
  { key: 'idle',    label: 'Idle' },
];

export function SwarmView() {
  const apiState = useStateStore((s) => s.state);
  const workItems = useWorkItemsStore((s) => s.items);
  const navigate = useNavigate();

  const [status, setStatus] = useState<StatusFilter>('all');
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all');
  const [roleDropdownOpen, setRoleDropdownOpen] = useState(false);
  const [collapsedRoles, setCollapsedRoles] = useState<Set<string>>(() => new Set());
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [dateRange, setDateRange] = useState<'day' | 'week' | 'month' | 'all'>('all');
  const [dateDropdownOpen, setDateDropdownOpen] = useState(false);
  const [productFilter, setProductFilter] = useState<string[]>([]);
  const [repoFilter, setRepoFilter] = useState<string[]>([]);

  const toggleRole = (role: string) => {
    setCollapsedRoles((prev) => {
      const next = new Set(prev);
      if (next.has(role)) next.delete(role); else next.add(role);
      return next;
    });
  };

  // Fetch real agents from /api/agents
  const [agentDetails, setAgentDetails] = useState<AgentDetail[]>([]);
  useEffect(() => {
    let cancelled = false;
    void forgeApi.listAgents()
      .then((rows) => { if (!cancelled) setAgentDetails(rows); })
      .catch(() => {});
    const id = setInterval(() => {
      void forgeApi.listAgents()
        .then((rows) => { if (!cancelled) setAgentDetails(rows); })
        .catch(() => {});
    }, 30_000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  const agents = useMemo<AgentWithCount[]>(
    () => agentDetails.map(agentToOverview),
    [agentDetails],
  );

  // Fetch products and repos for multi-select filters
  const [products, setProducts] = useState<Array<{ id: string; name: string }>>([]);
  const [repos, setRepos] = useState<Array<{ id: string; name: string; productId: string }>>([]);
  useEffect(() => {
    let cancelled = false;
    void Promise.all([
      forgeApi.listProducts().catch(() => []),
      operatorFetch('/api/repos').then((r) => r.ok ? r.json() : []).catch(() => []),
    ]).then(([prods, rps]) => {
      if (cancelled) return;
      setProducts((prods as Array<{ id: string; name: string }>).map((p) => ({ id: p.id, name: p.name })));
      setRepos((rps as Array<{ id: string; ghRepo?: string; remoteUrl?: string; productId: string }>).map((r) => ({
        id: r.id, name: r.ghRepo || r.id, productId: r.productId,
      })));
    });
    return () => { cancelled = true; };
  }, []);

  // Filter agents by crew → product/repo (crewId maps to a crew which belongs to a product)
  const hasFilter = status !== 'all' || roleFilter !== 'all' || productFilter.length > 0 || repoFilter.length > 0;

  // Live tape from API state activity feed
  const liveTape = useMemo<OverviewActivity[]>(() => {
    const feed = apiState?.overviewActivity ?? [];
    return feed.slice(0, 8);
  }, [apiState]);

  // Capability heat — computed from actual agent capabilities
  const capHeat = useMemo(() => {
    const counts = new Map<string, { total: number; working: number }>();
    for (const a of agentDetails) {
      for (const cap of a.capabilities ?? []) {
        const entry = counts.get(cap) ?? { total: 0, working: 0 };
        entry.total++;
        const s = (a.state ?? 'idle').toLowerCase();
        if (s === 'working' || s === 'busy') entry.working++;
        counts.set(cap, entry);
      }
    }
    return Array.from(counts.entries())
      .map(([cap, { total, working }]) => ({ cap, count: working, max: total }))
      .sort((a, b) => b.count - a.count || b.max - a.max)
      .slice(0, 8);
  }, [agentDetails]);

  // Phase 16.11 — pull crews so we can render the pair lane. Server-driven
  // because pair state lives on the crew row, not in sample data.
  const [crews, setCrews] = useState<Crew[]>([]);
  useEffect(() => {
    let cancelled = false;
    void forgeApi.listCrews()
      .then((rows) => { if (!cancelled) setCrews(rows); })
      .catch(() => { /* offline / unconfigured — pair lane stays empty */ });
    return () => { cancelled = true; };
  }, []);

  const pairs = useMemo(() => {
    // Pair-crew rows come back symmetric; collapse to one row per pair.
    const byId = new Map(crews.map((c) => [c.id, c]));
    const seen = new Set<string>();
    const out: Array<{ generator: Crew; evaluator: Crew; activeWorkItemId: string | null }> = [];
    for (const c of crews) {
      if (c.pairRole !== 'generator' || !c.pairedWithCrewId) continue;
      const peer = byId.get(c.pairedWithCrewId);
      if (!peer) continue;
      const pairKey = [c.id, peer.id].sort().join(':');
      if (seen.has(pairKey)) continue;
      seen.add(pairKey);
      // Find an in-flight work item routed through this generator crew.
      const wi = workItems.find((w) =>
        w.autoDispatchedAt != null
        && (w.state === 'proposed' || w.state === 'active')
        && w.assignedTo?.id === c.lead);
      out.push({ generator: c, evaluator: peer, activeWorkItemId: wi?.id ?? null });
    }
    return out;
  }, [crews, workItems]);

  const counts = useMemo(() => {
    const acc = { all: agents.length, working: 0, queued: 0, idle: 0 };
    for (const a of agents) acc[a.status]++;
    return acc;
  }, [agents]);

  const visible = useMemo(() => {
    return agents.filter((a: AgentWithCount) => {
      if (status !== 'all' && a.status !== status) return false;
      if (roleFilter !== 'all' && a.role !== roleFilter) return false;
      return true;
    });
  }, [agents, status, roleFilter]);

  const grouped = useMemo(() => {
    const map = new Map<Role, AgentWithCount[]>();
    for (const a of visible) {
      const list = map.get(a.role) ?? [];
      list.push(a);
      map.set(a.role, list);
    }
    return ROLE_ORDER
      .map((r) => ({ role: r, agents: map.get(r) ?? [] }))
      .filter((g) => g.agents.length > 0);
  }, [visible]);

  const totalSpend = 0; // Real cost comes from agent_usage API — placeholder until wired

  return (
    <section className="view active" id="view-swarm">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">07</span>&nbsp;Swarm</span>
            <span className="sep">/</span>
            <span>{counts.working} working · {counts.queued} queued · {counts.idle} idle</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />24h spend ${totalSpend.toFixed(2)}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Agent crew on shift
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M2 6 H 10 M 6 2 V 10" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
            </svg>
            Hire agent
          </button>
          <button className="btn btn-primary">Pause swarm</button>
        </div>
      </div>

      <div className="filter-bar" style={{ paddingBottom: 14 }}>
        <div className="filter-chips">
          {STATUS_CHIPS.map((chip) => (
            <button
              key={chip.key}
              className={`filter-chip${status === chip.key ? ' active' : ''}`}
              onClick={() => setStatus(chip.key)}
            >
              {chip.pulse && <span className="pulse-dot" />}
              {chip.label} <span className="count">{counts[chip.key]}</span>
            </button>
          ))}
        </div>
        <div style={{ position: 'relative' }}>
          <button className="filter-select" onClick={() => setRoleDropdownOpen((v) => !v)}>
            <span className="label">Role</span>
            <span>{roleFilter === 'all' ? 'All' : ROLE_LABEL[roleFilter]}</span>
            <Chevron />
          </button>
          {roleDropdownOpen && (
            <div className="filter-dropdown" style={{
              position: 'absolute', top: '100%', left: 0, zIndex: 30,
              background: 'var(--bg-surface)', border: '1px solid var(--border)',
              borderRadius: 'var(--radius-sm)', boxShadow: 'var(--shadow-lg)',
              minWidth: 160, padding: '4px 0', marginTop: 4,
            }}>
              <button
                className="filter-dropdown-item"
                style={{ display: 'block', width: '100%', textAlign: 'left', padding: '6px 12px', fontSize: 12, color: roleFilter === 'all' ? 'var(--accent)' : 'var(--text-primary)', background: 'none', border: 'none', cursor: 'pointer' }}
                onClick={() => { setRoleFilter('all'); setRoleDropdownOpen(false); }}
              >All roles</button>
              {ROLE_ORDER.map((r) => (
                <button
                  key={r}
                  className="filter-dropdown-item"
                  style={{ display: 'block', width: '100%', textAlign: 'left', padding: '6px 12px', fontSize: 12, color: roleFilter === r ? 'var(--accent)' : 'var(--text-primary)', background: 'none', border: 'none', cursor: 'pointer' }}
                  onClick={() => { setRoleFilter(r); setRoleDropdownOpen(false); }}
                >{ROLE_LABEL[r]}</button>
              ))}
            </div>
          )}
        </div>
        <div style={{ position: 'relative' }}>
          <button className="filter-select" onClick={() => setDateDropdownOpen((v) => !v)}>
            <span className="label">Period</span>
            <span>{{ day: 'Past day', week: 'Past week', month: 'Past month', all: 'All time' }[dateRange]}</span>
            <Chevron />
          </button>
          {dateDropdownOpen && (
            <div className="filter-dropdown" style={{
              position: 'absolute', top: '100%', left: 0, zIndex: 30,
              background: 'var(--bg-surface)', border: '1px solid var(--border)',
              borderRadius: 'var(--radius-sm)', boxShadow: 'var(--shadow-lg)',
              minWidth: 140, padding: '4px 0', marginTop: 4,
            }}>
              {(['all', 'day', 'week', 'month'] as const).map((r) => (
                <button
                  key={r}
                  style={{ display: 'block', width: '100%', textAlign: 'left', padding: '6px 12px', fontSize: 12, color: dateRange === r ? 'var(--accent)' : 'var(--text-primary)', background: 'none', border: 'none', cursor: 'pointer' }}
                  onClick={() => { setDateRange(r); setDateDropdownOpen(false); }}
                >{{ day: 'Past day', week: 'Past week', month: 'Past month', all: 'All time' }[r]}</button>
              ))}
            </div>
          )}
        </div>
        <div style={{ display: 'flex', border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)', overflow: 'hidden' }}>
          <button
            style={{ padding: '4px 10px', fontSize: 11, background: viewMode === 'grid' ? 'var(--bg-surface)' : 'transparent', color: 'var(--text-primary)', border: 'none', cursor: 'pointer' }}
            onClick={() => setViewMode('grid')}
            title="Grid view"
          >▦</button>
          <button
            style={{ padding: '4px 10px', fontSize: 11, background: viewMode === 'list' ? 'var(--bg-surface)' : 'transparent', color: 'var(--text-primary)', border: 'none', cursor: 'pointer', borderLeft: '1px solid var(--border)' }}
            onClick={() => setViewMode('list')}
            title="List view"
          >≡</button>
        </div>
        {products.length > 0 && (
          <SwarmMultiSelect
            label="Product"
            options={products.map((p) => ({ value: p.id, label: p.name }))}
            selected={productFilter}
            onChange={setProductFilter}
          />
        )}
        {repos.length > 0 && (
          <SwarmMultiSelect
            label="Repo"
            options={repos
              .filter((r) => productFilter.length === 0 || productFilter.includes(r.productId))
              .map((r) => ({ value: r.id, label: r.name }))}
            selected={repoFilter}
            onChange={setRepoFilter}
          />
        )}
        {hasFilter && (
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            onClick={() => { setStatus('all'); setRoleFilter('all'); setProductFilter([]); setRepoFilter([]); }}
          >
            Clear filters
          </button>
        )}
        <div className="filter-search">
          <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
            <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
            <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </svg>
          <input type="text" placeholder="Search by name, capability…" />
        </div>
      </div>

      <div className="workstreams-stats" style={{ margin: '0 28px 16px' }}>
        <div className="stat-cell">
          <div className="stat-cell-label">Working now</div>
          <div className="stat-cell-val">{counts.working}<span className="unit">/ {agents.length}</span></div>
          <div className="stat-cell-trend"><span className="up">+2</span> vs last hour</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Queued (waiting on input)</div>
          <div className="stat-cell-val">{counts.queued}</div>
          <div className="stat-cell-trend">avg wait 14m</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Idle (capacity available)</div>
          <div className="stat-cell-val">{counts.idle}</div>
          <div className="stat-cell-trend">free capacity for {Math.max(0, counts.idle * 2)} cards</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Spend last 24h</div>
          <div className="stat-cell-val">${totalSpend.toFixed(2)}<span className="unit">/ $7.50 cap</span></div>
          <div className="stat-cell-trend">{Math.round((totalSpend / 7.5) * 100)}% of budget</div>
        </div>
      </div>

      <div className="swarm-wrap">
        <div className="swarm-main">
          {pairs.length > 0 && (
            <div className="swarm-role-section" key="__pair-lane">
              <div className="swarm-role-head">
                <span className="name">Pair crews</span>
                <span className="meta">· generator + evaluator working a single work item</span>
                <span className="swarm-role-pill arch">{pairs.length}</span>
              </div>
              <div className="swarm-grid">
                {pairs.map((p) => (
                  <button
                    key={`${p.generator.id}-${p.evaluator.id}`}
                    type="button"
                    className="swarm-card"
                    onClick={() => p.activeWorkItemId && navigate(`/work-items/${p.activeWorkItemId}/live`)}
                    disabled={!p.activeWorkItemId}
                    style={{ textAlign: 'left', cursor: p.activeWorkItemId ? 'pointer' : 'default' }}
                    title={p.activeWorkItemId ? `Watch ${p.activeWorkItemId} live` : 'No active work item'}
                  >
                    <div style={{ fontSize: 12, fontWeight: 600 }}>{p.generator.name}</div>
                    <div style={{ fontSize: 11, color: 'var(--text-tertiary)', margin: '4px 0' }}>
                      ↔ {p.evaluator.name}
                    </div>
                    <div style={{ fontSize: 10, color: 'var(--text-secondary)' }}>
                      {p.activeWorkItemId
                        ? <>working <strong>{p.activeWorkItemId}</strong></>
                        : 'idle'}
                    </div>
                  </button>
                ))}
              </div>
            </div>
          )}
          {grouped.map(({ role, agents: rs }) => {
            const collapsed = collapsedRoles.has(role);
            const working = rs.filter((a) => a.status === 'working').length;
            const idle = rs.filter((a) => a.status === 'idle').length;
            const queued = rs.filter((a) => a.status === 'queued').length;
            const totalItems = rs.reduce((s, a) => s + a.workItemCount, 0);
            return (
            <div className="swarm-role-section" key={role}>
              <button
                type="button"
                className="swarm-role-head"
                onClick={() => toggleRole(role)}
                style={{ cursor: 'pointer', width: '100%', background: 'none', border: 'none', textAlign: 'left', display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}
              >
                <svg width="10" height="10" viewBox="0 0 10 10" fill="none" style={{ flexShrink: 0, transition: 'transform 120ms', transform: collapsed ? '' : 'rotate(90deg)' }}>
                  <path d="M3 2 L 7 5 L 3 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
                <span className="name">{ROLE_LABEL[role]}</span>
                <span className={`swarm-role-pill ${role}`}>{rs.length}</span>
                {working > 0 && (
                  <span style={{ fontSize: 10, fontWeight: 600, color: 'var(--success)', background: 'color-mix(in srgb, var(--success) 15%, transparent)', padding: '1px 6px', borderRadius: 99 }}>
                    {working} active
                  </span>
                )}
                {idle > 0 && (
                  <span style={{ fontSize: 10, fontWeight: 600, color: 'var(--text-tertiary)', background: 'var(--bg-surface)', padding: '1px 6px', borderRadius: 99 }}>
                    {idle} idle
                  </span>
                )}
                {queued > 0 && (
                  <span style={{ fontSize: 10, fontWeight: 600, color: 'var(--warning)', background: 'color-mix(in srgb, var(--warning) 15%, transparent)', padding: '1px 6px', borderRadius: 99 }}>
                    {queued} queued
                  </span>
                )}
                {totalItems > 0 && (
                  <span style={{ fontSize: 10, fontWeight: 600, color: 'var(--info)', background: 'color-mix(in srgb, var(--info) 15%, transparent)', padding: '1px 6px', borderRadius: 99 }}>
                    {totalItems} items
                  </span>
                )}
              </button>
              {!collapsed && viewMode === 'grid' && (
                <div className="swarm-grid">
                  {rs.map((a) => <SwarmCard key={a.id} agent={a} />)}
                </div>
              )}
              {!collapsed && viewMode === 'list' && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 2, padding: '8px 0' }}>
                  {rs.map((a) => (
                    <div key={a.id} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '6px 12px', fontSize: 12, borderBottom: '1px solid var(--border)' }}>
                      <span style={{ width: 32, fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', fontSize: 10 }}>{a.id}</span>
                      <span style={{ flex: 1, fontWeight: 500 }}>{a.name}</span>
                      <span style={{ width: 60, fontSize: 10, color: a.status === 'working' ? 'var(--success)' : 'var(--text-tertiary)' }}>{a.status}</span>
                      <span style={{ width: 60, fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--info)' }}>{a.workItemCount} items</span>
                      {a.capabilities && a.capabilities.length > 0 && (
                        <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>{a.capabilities.slice(0, 2).join(', ')}</span>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </div>
            );
          })}
          {grouped.length === 0 && (
            <div className="empty-state" style={{ margin: 0 }}>
              <div className="title">No agents match</div>
              <div className="sub">Clear the filters above to see the full crew.</div>
            </div>
          )}
        </div>

        <aside className="swarm-rail">
          <div className="swarm-rail-card">
            <div className="swarm-rail-card-head">
              <span className="swarm-rail-card-title">Live tape</span>
              <span className="swarm-rail-card-eyebrow">last 8</span>
            </div>
            <div className="swarm-tape">
              {liveTape.length === 0 && (
                <div style={{ padding: '12px 0', color: 'var(--text-tertiary)', fontSize: 11 }}>No recent activity</div>
              )}
              {liveTape.map((t, i) => (
                <div className="swarm-tape-row" key={`${t.time}-${i}`}>
                  <span className="swarm-tape-time">{t.time}</span>
                  <div className="swarm-tape-msg">
                    <span className="who">{t.who}</span>{' '}
                    <span>{t.msg}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="swarm-rail-card">
            <div className="swarm-rail-card-head">
              <span className="swarm-rail-card-title">Capability heat</span>
              <span className="swarm-rail-card-eyebrow">working / cap</span>
            </div>
            <div className="swarm-cap-rows">
              {capHeat.length === 0 && (
                <div style={{ padding: '12px 0', color: 'var(--text-tertiary)', fontSize: 11 }}>No capabilities registered</div>
              )}
              {capHeat.map((c) => (
                <div className="swarm-cap-row" key={c.cap}>
                  <div>
                    <span className="name">{c.cap}</span>
                    <div className="bar">
                      <div className="bar-fill" style={{ width: `${(c.count / c.max) * 100}%` }} />
                    </div>
                  </div>
                  <span className="num">{c.count} / {c.max}</span>
                </div>
              ))}
            </div>
          </div>
        </aside>
      </div>
    </section>
  );
}

function SwarmCard({ agent }: { agent: AgentWithCount }) {
  const taskEmpty = !agent.task || agent.task === '—';
  const portrait = agent.avatar ?? avatarFor(agent.id, agent.name);
  const tip = agentTooltip(agent.id, agent.name, agent.role);
  return (
    <div className="swarm-card" data-status={agent.status} title={agent.bio ?? agent.name}>
      <div className="swarm-card-head">
        {portrait ? (
          <img className="swarm-card-avatar" src={portrait} alt={agent.name} title={tip} />
        ) : (
          <span className="swarm-card-mono" title={tip}>{agent.id}</span>
        )}
        <div className="swarm-card-name-block">
          <div className="swarm-card-name">{agent.name}</div>
          <div className="swarm-card-id">
            <span>{agent.id}</span>
            <span style={{ opacity: 0.5 }}>·</span>
            <span className="role">{agent.role}</span>
          </div>
        </div>
      </div>

      <div className="swarm-card-task" data-empty={taskEmpty || undefined}>
        <span className="label">{agent.status === 'working' ? 'now' : agent.status === 'queued' ? 'waiting on' : 'idle'}</span>
        {taskEmpty ? 'No task assigned' : agent.task}
      </div>

      {agent.capabilities && agent.capabilities.length > 0 && (
        <div className="swarm-card-caps">
          {agent.capabilities.slice(0, 4).map((c) => (
            <span className="swarm-card-cap" key={c}>{c}</span>
          ))}
        </div>
      )}

      <div className="swarm-card-foot">
        <span><span className="num">{agent.workItemCount}</span> work items</span>
      </div>
    </div>
  );
}

function Chevron() {
  return (
    <svg width="9" height="9" viewBox="0 0 9 9">
      <path d="M2 3.5 L4.5 6 L7 3.5" stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round" />
    </svg>
  );
}

function SwarmMultiSelect({ label, options, selected, onChange }: {
  label: string;
  options: Array<{ value: string; label: string }>;
  selected: string[];
  onChange: (v: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    if (open) document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  const toggle = (value: string) => {
    onChange(
      selected.includes(value)
        ? selected.filter((v) => v !== value)
        : [...selected, value],
    );
  };

  const displayLabel = selected.length === 0
    ? `${label}: All`
    : `${label}: ${selected.length}`;

  return (
    <div ref={ref} style={{ position: 'relative' }}>
      <button
        type="button"
        className="filter-select"
        onClick={() => setOpen(!open)}
        style={{
          cursor: 'pointer',
          background: selected.length > 0 ? 'rgba(79,140,255,0.12)' : undefined,
          borderColor: selected.length > 0 ? 'var(--accent)' : undefined,
          minWidth: 110,
        }}
      >
        {displayLabel} ▾
      </button>
      {open && (
        <div style={{
          position: 'absolute', top: '100%', left: 0, zIndex: 100,
          background: 'var(--bg-elevated, var(--bg-surface))',
          border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
          padding: '6px 0', minWidth: 180, maxHeight: 240, overflow: 'auto',
          boxShadow: '0 4px 16px rgba(0,0,0,0.2)', marginTop: 4,
        }}>
          {options.map((opt) => (
            <label
              key={opt.value}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                padding: '5px 12px', cursor: 'pointer', fontSize: 12,
                color: 'var(--text-primary)',
              }}
              onMouseDown={(e) => e.preventDefault()}
            >
              <input
                type="checkbox"
                checked={selected.includes(opt.value)}
                onChange={() => toggle(opt.value)}
                style={{ accentColor: 'var(--accent)' }}
              />
              {opt.label}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}
