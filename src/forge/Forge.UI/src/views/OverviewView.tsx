import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useStateStore } from '@/state/useStateStore';
import { useBoardStore, computeBurndown } from '@/state/useBoardStore';
import { sampleState } from '@/sampleData';
import type { OverviewActivity, OverviewAgent } from '@/types';
import { agentTooltip, avatarFor } from '@/agentAvatar';

// Verbatim port of the prototype's renderOverview (lines 4094-4170).
export function OverviewView() {
  const apiState = useStateStore((s) => s.state);
  const cards = useBoardStore((s) => s.cards);
  const epics = apiState?.epics ?? sampleState.epics;
  const workstreams = apiState?.workstreams ?? sampleState.workstreams;
  const prs = apiState?.prs ?? sampleState.prs;
  const overviewActivity = apiState?.overviewActivity ?? sampleState.overviewActivity;
  // Crew is presentation-only seed — always source from the local crew so
  // the bundled avatars + capability chips render even when the API has its
  // own (older) agent list cached.
  const overviewAgents = sampleState.overviewAgents;
  const navigate = useNavigate();

  const burndown = useMemo(() => computeBurndown(cards), [cards]);
  const activeWs = workstreams.filter((w) => w.status === 'active' || w.status === 'review' || w.status === 'merging').length;
  const openPrs = prs.length;
  const needApproval = prs.filter((p) => p.humanGate).length;
  const workingAgents = overviewAgents.filter((a) => a.status === 'working').length;
  const conflicts = workstreams.filter((w) => w.conflicts > 0).length;

  const stats = [
    { label: 'Active workstreams', val: String(activeWs), sub: `across ${epics.length} epics` },
    { label: 'PRs in flight',      val: String(openPrs),  sub: `${needApproval} awaiting approval` },
    { label: 'Sprint progress',    val: `${burndown.pct}%`, sub: `${burndown.done} / ${burndown.total} pts · day 6/10` },
    { label: 'Spend today',        val: '$4.81',         sub: '64% of $7.50 cap' },
  ];

  const links: Array<{ view: string; label: string; sub: string }> = [
    { view: '/plan',        label: 'Plan & Design board', sub: `${cards.length} items · ${epics.length} epics` },
    { view: '/timeline',    label: 'Branch timeline',     sub: `14d window · ${workstreams.length} branches` },
    { view: '/workstreams', label: 'Workstreams',         sub: `${activeWs} active · ${conflicts} conflict${conflicts === 1 ? '' : 's'}` },
    { view: '/prs',         label: 'Pull requests',       sub: `${openPrs} open · ${needApproval} need approval` },
    { view: '/audit',       label: 'Audit log',           sub: 'every write, who & when' },
  ];

  return (
    <section className="view active" id="view-overview">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">00</span>&nbsp;Overview</span>
            <span className="sep">/</span>
            <span>last 24h</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{workingAgents} agents working</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Atlas factory floor
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary" onClick={() => navigate('/plan')}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 1 V 11 M 1 6 H 11" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
            </svg>
            New
          </button>
          <button className="btn btn-primary" onClick={() => navigate('/plan')}>Open last touched</button>
        </div>
      </div>

      <div className="overview-grid" id="overview-grid">
        <div className="overview-stats">
          {stats.map((s) => (
            <div className="stat-cell" key={s.label}>
              <div className="stat-cell-label">{s.label}</div>
              <div className="stat-cell-val">{s.val}</div>
              <div className="stat-cell-trend">{s.sub}</div>
            </div>
          ))}
        </div>

        <div className="overview-card">
          <div className="overview-card-head">
            <span className="overview-card-title">Activity feed</span>
            <span className="overview-card-eyebrow">live</span>
          </div>
          <div className="activity-feed">
            {overviewActivity.map((a, idx) => (
              <ActivityRow key={`${a.time}-${idx}`} a={a} />
            ))}
          </div>
        </div>

        <div className="overview-card">
          <div className="overview-card-head">
            <span className="overview-card-title">Agent crew</span>
            <span className="overview-card-eyebrow">{workingAgents} working</span>
          </div>
          <div className="agents-list">
            {overviewAgents.map((a) => <AgentRow key={a.id} a={a} />)}
          </div>

          <div className="overview-card-head" style={{ marginTop: 22 }}>
            <span className="overview-card-title">Jump to</span>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {links.map((l) => (
              <div
                key={l.view}
                className="ws-pr-row"
                style={{ cursor: 'pointer', padding: '9px 12px' }}
                onClick={() => navigate(l.view)}
              >
                <span className="title" style={{ fontWeight: 600 }}>{l.label}</span>
                <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)' }}>
                  {l.sub}
                </span>
                <span style={{ color: 'var(--text-tertiary)', fontSize: 14 }}>→</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}

function ActivityRow({ a }: { a: OverviewActivity }) {
  return (
    <div className="activity-feed-row">
      <div className="activity-feed-time">{a.time}</div>
      <div className="activity-feed-msg">
        <span className="who">{a.who}</span>{' '}
        <span dangerouslySetInnerHTML={{ __html: a.msg }} />
      </div>
    </div>
  );
}

function AgentRow({ a }: { a: OverviewAgent }) {
  const dotCls = a.status === 'idle' ? 'idle' : a.status === 'queued' ? 'queued' : '';
  const avatarTip = agentTooltip(a.id, a.name, a.role);
  // Server-side overview agents (A1/A2/E4/…) lack a bundled `avatar`; fall
  // back to the global lookup keyed by id so the row gets a portrait.
  const portrait = a.avatar ?? avatarFor(a.id, a.name);
  return (
    <div className="agent-row" title={a.bio ?? a.name}>
      {portrait ? (
        <img
          src={portrait}
          alt={a.name}
          className={`agent-avatar ${a.role}`}
          title={avatarTip}
          onError={(e) => {
            const img = e.currentTarget;
            const fallback = document.createElement('span');
            fallback.className = `agent-chip ${a.role}`;
            fallback.style.borderColor = 'var(--bg-sunken)';
            fallback.textContent = a.id;
            fallback.title = avatarTip;
            img.replaceWith(fallback);
          }}
        />
      ) : (
        <span className={`agent-chip ${a.role}`} style={{ borderColor: 'var(--bg-sunken)' }} title={avatarTip}>{a.id}</span>
      )}
      <div className="agent-id-block">
        <div className="agent-id-row">
          <span className="name">{a.name}</span>
          <span className="role-tag">{a.role}</span>
        </div>
        {a.capabilities && a.capabilities.length > 0 && (
          <div className="agent-caps">
            {a.capabilities.slice(0, 4).map((c) => (
              <span className="agent-cap" key={c}>{c}</span>
            ))}
          </div>
        )}
      </div>
      <span className="agent-task" title={a.task}>{a.task}</span>
      <span className={`activity-dot ${dotCls}`} />
    </div>
  );
}
