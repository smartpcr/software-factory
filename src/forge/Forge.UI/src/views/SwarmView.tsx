import { useMemo, useState } from 'react';
import { sampleState } from '@/sampleData';
import { useStateStore } from '@/state/useStateStore';
import type { OverviewAgent, Role } from '@/types';

// Mocked agent telemetry. The crew + role assignments come from
// src/crew.ts; everything below is presentational seed data so the view
// has shape — the API does not yet emit per-agent throughput / progress.
interface AgentTelemetry {
  progress: number;
  cardsTouched: number;
  prsOpened: number;
  spend: number;
  uptime: string;
}
const TELEMETRY: Record<string, AgentTelemetry> = {
  TY: { progress: 0,  cardsTouched: 1,  prsOpened: 0, spend: 0.42, uptime: '2h 14m' },
  YS: { progress: 0,  cardsTouched: 0,  prsOpened: 0, spend: 0.38, uptime: '5h 02m' },
  WZ: { progress: 41, cardsTouched: 1,  prsOpened: 0, spend: 0.27, uptime: '1h 12m' },
  NZ: { progress: 64, cardsTouched: 3,  prsOpened: 1, spend: 0.42, uptime: '3h 21m' },
  AB: { progress: 92, cardsTouched: 2,  prsOpened: 1, spend: 0.38, uptime: '1h 48m' },
  ZR: { progress: 28, cardsTouched: 2,  prsOpened: 1, spend: 0.31, uptime: '2h 09m' },
  GG: { progress: 11, cardsTouched: 1,  prsOpened: 0, spend: 0.14, uptime: '0h 38m' },
  MB: { progress: 55, cardsTouched: 4,  prsOpened: 0, spend: 0.18, uptime: '1h 02m' },
  SG: { progress: 78, cardsTouched: 1,  prsOpened: 0, spend: 0.21, uptime: '1h 30m' },
  YX: { progress: 33, cardsTouched: 1,  prsOpened: 0, spend: 0.12, uptime: '0h 46m' },
  SF: { progress: 70, cardsTouched: 2,  prsOpened: 0, spend: 0.09, uptime: '4h 11m' },
  ZZ: { progress: 22, cardsTouched: 0,  prsOpened: 0, spend: 0.06, uptime: '6h 03m' },
};

// Live tape — recent emissions from agents. Newest first.
const TAPE: Array<{ time: string; who: string; msg: string }> = [
  { time: '12:51', who: 'NZ Ne Zha',          msg: 'pushed 1 commit · <span class="ref">ws/oidc-rotation</span>' },
  { time: '12:48', who: 'CI',                  msg: '<span class="ref">PR #228</span> merged' },
  { time: '12:46', who: 'AB Ao Bing',          msg: 'tests passing · opening <span class="ref">PR #237</span>' },
  { time: '12:42', who: 'SG Shen Gongbao',     msg: 'flagged <span class="ref">PR #240</span> · /security/' },
  { time: '12:31', who: 'ZR Zhu Rong',         msg: 'building rate-limit store · 28%' },
  { time: '12:18', who: 'WZ Wen Zhong',        msg: 'drafted <span class="ref">ADR-022</span> · OTel propagation' },
  { time: '11:52', who: 'TY Taiji Zhenren',    msg: 'reviewed <span class="ref">ADR-021</span> · approved' },
  { time: '11:09', who: 'YX Yun Xiao',         msg: 'API design feedback · <span class="ref">PR #241</span>' },
];

// Capability heat — count of currently-working agents per capability.
const CAP_HEAT: Array<{ cap: string; count: number; max: number }> = [
  { cap: 'auth',                 count: 3, max: 5 },
  { cap: 'distributed systems',  count: 2, max: 5 },
  { cap: 'observability',        count: 2, max: 5 },
  { cap: 'frontend',             count: 1, max: 5 },
  { cap: 'security',             count: 1, max: 5 },
  { cap: 'fuzzing',              count: 1, max: 5 },
  { cap: 'platform plumbing',    count: 0, max: 5 },
];

const ROLE_LABEL: Record<Role, string> = {
  arch:  'Architects',
  eng:   'Engineers',
  rev:   'Reviewers',
  qa:    'QA / Verifiers',
  human: 'Humans',
};
const ROLE_BLURB: Record<Role, string> = {
  arch:  'Frame the problem · own the ADRs · keep blast radii small',
  eng:   'Cut PRs · ship features · keep the test suite green',
  rev:   'Read every diff · catch what the engineers missed',
  qa:    'Fuzz, soak, watch the dials · break things so users don\'t',
  human: 'Approve gates · arbitrate on close calls',
};
const ROLE_ORDER: Role[] = ['arch', 'eng', 'rev', 'qa', 'human'];

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
  const agents = apiState?.overviewAgents ?? sampleState.overviewAgents;

  const [status, setStatus] = useState<StatusFilter>('all');
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all');

  const counts = useMemo(() => {
    const acc = { all: agents.length, working: 0, queued: 0, idle: 0 };
    for (const a of agents) acc[a.status]++;
    return acc;
  }, [agents]);

  const visible = useMemo(() => {
    return agents.filter((a) => {
      if (status !== 'all' && a.status !== status) return false;
      if (roleFilter !== 'all' && a.role !== roleFilter) return false;
      return true;
    });
  }, [agents, status, roleFilter]);

  const grouped = useMemo(() => {
    const map = new Map<Role, OverviewAgent[]>();
    for (const a of visible) {
      const list = map.get(a.role) ?? [];
      list.push(a);
      map.set(a.role, list);
    }
    return ROLE_ORDER
      .map((r) => ({ role: r, agents: map.get(r) ?? [] }))
      .filter((g) => g.agents.length > 0);
  }, [visible]);

  const totalSpend = useMemo(
    () => agents.reduce((s, a) => s + (TELEMETRY[a.id]?.spend ?? 0), 0),
    [agents],
  );

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
        <button className="filter-select" onClick={() => setRoleFilter('all')}>
          <span className="label">Role</span>
          <span>{roleFilter === 'all' ? 'All 4' : ROLE_LABEL[roleFilter]}</span>
          <Chevron />
        </button>
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
          {grouped.map(({ role, agents: rs }) => (
            <div className="swarm-role-section" key={role}>
              <div className="swarm-role-head">
                <span className="name">{ROLE_LABEL[role]}</span>
                <span className="meta">· {ROLE_BLURB[role]}</span>
                <span className={`swarm-role-pill ${role}`}>{rs.length}</span>
              </div>
              <div className="swarm-grid">
                {rs.map((a) => <SwarmCard key={a.id} agent={a} />)}
              </div>
            </div>
          ))}
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
              {TAPE.map((t, i) => (
                <div className="swarm-tape-row" key={`${t.time}-${i}`}>
                  <span className="swarm-tape-time">{t.time}</span>
                  <div className="swarm-tape-msg">
                    <span className="who">{t.who}</span>{' '}
                    <span dangerouslySetInnerHTML={{ __html: t.msg }} />
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
              {CAP_HEAT.map((c) => (
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

function SwarmCard({ agent }: { agent: OverviewAgent }) {
  const t = TELEMETRY[agent.id];
  const taskEmpty = !agent.task || agent.task === '—';
  return (
    <div className="swarm-card" data-status={agent.status} title={agent.bio ?? agent.name}>
      <div className="swarm-card-head">
        {agent.avatar ? (
          <img className="swarm-card-avatar" src={agent.avatar} alt={agent.name} />
        ) : (
          <span className="swarm-card-mono">{agent.id}</span>
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
        {agent.status === 'working' && t && (
          <div className="swarm-card-progress">
            <div className="swarm-card-progress-fill" style={{ width: `${t.progress}%` }} />
          </div>
        )}
      </div>

      {agent.capabilities && agent.capabilities.length > 0 && (
        <div className="swarm-card-caps">
          {agent.capabilities.slice(0, 4).map((c) => (
            <span className="swarm-card-cap" key={c}>{c}</span>
          ))}
        </div>
      )}

      <div className="swarm-card-foot">
        <span><span className="num">{t?.cardsTouched ?? 0}</span> cards · <span className="num">{t?.prsOpened ?? 0}</span> PRs</span>
        <span>${(t?.spend ?? 0).toFixed(2)} · {t?.uptime ?? '0m'}</span>
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
